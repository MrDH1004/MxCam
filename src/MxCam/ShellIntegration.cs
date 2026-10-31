using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MxCam;

/// <summary>
/// 탐색기 우클릭 메뉴 (현재 사용자만, 관리자 권한 불필요).
/// 비디오 파일(PerceivedType=video)에만 표시: "MxCam: 용량 최적화 변환", "MxCam: 변환 옵션…"
/// Windows 11에서는 "추가 옵션 표시"(Shift+F10) 메뉴 안에 나타난다.
/// </summary>
public static class ShellIntegration
{
    const string OptimizeVerb = "MxCam.Optimize";
    const string ConvertVerb = "MxCam.Convert";

    static readonly string[] VideoExts =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".vob", ".3gp",
    };

    /// <summary>등록할 위치: video 지각 형식 전체 + video로 분류되지 않은 확장자 개별 등록 (중복 표시 방지).</summary>
    static IEnumerable<string> Targets()
    {
        yield return @"Software\Classes\SystemFileAssociations\video\shell";
        foreach (var ext in VideoExts)
        {
            string? perceived = null;
            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey(ext);
                perceived = k?.GetValue("PerceivedType") as string;
            }
            catch { }
            if (!string.Equals(perceived, "video", StringComparison.OrdinalIgnoreCase))
                yield return $@"Software\Classes\SystemFileAssociations\{ext}\shell";
        }
    }

    public static void Register(string exe)
    {
        foreach (var t in Targets())
        {
            Write($@"{t}\{OptimizeVerb}", "MxCam: 용량 최적화 변환", exe, $"\"{exe}\" --optimize \"%1\"");
            Write($@"{t}\{ConvertVerb}", "MxCam: 변환 옵션…", exe, $"\"{exe}\" --convert \"%1\"");
        }
        Notify();
    }

    static void Write(string keyPath, string title, string exe, string command)
    {
        using var k = Registry.CurrentUser.CreateSubKey(keyPath);
        k.SetValue("", title);
        k.SetValue("Icon", $"\"{exe}\",0");
        using var c = k.CreateSubKey("command");
        c.SetValue("", command);
    }

    public static void Unregister()
    {
        // 등록 당시 대상이 달라졌을 수 있으므로 모든 확장자 위치를 지운다
        var all = new List<string> { @"Software\Classes\SystemFileAssociations\video\shell" };
        all.AddRange(VideoExts.Select(e => $@"Software\Classes\SystemFileAssociations\{e}\shell"));
        foreach (var t in all)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{t}\{OptimizeVerb}", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{t}\{ConvertVerb}", false); } catch { }
        }
        Notify();
    }

    /// <summary>현재 등록된 명령 (없으면 null) — 테스트/확인용.</summary>
    public static string? RegisteredCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\SystemFileAssociations\video\shell\{OptimizeVerb}\command");
        return k?.GetValue("") as string;
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
}
