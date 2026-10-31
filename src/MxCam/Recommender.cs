using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MxCam;

/// <summary>
/// 실측 추천 ("용량은 작고 화질은 좋은" 코덱 찾기).
///  1) 지금 녹화 영역을 몇 초 무손실로 캡처해 기준 영상을 만든다.
///  2) 이 PC에서 쓸 수 있는 인코더마다 품질 1~5단계를 모두 인코딩해 용량·화질(SSIM)·속도를 잰다 → 품질-용량 곡선.
///  3) 기준 화질 = 선택한 단계에서 x264가 낸 화질. 각 코덱 곡선에서 그 화질일 때의 용량을 보간(외삽 안 함).
///  4) 실시간 녹화가 가능한 코덱 중 "같은 화질 환산 용량"이 가장 작은 것을 추천.
///     자동 모드는 추천 코덱을 기준 화질에 맞춘 보정 단계로 녹화한다.
/// </summary>
public static class Recommender
{
    public const double RealtimeMargin = 1.25;   // 녹화 중 캡처 부하를 고려한 여유

    public static VKind[] Candidates => new[]
    {
        VKind.NvencHevc, VKind.Nvenc264, VKind.NvencAv1, VKind.QsvHevc, VKind.Qsv264, VKind.AmfHevc, VKind.Amf264,
        VKind.X264, VKind.X265, VKind.SvtAv1, VKind.Vp9, VKind.Mpeg4,
    };

    public static async Task<List<BenchResult>> RunAsync(Settings s, string ffmpeg, Rectangle region, int seconds,
        IProgress<string> progress, CancellationToken ct)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mxcam_bench_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string reference = Path.Combine(dir, "ref.mkv");
        var results = new List<BenchResult>();
        try
        {
            progress.Report($"화면을 {seconds}초 동안 무손실로 캡처하는 중… (평소 녹화할 화면을 띄워 두세요)");
            var refProfile = new CodecProfile { Id = "ref", Name = "ref", Group = "", Video = VKind.X264, Audio = AKind.None, Ext = "mkv" };
            var rec = new Recorder(new RecordOptions
            {
                Region = region, Mode = RecordMode.Gif, Profile = refProfile, Level = 5, Fps = s.Fps, Cursor = s.RecordCursor,
                SystemAudio = false, OutputPath = reference, FfmpegExe = ffmpeg, LosslessOnly = true,
            });
            rec.Start();
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
            finally
            {
                var rr = await Task.Run(rec.Stop);
                rec.Dispose();
                if (!rr.Ok) throw new InvalidOperationException("기준 영상 캡처 실패: " + rr.Message);
            }
            double duration = ProbeDuration(ffmpeg, reference);
            if (duration <= 0) throw new InvalidOperationException("기준 영상 캡처 실패");

            var kinds = Candidates.Where(Codecs.IsVideoAvailable).ToList();
            foreach (var k in kinds)
            {
                ct.ThrowIfCancellationRequested();
                var info = Codecs.Video(k);
                var r = new BenchResult { Kind = k, Label = info.Label };
                double minSpeed = double.MaxValue;
                for (int lv = 1; lv <= 5 && r.Error == null; lv++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report($"[{results.Count + 1}/{kinds.Count}] {info.Label} — {lv}단계 측정 중…");
                    int level = lv;
                    var p = await Task.Run(() => EncodeOnce(ffmpeg, reference, dir, info, level, s.Fps, duration), ct);
                    if (p.error != null) { r.Error = p.error; break; }
                    r.Mb[lv - 1] = p.mb;
                    r.Ssim[lv - 1] = p.ssim;
                    minSpeed = Math.Min(minSpeed, p.speed);
                }
                if (r.Error == null)
                {
                    r.Speed = minSpeed;
                    r.Realtime = minSpeed >= RealtimeMargin;
                }
                results.Add(r);
            }
            Decide(results, s.Level);
            return results;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static (double mb, double ssim, double speed, string? error) EncodeOnce(string ffmpeg, string reference, string dir,
        VideoEncoderInfo info, int level, int fps, double duration)
    {
        string ext = info.Kind == VKind.Vp9 ? "webm" : "mp4";
        string outFile = Path.Combine(dir, $"{info.Kind}_{level}.{ext}");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", reference };
        args.AddRange(Codecs.VideoArgs(info.Kind, level, ext, fps));
        args.AddRange(new[] { "-an", outFile });
        var sw = Stopwatch.StartNew();
        var (code, log) = Ffmpeg.RunCapture(ffmpeg, args, 300_000);
        sw.Stop();
        if (code != 0 || !File.Exists(outFile))
            return (0, 0, 0, log.Trim().Split('\n').LastOrDefault() ?? "실패");
        double mb = new FileInfo(outFile).Length / 1_000_000.0 / duration * 60;
        double ssim = Ssim(ffmpeg, outFile, reference);
        try { File.Delete(outFile); } catch { }
        return (mb, ssim, duration / Math.Max(0.001, sw.Elapsed.TotalSeconds), ssim > 0 ? null : "화질 측정 실패");
    }

    static double Ssim(string ffmpeg, string distorted, string reference)
    {
        var (_, log) = Ffmpeg.RunCapture(ffmpeg, new[]
        {
            "-hide_banner", "-i", distorted, "-i", reference,
            "-lavfi", "[0:v]format=yuv420p[a];[1:v]format=yuv420p[b];[a][b]ssim", "-f", "null", "-",
        }, 300_000);
        var m = Regex.Match(log, @"All:([0-9.]+)");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public static double ProbeDuration(string ffmpeg, string file)
    {
        var (code, outp) = Ffmpeg.RunCapture(Ffmpeg.ProbePath(ffmpeg), new[]
            { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file }, 30000);
        return code == 0 && double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>SSIM → dB (화질 차이를 선형에 가깝게 비교하기 위함).</summary>
    public static double Db(double ssim) => -10 * Math.Log10(Math.Max(1e-9, 1 - ssim));

    static bool Valid(BenchResult r) => r.Error == null && r.Ssim.Length == 5 && r.Ssim.All(v => v > 0) && r.Mb.All(v => v > 0);

    /// <summary>기준 화질(dB): 선택 단계에서 x264의 화질. x264가 없으면 측정 코덱들의 중앙값.</summary>
    public static double TargetDb(List<BenchResult> results, int level)
    {
        var ok = results.Where(Valid).ToList();
        if (ok.Count == 0) return 0;
        var x264 = ok.FirstOrDefault(r => r.Kind == VKind.X264);
        if (x264 != null) return Db(x264.Ssim[level - 1]);
        var v = ok.Select(r => Db(r.Ssim[level - 1])).OrderBy(d => d).ToList();
        return v[v.Count / 2];
    }

    /// <summary>
    /// 코덱 곡선 위에서 목표 화질의 위치. 반환: (환산 용량, 보정 단계, 상태)
    ///  상태 0 = 범위 안(정확), -1 = 가장 낮은 단계도 목표보다 화질이 좋음(1단계 용량 = 상한값), 1 = 5단계도 목표 화질 미달.
    /// </summary>
    public static (double mb, double level, int state) AtQuality(BenchResult r, double targetDb)
    {
        var db = r.Ssim.Select(Db).ToArray();
        // 단조 증가로 정리 (측정 잡음 보정)
        for (int i = 1; i < 5; i++) db[i] = Math.Max(db[i], db[i - 1]);
        if (targetDb <= db[0]) return (r.Mb[0], 1, -1);
        if (targetDb > db[4]) return (r.Mb[4], 5, 1);
        for (int i = 0; i < 4; i++)
        {
            if (targetDb > db[i + 1]) continue;
            double span = db[i + 1] - db[i];
            double f = span < 1e-6 ? 0 : (targetDb - db[i]) / span;
            double lnMb = Math.Log(r.Mb[i]) + f * (Math.Log(r.Mb[i + 1]) - Math.Log(r.Mb[i]));
            return (Math.Exp(lnMb), i + 1 + f, 0);
        }
        return (r.Mb[4], 5, 1);
    }

    /// <summary>
    /// 판정: 실시간 녹화 가능 + 목표 화질 도달 가능한 코덱 중 같은 화질 환산 용량 최소.
    /// 5% 이내 차이면 GPU 인코더(CPU 부담·끊김 적음) → 호환성 순.
    /// 각 결과의 MBAtTarget / CalibratedLevel / State 를 채운다.
    /// </summary>
    public static BenchResult? Decide(List<BenchResult> results, int level)
    {
        double target = TargetDb(results, level);
        foreach (var r in results)
        {
            if (!Valid(r)) { r.MBAtTarget = 0; continue; }
            var (mb, lv, state) = AtQuality(r, target);
            r.MBAtTarget = mb;
            r.CalibratedLevel = lv;
            r.State = state;
        }
        var ok = results.Where(r => Valid(r) && r.Realtime && r.State <= 0).ToList();
        if (ok.Count == 0) return null;
        double min = ok.Min(r => r.MBAtTarget);
        return ok.Where(r => r.MBAtTarget <= min * 1.05)
                 .OrderByDescending(r => Codecs.Video(r.Kind).Hardware)
                 .ThenByDescending(r => Codecs.Video(r.Kind).Compat)
                 .ThenBy(r => r.MBAtTarget)
                 .First();
    }
}
