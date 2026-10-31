namespace MxCam;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run(args);
        if (args.Length > 0 && args[0] == "--uitest")
            return RunUiTest(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "uitest.log"));

        if (args.Length > 0 && args[0] == "--updatecheck")
            return UpdateCheck(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "update.log"));

        // 제거 프로그램에서 호출: 탐색기 우클릭 메뉴 삭제
        if (args.Length > 0 && args[0] == "--unregister")
        {
            ShellIntegration.Unregister();
            return 0;
        }

        // 탐색기 우클릭 메뉴에서 호출 (녹화 창과 별개로 여러 개 동시 실행 가능)
        if (args.Length > 1 && args[0] == "--optimize")
        {
            Application.Run(new OptimizeForm(Settings.Load(), args[1]));
            return 0;
        }
        if (args.Length > 1 && args[0] == "--convert")
        {
            var s = Settings.Load();
            var f = Ffmpeg.Resolve(s.FfmpegPath);
            if (f == null) { MessageBox.Show("ffmpeg.exe를 찾을 수 없습니다.", "MxCam"); return 1; }
            Codecs.Probe(f);
            var form = new ConvertForm(s, f);
            form.Shown += async (_, _) => await form.LoadFileAsync(args[1]);
            form.FormClosed += (_, _) => s.Save();
            Application.Run(form);
            return 0;
        }

        // 자동 업데이트: 내려받아 둔 새 버전이 있으면 교체 후 재시작
        Updater.CleanupOld();
        if (Updater.ApplyPendingAndRestart(args)) return 0;

        using var mutex = new Mutex(false, "MxCam_single_instance");
        // 업데이트 직후에는 이전 프로세스가 끝날 때까지 잠시 기다린다
        if (!mutex.WaitOne(args.Contains("--after-update") ? 10000 : 0))
        {
            MessageBox.Show("MxCam이 이미 실행 중입니다.", "MxCam");
            return 0;
        }
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>--updatecheck: UI 없이 업데이트 확인·다운로드만 수행 (검증용).</summary>
    static int UpdateCheck(string log)
    {
        using var w = new StreamWriter(log);
        w.WriteLine($"current {AppInfo.VersionText}");
        var u = Updater.CheckAsync().GetAwaiter().GetResult();
        if (u == null) { w.WriteLine("no update"); return 0; }
        w.WriteLine($"latest {u.Tag} installed={Updater.IsInstalled} setup={u.SetupUrl} exe={u.ExeUrl}");
        bool ok = Updater.DownloadAsync(u).GetAwaiter().GetResult();
        w.WriteLine(ok ? "downloaded+verified" : "download failed");
        return ok ? 0 : 1;
    }

    static int RunUiTest(string log)
    {
        // 사용자 설정을 건드리지 않도록 임시 설정 폴더 사용
        string dir = Path.Combine(Path.GetTempPath(), "mxcam_uitest_settings");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Environment.SetEnvironmentVariable("MXCAM_SETTINGS_DIR", dir);
        int result = 1;
        var form = new MainForm { TestMode = true };
        form.Shown += async (_, _) =>
        {
            result = await form.RunUiTestAsync(log);
            if (!form.IsDisposed) form.Close();
        };
        Application.Run(form);
        return result;
    }
}
