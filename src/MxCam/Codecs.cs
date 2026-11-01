using System.Diagnostics;

namespace MxCam;

public enum VKind
{
    None, X264, X265, OpenH264, Nvenc264, NvencHevc, NvencAv1, Qsv264, QsvHevc, Amf264, AmfHevc,
    Mpeg1, Mpeg2, Mpeg4, Xvid, DivX, MsMpeg4v2, MsMpeg4v3, Flv, Wmv2, Vp9, SvtAv1, Gif
}

public enum AKind { None, Aac, Mp3, Flac, Mp2, Wma, Pcm, Opus }

/// <summary>
/// Efficiency = 같은 화질 기준 용량 효율(10이 가장 작음), Speed = 실시간 인코딩 여유(5가 가장 빠름/가벼움),
/// Compat = 재생 호환성(5가 어디서나 재생).
/// </summary>
public sealed record VideoEncoderInfo(VKind Kind, string Encoder, string Label, int Efficiency, int Speed, int Compat, bool Hardware);

public sealed class CodecProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public VKind Video { get; init; }
    public AKind Audio { get; init; }
    public required string Ext { get; init; }
    public string? Muxer { get; init; }
    public bool FromOcam { get; init; } = true;

    public bool AudioOnly => Video == VKind.None;
    public VideoEncoderInfo? VideoInfo => AudioOnly ? null : Codecs.Video(Video);
    public override string ToString() => Name;
}

public static class Codecs
{
    public const string AutoId = "auto";

    public static readonly string[] LevelNames =
    {
        "1단계 · 최소 용량",
        "2단계 · 작은 용량",
        "3단계 · 표준 (권장)",
        "4단계 · 고화질",
        "5단계 · 최고 화질",
    };

    static readonly VideoEncoderInfo[] VideoInfos =
    {
        new(VKind.SvtAv1,     "libsvtav1",  "AV1 (SVT, CPU)",          10, 2, 2, false),
        new(VKind.NvencAv1,   "av1_nvenc",  "NVIDIA NVENC AV1",         9, 5, 2, true),
        new(VKind.X265,       "libx265",    "HEVC (x265, CPU)",         9, 2, 3, false),
        new(VKind.NvencHevc,  "hevc_nvenc", "NVIDIA NVENC HEVC",        8, 5, 3, true),
        new(VKind.QsvHevc,    "hevc_qsv",   "Intel QSV HEVC",           8, 5, 3, true),
        new(VKind.AmfHevc,    "hevc_amf",   "AMD AMF HEVC",             7, 5, 3, true),
        new(VKind.Vp9,        "libvpx-vp9", "VP9 (CPU)",                8, 3, 3, false),
        new(VKind.X264,       "libx264",    "H.264 (x264, CPU)",        7, 4, 5, false),
        new(VKind.Nvenc264,   "h264_nvenc", "NVIDIA NVENC H.264",       6, 5, 5, true),
        new(VKind.Qsv264,     "h264_qsv",   "Intel QSV H.264",          6, 5, 5, true),
        new(VKind.Amf264,     "h264_amf",   "AMD AMF H.264",            5, 5, 5, true),
        new(VKind.OpenH264,   "libopenh264","Open H.264",               5, 4, 5, false),
        new(VKind.Xvid,       "libxvid",    "MPEG-4 Xvid",              4, 5, 4, false),
        new(VKind.Mpeg4,      "mpeg4",      "ISO MPEG-4",               4, 5, 4, false),
        new(VKind.DivX,       "mpeg4",      "MPEG-4 OpenDivX",          4, 5, 4, false),
        new(VKind.MsMpeg4v3,  "msmpeg4",    "MS MPEG-4 파트 3",         3, 5, 3, false),
        new(VKind.MsMpeg4v2,  "msmpeg4v2",  "MS MPEG-4 파트 2",         3, 5, 3, false),
        new(VKind.Wmv2,       "wmv2",       "WMV 8",                    3, 5, 3, false),
        new(VKind.Flv,        "flv",        "Flash Video (H.263)",      2, 5, 2, false),
        new(VKind.Mpeg2,      "mpeg2video", "ISO MPEG-2",               2, 5, 4, false),
        new(VKind.Mpeg1,      "mpeg1video", "ISO MPEG-1",               1, 5, 4, false),
        new(VKind.Gif,        "gif",        "GIF 애니메이션",           0, 3, 5, false),
    };

    static readonly Dictionary<VKind, VideoEncoderInfo> InfoMap = VideoInfos.ToDictionary(i => i.Kind);
    public static VideoEncoderInfo Video(VKind k) => InfoMap[k];
    public static IEnumerable<VideoEncoderInfo> AllVideoInfos => VideoInfos;

    public static string AudioEncoder(AKind a) => a switch
    {
        AKind.Aac => "aac",
        AKind.Mp3 => "libmp3lame",
        AKind.Flac => "flac",
        AKind.Mp2 => "mp2",
        AKind.Wma => "wmav2",
        AKind.Pcm => "pcm_s16le",
        AKind.Opus => "libopus",
        _ => "",
    };

    public static string AudioLabel(AKind a) => a switch
    {
        AKind.Aac => "AAC", AKind.Mp3 => "MP3", AKind.Flac => "FLAC", AKind.Mp2 => "MP2",
        AKind.Wma => "WMA", AKind.Pcm => "PCM", AKind.Opus => "Opus", _ => "",
    };

    /// <summary>기본 코덱 목록(oCam 코덱 메뉴와 호환되는 구성) + 확장(x264/x265/VP9/AV1/AMF).</summary>
    public static readonly List<CodecProfile> All = BuildCatalog();

    static List<CodecProfile> BuildCatalog()
    {
        var l = new List<CodecProfile>();
        void Add(string id, string name, string group, VKind v, AKind a, string ext, string? mux = null, bool ocam = true) =>
            l.Add(new CodecProfile { Id = id, Name = name, Group = group, Video = v, Audio = a, Ext = ext, Muxer = mux, FromOcam = ocam });

        // AVI
        Add("avi_mpeg1", "ISO MPEG-1 표준 (.AVI)", "AVI", VKind.Mpeg1, AKind.Mp3, "avi");
        Add("avi_mpeg2", "ISO MPEG-2 표준 (.AVI)", "AVI", VKind.Mpeg2, AKind.Mp3, "avi");
        Add("avi_mpeg4", "ISO MPEG-4 표준 (.AVI)", "AVI", VKind.Mpeg4, AKind.Mp3, "avi");
        Add("avi_xvid", "ISO MPEG-4 XVID (.AVI)", "AVI", VKind.Xvid, AKind.Mp3, "avi");
        Add("avi_divx", "ISO MPEG-4 OpenDivx (.AVI)", "AVI", VKind.DivX, AKind.Mp3, "avi");
        Add("avi_msmpeg4v2", "마이크로소프트 MPEG-4 파트 2 (.AVI)", "AVI", VKind.MsMpeg4v2, AKind.Mp3, "avi");
        Add("avi_msmpeg4v3", "마이크로소프트 MPEG-4 파트 3 (.AVI)", "AVI", VKind.MsMpeg4v3, AKind.Mp3, "avi");
        Add("avi_nvenc264", "엔비디아 NVENC H.264 + AAC (.AVI)", "AVI", VKind.Nvenc264, AKind.Aac, "avi");
        Add("avi_nvenchevc", "엔비디아 NVENC HEVC + AAC (.AVI)", "AVI", VKind.NvencHevc, AKind.Aac, "avi");
        Add("avi_qsv264", "인텔 퀵 싱크 비디오 H.264 + AAC (.AVI)", "AVI", VKind.Qsv264, AKind.Aac, "avi");
        // MP4
        Add("mp4_mpeg4_mp3", "ISO MPEG-4 표준 + MP3 (.MP4)", "MP4", VKind.Mpeg4, AKind.Mp3, "mp4");
        Add("mp4_mpeg4_aac", "ISO MPEG-4 표준 + AAC (.MP4)", "MP4", VKind.Mpeg4, AKind.Aac, "mp4");
        Add("mp4_nvenc264", "엔비디아 NVENC H.264 + AAC (.MP4)", "MP4", VKind.Nvenc264, AKind.Aac, "mp4");
        Add("mp4_nvenchevc", "엔비디아 NVENC HEVC + AAC (.MP4)", "MP4", VKind.NvencHevc, AKind.Aac, "mp4");
        Add("mp4_qsv264", "인텔 퀵 싱크 비디오 H.264 + AAC (.MP4)", "MP4", VKind.Qsv264, AKind.Aac, "mp4");
        Add("mp4_qsvhevc", "인텔 퀵 싱크 비디오 HEVC + AAC (.MP4)", "MP4", VKind.QsvHevc, AKind.Aac, "mp4");
        Add("mp4_x264", "H.264 x264 + AAC (.MP4)", "MP4", VKind.X264, AKind.Aac, "mp4", ocam: false);
        Add("mp4_x265", "HEVC x265 + AAC (.MP4)", "MP4", VKind.X265, AKind.Aac, "mp4", ocam: false);
        Add("mp4_amf264", "AMD AMF H.264 + AAC (.MP4)", "MP4", VKind.Amf264, AKind.Aac, "mp4", ocam: false);
        Add("mp4_amfhevc", "AMD AMF HEVC + AAC (.MP4)", "MP4", VKind.AmfHevc, AKind.Aac, "mp4", ocam: false);
        Add("mp4_nvencav1", "엔비디아 NVENC AV1 + AAC (.MP4)", "MP4", VKind.NvencAv1, AKind.Aac, "mp4", ocam: false);
        Add("mp4_svtav1", "AV1 SVT + AAC (.MP4)", "MP4", VKind.SvtAv1, AKind.Aac, "mp4", ocam: false);
        // MOV
        Add("mov_mpeg4_mp3", "애플 퀵타임 + MP3 (.MOV)", "MOV", VKind.Mpeg4, AKind.Mp3, "mov");
        Add("mov_mpeg4_aac", "애플 퀵타임 + AAC (.MOV)", "MOV", VKind.Mpeg4, AKind.Aac, "mov");
        Add("mov_nvenc264", "엔비디아 NVENC H.264 + AAC (.MOV)", "MOV", VKind.Nvenc264, AKind.Aac, "mov");
        Add("mov_qsv264", "인텔 퀵 싱크 비디오 H.264 + AAC (.MOV)", "MOV", VKind.Qsv264, AKind.Aac, "mov");
        // MKV
        Add("mkv_mpeg4_flac", "ISO MPEG-4 표준 + FLAC (.MKV)", "MKV", VKind.Mpeg4, AKind.Flac, "mkv");
        Add("mkv_mpeg4_aac", "ISO MPEG-4 표준 + AAC (.MKV)", "MKV", VKind.Mpeg4, AKind.Aac, "mkv");
        Add("mkv_mpeg4_mp3", "ISO MPEG-4 표준 + MP3 (.MKV)", "MKV", VKind.Mpeg4, AKind.Mp3, "mkv");
        Add("mkv_nvenc264_aac", "엔비디아 NVENC H.264 + AAC (.MKV)", "MKV", VKind.Nvenc264, AKind.Aac, "mkv");
        Add("mkv_nvenc264_flac", "엔비디아 NVENC H.264 + FLAC (.MKV)", "MKV", VKind.Nvenc264, AKind.Flac, "mkv");
        Add("mkv_nvenchevc_aac", "엔비디아 NVENC HEVC + AAC (.MKV)", "MKV", VKind.NvencHevc, AKind.Aac, "mkv");
        Add("mkv_nvenchevc_flac", "엔비디아 NVENC HEVC + FLAC (.MKV)", "MKV", VKind.NvencHevc, AKind.Flac, "mkv");
        Add("mkv_qsv264_aac", "인텔 퀵 싱크 비디오 H.264 + AAC (.MKV)", "MKV", VKind.Qsv264, AKind.Aac, "mkv");
        Add("mkv_qsv264_flac", "인텔 퀵 싱크 비디오 H.264 + FLAC (.MKV)", "MKV", VKind.Qsv264, AKind.Flac, "mkv");
        Add("mkv_openh264_flac", "Open H.264 + FLAC (.MKV)", "MKV", VKind.OpenH264, AKind.Flac, "mkv");
        Add("mkv_x264_flac", "H.264 x264 + FLAC (.MKV)", "MKV", VKind.X264, AKind.Flac, "mkv", ocam: false);
        // M4V
        Add("m4v_mpeg4_aac", "ISO MPEG-4 표준 + AAC (.M4V)", "M4V", VKind.Mpeg4, AKind.Aac, "m4v");
        Add("m4v_nvenc264", "엔비디아 NVENC H.264 + AAC (.M4V)", "M4V", VKind.Nvenc264, AKind.Aac, "m4v");
        Add("m4v_qsv264", "인텔 퀵 싱크 비디오 H.264 + AAC (.M4V)", "M4V", VKind.Qsv264, AKind.Aac, "m4v");
        // MPEG-2 TS / PS
        Add("ts_mpeg2", "MPEG-2 전송 스트림 (.TS)", "TS / VOB", VKind.Mpeg2, AKind.Mp2, "ts");
        Add("vob_mpeg2", "MPEG-2 PS (.VOB)", "TS / VOB", VKind.Mpeg2, AKind.Mp2, "vob", "vob");
        // WMV / FLV
        Add("wmv_msmpeg4v3", "마이크로소프트 MPEG-4 파트 3 (.WMV)", "WMV / FLV", VKind.MsMpeg4v3, AKind.Wma, "wmv");
        Add("wmv_wmv2", "윈도우 미디어 비디오 8 (.WMV)", "WMV / FLV", VKind.Wmv2, AKind.Wma, "wmv");
        Add("flv_flv1", "플래시 비디오 (.FLV)", "WMV / FLV", VKind.Flv, AKind.Mp3, "flv");
        // WebM
        Add("webm_vp9", "VP9 + Opus (.WEBM)", "WebM", VKind.Vp9, AKind.Opus, "webm", ocam: false);
        // GIF
        Add("gif", "움짤 - GIF 애니메이션 (.GIF)", "GIF", VKind.Gif, AKind.None, "gif");
        // 소리만 녹음
        Add("a_mp3", "MPEG 오디오 레이어 3 (.MP3)", "소리", VKind.None, AKind.Mp3, "mp3");
        Add("a_wma", "윈도우 미디어 오디오 2 (.WMA)", "소리", VKind.None, AKind.Wma, "wma");
        Add("a_aac", "AAC (.M4A)", "소리", VKind.None, AKind.Aac, "m4a", ocam: false);
        Add("a_flac", "FLAC 무손실 (.FLAC)", "소리", VKind.None, AKind.Flac, "flac", ocam: false);
        Add("a_wav", "WAV 무압축 (.WAV)", "소리", VKind.None, AKind.Pcm, "wav", ocam: false);
        return l;
    }

    public static CodecProfile? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    // ───────────── 인코더 사용 가능 여부 탐지 ─────────────

    static readonly HashSet<string> Encoders = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> WorkingHw = new(StringComparer.OrdinalIgnoreCase);
    public static bool Probed { get; private set; }

    public static void Probe(string ffmpeg)
    {
        Encoders.Clear();
        WorkingHw.Clear();
        var (code, output) = Ffmpeg.RunCapture(ffmpeg, new[] { "-hide_banner", "-encoders" }, 15000);
        if (code == 0)
        {
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 6) Encoders.Add(parts[1]);
            }
        }
        // 하드웨어 인코더는 목록에 있어도 GPU/드라이버가 없으면 실패하므로 실제로 한 번 인코딩해 본다.
        var hw = VideoInfos.Where(i => i.Hardware && Encoders.Contains(i.Encoder)).ToList();
        Parallel.ForEach(hw, info =>
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=320x240:r=30",
                "-frames:v", "3" };
            args.AddRange(VideoArgs(info.Kind, 3, "mp4", 30));
            args.AddRange(new[] { "-f", "null", "-" });
            var (c, _) = Ffmpeg.RunCapture(ffmpeg, args, 20000);
            if (c == 0) lock (WorkingHw) WorkingHw.Add(info.Encoder);
        });
        Probed = true;
    }

    public static bool HasEncoder(string name) => Encoders.Contains(name);

    public static bool IsVideoAvailable(VKind k)
    {
        if (k == VKind.None) return true;
        if (!Probed) return !Video(k).Hardware;
        var info = Video(k);
        if (k == VKind.OpenH264) return true; // libopenh264 없으면 x264로 대체
        return info.Hardware ? WorkingHw.Contains(info.Encoder) : Encoders.Contains(info.Encoder);
    }

    // Opus는 libopus가 없으면 AudioArgs에서 Vorbis로 대체
    public static bool IsAudioAvailable(AKind a) =>
        a is AKind.None or AKind.Opus || !Probed || Encoders.Contains(AudioEncoder(a));
    public static bool IsAvailable(CodecProfile p) => IsVideoAvailable(p.Video) && IsAudioAvailable(p.Audio);

    // ───────────── 5단계 품질 → ffmpeg 인수 ─────────────

    /// <summary>단계별 값 표에서 (소수) 단계에 해당하는 값을 선형 보간 — 자동 모드의 보정 단계용.</summary>
    static int Pick(double level, params int[] v)
    {
        double l = Math.Clamp(level, 1, 5) - 1;
        int lo = (int)Math.Floor(l), hi = Math.Min(lo + 1, 4);
        return (int)Math.Round(v[lo] + (v[hi] - v[lo]) * (l - lo));
    }

    static readonly int[] StandardMpegRates = { 24, 25, 30, 50, 60 };

    public static List<string> VideoArgs(VKind k, double level, string ext, int fps)
    {
        var args = VideoArgsCore(k, level, ext, fps);
        // AVI에는 HEVC 표준 FourCC가 없어 태그를 명시하지 않으면 rawvideo로 인식됨
        if (ext == "avi" && k is VKind.NvencHevc or VKind.QsvHevc or VKind.AmfHevc or VKind.X265)
            args.AddRange(new[] { "-tag:v", "HEVC" });
        return args;
    }

    static List<string> VideoArgsCore(VKind k, double level, string ext, int fps)
    {
        var a = new List<string>();
        bool isoBmff = ext is "mp4" or "mov" or "m4v";
        int qscale = Pick(level, 13, 9, 6, 4, 2);
        switch (k)
        {
            case VKind.X264:
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", Pick(level, 32, 28, 24, 21, 18).ToString(), "-pix_fmt", "yuv420p" });
                break;
            case VKind.OpenH264:
                if (HasEncoder("libopenh264"))
                    a.AddRange(new[] { "-c:v", "libopenh264", "-b:v", Pick(level, 1000, 2000, 4000, 7000, 12000) + "k", "-pix_fmt", "yuv420p" });
                else
                    return VideoArgs(VKind.X264, level, ext, fps);
                break;
            case VKind.X265:
                a.AddRange(new[] { "-c:v", "libx265", "-preset", "ultrafast", "-crf", Pick(level, 34, 30, 27, 24, 21).ToString(),
                    "-pix_fmt", "yuv420p", "-x265-params", "log-level=error" });
                if (isoBmff) a.AddRange(new[] { "-tag:v", "hvc1" });
                break;
            case VKind.Nvenc264:
                a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", Pick(level, 33, 29, 25, 22, 19).ToString(),
                    "-b:v", "0", "-pix_fmt", "yuv420p" });
                break;
            case VKind.NvencHevc:
                a.AddRange(new[] { "-c:v", "hevc_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", Pick(level, 35, 31, 28, 25, 22).ToString(),
                    "-b:v", "0", "-pix_fmt", "yuv420p" });
                if (isoBmff) a.AddRange(new[] { "-tag:v", "hvc1" });
                break;
            case VKind.NvencAv1:
                a.AddRange(new[] { "-c:v", "av1_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", Pick(level, 40, 35, 31, 27, 24).ToString(),
                    "-b:v", "0", "-pix_fmt", "yuv420p" });
                break;
            case VKind.Qsv264:
                a.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", Pick(level, 33, 29, 25, 22, 19).ToString(), "-pix_fmt", "nv12" });
                break;
            case VKind.QsvHevc:
                a.AddRange(new[] { "-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", Pick(level, 35, 31, 28, 25, 22).ToString(), "-pix_fmt", "nv12" });
                if (isoBmff) a.AddRange(new[] { "-tag:v", "hvc1" });
                break;
            case VKind.Amf264:
            {
                var q = Pick(level, 33, 29, 25, 22, 19).ToString();
                a.AddRange(new[] { "-c:v", "h264_amf", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-quality", "balanced", "-pix_fmt", "yuv420p" });
                break;
            }
            case VKind.AmfHevc:
            {
                var q = Pick(level, 35, 31, 28, 25, 22).ToString();
                a.AddRange(new[] { "-c:v", "hevc_amf", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-quality", "balanced", "-pix_fmt", "yuv420p" });
                if (isoBmff) a.AddRange(new[] { "-tag:v", "hvc1" });
                break;
            }
            case VKind.Vp9:
                a.AddRange(new[] { "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1",
                    "-crf", Pick(level, 44, 39, 34, 30, 26).ToString(), "-b:v", "0", "-pix_fmt", "yuv420p" });
                break;
            case VKind.SvtAv1:
                a.AddRange(new[] { "-c:v", "libsvtav1", "-preset", "10", "-crf", Pick(level, 46, 40, 35, 31, 27).ToString(), "-pix_fmt", "yuv420p",
                    "-svtav1-params", "lp=0" });
                break;
            case VKind.Mpeg1:
            case VKind.Mpeg2:
            {
                // MPEG-1/2는 표준 프레임레이트만 허용
                int r = StandardMpegRates.FirstOrDefault(s => s >= fps, 60);
                a.AddRange(new[] { "-c:v", k == VKind.Mpeg1 ? "mpeg1video" : "mpeg2video", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p", "-r", r.ToString() });
                if (k == VKind.Mpeg1) a.AddRange(new[] { "-maxrate", "0" });
                break;
            }
            case VKind.Mpeg4:
                a.AddRange(new[] { "-c:v", "mpeg4", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p" });
                if (ext == "avi") a.AddRange(new[] { "-vtag", "FMP4" });
                break;
            case VKind.DivX:
                a.AddRange(new[] { "-c:v", "mpeg4", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p", "-vtag", "DX50" });
                break;
            case VKind.Xvid:
                a.AddRange(new[] { "-c:v", "libxvid", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p", "-vtag", "XVID" });
                break;
            case VKind.MsMpeg4v2:
                a.AddRange(new[] { "-c:v", "msmpeg4v2", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p" });
                break;
            case VKind.MsMpeg4v3:
                a.AddRange(new[] { "-c:v", "msmpeg4", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p" });
                break;
            case VKind.Wmv2:
                a.AddRange(new[] { "-c:v", "wmv2", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p" });
                break;
            case VKind.Flv:
                a.AddRange(new[] { "-c:v", "flv", "-q:v", qscale.ToString(), "-pix_fmt", "yuv420p" });
                break;
        }
        return a;
    }

    /// <param name="sampleRate">출력 샘플레이트 (0 = 원본). MP2/Opus는 48kHz 고정.</param>
    /// <param name="channels">1 = 모노, 2 = 스테레오</param>
    public static List<string> AudioArgs(AKind k, int level, int sampleRate = 48000, int channels = 2)
    {
        string br = Pick(level, 96, 128, 160, 192, 256) + "k";
        var a = new List<string>();
        switch (k)
        {
            case AKind.Aac: a.AddRange(new[] { "-c:a", "aac", "-b:a", br }); break;
            case AKind.Mp3: a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", br }); break;
            case AKind.Flac: a.AddRange(new[] { "-c:a", "flac" }); break;
            case AKind.Mp2: a.AddRange(new[] { "-c:a", "mp2", "-b:a", Pick(level, 128, 160, 192, 224, 256) + "k", "-ar", "48000" }); break;
            case AKind.Wma: a.AddRange(new[] { "-c:a", "wmav2", "-b:a", br }); break;
            case AKind.Pcm: a.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
            case AKind.Opus:
                if (!Probed || HasEncoder("libopus")) a.AddRange(new[] { "-c:a", "libopus", "-b:a", Pick(level, 64, 96, 128, 160, 192) + "k", "-ar", "48000" });
                else a.AddRange(new[] { "-c:a", "libvorbis", "-q:a", Pick(level, 2, 3, 5, 6, 8).ToString() });
                break;
        }
        if (k == AKind.None) return a;
        a.AddRange(new[] { "-ac", Math.Clamp(channels, 1, 2).ToString() });
        if (sampleRate > 0 && !a.Contains("-ar")) a.AddRange(new[] { "-ar", sampleRate.ToString() });
        return a;
    }

    // ───────────── 추천 (용량은 작고 화질은 좋은 것 기준) ─────────────

    /// <summary>같은 화질 기준 용량 배수 (x264 = 1.0). 효율 1점 ≈ 22% 차이.</summary>
    public static double SizeFactor(VideoEncoderInfo i) => i.Kind == VKind.Gif ? 6.0 : Math.Pow(1.22, 7 - i.Efficiency);

    static bool HeavyLoad(int w, int h, int fps) => (long)w * h * fps > 1920L * 1080 * 30;

    /// <summary>
    /// 정적 추천: 이 PC에서 실시간 녹화 가능한 인코더 중 용량 효율이 가장 높은 것.
    /// 고해상도·고프레임이면 CPU 부하가 큰 인코더(x265/AV1-SVT)는 제외.
    /// </summary>
    public static VideoEncoderInfo BestVideo(int w, int h, int fps)
    {
        bool heavy = HeavyLoad(w, h, fps);
        return VideoInfos
            .Where(i => i.Kind is not (VKind.Gif or VKind.OpenH264 or VKind.DivX) && IsVideoAvailable(i.Kind) && Recommender.RecordingCapable(i.Kind))
            .Where(i => i.Hardware || i.Speed >= (heavy ? 4 : 3))
            .OrderByDescending(i => i.Efficiency).ThenByDescending(i => i.Hardware).ThenByDescending(i => i.Compat)
            .First();
    }


    static bool HasBench(Settings s) => s.Bench.Any(b => b.Error == null && b.Ssim.All(v => v > 0));

    /// <summary>녹화 학습 결과 (이 해상도·프레임 대역에서 실시간 가능 + 목표 화질 + 최소 용량). 학습 꺼짐/데이터 없음이면 null.</summary>
    public static Learned? LearnedFor(Settings s, int w, int h, int fps)
    {
        if (!s.AutoLearn) return null;
        var rank = Learning.Ranking(Learning.RecClass(w, h, fps), s.Level, Recommender.RealtimeMargin).Where(x => Recommender.RecordingCapable(x.Kind)).ToList();
        var best = rank.FirstOrDefault();
        if (best == null || best.Sessions < 3) return null;
        // 녹화는 그래픽카드 인코더 우선 (CPU 인코더가 15% 이상 작을 때만 CPU)
        if (!Video(best.Kind).Hardware && rank.FirstOrDefault(x => Video(x.Kind).Hardware && x.Sessions >= 3) is { } hw
            && best.Ratio > hw.Ratio * (1 - Recommender.HardwarePreference))
            best = hw;
        return best;
    }

    /// <summary>특정 코덱의 학습된 품질값 (학습 1회 이상이면 사용).</summary>
    static Learned? LearnedForKind(Settings s, VKind kind, int w, int h, int fps) =>
        s.AutoLearn ? Learning.Ranking(Learning.RecClass(w, h, fps), s.Level, Recommender.RealtimeMargin).FirstOrDefault(x => x.Kind == kind) : null;

    /// <summary>
    /// 추천 우선순위: ① 내 PC 분석(실측) ② 하드웨어 기준. 단, 녹화 자가 학습이 3회 이상 쌓여
    /// 다른 코덱이 더 작다는 근거가 있으면 그 코덱으로 (학습은 보조).
    /// </summary>
    public static VideoEncoderInfo Recommended(Settings s, int w, int h, int fps)
    {
        if (LearnedFor(s, w, h, fps) is { } l) return Video(l.Kind);
        if (HasBench(s) && Recommender.Decide(s.Bench, s.Level) is { } best && IsVideoAvailable(best.Kind))
            return Video(best.Kind);
        return BestVideo(w, h, fps);
    }

    /// <summary>추천 근거 (화면 표시용).</summary>
    public static string RecommendBasis(Settings s, int w, int h, int fps) =>
        LearnedFor(s, w, h, fps) is { } l ? $"학습 {l.Sessions}회" : HasBench(s) ? (s.PcFingerprint != null ? "PC 분석" : "실측") : "사양 기준";

    /// <summary>
    /// 자동 모드에서 실제로 쓸 (소수) 품질 단계: 실측이 있으면 선택 단계의 기준 화질(x264)을 내도록 보정.
    /// 예) VP9가 3단계에서 기준보다 훨씬 고화질·대용량이면 더 낮은 단계로 내려 같은 화질·더 작은 용량으로 녹화.
    /// </summary>
    public static double AutoLevel(Settings s, VKind kind, int w, int h, int fps) =>
        Recommender.GuardLevel(LearnedForKind(s, kind, w, h, fps) is { } l ? l.Level : AutoLevelFor(s, kind, s.Level), s.Level);

    /// <summary>임의 단계에 대한 보정 단계 (변환 기능에서 사용).</summary>
    public static double AutoLevelFor(Settings s, VKind kind, int level)
    {
        if (!HasBench(s)) return level;
        Recommender.Decide(s.Bench, level);
        var b = s.Bench.FirstOrDefault(r => r.Kind == kind && r.Error == null && r.MBAtTarget > 0);
        return Recommender.GuardLevel(b?.CalibratedLevel ?? level, level);
    }

    /// <summary>자동 모드가 실제로 사용할 프로필 (추천 인코더 + AAC, MP4).</summary>
    public static CodecProfile AutoProfile(Settings s, int w, int h, int fps)
    {
        var v = Recommended(s, w, h, fps);
        return new CodecProfile
        {
            Id = AutoId,
            Name = $"자동 → {v.Label}",
            Group = "자동",
            Video = v.Kind,
            Audio = v.Kind == VKind.Vp9 ? AKind.Opus : AKind.Aac,
            Ext = v.Kind == VKind.Vp9 ? "webm" : "mp4",
            FromOcam = false,
        };
    }

    static readonly double[] BitsPerPixel = { 0.012, 0.022, 0.04, 0.065, 0.10 };

    /// <summary>분당 예상 용량(MB). 실측값이 있으면 그것을 현재 해상도·프레임으로 환산.</summary>
    public static double EstimateMBPerMin(Settings s, VideoEncoderInfo v, double level, int w, int h, int fps)
    {
        var b = s.Bench.FirstOrDefault(r => r.Kind == v.Kind && r.Error == null && r.Mb.All(m => m > 0));
        if (b != null && s.BenchPixelRate > 0)
        {
            double l = Math.Clamp(level, 1, 5) - 1;
            int lo = (int)Math.Floor(l), hi = Math.Min(lo + 1, 4);
            double mb = Math.Exp(Math.Log(b.Mb[lo]) + (l - lo) * (Math.Log(b.Mb[hi]) - Math.Log(b.Mb[lo])));
            return mb * ((double)w * h * fps / s.BenchPixelRate);
        }
        double bps = BitsPerPixel[(int)Math.Round(Math.Clamp(level, 1, 5)) - 1] * w * h * fps * SizeFactor(v);
        return bps * 60 / 8 / 1_000_000;
    }

    public static string Stars(int n, int max = 5) => new string('★', Math.Clamp(n, 0, max)) + new string('☆', max - Math.Clamp(n, 0, max));

    /// <summary>현재 선택된 코덱에 대한 한 줄 추천 문구.</summary>
    public static string Hint(Settings s, CodecProfile p, int w, int h, int fps)
    {
        if (p.AudioOnly) return $"소리만 녹음 · {AudioLabel(p.Audio)}";
        var cur = p.VideoInfo!;
        if (cur.Kind == VKind.Gif) return "GIF는 용량이 매우 큼 · 짧은 움짤용 (MP4 권장)";
        var best = Recommended(s, w, h, fps);
        double level = p.Id == AutoId ? AutoLevel(s, cur.Kind, w, h, fps) : s.Level;
        double est = EstimateMBPerMin(s, cur, level, w, h, fps);
        string head = $"예상 약 {est:0.#}MB/분 · 용량효율 {Stars((cur.Efficiency + 1) / 2)}";
        if (best.Kind != cur.Kind)
        {
            double pct = (1 - SizeFactor(best) / SizeFactor(cur)) * 100;
            var lb = LearnedFor(s, w, h, fps);
            var lc = s.AutoLearn ? Learning.Ranking(Learning.RecClass(w, h, fps), s.Level).FirstOrDefault(x => x.Kind == cur.Kind) : null;
            if (lb != null && lc != null && lc.Ratio > 0) pct = (1 - lb.Ratio / lc.Ratio) * 100;
            else if (HasBench(s))
            {
                Recommender.Decide(s.Bench, s.Level);
                var bb = s.Bench.FirstOrDefault(b => b.Kind == best.Kind && b.MBAtTarget > 0);
                var bc = s.Bench.FirstOrDefault(b => b.Kind == cur.Kind && b.MBAtTarget > 0);
                if (bb != null && bc != null) pct = (1 - bb.MBAtTarget / bc.MBAtTarget) * 100;
            }
            if (pct > 3) return $"{head} · 💡 {best.Label}(으)로 바꾸면 같은 화질에 약 {pct:0}% 작아짐";
        }
        return $"{head} · ★ 이 PC 추천 코덱 ({RecommendBasis(s, w, h, fps)})";
    }
}