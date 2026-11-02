using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MxCam;

/// <summary>
/// ffmpeg가 없거나 MxCam이 쓸 수 없는(너무 오래된) 버전일 때 최신 빌드를 내려받아 설치한다.
///  · 출처: gyan.dev "release essentials" 빌드 (Windows x64, NVENC·QSV·AMF·x264·x265·VP9·Xvid 포함)
///  · 같은 위치의 .sha256 파일로 무결성 검증 후 zip 안의 bin/ffmpeg.exe, bin/ffprobe.exe만 꺼낸다
///  · 설치 위치: %LOCALAPPDATA%\MxCam\ffmpeg (관리자 권한 불필요, Ffmpeg.Resolve가 PATH보다 먼저 찾음)
/// </summary>
public static class FfmpegInstaller
{
    const string ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    const string ShaUrl = ZipUrl + ".sha256";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxCam", "ffmpeg");
    public static string InstalledExe => Path.Combine(InstallDir, "ffmpeg.exe");

    /// <summary>내려받기 → SHA256 검증 → 압축 해제. 성공하면 ffmpeg.exe 경로, 실패하면 예외.</summary>
    public static async Task<string> InstallAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MxCam", AppInfo.Version.ToString()));

        string tmpDir = Path.Combine(Path.GetTempPath(), "mxcam_ffmpeg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            progress.Report("ffmpeg 확인 중…");
            string expected = (await http.GetStringAsync(ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            if (expected.Length != 64) throw new InvalidDataException("ffmpeg 체크섬 파일 형식이 올바르지 않습니다.");

            string zip = Path.Combine(tmpDir, "ffmpeg.zip");
            using (var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[1 << 16];
                long done = 0;
                int lastPct = -1, n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progress.Report(pct >= 0 ? $"ffmpeg 내려받는 중… {pct}% ({done / 1048576} / {total / 1048576} MB)" : $"ffmpeg 내려받는 중… {done / 1048576} MB");
                    }
                }
            }

            progress.Report("ffmpeg 검증 중…");
            string actual;
            await using (var fs = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != expected) throw new InvalidDataException("내려받은 ffmpeg의 SHA256이 일치하지 않습니다.");

            progress.Report("ffmpeg 설치 중…");
            string stage = Path.Combine(tmpDir, "bin");
            Directory.CreateDirectory(stage);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"압축 파일에 {name}이 없습니다.");
                    entry.ExtractToFile(Path.Combine(stage, name), true);
                }
            }
            if (!Ffmpeg.IsUsable(Path.Combine(stage, "ffmpeg.exe")))
                throw new InvalidOperationException("내려받은 ffmpeg가 실행되지 않습니다.");

            Directory.CreateDirectory(InstallDir);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                File.Move(Path.Combine(stage, name), Path.Combine(InstallDir, name), true);
            Ffmpeg.ForgetUsable(InstalledExe);
            if (!Ffmpeg.IsUsable(InstalledExe)) throw new InvalidOperationException("설치한 ffmpeg가 실행되지 않습니다.");
            return InstalledExe;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
