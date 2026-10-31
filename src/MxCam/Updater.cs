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

public sealed record UpdateInfo(Version Version, string Tag, string ExeUrl, string? ShaUrl, string Notes);

/// <summary>
/// GitHub Releases 기반 자동 업데이트.
///  1) 시작 시 최신 릴리스 확인 → 새 버전이면 MxCam.exe를 "MxCam.exe.new"로 내려받고 SHA256 검증
///  2) 다음 실행 때(또는 사용자가 "지금 재시작" 클릭 시) 실행 파일 교체 후 새 버전으로 재시작
///     (실행 중인 exe는 덮어쓸 수 없지만 이름 변경은 가능 → .old로 옮기고 .new를 제자리에)
/// </summary>
public static class Updater
{
    static string ExePath => Environment.ProcessPath!;
    static string NewPath => ExePath + ".new";
    static string OldPath => ExePath + ".old";

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MxCam", AppInfo.Version.ToString()));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    /// <summary>이전 업데이트에서 남은 옛 실행 파일 정리.</summary>
    public static void CleanupOld()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
    }

    public static bool HasPending => File.Exists(NewPath);

    /// <summary>내려받아 둔 새 버전이 있으면 교체하고 재시작. 교체했으면 true (호출자는 즉시 종료).</summary>
    public static bool ApplyPendingAndRestart(string[] args)
    {
        if (!HasPending) return false;
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
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--after-update");
        foreach (var a in args.Where(a => a != "--after-update")) psi.ArgumentList.Add(a);
        Process.Start(psi);
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
        string? exe = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.Equals("MxCam.exe", StringComparison.OrdinalIgnoreCase)) exe = url;
            if (name.Equals("MxCam.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (exe == null) return null;
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(v, tag, exe, sha, notes);
    }

    /// <summary>새 버전을 MxCam.exe.new로 내려받는다. SHA256이 맞지 않으면 버린다.</summary>
    public static async Task<bool> DownloadAsync(UpdateInfo u, CancellationToken ct = default)
    {
        using var http = Http();
        string tmp = NewPath + ".part";
        try
        {
            await using (var fs = File.Create(tmp))
            await using (var s = await http.GetStreamAsync(u.ExeUrl, ct))
                await s.CopyToAsync(fs, ct);
            if (u.ShaUrl != null)
            {
                string expected = (await http.GetStringAsync(u.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                string actual;
                await using (var fs = File.OpenRead(tmp))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != expected) { File.Delete(tmp); return false; }
            }
            File.Move(tmp, NewPath, true);
            return true;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }
}
