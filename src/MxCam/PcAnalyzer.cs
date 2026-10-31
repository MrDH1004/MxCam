using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MxCam;

/// <summary>이 PC의 사양 (코덱 추천의 근거).</summary>
public sealed class PcSpec
{
    public string Cpu { get; init; } = "";
    public int Threads { get; init; }
    public double RamGB { get; init; }
    public List<string> Gpus { get; init; } = new();
    public string Display { get; init; } = "";
    public string FfmpegPath { get; init; } = "";

    /// <summary>사양 지문: CPU·GPU·ffmpeg가 바뀌면 달라짐 → 자동 재분석.</summary>
    public string Fingerprint
    {
        get
        {
            var raw = $"{Cpu}|{Threads}|{string.Join(",", Gpus)}|{FfmpegPath}|{(File.Exists(FfmpegPath) ? File.GetLastWriteTimeUtc(FfmpegPath).Ticks : 0)}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        }
    }

    public string GpuText => Gpus.Count == 0 ? "(알 수 없음)" : string.Join(" + ", Gpus);
}

public static class PcAnalyzer
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static PcSpec Collect(string? ffmpeg)
    {
        string cpu = "";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch { }

        double ram = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) ram = ms.ullTotalPhys / 1073741824.0;

        var gpus = new List<string>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            var name = dd.DeviceString?.Trim();
            if (!string.IsNullOrEmpty(name) && !gpus.Contains(name) && !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                gpus.Add(name);
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }

        string display = "";
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, -1, ref dm)) // ENUM_CURRENT_SETTINGS
            display = $"{dm.dmPelsWidth}×{dm.dmPelsHeight} {dm.dmDisplayFrequency}Hz" + (Screen.AllScreens.Length > 1 ? $" (모니터 {Screen.AllScreens.Length}대)" : "");

        return new PcSpec
        {
            Cpu = cpu, Threads = Environment.ProcessorCount, RamGB = Math.Round(ram, 1), Gpus = gpus, Display = display, FfmpegPath = ffmpeg ?? "",
        };
    }

    /// <summary>
    /// 실측할 후보: 이 PC에서 쓸 수 있는 하드웨어 인코더 전부 + x264(기준) + CPU가 충분하면 x265·SVT-AV1.
    /// (VP9·MPEG-4 등 비효율/느린 후보는 PC 분석에서 제외해 30초 안팎으로 끝냄)
    /// </summary>
    public static VKind[] Candidates(PcSpec pc)
    {
        var list = new List<VKind>();
        foreach (var k in new[] { VKind.NvencHevc, VKind.NvencAv1, VKind.Nvenc264, VKind.QsvHevc, VKind.Qsv264, VKind.AmfHevc, VKind.Amf264 })
            if (Codecs.IsVideoAvailable(k)) list.Add(k);
        list.Add(VKind.X264);
        if (pc.Threads >= 12)
        {
            if (Codecs.IsVideoAvailable(VKind.X265)) list.Add(VKind.X265);
            if (Codecs.IsVideoAvailable(VKind.SvtAv1)) list.Add(VKind.SvtAv1);
        }
        return list.ToArray();
    }

    /// <summary>분석 결과 설명 문장.</summary>
    public static string Explain(PcSpec pc, VideoEncoderInfo best, List<BenchResult> bench, int level)
    {
        var sb = new StringBuilder();
        var vendor = best.Kind switch
        {
            VKind.NvencHevc or VKind.Nvenc264 or VKind.NvencAv1 => pc.Gpus.FirstOrDefault(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)),
            VKind.QsvHevc or VKind.Qsv264 => pc.Gpus.FirstOrDefault(g => g.Contains("Intel", StringComparison.OrdinalIgnoreCase)),
            VKind.AmfHevc or VKind.Amf264 => pc.Gpus.FirstOrDefault(g => g.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        sb.Append(best.Hardware
            ? $"{vendor ?? "그래픽카드"}의 하드웨어 인코더를 써서 CPU 부담 없이 녹화합니다."
            : $"{pc.Cpu} ({pc.Threads}스레드)로 실시간 녹화가 가능한 CPU 인코더입니다.");
        var b = bench.FirstOrDefault(r => r.Kind == best.Kind && r.MBAtTarget > 0);
        var x = bench.FirstOrDefault(r => r.Kind == VKind.X264 && r.MBAtTarget > 0);
        if (b != null && x != null && best.Kind != VKind.X264)
        {
            if (b.MBAtTarget < x.MBAtTarget)
                sb.Append($" 같은 화질에서 일반 H.264(x264)보다 약 {(1 - b.MBAtTarget / x.MBAtTarget) * 100:0}% 작습니다.");
            else if (best.Hardware)
                sb.Append($" CPU 인코더(x264)와 용량은 비슷하지만(차이 {(b.MBAtTarget / x.MBAtTarget - 1) * 100:0}%) CPU를 쓰지 않아 게임·작업에 영향이 적습니다.");
        }
        if (b != null) sb.Append($" 실측 속도 x{b.Speed:0.0} (실시간 대비).");
        return sb.ToString();
    }
}
