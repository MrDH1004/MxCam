using System.Diagnostics;
using System.Drawing.Text;

namespace MxCam;

public sealed partial class MainForm : Form
{
    readonly Settings _s = Settings.Load();
    readonly RegionFrame _frame = new();
    readonly ClickThroughToolStrip _bar = new();
    readonly Label _hint = new();
    readonly ClickThroughStatusStrip _status = new();
    readonly ToolStripStatusLabel _lblState = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _lblSize = new();
    readonly ToolStripStatusLabel _lblCodec = new();
    readonly ToolStripStatusLabel _lblUpdate = new() { IsLink = true, Visible = false, ForeColor = Color.FromArgb(22, 163, 74) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    ToolStripButton _btnRecord = null!, _btnPause = null!, _btnCapture = null!;
    ToolStripDropDownButton _btnSize = null!, _btnCodec = null!, _btnSound = null!, _btnMode = null!;

    Recorder? _rec;
    bool _stopping;
    string? _ffmpeg;
    string? _lastSaved;

    public MainForm()
    {
        Text = $"MxCam {AppInfo.VersionText}";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("맑은 고딕", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        BuildToolbar();
        _hint.Dock = DockStyle.Bottom;
        _hint.AutoSize = false;
        _hint.Height = 22;
        _hint.Padding = new Padding(6, 3, 6, 0);
        _hint.ForeColor = Color.FromArgb(30, 90, 170);
        _hint.Cursor = Cursors.Hand;
        _hint.Click += (_, _) => ShowRecommend();
        _lblUpdate.Click += (_, _) => RestartForUpdate();
        _status.Items.AddRange(new ToolStripItem[] { _lblState, _lblSize, _lblCodec, _lblUpdate });
        _status.SizingGrip = false;
        _lblState.IsLink = false;
        _lblState.Click += (_, _) => { if (_lastSaved != null) OpenInExplorer(_lastSaved); };

        Controls.Add(_bar);
        Controls.Add(_hint);
        Controls.Add(_status);

        _timer.Tick += (_, _) => UpdateStatus();
        _frame.AreaChanged += OnAreaChanged;
        _frame.Move += (_, _) => AttachToFrame();
        _frame.Resize += (_, _) => AttachToFrame();

        _ffmpeg = Ffmpeg.Resolve(_s.FfmpegPath);
        UpdateStatus();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var r = ClampToScreens(_s.Region);
        _frame.CaptureRect = r;
        _frame.Show();
        ClientSize = new Size(Math.Max(_bar.PreferredSize.Width + 8, 520), _bar.Height + _hint.Height + _status.Height);
        // 툴바는 마지막 위치에 고정 (처음 실행 때만 녹화 영역 근처에 배치)
        var saved = new Point(_s.ToolbarX, _s.ToolbarY);
        if (_s.ToolbarX != int.MinValue && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(saved)))
            Location = saved;
        else
            AttachToFrame(force: true);
        RegisterHotkeys();
        UpdateHint();
        // 업데이트 확인은 ffmpeg·코덱 상태와 무관하게 수행 (코덱 쪽 문제로 업데이트까지 막히지 않게)
        if (!TestMode && _s.AutoUpdate) _ = CheckUpdateAsync();

        if (_ffmpeg == null || !Ffmpeg.IsUsable(_ffmpeg))
        {
            _lblState.Text = _ffmpeg == null ? "ffmpeg를 찾을 수 없음 — 옵션에서 경로 지정" : "ffmpeg가 너무 오래됨 — 최신 버전 설치 필요";
            // 창이 보인 뒤 설치 여부를 묻는다
            if (!TestMode) BeginInvoke(new Action(() => _ = OfferFfmpegInstallAsync()));
            return;
        }
        StartCodecProbe();
    }

    bool _installingFfmpeg;

    /// <summary>ffmpeg가 없거나 너무 오래되었으면 확인창을 띄우고, 동의하면 최신 ffmpeg를 내려받아 설치한 뒤 코덱 탐지를 다시 한다.</summary>
    async Task OfferFfmpegInstallAsync()
    {
        if (_installingFfmpeg || IsDisposed) return;
        _installingFfmpeg = true;
        try
        {
            bool missing = _ffmpeg == null;
            string why = missing
                ? "녹화에 필요한 ffmpeg를 찾을 수 없습니다."
                : $"설치된 ffmpeg가 너무 오래되었거나 실행되지 않아 녹화할 수 없습니다.\n({_ffmpeg})";
            TopMost = false;
            var ans = MessageBox.Show(this,
                why + "\n\n최신 ffmpeg(약 110MB)를 내려받아 자동으로 설치할까요?\n\n" +
                $"· 설치 위치: {FfmpegInstaller.InstallDir}\n· 출처: gyan.dev (SHA256 검증)\n· 관리자 권한 필요 없음",
                "MxCam - ffmpeg 설치", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            TopMost = true;
            if (ans != DialogResult.Yes)
            {
                _lblState.Text = missing ? "ffmpeg를 찾을 수 없음 — 옵션에서 경로 지정" : "ffmpeg가 너무 오래됨 — 녹화 버튼을 누르면 다시 설치를 안내";
                return;
            }
            try
            {
                _ffmpeg = await FfmpegInstaller.InstallAsync(new Progress<string>(t => { if (!IsDisposed) _lblState.Text = t; }));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
            {
                if (IsDisposed) return;
                _lblState.Text = "ffmpeg 자동 설치 실패 — 옵션에서 경로 지정";
                TopMost = false;
                MessageBox.Show(this, "ffmpeg 자동 설치에 실패했습니다.\n" + ex.Message + "\n\n직접 설치: winget install Gyan.FFmpeg", "MxCam", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                TopMost = true;
                return;
            }
            if (IsDisposed) return;
            StartCodecProbe();
        }
        finally
        {
            _installingFfmpeg = false;
            MaybePromptUpdate(); // ffmpeg 확인창 때문에 미뤄 둔 업데이트 알림
        }
    }

    void StartCodecProbe()
    {
        if (_ffmpeg == null) return;
        _lblState.Text = "코덱 확인 중…";
        var ff = _ffmpeg;
        Task.Run(() => Codecs.Probe(ff)).ContinueWith(_ => BeginInvoke(() =>
        {
            _lblState.Text = Codecs.ProbeFailed ? "ffmpeg 실행 실패(너무 오래된 버전?) — 옵션에서 최신 ffmpeg 경로 지정" : "준비";
            UpdateHint();
            if (!TestMode) ApplyShellMenu();
            if (!TestMode && _ffmpeg != null)
            {
                // 처음 한 번만 자동 분석 + 설명. 이후엔 PC 구성이 바뀌어도 알림만 (옵션에서 다시 분석)
                if (_s.PcFingerprint == null) ShowPcAnalysis(automatic: true);
                else if (PcAnalyzer.Collect(_ffmpeg).Fingerprint != _s.PcFingerprint)
                    _lblState.Text = "PC 구성이 바뀌었습니다 — [옵션] → [내 PC 다시 분석]을 권장";
            }
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ExcludeFromCapture(Handle);
    }

    // ───────────── 툴바 ─────────────

    void BuildToolbar()
    {
        _bar.GripStyle = ToolStripGripStyle.Hidden;
        _bar.ImageScalingSize = new Size(24, 24);
        _bar.Padding = new Padding(4, 2, 4, 2);
        _bar.RenderMode = ToolStripRenderMode.System;
        _bar.Dock = DockStyle.Top;
        _bar.AutoSize = true;

        _btnRecord = Btn("녹화", Glyphs.Record, (_, _) => ToggleRecord());
        _btnPause = Btn("일시정지", Glyphs.Pause, (_, _) => TogglePause());
        _btnPause.Enabled = false;
        _btnCapture = Btn("캡처", Glyphs.Camera, (_, _) => TakeScreenshot());
        _btnSize = Drop("크기 조정", Glyphs.Resize, BuildSizeMenu);
        var btnOpen = Btn("열기", Glyphs.Folder, (_, _) => OpenOutputFolder());
        _btnCodec = Drop("코덱", Glyphs.Codec, BuildCodecMenu);
        _btnSound = Drop("소리", Glyphs.Sound, BuildSoundMenu);
        _btnMode = Drop("모드", Glyphs.Mode, BuildModeMenu);
        var btnConv = Btn("변환", Glyphs.Convert, (_, _) => ShowConvert());
        var btnRec = Btn("추천", Glyphs.Star, (_, _) => ShowRecommend());
        var btnOpt = Btn("옵션", Glyphs.Settings, (_, _) => ShowOptions());

        _bar.Items.AddRange(new ToolStripItem[]
        {
            _btnRecord, _btnPause, _btnCapture, new ToolStripSeparator(), _btnSize, btnOpen, new ToolStripSeparator(),
            _btnCodec, _btnSound, _btnMode, btnConv, btnRec, btnOpt,
        });
        UpdateTooltips();
    }

    static ToolStripButton Btn(string text, Image img, EventHandler onClick)
    {
        var b = new ToolStripButton(text, img, onClick) { TextImageRelation = TextImageRelation.ImageAboveText, AutoSize = false, Width = 62, Height = 52 };
        return b;
    }

    static ToolStripDropDownButton Drop(string text, Image img, Action<ToolStripDropDownButton> build)
    {
        var b = new ToolStripDropDownButton(text, img) { TextImageRelation = TextImageRelation.ImageAboveText, AutoSize = false, Width = 70, Height = 52 };
        b.DropDownOpening += (_, _) => build(b);
        b.DropDownItems.Add("…"); // 화살표 표시용, 열 때 다시 만든다
        return b;
    }

    void UpdateTooltips()
    {
        _btnRecord.ToolTipText = $"녹화 시작/중지 ({_s.RecordKey})";
        _btnPause.ToolTipText = $"일시정지/재개 ({_s.PauseKey})";
        _btnCapture.ToolTipText = $"화면 캡처 ({_s.CaptureKey})";
    }

    // ───────────── 메뉴 ─────────────

    void BuildSizeMenu(ToolStripDropDownButton b)
    {
        b.DropDownItems.Clear();
        var full = new ToolStripMenuItem("전체 화면");
        int n = 1;
        foreach (var sc in Screen.AllScreens)
        {
            var bounds = sc.Bounds;
            full.DropDownItems.Add($"모니터 {n++}{(sc.Primary ? " (주)" : "")} — {bounds.Width}×{bounds.Height}", null, (_, _) => SetRegion(bounds));
        }
        b.DropDownItems.Add(full);
        b.DropDownItems.Add("녹화 대상 창 찾기…", null, (_, _) => PickWindow());
        b.DropDownItems.Add(new ToolStripSeparator());
        var cur = _frame.CaptureRect;
        foreach (var (w, h) in new[] { (320, 240), (640, 360), (640, 480), (800, 600), (1024, 768), (1280, 720), (1280, 1024), (1600, 900), (1920, 1080), (2560, 1440) })
        {
            var item = new ToolStripMenuItem($"{w} × {h}", null, (_, _) => SetRegion(new Rectangle(cur.X, cur.Y, w, h)))
            {
                Checked = (cur.Width & ~1) == w && (cur.Height & ~1) == h,
            };
            b.DropDownItems.Add(item);
        }
        b.DropDownItems.Add(new ToolStripSeparator());
        b.DropDownItems.Add("사용자 지정 크기…", null, (_, _) =>
        {
            var r = _frame.CaptureRect;
            using var dlg = new SizeDialog(r.Width, r.Height);
            if (dlg.ShowDialog(this) == DialogResult.OK) SetRegion(new Rectangle(r.X, r.Y, dlg.W, dlg.H));
        });
    }

    void BuildCodecMenu(ToolStripDropDownButton b)
    {
        b.DropDownItems.Clear();
        var r = _frame.CaptureRect;
        var rec = Codecs.Recommended(_s, r.Width, r.Height, _s.Fps);

        var auto = new ToolStripMenuItem($"자동 (추천) — {rec.Label}", null, (_, _) => SelectCodec(Codecs.AutoId))
        {
            Checked = _s.CodecId == Codecs.AutoId,
            Font = new Font(Font, FontStyle.Bold),
        };
        b.DropDownItems.Add(auto);

        var levels = new ToolStripMenuItem($"품질 단계: {Codecs.LevelNames[_s.Level - 1]}");
        for (int i = 1; i <= 5; i++)
        {
            int lv = i;
            levels.DropDownItems.Add(new ToolStripMenuItem(Codecs.LevelNames[i - 1], null, (_, _) => { _s.Level = lv; _s.Save(); UpdateHint(); })
                { Checked = _s.Level == lv });
        }
        b.DropDownItems.Add(levels);
        b.DropDownItems.Add("코덱 추천 받기 (실측 비교)…", null, (_, _) => ShowRecommend());
        b.DropDownItems.Add("내 PC 분석 (최적 코덱 다시 찾기)…", null, (_, _) => ShowPcAnalysis(automatic: false));
        b.DropDownItems.Add(new ToolStripSeparator());

        foreach (var group in Codecs.All.Where(p => p.Group != "소리").GroupBy(p => p.Group))
        {
            var g = new ToolStripMenuItem(group.Key);
            foreach (var p in group)
            {
                var info = p.VideoInfo!;
                bool avail = Codecs.IsAvailable(p);
                string tag = !avail ? "  (이 PC 미지원)" : info.Kind == rec.Kind ? "  ★추천" : "";
                string eff = info.Kind == VKind.Gif ? "" : $"   용량효율 {Codecs.Stars((info.Efficiency + 1) / 2)}";
                var item = new ToolStripMenuItem(p.Name + tag + eff, null, (_, _) => SelectCodec(p.Id))
                {
                    Enabled = avail,
                    Checked = _s.CodecId == p.Id,
                    ToolTipText = avail ? Codecs.Hint(_s, p, r.Width, r.Height, _s.Fps) : "이 PC의 그래픽카드/ffmpeg에서 사용할 수 없습니다",
                };
                if (info.Kind == rec.Kind && avail) item.Font = new Font(Font, FontStyle.Bold);
                if (p.Id == _s.CodecId) g.Checked = true;
                g.DropDownItems.Add(item);
            }
            b.DropDownItems.Add(g);
        }

        b.DropDownItems.Add(new ToolStripSeparator());
        var audio = new ToolStripMenuItem("소리만 녹음 형식");
        foreach (var p in Codecs.All.Where(p => p.Group == "소리"))
        {
            audio.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, (_, _) => { _s.AudioCodecId = p.Id; _s.Save(); UpdateHint(); })
                { Checked = _s.AudioCodecId == p.Id, Enabled = Codecs.IsAvailable(p) });
        }
        b.DropDownItems.Add(audio);
    }

    void BuildSoundMenu(ToolStripDropDownButton b)
    {
        b.DropDownItems.Clear();
        b.DropDownItems.Add(new ToolStripMenuItem("시스템 소리 녹음 (스피커 출력)", null, (_, _) => { _s.SystemAudio = !_s.SystemAudio; _s.Save(); UpdateHint(); })
            { Checked = _s.SystemAudio });
        b.DropDownItems.Add(new ToolStripMenuItem("마이크 녹음", null, (_, _) => { _s.Microphone = !_s.Microphone; _s.Save(); UpdateHint(); })
            { Checked = _s.Microphone });
        var mics = new ToolStripMenuItem("마이크 장치");
        mics.DropDownItems.Add(new ToolStripMenuItem("기본 장치", null, (_, _) => { _s.MicDeviceId = null; _s.Save(); })
            { Checked = string.IsNullOrEmpty(_s.MicDeviceId) });
        foreach (var (id, name) in AudioSource.ListMicrophones())
            mics.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => { _s.MicDeviceId = id; _s.Microphone = true; _s.Save(); UpdateHint(); })
                { Checked = _s.MicDeviceId == id });
        b.DropDownItems.Add(mics);
        b.DropDownItems.Add(new ToolStripSeparator());
        b.DropDownItems.Add(new ToolStripMenuItem("소리 녹음 안 함", null, (_, _) => { _s.SystemAudio = false; _s.Microphone = false; _s.Save(); UpdateHint(); })
            { Checked = !_s.SystemAudio && !_s.Microphone });
    }

    void BuildModeMenu(ToolStripDropDownButton b)
    {
        b.DropDownItems.Clear();
        void Add(string text, RecordMode m) =>
            b.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { _s.Mode = m; _s.Save(); UpdateHint(); }) { Checked = _s.Mode == m });
        Add("화면 녹화 및 소리 녹음", RecordMode.Screen);
        Add("소리만 녹음", RecordMode.Audio);
        Add("움짤 - GIF 녹화", RecordMode.Gif);
    }

    void SelectCodec(string id)
    {
        _s.CodecId = id;
        if (_s.Mode == RecordMode.Gif && id != "gif") _s.Mode = RecordMode.Screen;
        if (id == "gif") _s.Mode = RecordMode.Gif;
        _s.Save();
        UpdateHint();
    }

    // ───────────── 영역 ─────────────

    void SetRegion(Rectangle r)
    {
        if (_rec != null) return;
        r = ClampToScreens(r);
        _frame.CaptureRect = r;
        OnAreaChanged();
    }

    void OnAreaChanged()
    {
        _s.Region = _frame.CaptureRect;
        _s.Save();
        UpdateHint();
    }

    static Rectangle ClampToScreens(Rectangle r)
    {
        var vs = SystemInformation.VirtualScreen;
        int w = Math.Clamp(r.Width, 64, vs.Width), h = Math.Clamp(r.Height, 48, vs.Height);
        int x = Math.Clamp(r.X, vs.Left, vs.Right - w), y = Math.Clamp(r.Y, vs.Top, vs.Bottom - h);
        return new Rectangle(x, y, w, h);
    }

    void PickWindow()
    {
        using var picker = new WindowPicker(new[] { Handle, _frame.Handle });
        picker.ShowDialog(this);
        if (picker.Picked is { } r) SetRegion(r);
    }

    void AttachToFrame(bool force = false)
    {
        if (!_s.AttachToolbar && !force) return;
        var f = _frame.Bounds;
        var scr = Screen.FromRectangle(f).WorkingArea;
        int x = Math.Clamp(f.Left, scr.Left, Math.Max(scr.Left, scr.Right - Width));
        int y = f.Top - Height;
        if (y < scr.Top) y = Math.Min(f.Bottom, scr.Bottom - Height); // 위에 공간이 없으면 아래쪽
        if (y + Height > scr.Bottom) y = scr.Top;                       // 전체화면이면 안쪽 위 (캡처에서 제외되므로 영상엔 안 보임)
        Location = new Point(x, y);
    }

    // ───────────── 녹화 ─────────────

    CodecProfile ResolveProfile(Rectangle r)
    {
        switch (_s.Mode)
        {
            case RecordMode.Audio:
                return Codecs.Find(_s.AudioCodecId) ?? Codecs.Find("a_mp3")!;
            case RecordMode.Gif:
                return Codecs.Find("gif")!;
        }
        if (_s.CodecId != Codecs.AutoId && Codecs.Find(_s.CodecId) is { } p && Codecs.IsAvailable(p)) return p;
        return Codecs.AutoProfile(_s, r.Width, r.Height, _s.Fps);
    }

    void ToggleRecord()
    {
        if (_stopping) return;
        if (_rec == null) StartRecording();
        else StopRecording();
    }

    void StartRecording()
    {
        _learnCts?.Cancel(); // 학습보다 녹화가 우선
        _ffmpeg ??= Ffmpeg.Resolve(_s.FfmpegPath);
        if (_ffmpeg == null || !Ffmpeg.IsUsable(_ffmpeg))
        {
            if (TestMode) return;
            _ = OfferFfmpegInstallAsync();
            return;
        }
        var region = _frame.CaptureRect;
        var profile = ResolveProfile(region);
        string path = NewFilePath(profile.Ext);
        var rec = new Recorder(new RecordOptions
        {
            Region = region,
            Mode = _s.Mode,
            Profile = profile,
            Level = _s.Level,
            VideoLevel = profile.Id == Codecs.AutoId ? Codecs.AutoLevel(_s, profile.Video, region.Width & ~1, region.Height & ~1, _s.Fps) : _s.Level,
            LearnSamplePath = _s.AutoLearn && _s.Mode == RecordMode.Screen && region.Width >= 160
                ? Path.Combine(Path.GetTempPath(), $"mxcam_learn_{Guid.NewGuid():N}.mkv") : null,
            Fps = _s.Fps,
            Cursor = _s.RecordCursor,
            SystemAudio = _s.SystemAudio,
            Microphone = _s.Microphone,
            MicDeviceId = _s.MicDeviceId,
            OutputPath = path,
            FfmpegExe = _ffmpeg,
            TimeLimitSec = _s.TimeLimitSeconds,
            ClickEffect = _s.ClickEffect,
            WatermarkPath = _s.WatermarkPath,
            WatermarkOpacity = _s.WatermarkOpacity,
            WatermarkPosition = _s.WatermarkPosition,
            AudioSampleRate = _s.AudioSampleRate,
            AudioChannels = _s.AudioChannels,
        });
        rec.Faulted += msg => BeginInvoke(() => OnFault(rec, msg));
        rec.TimeLimitReached += () => BeginInvoke(() => OnTimeLimit(rec));
        try
        {
            rec.Start();
        }
        catch (Exception ex)
        {
            rec.Dispose();
            ShowError("MxCam", "녹화를 시작할 수 없습니다.\n\n" + ex.Message);
            return;
        }
        _rec = rec;
        _lblCodec.Text = profile.AudioOnly ? Codecs.AudioLabel(profile.Audio) : $"{profile.VideoInfo!.Label} · {_s.Level}단계";
        _frame.Recording = true;
        _frame.IsPaused = false;
        _btnRecord.Text = "중지";
        _btnRecord.Image = Glyphs.Stop;
        _btnPause.Enabled = true;
        _btnSize.Enabled = _btnCodec.Enabled = _btnSound.Enabled = _btnMode.Enabled = false;
        _timer.Start();
        if (_s.MinimizeWhileRecording) WindowState = FormWindowState.Minimized;
        if (rec.Warnings.Count > 0) _hint.Text = "⚠ " + string.Join(" / ", rec.Warnings);
    }

    async void StopRecording(bool restart = false) => await StopRecordingAsync(restart);

    internal async Task StopRecordingAsync(bool restart = false)
    {
        var rec = _rec;
        if (rec == null || _stopping) return;
        _stopping = true;
        _timer.Stop();
        _lblState.Text = "저장 중…";
        _btnRecord.Enabled = _btnPause.Enabled = false;
        var result = await Task.Run(rec.Stop);
        var learnSample = rec.LearnSample;
        var learnSize = new Size(rec.VideoWidth, rec.VideoHeight);
        rec.Dispose();
        _rec = null;
        _stopping = false;
        _frame.Recording = false;
        _frame.IsPaused = false;
        _btnRecord.Text = "녹화";
        _btnRecord.Image = Glyphs.Record;
        _btnPause.Text = "일시정지";
        _btnPause.Image = Glyphs.Pause;
        _btnRecord.Enabled = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        _btnPause.Enabled = false;
        _btnSize.Enabled = _btnCodec.Enabled = _btnSound.Enabled = _btnMode.Enabled = true;

        if (result.Ok)
        {
            _lastSaved = result.Path;
            long size = File.Exists(result.Path) ? new FileInfo(result.Path).Length : 0;
            _lblState.Text = $"저장됨: {Path.GetFileName(result.Path)} (클릭하면 폴더 열기)";
            _lblSize.Text = FormatSize(size);
            if (!string.IsNullOrEmpty(result.Message)) _hint.Text = "⚠ " + result.Message.Split('\n')[0];
            if (_s.OpenFolderAfterRecord && !restart) OpenInExplorer(result.Path);
            if (learnSample != null) _ = LearnFromRecordingAsync(learnSample, learnSize);
        }
        else
        {
            _lblState.Text = "녹화 실패";
            ShowError("MxCam - 녹화 실패", Trim(result.Message, 1500));
        }
        if (restart && result.Ok) StartRecording();
        else MaybePromptUpdate(); // 녹화 중 미뤄 둔 업데이트 알림
    }

    // ───────────── 녹화 자가 학습 ─────────────

    CancellationTokenSource? _learnCts;
    internal bool Learning_Running => _learnCts != null;
    internal string? LastLearnResult { get; private set; }

    /// <summary>
    /// 녹화가 끝나면 녹화 중 저장한 3초 무손실 샘플로 코덱·품질값을 시험해 학습한다 (백그라운드, 녹화를 다시 시작하면 취소).
    /// 학습이 쌓이면 시험 후보를 줄이고, 가끔만 다른 후보를 탐색한다.
    /// </summary>
    async Task LearnFromRecordingAsync(string sample, Size size)
    {
        if (_learnCts != null || _ffmpeg == null || !_s.AutoLearn) { TryDelete(sample); return; }
        string cls = Learning.RecClass(size.Width, size.Height, _s.Fps);
        int sessions = Learning.SessionCounts().GetValueOrDefault(cls);
        // 충분히 학습된 화면 종류는 가끔만(30%) 다시 학습해 계속 개선
        if (sessions >= 5 && Random.Shared.NextDouble() > 0.3) { TryDelete(sample); return; }
        var cts = _learnCts = new CancellationTokenSource();
        string ff = _ffmpeg;
        int level = _s.Level;
        _hint.Text = "🧠 방금 녹화한 화면으로 코덱 자가 학습 중… (백그라운드)";
        try
        {
            var info = await Task.Run(() => MediaInfo.Probe(ff, sample), cts.Token);
            var d = await Task.Run(() => Optimizer.Decide(ff, info, _s, level, new Progress<string>(), cts.Token, cls, Recommender.RealtimeMargin, Recommender.RecordingCapable), cts.Token);
            var best = Learning.Best(cls, level, Recommender.RealtimeMargin);
            LastLearnResult = best != null ? $"{Codecs.Video(best.Kind).Label} {best.Level:0.0}단계" : Codecs.Video(d.Kind).Label;
            if (!IsDisposed && _rec == null)
                _hint.Text = best != null
                    ? $"🧠 학습 완료 ({best.Sessions}회) → 다음 녹화부터 자동: {Codecs.Video(best.Kind).Label} {best.Level:0.0}단계"
                    : "🧠 학습 완료";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastLearnResult = "실패: " + ex.Message;
        }
        finally
        {
            _learnCts = null;
            cts.Dispose();
            TryDelete(sample);
        }
    }

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch { }
    }

    void OnFault(Recorder rec, string msg)
    {
        if (_rec != rec) return;
        StopRecording();
        ShowError("MxCam - 녹화 오류", Trim(msg, 1500));
    }

    async void OnTimeLimit(Recorder rec)
    {
        if (_rec != rec) return;
        var action = _s.TimeLimitAction;
        await StopRecordingAsync(restart: action == LimitAction.Restart);
        switch (action)
        {
            case LimitAction.Exit:
                Close();
                break;
            case LimitAction.Shutdown:
                // 1분 유예 후 종료 (취소: 명령 프롬프트에서 shutdown /a)
                if (!TestMode) Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false });
                LastShutdownRequested = true;
                _lblState.Text = "녹화 완료 — 1분 후 시스템 종료 (취소: shutdown /a)";
                break;
        }
    }

    string NewFilePath(string ext)
    {
        string baseName = $"{_s.FilePrefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(_s.OutputDir, $"{baseName}.{ext}");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(_s.OutputDir, $"{baseName}_{i}.{ext}");
        return path;
    }

    /// <summary>UI 자동 테스트 중에는 메시지 상자 대신 기록만 한다.</summary>
    internal bool TestMode { get; set; }
    internal bool LastShutdownRequested { get; private set; }
    internal List<string> Errors { get; } = new();

    void ShowError(string title, string msg)
    {
        Errors.Add(msg);
        if (!TestMode) MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void TogglePause()
    {
        if (_rec == null) return;
        bool p = !_rec.IsPaused;
        _rec.SetPaused(p);
        _frame.IsPaused = p;
        _btnPause.Text = p ? "재개" : "일시정지";
        _btnPause.Image = p ? Glyphs.Record : Glyphs.Pause;
        UpdateStatus();
    }

    void TakeScreenshot()
    {
        try
        {
            string ext = _s.ScreenshotFormat is "jpg" or "bmp" ? _s.ScreenshotFormat : "png";
            string path = NewFilePath(ext);
            Recorder.Screenshot(_frame.CaptureRect, _s.RecordCursor, path);
            _lastSaved = path;
            if (_rec == null) _lblState.Text = $"캡처 저장: {Path.GetFileName(path)}";
            else _hint.Text = $"📷 캡처 저장: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ShowError("MxCam", "캡처 실패: " + ex.Message);
        }
    }

    // ───────────── 상태 표시 ─────────────

    void UpdateStatus()
    {
        if (_rec != null && !_stopping)
        {
            var t = TimeSpan.FromSeconds(_rec.Seconds);
            string state = _rec.IsPaused ? "⏸ 일시정지" : "● 녹화 중";
            _lblState.Text = $"{state}  {t:hh\\:mm\\:ss}";
            _lblSize.Text = FormatSize(_rec.OutputBytes);
            _frame.OverlayText = _rec.IsPaused ? "일시정지" : "";
            return;
        }
        if (_rec == null && !_stopping)
        {
            var r = _frame.CaptureRect;
            _lblSize.Text = _s.Mode == RecordMode.Audio ? "" : $"{r.Width & ~1}×{r.Height & ~1} · {_s.Fps}fps";
        }
    }

    void UpdateHint()
    {
        var r = _frame.CaptureRect;
        var p = ResolveProfile(r);
        string mode = _s.Mode switch { RecordMode.Audio => "소리만", RecordMode.Gif => "GIF", _ => "" };
        string codecName = p.AudioOnly ? p.Name : _s.CodecId == Codecs.AutoId && _s.Mode == RecordMode.Screen ? p.Name : p.VideoInfo!.Label;
        if (_rec == null) _lblCodec.Text = $"{(mode.Length > 0 ? mode + " · " : "")}{codecName} · {_s.Level}단계";
        string sound = _s.SystemAudio && _s.Microphone ? "시스템+마이크" : _s.SystemAudio ? "시스템 소리" : _s.Microphone ? "마이크" : "소리 없음";
        _hint.Text = (_s.Mode == RecordMode.Gif ? "" : $"🔊 {sound} · ") + Codecs.Hint(_s, p, r.Width & ~1, r.Height & ~1, _s.Fps);
        UpdateStatus();
    }

    static string FormatSize(long b) => b < 1024 * 1024 ? $"{b / 1024.0:0} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1073741824.0:0.00} GB";
    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    void OpenOutputFolder()
    {
        if (TestMode) return;
        Directory.CreateDirectory(_s.OutputDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_s.OutputDir}\"") { UseShellExecute = true });
    }

    void OpenInExplorer(string file)
    {
        if (!TestMode && File.Exists(file)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }

    // ───────────── 대화상자 ─────────────

    void ShowRecommend()
    {
        if (_rec != null) return;
        if (_ffmpeg == null) { MessageBox.Show(this, "ffmpeg 경로를 먼저 지정하세요.", "MxCam"); return; }
        using var dlg = new RecommendForm(_s, _ffmpeg, _frame.CaptureRect);
        TopMost = false;
        dlg.ShowDialog(this);
        TopMost = true;
        _s.Save();
        UpdateHint();
    }

// ───────────── 자동 업데이트 ─────────────

    async Task CheckUpdateAsync()
    {
        try
        {
            if (!Updater.HasPending)
            {
                var u = await Updater.CheckAsync();
                if (u == null || !await Updater.DownloadAsync(u)) return;
                _lblUpdate.Text = $"🔄 {u.Tag} 업데이트 준비됨 — 클릭하면 재시작";
                _updateTag = u.Tag;
                _updateNotes = u.Notes;
            }
            else _lblUpdate.Text = "🔄 새 버전 준비됨 — 클릭하면 재시작";
            _lblUpdate.ToolTipText = "지금 재시작하지 않아도 다음 실행 때 자동으로 적용됩니다.";
            _lblUpdate.Visible = true;
        }
        catch { /* 오프라인 등 → 조용히 무시 */ }
        if (_lblUpdate.Visible && !IsDisposed)
        {
            _updatePromptPending = true;
            MaybePromptUpdate();
        }
    }

    string? _updateTag, _updateNotes;
    bool _updatePromptPending;

    /// <summary>
    /// 업데이트 준비가 끝나면 팝업으로 알림 (실행당 한 번). 녹화 중이거나 다른 확인창(ffmpeg 설치)이 떠 있으면
    /// 그것이 끝난 뒤에 띄운다. "나중에"를 고르면 상태 줄 링크로 남고 다음 실행 때 자동 적용.
    /// </summary>
    void MaybePromptUpdate()
    {
        if (!_updatePromptPending || IsDisposed || TestMode) return;
        if (_rec != null || _stopping || _installingFfmpeg) return; // 끝난 뒤 다시 호출됨
        if (!Updater.HasPending) { _updatePromptPending = false; return; }
        _updatePromptPending = false;

        string ver = _updateTag != null ? $"새 버전 {_updateTag}" : "새 버전";
        // 마크다운 굵게 표시·자동 생성된 "Full Changelog" 링크 줄은 빼고 보여 준다
        string body = string.Join("\n", (_updateNotes ?? "").Replace("\r", "").Split('\n')
            .Where(l => !l.Contains("Full Changelog", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Replace("**", ""))).Trim();
        string notes = body.Length == 0 ? "" : "\n\n변경 내용:\n" + Trim(body, 600);
        TopMost = false;
        var ans = MessageBox.Show(this,
            $"{ver}이(가) 준비되었습니다. (현재 {AppInfo.VersionText})" + notes + "\n\n지금 재시작해서 업데이트할까요?\n[아니요]를 누르면 다음 실행 때 자동으로 적용됩니다.",
            "MxCam - 업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        TopMost = true;
        if (ans == DialogResult.Yes) RestartForUpdate();
    }

    void RestartForUpdate()
    {
        if (_rec != null || !Updater.HasPending) return;
        if (Updater.ApplyPendingAndRestart(Array.Empty<string>())) Close();
    }

/// <summary>우클릭 메뉴 등록 상태를 설정에 맞춤 (실행 파일 위치가 바뀌어도 경로 갱신).</summary>
    void ApplyShellMenu()
    {
        try
        {
            if (_s.ShellMenu) ShellIntegration.Register(Environment.ProcessPath!);
            else ShellIntegration.Unregister();
        }
        catch { }
    }

    internal PcAnalysisForm? ShowPcAnalysis(bool automatic)
    {
        if (_rec != null || _ffmpeg == null) return null;
        var f = new PcAnalysisForm(_s, _ffmpeg, automatic);
        f.FormClosed += (_, _) => { _s.Save(); UpdateHint(); };
        f.Show(this);
        return f;
    }

    void ShowConvert()
    {
        if (_rec != null) return;
        if (_ffmpeg == null) { MessageBox.Show(this, "ffmpeg 경로를 먼저 지정하세요.", "MxCam"); return; }
        using var dlg = new ConvertForm(_s, _ffmpeg);
        TopMost = false;
        dlg.ShowDialog(this);
        TopMost = true;
        _s.Save();
    }

    void ShowOptions()
    {
        if (_rec != null) return;
        using var dlg = new OptionsForm(_s, _ffmpeg);
        TopMost = false;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _s.Save();
            _ffmpeg = Ffmpeg.Resolve(_s.FfmpegPath);
            if (_ffmpeg != null && (!Codecs.Probed || Codecs.ProbeFailed)) Task.Run(() => Codecs.Probe(_ffmpeg)).ContinueWith(_ => BeginInvoke(UpdateHint));
            RegisterHotkeys();
            UpdateTooltips();
            if (!TestMode) ApplyShellMenu();
            if (!TestMode && _ffmpeg != null)
            {
                // 처음 한 번만 자동 분석 + 설명. 이후엔 PC 구성이 바뀌어도 알림만 (옵션에서 다시 분석)
                if (_s.PcFingerprint == null) ShowPcAnalysis(automatic: true);
                else if (PcAnalyzer.Collect(_ffmpeg).Fingerprint != _s.PcFingerprint)
                    _lblState.Text = "PC 구성이 바뀌었습니다 — [옵션] → [내 PC 다시 분석]을 권장";
            }
            AttachToFrame();
            UpdateHint();
        }
        TopMost = true;
    }

    // ───────────── 단축키 ─────────────

    void RegisterHotkeys()
    {
        for (int i = 1; i <= 3; i++) Native.UnregisterHotKey(Handle, i);
        var failed = new List<string>();
        void Reg(int id, Keys k)
        {
            if (k == Keys.None) return;
            if (!Native.RegisterHotKey(Handle, id, Native.MOD_NOREPEAT, (uint)k)) failed.Add(k.ToString());
        }
        Reg(1, _s.RecordKey);
        Reg(2, _s.PauseKey);
        Reg(3, _s.CaptureKey);
        if (failed.Count > 0)
            _lblState.Text = $"단축키 {string.Join(", ", failed)} 등록 실패 (다른 프로그램이 사용 중 — oCam 등)";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            switch ((int)m.WParam)
            {
                case 1: ToggleRecord(); break;
                case 2: TogglePause(); break;
                case 3: TakeScreenshot(); break;
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (TestMode && !_testDone)
        {
            // 테스트 중 "종료" 동작은 기록만 하고 창은 유지 (테스트가 끝까지 실행되도록)
            _closingForTest = true;
            e.Cancel = true;
            return;
        }
        if (_rec != null)
        {
            if (!TestMode && MessageBox.Show(this, "녹화 중입니다. 녹화를 저장하고 종료할까요?", "MxCam", MessageBoxButtons.OKCancel) != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
            var r = _rec.Stop();
            _rec.Dispose();
            _rec = null;
            if (!r.Ok) MessageBox.Show(Trim(r.Message, 1000), "MxCam - 저장 실패");
        }
        for (int i = 1; i <= 3; i++) Native.UnregisterHotKey(Handle, i);
        _s.Region = _frame.CaptureRect;
        _s.ToolbarX = Location.X;
        _s.ToolbarY = Location.Y;
        _s.Save();
        _frame.Close();
        base.OnFormClosing(e);
    }
}

/// <summary>Segoe Fluent Icons(없으면 MDL2) 글리프로 툴바 아이콘 생성 — 외부 이미지 파일 불필요.</summary>
internal static class Glyphs
{
    static readonly string FontName = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static readonly Image Record = Make('', Color.FromArgb(220, 38, 38));
    public static readonly Image Stop = Make('', Color.FromArgb(220, 38, 38));
    public static readonly Image Pause = Make('', Color.FromArgb(60, 60, 60));
    public static readonly Image Camera = Make('', Color.FromArgb(60, 60, 60));
    public static readonly Image Resize = Make('', Color.FromArgb(60, 60, 60));
    public static readonly Image Folder = Make('', Color.FromArgb(202, 138, 4));
    public static readonly Image Codec = Make('', Color.FromArgb(37, 99, 235));
    public static readonly Image Sound = Make('', Color.FromArgb(60, 60, 60));
    public static readonly Image Mode = Make('', Color.FromArgb(60, 60, 60));
    public static readonly Image Convert = Make('', Color.FromArgb(22, 163, 74));
    public static readonly Image Star = Make('', Color.FromArgb(234, 179, 8));
    public static readonly Image Settings = Make('', Color.FromArgb(60, 60, 60));

    static Image Make(char glyph, Color color, int size = 24)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(FontName, size * 0.62f, GraphicsUnit.Pixel);
        using var br = new SolidBrush(color);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph.ToString(), font, br, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }

}

/// <summary>
/// 창이 비활성일 때도 첫 클릭이 바로 버튼에 전달되는 툴바.
/// (기본 ToolStrip은 WM_MOUSEACTIVATE에 MA_ACTIVATEANDEAT를 돌려줘 첫 클릭을 창 활성화에만 쓰고 버림)
/// </summary>
internal sealed class ClickThroughToolStrip : ToolStrip
{
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x21 && m.Result == (IntPtr)2) m.Result = (IntPtr)1; // WM_MOUSEACTIVATE: MA_ACTIVATEANDEAT → MA_ACTIVATE
    }
}

internal sealed class ClickThroughStatusStrip : StatusStrip
{
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x21 && m.Result == (IntPtr)2) m.Result = (IntPtr)1;
    }
}

internal sealed class SizeDialog : Form
{
    readonly NumericUpDown _w = new() { Minimum = 64, Maximum = 7680, Width = 90 };
    readonly NumericUpDown _h = new() { Minimum = 48, Maximum = 4320, Width = 90 };
    public int W => (int)_w.Value;
    public int H => (int)_h.Value;

    public SizeDialog(int w, int h)
    {
        Text = "사용자 지정 크기";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("맑은 고딕", 9f);
        _w.Value = Math.Clamp(w, 64, 7680);
        _h.Value = Math.Clamp(h, 48, 4320);
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel };
        var p = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(10), WrapContents = false };
        p.Controls.AddRange(new Control[] { new Label { Text = "가로", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _w,
            new Label { Text = "세로", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _h, ok, cancel });
        Controls.Add(p);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
