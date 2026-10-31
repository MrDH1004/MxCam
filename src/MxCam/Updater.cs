using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MxCam;

public static class AppInfo
{
    public const string Repo = "MrDH1004/MxCam";

    /// <summary>빌드 시 -p:Version=x.y.z 로 들어간 버전 (GitHub 태그 vX.Y.Z와 같음).</summary>
    public static Version Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string VersionText => $"v{Version}";
}

public sealed record UpdateInfo(Version Version, string Tag, string? ExeUrl, string? ExeShaUrl, string? SetupUrl, string? SetupShaUrl, string Notes);

/// <summary>
/// GitHub Releases 기반 자동 업데이트.
///  · 설치판(Inno Setup, unins000.exe 있음): 새 MxCam-Setup-x.y.z.exe를 받아 SHA256 검증 → 무인 설치 후 자동 재시작
///    (제어판 "앱 및 기능"의 버전 표기도 함께 갱신)
///  · 포터블(exe 하나): MxCam.exe.new로 받아 두고 다음 실행 때 실행 파일 교체 후 재시작
/// </summary>
public static class Updater
{
    static string ExePath => Environment.ProcessPath!;
    static string ExeDir => Path.GetDirectoryName(ExePath)!;
    static string NewPath => ExePath + ".new";
    static string OldPath => ExePath + ".old";
    static string PendingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxCam", "update");
    static string PendingSetup => Path.Combine(PendingDir, "MxCam-Setup.exe");
    static string PendingVersionFile => Path.Combine(PendingDir, "version.txt");

    public static bool IsInstalled => File.Exists(Path.Combine(ExeDir, "unins000.exe"));

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MxCam", AppInfo.Version.ToString()));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    static Version? PendingSetupVersion()
    {
        try
        {
            return File.Exists(PendingSetup) && File.Exists(PendingVersionFile) && Version.TryParse(File.ReadAllText(PendingVersionFile).Trim(), out var v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>이전 업데이트에서 남은 파일 정리 (옛 실행 파일, 이미 적용된 설치 파일).</summary>
    public static void CleanupOld()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
        try
        {
            if (Directory.Exists(PendingDir) && (PendingSetupVersion() is not { } v || v <= AppInfo.Version))
                Directory.Delete(PendingDir, true);
        }
        catch { }
    }

    public static bool HasPending => File.Exists(NewPath) || (IsInstalled && PendingSetupVersion() is { } v && v > AppInfo.Version);

    /// <summary>내려받아 둔 새 버전이 있으면 적용하고 재시작. 적용을 시작했으면 true (호출자는 즉시 종료).</summary>
    public static bool ApplyPendingAndRestart(string[] args)
    {
        if (IsInstalled && PendingSetupVersion() is { } sv && sv > AppInfo.Version)
        {
            try
            {
                var psi = new ProcessStartInfo(PendingSetup) { UseShellExecute = false };
                foreach (var a in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/relaunch=1" }) psi.ArgumentList.Add(a);
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }
        if (!File.Exists(NewPath)) return false;
        try
        {
            File.Move(ExePath, OldPath, true);
            File.Move(NewPath, ExePath, true);
        }
        catch
        {
            // 권한 문제 등 → 원래대로 두고 계속 실행
            try { if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath); } catch { }
            return false;
        }
        var p = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        p.ArgumentList.Add("--after-update");
        foreach (var a in args.Where(a => a != "--after-update")) p.ArgumentList.Add(a);
        Process.Start(p);
        return true;
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = Http();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        if (v <= AppInfo.Version) return null;
        string? exe = null, exeSha = null, setup = null, setupSha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.Equals("MxCam.exe", StringComparison.OrdinalIgnoreCase)) exe = url;
            else if (name.Equals("MxCam.exe.sha256", StringComparison.OrdinalIgnoreCase)) exeSha = url;
            else if (name.StartsWith("MxCam-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase)) setupSha = url;
            else if (name.StartsWith("MxCam-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup = url;
        }
        if (exe == null && setup == null) return null;
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(v, tag, exe, exeSha, setup, setupSha, notes);
    }

    /// <summary>설치판이면 설치 파일, 포터블이면 실행 파일을 내려받는다. SHA256이 맞지 않으면 버린다.</summary>
    public static async Task<bool> DownloadAsync(UpdateInfo u, CancellationToken ct = default)
    {
        if (IsInstalled && u.SetupUrl != null)
        {
            Directory.CreateDirectory(PendingDir);
            if (!await DownloadVerified(u.SetupUrl, u.SetupShaUrl, PendingSetup, ct)) return false;
            await File.WriteAllTextAsync(PendingVersionFile, u.Version.ToString(), ct);
            return true;
        }
        return u.ExeUrl != null && await DownloadVerified(u.ExeUrl, u.ExeShaUrl, NewPath, ct);
    }

    static async Task<bool> DownloadVerified(string url, string? shaUrl, string dest, CancellationToken ct)
    {
        using var http = Http();
        string tmp = dest + ".part";
        try
        {
            await using (var fs = File.Create(tmp))
            await using (var s = await http.GetStreamAsync(url, ct))
                await s.CopyToAsync(fs, ct);
            if (shaUrl != null)
            {
                string expected = (await http.GetStringAsync(shaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                string actual;
                await using (var fs = File.OpenRead(tmp))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != expected) { File.Delete(tmp); return false; }
            }
            File.Move(tmp, dest, true);
            return true;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }
}
