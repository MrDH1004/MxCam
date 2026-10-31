using System.Globalization;
using System.Text;

namespace MxCam;

/// <summary>MxCam.exe --selftest [로그경로] [quick] : 녹화 엔진을 UI 없이 검증.</summary>
internal static class SelfTest
{
    static StreamWriter _log = null!;
    static int _fail;

    static void Log(string s)
    {
        _log.WriteLine(s);
        _log.Flush();
    }

    public static int Run(string[] args)
    {
        string logPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "selftest.log");
        bool quick = args.Contains("quick");
        if (args.Contains("excl"))
        {
            _log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            var sc = Screen.PrimaryScreen!.Bounds;
            ExclusionTest(new Rectangle(sc.Right - 500, sc.Bottom - 450, 300, 200));
            _log.Dispose();
            return _fail;
        }
        bool benchOnly = args.Contains("bench");
        _log = new StreamWriter(logPath, false, new UTF8Encoding(false));
        try
        {
            var ffmpeg = Ffmpeg.Resolve(null) ?? throw new Exception("ffmpeg 없음");
            Log("ffmpeg: " + ffmpeg);
            Codecs.Probe(ffmpeg);
            foreach (var i in Codecs.AllVideoInfos)
                Log($"  encoder {i.Label,-24} {(Codecs.IsVideoAvailable(i.Kind) ? "OK" : "-")}");
            var s = new Settings { Fps = 30, Level = 3 };
            string outDir = Path.Combine(Path.GetTempPath(), "mxcam_selftest");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var screen = Screen.PrimaryScreen!.Bounds;
            var region = new Rectangle(screen.X + 100, screen.Y + 100, 641, 361); // 홀수 크기 → 짝수 보정 확인
            Log($"best static: {Codecs.BestVideo(1920, 1080, 30).Label}");

            if (!benchOnly)
            {
                // 1) 자동 3단계 + 일시정지: 2초 녹화 → 1.5초 정지 → 2초 녹화 → 기대 길이 ≈ 4초
                var auto = Codecs.AutoProfile(s, region.Width, region.Height, 30);
                Log($"\n[auto] {auto.Name}");
                var file = Path.Combine(outDir, "auto_pause.mp4");
                var r = Record(new RecordOptions { Region = region, Profile = auto, Level = 3, Fps = 30, OutputPath = file, FfmpegExe = ffmpeg, SystemAudio = true },
                    2000, pauseMs: 1500, afterMs: 2000);
                Check("auto+pause", r, file, ffmpeg, expectSec: 4.0, expectAudio: true);

                // 2) 소리 + 마이크 동시
                file = Path.Combine(outDir, "mic_mix.mkv");
                r = Record(new RecordOptions { Region = region, Profile = Codecs.Find("mkv_nvenc264_aac")!.WithFallback(), Level = 3, Fps = 30,
                    OutputPath = file, FfmpegExe = ffmpeg, SystemAudio = true, Microphone = true }, 2500);
                Check("system+mic amix", r, file, ffmpeg, 2.5, true);

                // 3) 전체화면 60fps (부하/중복프레임 확인)
                file = Path.Combine(outDir, "full60.mp4");
                r = Record(new RecordOptions { Region = screen, Profile = auto, Level = 3, Fps = 60, OutputPath = file, FfmpegExe = ffmpeg, SystemAudio = true }, 3000);
                Check("fullscreen 60fps", r, file, ffmpeg, 3.0, true);

                // 4) GIF / 소리만
                file = Path.Combine(outDir, "anim.gif");
                r = Record(new RecordOptions { Region = region, Mode = RecordMode.Gif, Profile = Codecs.Find("gif")!, Level = 3, Fps = 15, OutputPath = file, FfmpegExe = ffmpeg }, 2000);
                Check("gif", r, file, ffmpeg, 2.0, false, gif: true);
                foreach (var id in new[] { "a_mp3", "a_wma", "a_aac", "a_flac", "a_wav" })
                {
                    var p = Codecs.Find(id)!;
                    file = Path.Combine(outDir, "audio." + p.Ext);
                    r = Record(new RecordOptions { Mode = RecordMode.Audio, Profile = p, Level = 3, OutputPath = file, FfmpegExe = ffmpeg, SystemAudio = true }, 2000);
                    Check("audio " + id, r, file, ffmpeg, 2.0, true, audioOnly: true);
                }

                // 5) 스크린샷
                file = Path.Combine(outDir, "shot.png");
                Recorder.Screenshot(region, true, file);
                Log($"\n[screenshot] {(File.Exists(file) && new FileInfo(file).Length > 1000 ? "PASS" : "FAIL")} {file}");

                // 6) 캡처 제외(WDA_EXCLUDEFROMCAPTURE) 확인
                ExclusionTest(new Rectangle(screen.Right - 500, screen.Bottom - 450, 300, 200));

                // 7) oCam 코덱 전체 (각 1.5초)
                if (!quick)
                {
                    Log("\n[all codecs]");
                    foreach (var p in Codecs.All.Where(p => !p.AudioOnly && p.Video != VKind.Gif))
                    {
                        if (!Codecs.IsAvailable(p)) { Log($"  SKIP {p.Name} (이 PC 미지원)"); continue; }
                        file = Path.Combine(outDir, p.Id + "." + p.Ext);
                        r = Record(new RecordOptions { Region = region, Profile = p, Level = 3, Fps = 30, OutputPath = file, FfmpegExe = ffmpeg, SystemAudio = true }, 1500);
                        Check("  " + p.Name, r, file, ffmpeg, 1.5, true);
                    }
                    for (int lv = 1; lv <= 5; lv++)
                    {
                        file = Path.Combine(outDir, $"auto_lv{lv}.mp4");
                        r = Record(new RecordOptions { Region = region, Profile = auto, Level = lv, Fps = 30, OutputPath = file, FfmpegExe = ffmpeg, SystemAudio = true }, 2000);
                        Check($"  auto level {lv}", r, file, ffmpeg, 2.0, true);
                    }
                }
            }

            // 8) 실측 추천
            Log("\n[benchmark]");
            var progress = new Progress<string>(m => Log("  .. " + m));
            var benchRegion = new Rectangle(screen.X, screen.Y, Math.Min(1280, screen.Width), Math.Min(720, screen.Height));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = Recommender.RunAsync(s, ffmpeg, benchRegion, 3, progress, CancellationToken.None).GetAwaiter().GetResult();
            Log($"  ({sw.Elapsed.TotalSeconds:0}s)");
            for (int lv = 1; lv <= 5; lv++)
            {
                var best = Recommender.Decide(results, lv);
                Log($"  -- {lv}단계: 기준 화질 {Recommender.TargetDb(results, lv):0.00} dB → 추천 {best?.Label ?? "(없음)"} (보정 단계 {best?.CalibratedLevel:0.00})");
                foreach (var b in results)
                    Log($"     {b.Label,-22} {(b.Error ?? $"실측 {b.Mb[lv - 1],6:0.00} MB/분 SSIM {b.Ssim[lv - 1]:0.0000} | 같은화질 {b.MBAtTarget,6:0.00} MB/분 (state {b.State}, lv {b.CalibratedLevel:0.00}) x{b.Speed:0.0}")}");
                if (best == null) _fail++;
            }
            s.Bench = results;
            s.BenchPixelRate = (long)benchRegion.Width * benchRegion.Height * s.Fps;
            var autoP = Codecs.AutoProfile(s, benchRegion.Width, benchRegion.Height, s.Fps);
            double autoLv = Codecs.AutoLevel(s, autoP.Video, benchRegion.Width, benchRegion.Height, s.Fps);
            Log($"  자동 모드 결과: {autoP.Name}, 보정 단계 {autoLv:0.00}");
            Log("  args: " + string.Join(' ', Codecs.VideoArgs(autoP.Video, autoLv, "mp4", s.Fps)));
            Log("  hint(x264 선택 시): " + Codecs.Hint(s, Codecs.Find("mp4_x264")!, benchRegion.Width, benchRegion.Height, s.Fps));
            Log("  hint(MPEG-4 선택 시): " + Codecs.Hint(s, Codecs.Find("avi_mpeg4")!, benchRegion.Width, benchRegion.Height, s.Fps));
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
            _fail++;
        }
        Log($"\nRESULT: {(_fail == 0 ? "ALL PASS" : _fail + " FAIL")}");
        _log.Dispose();
        return _fail == 0 ? 0 : 1;
    }

    static CodecProfile WithFallback(this CodecProfile p) =>
        Codecs.IsAvailable(p) ? p : Codecs.Find("mkv_x264_flac")!;

    static RecordResult Record(RecordOptions o, int ms, int pauseMs = 0, int afterMs = 0)
    {
        using var rec = new Recorder(o);
        string? fault = null;
        rec.Faulted += m => fault = m;
        rec.Start();
        Thread.Sleep(ms);
        if (pauseMs > 0)
        {
            rec.SetPaused(true);
            Thread.Sleep(pauseMs);
            rec.SetPaused(false);
            Thread.Sleep(afterMs);
        }
        var r = rec.Stop();
        if (fault != null) r = r with { Ok = false, Message = fault + "\n" + r.Message };
        foreach (var w in rec.Warnings) Log("    warn: " + w);
        return r;
    }

    static void Check(string name, RecordResult r, string file, string ffmpeg, double expectSec, bool expectAudio, bool audioOnly = false, bool gif = false)
    {
        if (!r.Ok)
        {
            Log($"FAIL {name}: {r.Message}");
            _fail++;
            return;
        }
        var (_, info) = Ffmpeg.RunCapture(Ffmpeg.ProbePath(ffmpeg), new[]
            { "-v", "error", "-show_entries", "stream=codec_type,codec_name,pix_fmt,width,height,duration:format=duration", "-of", "compact", file }, 30000);
        double dur = Recommender.ProbeDuration(ffmpeg, file);
        bool hasV = info.Contains("codec_type=video"), hasA = info.Contains("codec_type=audio");
        var problems = new List<string>();
        if (!audioOnly && !hasV) problems.Add("영상 없음");
        if (expectAudio && !hasA) problems.Add("소리 없음");
        if (!gif && Math.Abs(dur - expectSec) > Math.Max(0.6, expectSec * 0.2)) problems.Add($"길이 {dur:0.00}s (기대 {expectSec}s)");
        if (!gif && !audioOnly)
        {
            var vd = ParseStreamDuration(info, "video");
            var ad = ParseStreamDuration(info, "audio");
            if (vd > 0 && ad > 0 && Math.Abs(vd - ad) > 0.25) problems.Add($"A/V 길이 차 {vd - ad:+0.00;-0.00}s");
        }
        string status = problems.Count == 0 ? "PASS" : "FAIL";
        if (problems.Count > 0) _fail++;
        string streams = string.Join(" | ", info.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        Log($"{status} {name}: {dur:0.00}s, {new FileInfo(file).Length / 1024}KB, frames {r.Frames} (dup {r.Duplicated}) {string.Join(", ", problems)}");
        Log($"     {streams}");
    }

    static double ParseStreamDuration(string info, string type)
    {
        foreach (var line in info.Split('\n'))
        {
            if (!line.Contains("codec_type=" + type)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"duration=([0-9.]+)");
            if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return 0;
    }

    static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    static void ExclusionTest(Rectangle region)
    {
        using var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.FromArgb(255, 0, 255), TopMost = true,
            StartPosition = FormStartPosition.Manual, Bounds = region, ShowInTaskbar = false,
        };
        f.Show();
        Pump(800);
        Log($"  form bounds={f.Bounds} visible={f.Visible} region={region} screen={Screen.PrimaryScreen!.Bounds}");
        using var cap = new ScreenCapturer(region, false);
        Color before = Color.Empty;
        for (int i = 0; i < 20; i++) // 창이 실제로 화면에 그려질 때까지 최대 4초 대기
        {
            f.Activate();
            Pump(200);
            cap.Capture();
            before = cap.PixelAt(region.Width / 2, region.Height / 2);
            if (before.R == 255 && before.G == 0 && before.B == 255) break;
        }
        Native.ExcludeFromCapture(f.Handle);
        Pump(500);
        cap.Capture();
        var after = cap.PixelAt(region.Width / 2, region.Height / 2);
        f.Close();
        bool ok = before.R == 255 && before.G == 0 && before.B == 255 && !(after.R == 255 && after.G == 0 && after.B == 255);
        Log($"\n[exclude-from-capture] {(ok ? "PASS" : "FAIL")} before={before} after={after}");
        if (!ok) _fail++;
    }
}
