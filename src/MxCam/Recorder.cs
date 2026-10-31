using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;

namespace MxCam;

public sealed class RecordOptions
{
    public Rectangle Region { get; init; }
    public RecordMode Mode { get; init; } = RecordMode.Screen;
    public required CodecProfile Profile { get; init; }
    public int Level { get; init; } = 3;
    /// <summary>영상 품질 단계(소수 가능). 없으면 Level 사용 — 자동 모드의 보정 단계용.</summary>
    public double? VideoLevel { get; init; }
    public int Fps { get; init; } = 30;
    public bool Cursor { get; init; } = true;
    public bool SystemAudio { get; init; } = true;
    public bool Microphone { get; init; }
    public string? MicDeviceId { get; init; }
    public required string OutputPath { get; init; }
    public required string FfmpegExe { get; init; }
    public int TimeLimitSec { get; init; }
    public bool ClickEffect { get; init; }
    public string? WatermarkPath { get; init; }
    public int WatermarkOpacity { get; init; } = 70;
    public WatermarkPos WatermarkPosition { get; init; } = WatermarkPos.BottomRight;
    public int AudioSampleRate { get; init; } = 48000;
    public int AudioChannels { get; init; } = 2;
    /// <summary>GIF 모드에서 변환 없이 무손실 중간파일(mkv)을 OutputPath로 저장 (실측 추천의 기준 영상용).</summary>
    public bool LosslessOnly { get; init; }
}

public sealed record RecordResult(bool Ok, string Path, string Message, double Seconds, long Frames, long Duplicated);

/// <summary>
/// 녹화 파이프라인.
///   캡처 스레드(BitBlt) → [영상 pipe] ─┐
///   WASAPI 시스템 소리   → [소리 pipe] ─┼→ ffmpeg (실시간 인코딩/먹싱) → 파일
///   WASAPI 마이크        → [소리 pipe] ─┘
/// 기준 시계 = 녹음된 샘플 수(오디오 클럭 마스터). 프레임 번호 = 시계 × fps,
/// 늦어서 건너뛴 번호는 직전 프레임을 복제해 고정 프레임레이트를 유지.
/// 일시정지 동안은 소리를 버리고 프레임도 안 보내므로 한 파일 안에서 자연스럽게 이어진다.
/// </summary>
public sealed class Recorder : IDisposable
{
    readonly RecordOptions _o;
    readonly List<string> _warnings = new();
    ScreenCapturer? _cap;
    PipeFeeder? _video;
    readonly List<(AudioSource src, PipeFeeder pipe)> _audio = new();
    FfmpegProcess? _ff;
    Thread? _loop;
    AudioSource? _clockSource;
    readonly Stopwatch _sw = new();
    double _pausedTotal, _pauseStartedAt, _swOffset;
    volatile bool _stop, _paused, _limitRaised;
    string _encodePath = "";
    long _frames, _dups;

    public event Action<string>? Faulted;
    public event Action? TimeLimitReached;

    public Recorder(RecordOptions o) => _o = o;

    public string OutputPath => _o.OutputPath;
    public bool IsPaused => _paused;
    public long Frames => Interlocked.Read(ref _frames);
    public long Duplicated => Interlocked.Read(ref _dups);
    public IReadOnlyList<string> Warnings => _warnings;
    public string? CommandLine => _ff?.CommandLine;
    public int VideoWidth => _cap?.Width ?? 0;
    public int VideoHeight => _cap?.Height ?? 0;

    public long OutputBytes
    {
        get
        {
            try { return new FileInfo(_encodePath).Length; } catch { return 0; }
        }
    }

    double StopwatchClock()
    {
        double now = _sw.Elapsed.TotalSeconds;
        return now - _pausedTotal - (_paused ? now - _pauseStartedAt : 0);
    }

    /// <summary>녹화된 시간(초, 일시정지 제외).</summary>
    public double Seconds
    {
        get
        {
            var c = _clockSource;
            return c != null ? c.Seconds : StopwatchClock() + _swOffset;
        }
    }

    public void Start()
    {
        bool hasVideo = _o.Mode != RecordMode.Audio;
        bool wantAudio = _o.Mode != RecordMode.Gif;
        string id = Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(_o.OutputPath)!);
        _encodePath = _o.Mode == RecordMode.Gif && !_o.LosslessOnly
            ? Path.Combine(Path.GetTempPath(), $"mxcam_{id}.mkv")
            : _o.OutputPath;

        try
        {
            if (hasVideo) _cap = new ScreenCapturer(_o.Region, _o.Cursor, _o.ClickEffect);

            if (wantAudio)
            {
                if (_o.SystemAudio) TryAddAudio(AudioSource.SystemLoopback, id);
                if (_o.Microphone) TryAddAudio(() => AudioSource.Microphone(_o.MicDeviceId), id);
                if (_o.Mode == RecordMode.Audio && _audio.Count == 0)
                    throw new InvalidOperationException("녹음할 소리 장치가 없습니다. 소리 메뉴에서 시스템 소리나 마이크를 켜세요.\n" + string.Join("\n", _warnings));
            }

            var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y" };
            int index = 0, videoIndex = -1;
            var audioIndexes = new List<int>();
            if (_cap != null)
            {
                _video = new PipeFeeder("mxcam_v_" + id, 6);
                args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{_cap.Width}x{_cap.Height}",
                    "-framerate", _o.Fps.ToString(), "-thread_queue_size", "64", "-i", _video.Path });
                videoIndex = index++;
            }
            foreach (var (src, pipe) in _audio)
            {
                args.AddRange(new[] { "-f", src.FfmpegFormat, "-ar", src.Format.SampleRate.ToString(), "-ac", src.Format.Channels.ToString(),
                    "-thread_queue_size", "4096", "-i", pipe.Path });
                audioIndexes.Add(index++);
            }

            var filters = new List<string>();
            string? videoOut = videoIndex >= 0 ? $"{videoIndex}:v" : null;
            string? audioOut = audioIndexes.Count == 1 ? $"{audioIndexes[0]}:a" : null;
            if (videoIndex >= 0 && !string.IsNullOrWhiteSpace(_o.WatermarkPath) && File.Exists(_o.WatermarkPath))
            {
                // 워터마크: 영상 폭의 1/4 이내로 축소, 불투명도 적용, 가장자리에서 16px 띄움
                args.AddRange(new[] { "-i", _o.WatermarkPath });
                int wi = index++;
                string alpha = (Math.Clamp(_o.WatermarkOpacity, 0, 100) / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                string pos = _o.WatermarkPosition switch
                {
                    WatermarkPos.BottomLeft => "16:H-h-16",
                    WatermarkPos.TopRight => "W-w-16:16",
                    WatermarkPos.TopLeft => "16:16",
                    WatermarkPos.Center => "(W-w)/2:(H-h)/2",
                    _ => "W-w-16:H-h-16",
                };
                filters.Add($"[{wi}:v]scale=w='min(iw,{_cap!.Width / 4})':h=-1,format=rgba,colorchannelmixer=aa={alpha}[wm];" +
                            $"[{videoIndex}:v][wm]overlay={pos}:format=auto[vout]");
                videoOut = "[vout]";
            }
            if (audioIndexes.Count > 1)
            {
                var ins = string.Concat(audioIndexes.Select(i => $"[{i}:a]"));
                filters.Add($"{ins}amix=inputs={audioIndexes.Count}:duration=longest:dropout_transition=0:normalize=0[aout]");
                audioOut = "[aout]";
            }
            if (filters.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", filters) });
            if (videoOut != null) args.AddRange(new[] { "-map", videoOut });
            if (audioOut != null) args.AddRange(new[] { "-map", audioOut });

            var p = _o.Profile;
            if (_o.Mode == RecordMode.Gif)
            {
                // GIF는 무손실 중간 파일로 녹화 후 팔레트 2-pass 변환
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0" });
            }
            else
            {
                if (hasVideo) args.AddRange(Codecs.VideoArgs(p.Video, _o.VideoLevel ?? _o.Level, p.Ext, _o.Fps));
                if (audioIndexes.Count > 0) args.AddRange(Codecs.AudioArgs(p.Audio, _o.Level, _o.AudioSampleRate, _o.AudioChannels));
                if (p.Muxer != null) args.AddRange(new[] { "-f", p.Muxer });
            }
            args.Add(_encodePath);

            _ff = new FfmpegProcess(_o.FfmpegExe, args);

            foreach (var (src, pipe) in _audio)
            {
                var pp = pipe;
                src.Data += (buf, len) => pp.Add(buf, len);
            }
            _clockSource = _o.Mode == RecordMode.Screen ? _audio.FirstOrDefault().src : null;
            foreach (var (src, _) in _audio) src.Start();
            _sw.Start();

            _loop = new Thread(hasVideo ? CaptureLoop : MonitorLoop) { IsBackground = true, Name = "capture", Priority = ThreadPriority.Highest };
            _loop.Start();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    void TryAddAudio(Func<AudioSource> create, string id)
    {
        try
        {
            var src = create();
            _audio.Add((src, new PipeFeeder($"mxcam_a{_audio.Count}_{id}", 0)));
        }
        catch (Exception ex)
        {
            _warnings.Add("소리 장치를 열 수 없음: " + ex.Message);
        }
    }

    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        if (paused) _pauseStartedAt = _sw.Elapsed.TotalSeconds;
        else _pausedTotal += _sw.Elapsed.TotalSeconds - _pauseStartedAt;
        foreach (var (src, _) in _audio) src.Paused = paused;
        _paused = paused;
    }

    bool CheckHealth()
    {
        if (_ff != null && _ff.HasExited)
        {
            Fault("인코더(ffmpeg)가 중간에 종료되었습니다.\n" + _ff.Log);
            return false;
        }
        if (_video is { Failed: true })
        {
            Fault("인코더로 영상 전달 실패: " + _video.Error + "\n" + _ff?.Log);
            return false;
        }
        if (!_limitRaised && _o.TimeLimitSec > 0 && Seconds >= _o.TimeLimitSec)
        {
            _limitRaised = true;
            ThreadPool.QueueUserWorkItem(_ => TimeLimitReached?.Invoke());
        }
        return true;
    }

    void Fault(string msg)
    {
        _stop = true;
        ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(msg));
    }

    void MonitorLoop()
    {
        while (!_stop)
        {
            if (!CheckHealth()) return;
            Thread.Sleep(50);
        }
    }

    void CaptureLoop()
    {
        var cap = _cap!;
        var video = _video!;
        var pool = new ConcurrentBag<byte[]>();
        int fps = _o.Fps;
        long next = 0;
        double lastAudio = -1, lastAudioChange = 0;
        Native.timeBeginPeriod(1);
        try
        {
            while (!_stop)
            {
                if (!CheckHealth()) return;
                if (_paused)
                {
                    Thread.Sleep(5);
                    lastAudioChange = _sw.Elapsed.TotalSeconds;
                    continue;
                }

                // 오디오 장치가 멈추면(장치 분리 등) 영상이 얼지 않도록 스톱워치 시계로 전환
                var cs = _clockSource;
                if (cs != null)
                {
                    double a = cs.Seconds, now = _sw.Elapsed.TotalSeconds;
                    if (a != lastAudio) { lastAudio = a; lastAudioChange = now; }
                    else if (now - lastAudioChange > 1.0 && now > 1.5)
                    {
                        _swOffset = a - StopwatchClock();
                        _clockSource = null;
                        lock (_warnings) _warnings.Add("소리 장치 응답 없음 → 내부 시계로 전환");
                    }
                }

                double t = Seconds;
                long target = (long)Math.Floor(t * fps);
                if (target < next)
                {
                    double waitMs = (next / (double)fps - t) * 1000;
                    Thread.Sleep(Math.Clamp((int)waitMs, 1, 50));
                    continue;
                }

                cap.Capture();
                if (!pool.TryTake(out var buf)) buf = new byte[cap.FrameBytes];
                cap.CopyTo(buf);
                int repeat = (int)(target - next + 1);
                var b = buf;
                while (!video.TryAdd(b, cap.FrameBytes, repeat, () => pool.Add(b), 200))
                {
                    if (_stop || !CheckHealth()) return;
                }
                Interlocked.Add(ref _frames, repeat);
                Interlocked.Add(ref _dups, repeat - 1);
                next = target + 1;
            }
        }
        catch (Exception ex)
        {
            Fault("화면 캡처 오류: " + ex.Message);
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    public RecordResult Stop()
    {
        double secs = Seconds;
        _stop = true;
        _loop?.Join(5000);
        foreach (var (src, _) in _audio) src.Stop();
        Thread.Sleep(60); // 마지막 소리 패킷 수신 대기
        _video?.Complete();
        foreach (var (_, pipe) in _audio) pipe.Complete();

        bool exited = _ff?.WaitForExit(120_000) ?? true;
        if (!exited) _ff?.Kill();
        int code = exited ? _ff?.ExitCode ?? -1 : -1;
        string log = _ff?.Log ?? "";
        Cleanup();

        if (code != 0 || !File.Exists(_encodePath) || new FileInfo(_encodePath).Length == 0)
            return new RecordResult(false, _o.OutputPath, $"인코딩 실패 (code {code})\n{log}", secs, Frames, Duplicated);

        if (_o.Mode == RecordMode.Gif && !_o.LosslessOnly)
        {
            var (ok, msg) = ConvertToGif(_encodePath, _o.OutputPath, _o.Level, _o.FfmpegExe);
            try { File.Delete(_encodePath); } catch { }
            if (!ok) return new RecordResult(false, _o.OutputPath, "GIF 변환 실패\n" + msg, secs, Frames, Duplicated);
        }
        string warn = _warnings.Count > 0 ? string.Join("\n", _warnings) : "";
        return new RecordResult(true, _o.OutputPath, warn, secs, Frames, Duplicated);
    }

    public static (bool ok, string log) ConvertToGif(string src, string dst, int level, string ffmpeg)
    {
        int fps = new[] { 8, 10, 12, 15, 20 }[Math.Clamp(level, 1, 5) - 1];
        int maxW = new[] { 480, 640, 800, 1024, 0 }[Math.Clamp(level, 1, 5) - 1];
        string scale = maxW > 0 ? $",scale='min(iw,{maxW})':-2:flags=lanczos" : "";
        string vf = $"fps={fps}{scale},split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";
        var (code, log) = Ffmpeg.RunCapture(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", src, "-vf", vf, "-loop", "0", dst }, 600_000);
        return (code == 0 && File.Exists(dst), log);
    }

    public static void Screenshot(Rectangle region, bool cursor, string path)
    {
        using var cap = new ScreenCapturer(region, cursor);
        cap.Capture();
        using var bmp = cap.ToBitmap();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        bmp.Save(path, ext is ".jpg" or ".jpeg" ? ImageFormat.Jpeg : ext == ".bmp" ? ImageFormat.Bmp : ImageFormat.Png);
    }

    void Cleanup()
    {
        foreach (var (src, pipe) in _audio)
        {
            src.Dispose();
            pipe.Join(3000);
            pipe.Dispose();
        }
        _audio.Clear();
        if (_video != null)
        {
            _video.Join(3000);
            _video.Dispose();
            _video = null;
        }
        _cap?.Dispose();
        _cap = null;
        _ff?.Dispose();
        _ff = null;
    }

    public void Dispose()
    {
        _stop = true;
        _loop?.Join(3000);
        Cleanup();
    }
}
