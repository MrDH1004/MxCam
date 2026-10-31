using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MxCam;

/// <summary>
/// MxCam.exe --uitest [로그] : 실제 메인 창을 띄우고 툴바 버튼·메뉴·단축키·옵션·효과를 하나씩 실행해 검증.
/// (별도 설정 폴더를 쓰므로 사용자 설정은 바뀌지 않음)
/// </summary>
public sealed partial class MainForm
{
    StreamWriter _tlog = null!;
    int _tfail, _tpass;

    void T(string name, bool ok, string detail = "")
    {
        if (ok) _tpass++; else _tfail++;
        _tlog.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        _tlog.Flush();
    }

    static async Task<bool> Until(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }

    static IEnumerable<ToolStripMenuItem> AllItems(ToolStripItemCollection items)
    {
        foreach (var i in items.OfType<ToolStripMenuItem>())
        {
            yield return i;
            foreach (var c in AllItems(i.DropDownItems)) yield return c;
        }
    }

    /// <summary>드롭다운을 열어(메뉴 생성) 글자로 시작하는 항목을 클릭.</summary>
    async Task<bool> ClickMenu(ToolStripDropDownButton b, string startsWith)
    {
        b.ShowDropDown();
        await Task.Delay(150);
        var item = AllItems(b.DropDownItems).FirstOrDefault(i => i.Text != null && i.Text.StartsWith(startsWith));
        bool ok = item != null && item.Enabled;
        item?.PerformClick();
        b.HideDropDown();
        await Task.Delay(100);
        return ok;
    }

    (string info, double dur) Probe(string file)
    {
        var (_, info) = Ffmpeg.RunCapture(Ffmpeg.ProbePath(_ffmpeg!), new[]
            { "-v", "error", "-show_entries", "stream=codec_type,codec_name,sample_rate,channels,r_frame_rate,width,height", "-of", "compact", file }, 30000);
        return (info.Replace("\r", "").Replace("\n", " | "), Recommender.ProbeDuration(_ffmpeg!, file));
    }

    async Task<string?> RecordFor(int ms, Action? during = null)
    {
        _btnRecord.PerformClick();
        if (!await Until(() => _rec != null, 3000)) return null;
        await Task.Delay(ms);
        during?.Invoke();
        _btnRecord.PerformClick();
        await Until(() => _rec == null && !_stopping, 60000);
        return _lastSaved;
    }

    static void PressKey(Keys k)
    {
        Native.keybd_event((byte)k, 0, 0, IntPtr.Zero);
        Native.keybd_event((byte)k, 0, 2, IntPtr.Zero);
    }

    internal async Task<int> RunUiTestAsync(string logPath)
    {
        _tlog = new StreamWriter(logPath, false, new UTF8Encoding(false));
        string outDir = Path.Combine(Path.GetTempPath(), "mxcam_uitest");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        _s.OutputDir = outDir;
        _s.CodecId = Codecs.AutoId;
        _s.Mode = RecordMode.Screen;
        _s.SystemAudio = true;
        _s.Microphone = false;
        // oCam 등 다른 녹화 프로그램이 F2/F3/F11을 잡고 있어도 테스트가 되도록 별도 키 사용
        _s.RecordKey = Keys.F6; _s.PauseKey = Keys.F7; _s.CaptureKey = Keys.F8;
        RegisterHotkeys();
        try
        {
            await RunUiTestCore(outDir);
        }
        catch (Exception ex)
        {
            T("예외 없이 완료", false, ex.ToString());
        }
        _testDone = true;
        _tlog.WriteLine($"\nRESULT: {_tpass} PASS, {_tfail} FAIL");
        _tlog.Dispose();
        return _tfail;
    }

    async Task RunUiTestCore(string outDir)
    {
        T("ffmpeg 찾기", _ffmpeg != null, _ffmpeg ?? "");
        T("코덱 탐지 완료", await Until(() => Codecs.Probed, 30000));
        var screen = Screen.PrimaryScreen!.Bounds;

        // ── 1. 툴바 ──
        var buttons = _bar.Items.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator).Select(i => i.Text).ToList();
        T("툴바 버튼 11개", buttons.Count == 11, string.Join(",", buttons));
        T("앱 아이콘 적용", Icon != null && Icon.Width > 0);

        // ── 2. 메뉴 생성 ──
        foreach (var dd in new[] { _btnSize, _btnCodec, _btnSound, _btnMode })
        {
            dd.ShowDropDown();
            await Task.Delay(150);
            int n = dd.DropDownItems.Count;
            dd.HideDropDown();
            T($"메뉴 열기: {dd.Text}", n >= 3, $"{n}개 항목");
        }
        _btnCodec.ShowDropDown();
        await Task.Delay(150);
        var codecItems = AllItems(_btnCodec.DropDownItems).ToList();
        _btnCodec.HideDropDown();
        int ocamCount = Codecs.All.Count(p => p.FromOcam);
        T("코덱 메뉴에 oCam 코덱 전체 표시", Codecs.All.All(p => codecItems.Any(i => i.Text!.StartsWith(p.Name))), $"oCam {ocamCount}종 + 확장 {Codecs.All.Count - ocamCount}종");
        T("코덱 메뉴 ★추천 표시", codecItems.Any(i => i.Text!.Contains("★추천")));
        var unavailable = codecItems.Where(i => i.Text!.Contains("미지원")).ToList();
        T("미지원 코덱 비활성화", unavailable.All(i => !i.Enabled), $"{unavailable.Count}개");

        // ── 3. 크기 조정 ──
        _frame.CaptureRect = new Rectangle(screen.X + 200, screen.Y + 200, 1280, 720);
        T("크기: 640 × 360", await ClickMenu(_btnSize, "640 × 360") && _frame.CaptureRect.Size == new Size(640, 360), _frame.CaptureRect.ToString());
        T("크기: 1280 × 720", await ClickMenu(_btnSize, "1280 × 720") && _frame.CaptureRect.Size == new Size(1280, 720), _frame.CaptureRect.ToString());
        T("크기: 전체 화면(모니터 1)", await ClickMenu(_btnSize, "모니터 1") && _frame.CaptureRect == Screen.AllScreens[0].Bounds, _frame.CaptureRect.ToString());
        using (var sd = new SizeDialog(800, 600)) T("사용자 지정 크기 대화상자", sd.W == 800 && sd.H == 600);
        var toolbarPos = Location;
        SetRegion(new Rectangle(screen.X + 300, screen.Y + 250, 640, 360));
        T("영역을 옮겨도 툴바는 그대로", Location == toolbarPos, $"{toolbarPos} → {Location}");
        T("영역 설정 저장", _s.Region == _frame.CaptureRect);

        // ── 4. 녹화 영역 테두리 (크기 조절/이동 판정) ──
        var b = _frame.Bounds;
        int Hit(int x, int y) => (int)SendHit(_frame.Handle, x, y);
        T("테두리: 왼쪽 위 모서리 = 크기 조절", Hit(b.Left + 2, b.Top + 2) == 13);
        T("테두리: 오른쪽 아래 모서리 = 크기 조절", Hit(b.Right - 3, b.Bottom - 3) == 17);
        T("테두리: 오른쪽 변 = 크기 조절", Hit(b.Right - 2, b.Top + b.Height / 2 + 30) == 11);
        T("테두리: 가운데 크기 표시 = 이동", Hit(b.Left + b.Width / 2, b.Top + b.Height / 2) == 2);

        // ── 5. 코덱 / 품질 / 소리 / 모드 메뉴 ──
        T("코덱 선택: H.264 x264 MP4", await ClickMenu(_btnCodec, "H.264 x264 + AAC (.MP4)") && _s.CodecId == "mp4_x264");
        T("선택 코덱에 추천 문구", _hint.Text.Contains("💡") || _hint.Text.Contains("★"), _hint.Text);
        T("코덱 선택: 자동", await ClickMenu(_btnCodec, "자동 (추천)") && _s.CodecId == Codecs.AutoId);
        T("품질 5단계 선택", await ClickMenu(_btnCodec, "5단계") && _s.Level == 5);
        T("품질 1단계 선택", await ClickMenu(_btnCodec, "1단계") && _s.Level == 1);
        await ClickMenu(_btnCodec, "3단계");
        T("품질 3단계 복귀", _s.Level == 3);
        bool mic0 = _s.Microphone;
        T("소리: 마이크 켜기/끄기", await ClickMenu(_btnSound, "마이크 녹음") && _s.Microphone != mic0);
        await ClickMenu(_btnSound, "마이크 녹음");
        T("소리: 녹음 안 함", await ClickMenu(_btnSound, "소리 녹음 안 함") && !_s.SystemAudio && !_s.Microphone);
        T("소리: 시스템 소리 켜기", await ClickMenu(_btnSound, "시스템 소리") && _s.SystemAudio);

        // ── 6. 녹화 / 일시정지 / 캡처 (버튼) ──
        await ClickMenu(_btnCodec, "H.264 x264 + AAC (.MP4)");
        _btnRecord.PerformClick();
        T("녹화 버튼 → 녹화 시작", await Until(() => _rec != null, 3000) && _frame.Recording && _btnRecord.Text == "중지");
        T("녹화 중 메뉴 잠금", !_btnSize.Enabled && !_btnCodec.Enabled);
        await Task.Delay(2000);
        _btnPause.PerformClick();
        T("일시정지 버튼", _rec!.IsPaused && _btnPause.Text == "재개" && _frame.IsPaused);
        double tp = _rec.Seconds;
        await Task.Delay(1000);
        T("일시정지 중 시간 멈춤", Math.Abs(_rec.Seconds - tp) < 0.2, $"{tp:0.00}s → {_rec.Seconds:0.00}s");
        _btnPause.PerformClick();
        T("재개 버튼", !_rec.IsPaused);
        await Task.Delay(1500);
        _btnCapture.PerformClick();
        var shot = _lastSaved;
        T("녹화 중 캡처 버튼", shot != null && shot.EndsWith(".png") && File.Exists(shot), shot ?? "");
        _btnRecord.PerformClick();
        T("중지 버튼 → 저장", await Until(() => _rec == null && !_stopping, 60000) && _btnRecord.Text == "녹화");
        if (_lastSaved != null)
        {
            var (info, dur) = Probe(_lastSaved);
            T("녹화 파일 (일시정지 1초 제외 ≈3.5초)", Math.Abs(dur - 3.5) < 0.6 && info.Contains("h264") && info.Contains("aac"), $"{Path.GetFileName(_lastSaved)} {dur:0.00}s {info}");
        }

        // ── 7. 단축키 F2 / F3 / F11 ──
        PressKey(_s.RecordKey);
        T($"단축키 {_s.RecordKey}: 녹화 시작", await Until(() => _rec != null, 3000));
        await Task.Delay(1200);
        string? before = _lastSaved;
        PressKey(_s.CaptureKey);
        T($"단축키 {_s.CaptureKey}: 캡처", await Until(() => _lastSaved != before && File.Exists(_lastSaved!), 2000), _lastSaved ?? "");
        PressKey(_s.PauseKey);
        T($"단축키 {_s.PauseKey}: 일시정지", await Until(() => _rec?.IsPaused == true, 2000));
        PressKey(_s.PauseKey);
        T($"단축키 {_s.PauseKey}: 재개", await Until(() => _rec?.IsPaused == false, 2000));
        await Task.Delay(800);
        PressKey(_s.RecordKey);
        T($"단축키 {_s.RecordKey}: 중지/저장", await Until(() => _rec == null && !_stopping, 60000) && File.Exists(_lastSaved!));

        // ── 8. 모드: 소리만 / GIF ──
        T("모드: 소리만 녹음", await ClickMenu(_btnMode, "소리만 녹음") && _s.Mode == RecordMode.Audio);
        foreach (var (menu, ext) in new[] { ("MPEG 오디오 레이어 3", ".mp3"), ("윈도우 미디어 오디오 2", ".wma") })
        {
            await ClickMenu(_btnCodec, menu);
            var f = await RecordFor(1500);
            var (info, dur) = Probe(f ?? "");
            T($"소리만 녹음 {ext}", f != null && f.EndsWith(ext) && dur > 1.0 && !info.Contains("video"), $"{dur:0.00}s {info}");
        }
        T("모드: GIF", await ClickMenu(_btnMode, "움짤 - GIF") && _s.Mode == RecordMode.Gif);
        {
            var f = await RecordFor(1500);
            var (info, dur) = Probe(f ?? "");
            T("GIF 녹화", f != null && f.EndsWith(".gif") && info.Contains("gif"), $"{dur:0.00}s {info}");
        }
        await ClickMenu(_btnMode, "화면 녹화");
        T("모드: 화면 녹화 복귀", _s.Mode == RecordMode.Screen);

        // ── 9. 옵션 창 ──
        var json0 = System.Text.Json.JsonSerializer.Serialize(_s);
        using (var opt = new OptionsForm(_s))
        {
            opt.Show(this);
            await Task.Delay(200);
            opt.OkButton.PerformClick();
        }
        T("옵션 창: 열고 확인 시 설정 유지", System.Text.Json.JsonSerializer.Serialize(_s) == json0);
        _s.Fps = 15; _s.Level = 5; _s.FilePrefix = "XX";
        using (var opt = new OptionsForm(_s))
        {
            opt.Show(this);
            await Task.Delay(200);
            opt.ResetButton.PerformClick();
            opt.OkButton.PerformClick();
        }
        var def = new Settings();
        T("옵션 창: 기본값으로 되돌리기", _s.Fps == def.Fps && _s.Level == def.Level && _s.FilePrefix == def.FilePrefix && _s.AudioSampleRate == 48000 && _s.AudioChannels == 2
                                        && _s.RecordKey == Keys.F2 && _s.PauseKey == Keys.F3 && _s.CaptureKey == Keys.F11 && _s.TimeLimitSeconds == 0,
            $"fps {_s.Fps}, level {_s.Level}, prefix {_s.FilePrefix}, {_s.AudioSampleRate}Hz {_s.AudioChannels}ch");
        _s.OutputDir = outDir;

        // ── 10. 표준 옵션 효과: 파일 이름 / FPS / 샘플레이트·채널 / 워터마크 / 클릭 효과 ──
        string wm = Path.Combine(outDir, "wm.png");
        Directory.CreateDirectory(outDir);
        using (var bmp = new Bitmap(64, 64))
        {
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, 255, 0, 255));
            bmp.Save(wm);
        }
        var region = new Rectangle(screen.X + 300, screen.Y + 250, 640, 360);
        SetRegion(region);
        using var black = new Form
        {
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.Black, TopMost = true, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = region,
        };
        black.Show();
        _frame.BringToFront();
        await Task.Delay(500);
        _s.FilePrefix = "TEST"; _s.Fps = 24; _s.AudioSampleRate = 44100; _s.AudioChannels = 1;
        _s.WatermarkPath = wm; _s.WatermarkOpacity = 100; _s.WatermarkPosition = WatermarkPos.BottomRight;
        _s.ClickEffect = true; _s.RecordCursor = false;
        ScreenCapturer.ForceClickForTest = true;
        Cursor.Position = new Point(region.X + 200, region.Y + 150);
        await ClickMenu(_btnCodec, "H.264 x264 + AAC (.MP4)");
        var fx = await RecordFor(2000);
        ScreenCapturer.ForceClickForTest = false;
        black.Close();
        if (fx != null)
        {
            var (info, dur) = Probe(fx);
            T("옵션: 파일 이름 접두어", Path.GetFileName(fx).StartsWith("TEST_"), Path.GetFileName(fx));
            T("옵션: FPS 24", info.Contains("r_frame_rate=24/1"), info);
            T("옵션: 44100Hz 모노", info.Contains("sample_rate=44100") && info.Contains("channels=1"), info);
            string frame = Path.Combine(outDir, "frame.png");
            Ffmpeg.RunCapture(_ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", "1", "-i", fx, "-frames:v", "1", frame }, 30000);
            if (File.Exists(frame))
            {
                using var img = new Bitmap(frame);
                var wmPx = img.GetPixel(img.Width - 16 - 32, img.Height - 16 - 32);
                T("옵션: 워터마크 (오른쪽 아래)", wmPx.R > 200 && wmPx.G < 70 && wmPx.B > 200, wmPx.ToString());
                var clickPx = img.GetPixel(200 - 12, 150);
                var far = img.GetPixel(60, 60);
                T("옵션: 마우스 클릭 효과", clickPx.R > 80 && clickPx.G > 60 && clickPx.B < 40 && far.R < 30, $"클릭 위치 {clickPx}, 먼 곳 {far}");
            }
            else T("프레임 추출", false);
        }
        else T("옵션 효과 녹화", false, string.Join("\n", Errors));
        _s.ClickEffect = false; _s.WatermarkPath = ""; _s.RecordCursor = true;
        _s.FilePrefix = "MxCam"; _s.Fps = 30; _s.AudioSampleRate = 48000; _s.AudioChannels = 2;

        // ── 11. 녹화 시간 제한 ──
        _s.TimeLimitSeconds = 2; _s.TimeLimitAction = LimitAction.None;
        _btnRecord.PerformClick();
        T("시간 제한 2초: 자동 중지", await Until(() => _rec != null, 3000) && await Until(() => _rec == null && !_stopping, 8000));
        var (_, ld) = Probe(_lastSaved!);
        T("시간 제한 파일 길이 ≈2초", Math.Abs(ld - 2) < 0.5, $"{ld:0.00}s");
        _s.TimeLimitAction = LimitAction.Restart;
        _btnRecord.PerformClick();
        await Until(() => _rec != null, 3000);
        string? first = _lastSaved;
        bool restarted = await Until(() => _lastSaved != first && _rec != null, 8000);
        T("시간 제한 후 새 녹화 시작", restarted, _lastSaved ?? "");
        _s.TimeLimitSeconds = 0;
        await StopRecordingAsync();
        _s.TimeLimitSeconds = 2; _s.TimeLimitAction = LimitAction.Shutdown;
        _btnRecord.PerformClick();
        T("시간 제한 후 시스템 종료 (테스트에선 명령만 확인)", await Until(() => LastShutdownRequested, 8000));
        _s.TimeLimitSeconds = 0; _s.TimeLimitAction = LimitAction.None;

        // ── 12. 녹화 중 최소화 ──
        _s.MinimizeWhileRecording = true;
        _btnRecord.PerformClick();
        await Until(() => _rec != null, 3000);
        await Task.Delay(300);
        T("옵션: 녹화 중 최소화", WindowState == FormWindowState.Minimized);
        await StopRecordingAsync();
        T("녹화 끝나면 창 복원", WindowState == FormWindowState.Normal);
        _s.MinimizeWhileRecording = false;

        // ── 13. 추천 창 / 창 찾기 ──
        using (var rf = new RecommendForm(_s, _ffmpeg!, _frame.CaptureRect))
        {
            rf.Show(this);
            await Task.Delay(300);
            var list = rf.Controls.OfType<ListView>().First();
            T("추천 창: 코덱 비교 목록", list.Items.Count >= 3 && list.Items[0].SubItems[^1].Text.Contains("추천"), $"{list.Items.Count}개, 1위 {list.Items[0].Text}");
        }
        using (var picker = new WindowPicker(new[] { Handle, _frame.Handle }))
        {
            var r = picker.WindowAt(new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2));
            T("창 찾기: 마우스 아래 창 인식", !r.IsEmpty, r.ToString());
        }

        // ── 13-2. 동영상 변환 (포맷 추천 → 샘플 → 변환) ──
        string srcH264 = Path.Combine(outDir, "src_h264_1080p.mp4");
        Ffmpeg.RunCapture(_ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=30", "-f", "lavfi", "-i", "sine=f=440:r=48000",
            "-t", "10", "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", srcH264 }, 120000);
        using (var cf = new ConvertForm(_s, _ffmpeg!))
        {
            cf.Show(this);
            bool sampled = false;
            cf.SampleFinished += () => sampled = true;
            await cf.LoadFileAsync(srcH264);
            T("변환: 고화질 H.264 → 추천 형식 = 재인코딩", cf.RecommendedPreset == ConvertPreset.Recommended, cf.RecommendedPreset.ToString() ?? "");
            T("변환: 6초 샘플로 예상 용량·화질", await Until(() => sampled, 60000) && cf.LastSample is { Error: null, Ssim: > 0.9 } s1 && s1.EstimatedMB < new FileInfo(srcH264).Length / 1e6,
                cf.LastSample is { } ls ? $"예상 {ls.EstimatedMB:0.0}MB / 원본 {new FileInfo(srcH264).Length / 1e6:0.0}MB, SSIM {ls.Ssim:0.000}" : "");
            bool? done = null;
            cf.ConvertFinished += ok => done = ok;
            await cf.ConvertAsync();
            await Until(() => done != null, 120000);
            var outF = cf.LastOutput;
            var (ci, cd) = outF != null ? Probe(outF) : ("", 0);
            T("변환: 추천 형식으로 변환 완료", done == true && outF != null && new FileInfo(outF).Length < new FileInfo(srcH264).Length && Math.Abs(cd - 10) < 0.5,
                outF != null ? $"{Path.GetFileName(outF)} {new FileInfo(outF).Length / 1e6:0.0}MB {cd:0.0}s {ci}" : "실패");
        }
        string srcHevc = Path.Combine(outDir, "src_hevc_small.mkv");
        Ffmpeg.RunCapture(_ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30", "-t", "6",
            "-c:v", "libx265", "-preset", "fast", "-crf", "30", "-x265-params", "log-level=error", srcHevc }, 120000);
        using (var cf = new ConvertForm(_s, _ffmpeg!))
        {
            cf.Show(this);
            await cf.LoadFileAsync(srcHevc);
            T("변환: 이미 효율적인 HEVC → 포장만 변경 추천", cf.RecommendedPreset == ConvertPreset.Remux, cf.RecommendedPreset.ToString() ?? "");
            bool? done = null;
            cf.ConvertFinished += ok => done = ok;
            await cf.ConvertAsync();
            await Until(() => done != null, 60000);
            var (ci, _) = cf.LastOutput != null ? Probe(cf.LastOutput) : ("", 0);
            T("변환: MKV → MP4 스트림 복사", done == true && cf.LastOutput!.EndsWith(".mp4") && ci.Contains("hevc"), ci);
        }
        string srcWav = Path.Combine(outDir, "src_audio.wav");
        Ffmpeg.RunCapture(_ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:r=48000", "-t", "5", srcWav }, 60000);
        using (var cf = new ConvertForm(_s, _ffmpeg!))
        {
            cf.Show(this);
            await cf.LoadFileAsync(srcWav);
            T("변환: WAV 소리 파일 → MP3 추천", cf.RecommendedPreset == ConvertPreset.Mp3);
            bool? done = null;
            cf.ConvertFinished += ok => done = ok;
            await cf.ConvertAsync();
            await Until(() => done != null, 60000);
            T("변환: MP3 생성", done == true && cf.LastOutput!.EndsWith(".mp3") && new FileInfo(cf.LastOutput).Length < new FileInfo(srcWav).Length);
        }
        {
            var info = MediaInfo.Probe(_ffmpeg!, srcH264);
            foreach (var preset in new[] { ConvertPreset.Web720, ConvertPreset.Compatible, ConvertPreset.Hevc, ConvertPreset.Av1, ConvertPreset.Gif })
            {
                var plan = Converter.Plan(preset, info, _s, 3);
                string o = Path.Combine(outDir, $"preset_{preset}.{plan.Ext}");
                var (ok, msg) = await Converter.RunAsync(_ffmpeg!, info, plan, o, new Progress<double>(), CancellationToken.None);
                var (pi, pd) = ok ? Probe(o) : (msg, 0);
                bool good = ok && (preset != ConvertPreset.Web720 || pi.Contains("height=720"));
                T($"변환 프리셋: {preset}", good, $"{(ok ? new FileInfo(o).Length / 1e6 : 0):0.0}MB {pd:0.0}s {pi}");
            }
            var cancelPlan = Converter.Plan(ConvertPreset.Av1, info, _s, 5);
            string co = Path.Combine(outDir, "cancel_test.mp4");
            using var ccts = new CancellationTokenSource(1500);
            var (cok, cmsg) = await Converter.RunAsync(_ffmpeg!, info, cancelPlan, co, new Progress<double>(), ccts.Token);
            T("변환 취소 → 미완성 파일 삭제", !cok && cmsg == "취소됨" && !File.Exists(co), cmsg);
        }

        // ── 13-3. 탐색기 우클릭 메뉴 + 자가 학습 최적화 ──
        ShellIntegration.Register(Environment.ProcessPath!);
        var cmd = ShellIntegration.RegisteredCommand();
        T("우클릭 메뉴 등록 (비디오 파일)", cmd != null && cmd.Contains("--optimize") && cmd.Contains(Environment.ProcessPath!), cmd ?? "");
        {
            OptimizeForm? of1 = null, of2 = null;
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            of1 = new OptimizeForm(_s, srcH264) { TestMode = true };
            of1.Show(this);
            await Until(() => of1.Finished, 300000);
            sw1.Stop();
            var d1 = of1.Decision;
            long srcLen = new FileInfo(srcH264).Length;
            T("최적화 1회차: 후보 전체 탐색 후 변환", of1.Succeeded && d1 != null && !d1.UsedHistory && of1.Output != null && new FileInfo(of1.Output).Length < srcLen,
                d1 != null ? $"{Codecs.Video(d1.Kind).Label} {d1.Level:0.0}단계, 시도 {d1.Trials.Count}회, {sw1.Elapsed.TotalSeconds:0}초, {new FileInfo(of1.Output!).Length / 1e6:0.0}MB / 원본 {srcLen / 1e6:0.0}MB, SSIM {d1.Ssim:0.000}" : "");
            T("최적화: 목표 화질 만족", d1 != null && d1.Ssim >= Optimizer.TargetSsim(_s.Level), d1 != null ? $"{d1.Ssim:0.0000} ≥ {Optimizer.TargetSsim(_s.Level)}" : "");
            of1.Close();
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            of2 = new OptimizeForm(_s, srcH264) { TestMode = true };
            of2.Show(this);
            await Until(() => of2.Finished, 300000);
            sw2.Stop();
            var d2 = of2.Decision;
            T("최적화 2회차: 학습 데이터 활용 → 시도 횟수 감소", of2.Succeeded && d2 != null && d2.UsedHistory && d2.Trials.Count < d1!.Trials.Count,
                d2 != null ? $"학습 {d2.HistoryCount}건, 시도 {d2.Trials.Count}회 (1회차 {d1!.Trials.Count}회), {sw2.Elapsed.TotalSeconds:0}초, 선택 {Codecs.Video(d2.Kind).Label} {d2.Level:0.0}단계" : "");
            of2.Close();
        }
        // ── 14. 결과 파일 목록 ──
        var files = Directory.GetFiles(outDir).Select(Path.GetFileName).ToList();
        _tlog.WriteLine($"\n저장된 파일 {files.Count}개: {string.Join(", ", files)}");
        T("오류 메시지 없음", Errors.Count == 0, string.Join(" / ", Errors));

        // ── 15. 시간 제한 후 프로그램 종료 (마지막) ──
        _s.TimeLimitSeconds = 2; _s.TimeLimitAction = LimitAction.Exit;
        _btnRecord.PerformClick();
        T("시간 제한 후 프로그램 종료", await Until(() => IsDisposed || _closingForTest, 10000));
    }

    bool _closingForTest, _testDone;
    internal void MarkTestDone() => _testDone = true;

    static IntPtr SendHit(IntPtr hwnd, int x, int y) =>
        SendMessage(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
