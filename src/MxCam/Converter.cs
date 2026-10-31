using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MxCam;

/// <summary>원본 동영상 정보 (ffprobe).</summary>
public sealed class MediaInfo
{
    public string Path { get; init; } = "";
    public double Duration { get; init; }
    public long Size { get; init; }
    public string? VCodec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string? ACodec { get; init; }
    public int AChannels { get; init; }
    public string Container { get; init; } = "";

    public bool HasVideo => VCodec != null;
    public double Mbps => Duration > 0 ? Size * 8 / Duration / 1_000_000 : 0;
    /// <summary>픽셀당 비트 (압축 정도: 낮을수록 이미 많이 압축됨).</summary>
    public double BitsPerPixel => HasVideo && Width > 0 && Fps > 0 ? Size * 8 / Duration / (Width * (double)Height * Fps) : 0;

    public string Summary =>
        HasVideo
            ? $"{CodecName(VCodec)} {Width}×{Height} {Fps:0.##}fps · {TimeSpan.FromSeconds(Duration):h\\:mm\\:ss} · {Size / 1048576.0:0.0} MB ({Mbps:0.0} Mbps){(ACodec != null ? " · " + ACodec.ToUpperInvariant() : " · 소리 없음")}"
            : $"소리 파일 {ACodec?.ToUpperInvariant()} · {TimeSpan.FromSeconds(Duration):h\\:mm\\:ss} · {Size / 1048576.0:0.0} MB";

    public static string CodecName(string? c) => c switch
    {
        "h264" => "H.264", "hevc" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "vp8" => "VP8", "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2", "mpeg1video" => "MPEG-1", "wmv2" or "wmv3" or "vc1" => "WMV", "msmpeg4v3" => "MS MPEG-4",
        "prores" => "ProRes", "mjpeg" => "MJPEG", null => "-", _ => c.ToUpperInvariant(),
    };

    /// <summary>원본 코덱의 압축 효율 등급 (Codecs의 Efficiency와 같은 척도).</summary>
    public int SourceEfficiency => VCodec switch
    {
        "av1" => 9, "hevc" or "vp9" => 8, "h264" => 6, "vp8" or "mpeg4" or "msmpeg4v3" or "wmv3" or "vc1" => 4,
        "mpeg2video" or "wmv2" or "flv1" => 2, "mpeg1video" => 1, _ => 0, // 0: 무압축/중간코덱(ProRes, MJPEG 등)
    };

    public static MediaInfo Probe(string ffmpeg, string file)
    {
        var (code, json) = Ffmpeg.RunCapture(Ffmpeg.ProbePath(ffmpeg), new[]
        {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file,
        }, 30000);
        if (code != 0) throw new InvalidOperationException("동영상 정보를 읽을 수 없습니다.\n" + json.Trim());
        using var doc = JsonDocument.Parse(json[json.IndexOf('{')..]);
        var root = doc.RootElement;
        JsonElement? v = null, a = null;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            bool attached = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
            if (type == "video" && v == null && !attached) v = s;
            if (type == "audio" && a == null) a = s;
        }
        var fmt = root.GetProperty("format");
        double dur = double.TryParse(Str(fmt, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) ? dd : 0;
        return new MediaInfo
        {
            Path = file,
            Duration = dur,
            Size = new FileInfo(file).Length,
            Container = Str(fmt, "format_name") ?? "",
            VCodec = v is { } vs ? Str(vs, "codec_name") : null,
            Width = v is { } v1 && v1.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            Height = v is { } v2 && v2.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            Fps = v is { } v3 ? Rate(Str(v3, "avg_frame_rate")) is > 0 and var r ? r : Rate(Str(v3, "r_frame_rate")) : 0,
            ACodec = a is { } asx ? Str(asx, "codec_name") : null,
            AChannels = a is { } a2 && a2.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0,
        };
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString() : null;

    static double Rate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }
}

public enum ConvertPreset { Recommended, Compatible, Web720, Hevc, Av1, Remux, Gif, Mp3 }

/// <summary>변환 설정 한 가지 (프리셋 + 품질 단계).</summary>
public sealed class ConvertPlan
{
    public ConvertPreset Preset { get; init; }
    public VKind Video { get; init; } = VKind.None;
    public string Ext { get; init; } = "mp4";
    public string Title { get; init; } = "";
    public string Reason { get; set; } = "";
    public int Level { get; init; } = 3;
    public double VideoLevel { get; init; } = 3;
}

public sealed record SampleResult(double EstimatedMB, double Ssim, double Speed, string? Error);

public static class Converter
{
    public static readonly (ConvertPreset preset, string name)[] Presets =
    {
        (ConvertPreset.Recommended, "★ 추천 (용량↓ 화질 유지)"),
        (ConvertPreset.Compatible, "호환성 최우선 (H.264 MP4 — 어디서나 재생)"),
        (ConvertPreset.Web720, "웹·메신저 공유용 (H.264 720p)"),
        (ConvertPreset.Hevc, "고효율 HEVC (H.265 MP4)"),
        (ConvertPreset.Av1, "최소 용량 AV1 (느림)"),
        (ConvertPreset.Remux, "MP4로 포장만 변경 (재인코딩 없음 · 화질 그대로)"),
        (ConvertPreset.Gif, "움짤 GIF (앞부분 10초)"),
        (ConvertPreset.Mp3, "소리만 MP3"),
    };

    static VKind FirstAvailable(params VKind[] kinds) => kinds.FirstOrDefault(Codecs.IsVideoAvailable, VKind.X264);

    /// <summary>원본을 보고 추천 프리셋을 고른다. ("용량은 작게, 화질은 유지" 기준)</summary>
    public static ConvertPreset Recommend(MediaInfo src, Settings s, out string reason)
    {
        if (!src.HasVideo)
        {
            reason = src.ACodec is "mp3" ? "이미 MP3 소리 파일입니다." : "영상이 없는 소리 파일 → MP3가 가장 무난합니다.";
            return ConvertPreset.Mp3;
        }
        var best = Codecs.Recommended(s, src.Width, src.Height, (int)Math.Round(src.Fps));
        // 원본이 이미 추천 코덱 이상으로 효율적이고 압축도 충분하면 다시 인코딩해도 이득이 거의 없고 화질만 손해
        if (src.SourceEfficiency >= best.Efficiency && src.BitsPerPixel < 0.08)
        {
            bool mp4 = src.Container.Contains("mp4");
            reason = $"원본이 이미 효율적인 {MediaInfo.CodecName(src.VCodec)}이고 충분히 압축되어 있어(픽셀당 {src.BitsPerPixel:0.000}bit) 다시 인코딩하면 화질만 떨어집니다."
                     + (mp4 ? " 변환하지 않는 것을 권장합니다." : " MP4로 포장만 바꾸는 것을 권장합니다.");
            return ConvertPreset.Remux;
        }
        string why = src.SourceEfficiency < best.Efficiency
            ? $"원본 {MediaInfo.CodecName(src.VCodec)}보다 {best.Label}의 압축 효율이 높아"
            : $"원본 비트레이트가 높아({src.Mbps:0.0} Mbps)";
        reason = $"{why} 같은 화질에서 용량을 크게 줄일 수 있습니다. (이 PC 추천 인코더: {best.Label})";
        return ConvertPreset.Recommended;
    }

    public static ConvertPlan Plan(ConvertPreset preset, MediaInfo src, Settings s, int level)
    {
        int fps = Math.Max(1, (int)Math.Round(src.Fps));
        switch (preset)
        {
            case ConvertPreset.Recommended:
            {
                var best = Codecs.Recommended(s, src.Width, src.Height, fps);
                return new ConvertPlan { Preset = preset, Video = best.Kind, Ext = best.Kind == VKind.Vp9 ? "webm" : "mp4",
                    Title = best.Label, Level = level, VideoLevel = Codecs.AutoLevelFor(s, best.Kind, level) };
            }
            case ConvertPreset.Compatible:
                return new ConvertPlan { Preset = preset, Video = FirstAvailable(VKind.Nvenc264, VKind.Qsv264, VKind.Amf264, VKind.X264), Title = "H.264 MP4", Level = level, VideoLevel = level };
            case ConvertPreset.Web720:
                return new ConvertPlan { Preset = preset, Video = FirstAvailable(VKind.Nvenc264, VKind.Qsv264, VKind.Amf264, VKind.X264), Title = "H.264 720p MP4", Level = Math.Min(level, 3), VideoLevel = Math.Min(level, 3) };
            case ConvertPreset.Hevc:
                return new ConvertPlan { Preset = preset, Video = FirstAvailable(VKind.NvencHevc, VKind.QsvHevc, VKind.AmfHevc, VKind.X265), Title = "HEVC MP4", Level = level, VideoLevel = level };
            case ConvertPreset.Av1:
                return new ConvertPlan { Preset = preset, Video = FirstAvailable(VKind.NvencAv1, VKind.SvtAv1), Title = "AV1 MP4", Level = level, VideoLevel = level };
            case ConvertPreset.Remux:
                return new ConvertPlan { Preset = preset, Ext = src.HasVideo ? "mp4" : "m4a", Title = "MP4 (스트림 복사)", Level = level };
            case ConvertPreset.Gif:
                return new ConvertPlan { Preset = preset, Video = VKind.Gif, Ext = "gif", Title = "GIF", Level = level };
            default:
                return new ConvertPlan { Preset = ConvertPreset.Mp3, Ext = "mp3", Title = "MP3", Level = level };
        }
    }

    /// <summary>ffmpeg 인수 (입력/출력 제외 부분 포함 전체). start/length를 주면 그 구간만.</summary>
    public static List<string> BuildArgs(ConvertPlan p, MediaInfo src, string output, double? start = null, double? length = null)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (start is { } ss) a.AddRange(new[] { "-ss", ss.ToString("0.###", CultureInfo.InvariantCulture) });
        if (p.Preset == ConvertPreset.Gif && length == null) length = Math.Min(10, src.Duration);
        if (length is { } t) a.AddRange(new[] { "-t", t.ToString("0.###", CultureInfo.InvariantCulture) });
        a.AddRange(new[] { "-i", src.Path });
        int fps = Math.Max(1, (int)Math.Round(src.Fps));
        switch (p.Preset)
        {
            case ConvertPreset.Remux:
                a.AddRange(new[] { "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy" });
                if (p.Ext == "mp4") a.AddRange(new[] { "-movflags", "+faststart" });
                break;
            case ConvertPreset.Mp3:
                a.AddRange(new[] { "-vn" });
                a.AddRange(Codecs.AudioArgs(AKind.Mp3, p.Level, 0, Math.Clamp(src.AChannels, 1, 2)));
                break;
            case ConvertPreset.Gif:
            {
                int gfps = new[] { 8, 10, 12, 15, 20 }[Math.Clamp(p.Level, 1, 5) - 1];
                int maxW = new[] { 480, 640, 800, 1024, 1280 }[Math.Clamp(p.Level, 1, 5) - 1];
                a.AddRange(new[] { "-vf", $"fps={gfps},scale='min(iw,{maxW})':-2:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle", "-loop", "0" });
                break;
            }
            default:
                a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });
                if (p.Preset == ConvertPreset.Web720 && src.Height > 720) a.AddRange(new[] { "-vf", "scale=-2:720:flags=lanczos" });
                a.AddRange(Codecs.VideoArgs(p.Video, p.VideoLevel, p.Ext, fps));
                if (src.ACodec != null) a.AddRange(Codecs.AudioArgs(p.Ext == "webm" ? AKind.Opus : AKind.Aac, p.Level, 0, Math.Clamp(src.AChannels, 1, 2)));
                if (p.Ext is "mp4" or "mov" or "m4v") a.AddRange(new[] { "-movflags", "+faststart" });
                break;
        }
        a.Add(output);
        return a;
    }

    public static string DefaultOutput(MediaInfo src, ConvertPlan p)
    {
        string dir = System.IO.Path.GetDirectoryName(src.Path)!;
        string name = System.IO.Path.GetFileNameWithoutExtension(src.Path) + "_MxCam";
        string path = System.IO.Path.Combine(dir, $"{name}.{p.Ext}");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{name}_{i}.{p.Ext}");
        return path;
    }

    /// <summary>
    /// 샘플 테스트: 영상 가운데 6초를 실제로 변환해서 전체 예상 용량·화질(원본 대비 SSIM)·속도를 잰다.
    /// </summary>
    public static SampleResult Sample(string ffmpeg, MediaInfo src, ConvertPlan p, CancellationToken ct)
    {
        if (p.Preset is ConvertPreset.Remux)
            return new SampleResult(src.Size / 1048576.0, 1, 0, null);
        double len = Math.Min(6, src.Duration);
        double start = Math.Max(0, src.Duration / 2 - len / 2);
        if (p.Preset == ConvertPreset.Gif) { start = 0; len = Math.Min(10, src.Duration); }
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mxcam_sample_{Guid.NewGuid():N}.{p.Ext}");
        try
        {
            var sw = Stopwatch.StartNew();
            var (code, log) = Ffmpeg.RunCapture(ffmpeg, BuildArgs(p, src, tmp, start, len), 300_000);
            sw.Stop();
            ct.ThrowIfCancellationRequested();
            if (code != 0 || !File.Exists(tmp)) return new SampleResult(0, 0, 0, log.Trim().Split('\n').LastOrDefault());
            double sampleMB = new FileInfo(tmp).Length / 1048576.0;
            double total = p.Preset == ConvertPreset.Gif ? sampleMB : sampleMB / len * src.Duration;
            double ssim = 1;
            if (p.Preset != ConvertPreset.Mp3 && p.Preset != ConvertPreset.Gif && src.HasVideo)
            {
                var (_, slog) = Ffmpeg.RunCapture(ffmpeg, new[]
                {
                    "-hide_banner", "-i", tmp,
                    "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", len.ToString("0.###", CultureInfo.InvariantCulture), "-i", src.Path,
                    "-lavfi", "[1:v][0:v]scale2ref=flags=bicubic[b][a];[a]format=yuv420p[a2];[b]format=yuv420p[b2];[a2][b2]ssim", "-f", "null", "-",
                }, 300_000);
                var m = Regex.Match(slog, @"All:([0-9.]+)");
                ssim = m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            }
            return new SampleResult(total, ssim, len / Math.Max(0.001, sw.Elapsed.TotalSeconds), null);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>전체 변환. progress(0~1) 보고, 취소 시 ffmpeg 종료 후 미완성 파일 삭제.</summary>
    public static async Task<(bool ok, string message)> RunAsync(string ffmpeg, MediaInfo src, ConvertPlan p, string output,
        IProgress<double> progress, CancellationToken ct)
    {
        var args = BuildArgs(p, src, output);
        args.InsertRange(args.Count - 1, new[] { "-progress", "pipe:1", "-nostats" });
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var x in args) psi.ArgumentList.Add(x);
        using var proc = Process.Start(psi)!;
        var err = new StringBuilder();
        double total = p.Preset == ConvertPreset.Gif ? Math.Min(10, src.Duration) : src.Duration;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("out_time_us=") && long.TryParse(e.Data[12..], out var us) && total > 0)
                progress.Report(Math.Clamp(us / 1048576.0 / total, 0, 1));
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.StandardInput.Write('q'); proc.StandardInput.Flush(); } catch { }
            if (!proc.WaitForExit(3000)) try { proc.Kill(true); } catch { }
            try { File.Delete(output); } catch { }
            return (false, "취소됨");
        }
        if (proc.ExitCode != 0 || !File.Exists(output))
        {
            try { File.Delete(output); } catch { }
            return (false, "변환 실패\n" + err.ToString().Trim());
        }
        progress.Report(1);
        return (true, "");
    }
}
