namespace MxCam;

/// <summary>옵션 창 (oCam 옵션 구성 기준: 저장/비디오/오디오/효과/시간 제한/단축키).</summary>
internal sealed class OptionsForm : Form
{
    readonly Settings _s;
    // 일반
    readonly TextBox _out = new() { Width = 300 };
    readonly TextBox _prefix = new() { Width = 140 };
    readonly TextBox _ff = new() { Width = 300, PlaceholderText = "비워 두면 자동으로 찾음" };
    readonly ComboBox _shot = Combo(90, "png", "jpg", "bmp");
    readonly CheckBox _openAfter = Check("녹화 완료 후 저장 폴더 열기");
    readonly CheckBox _minimize = Check("녹화 중에는 메인 창 최소화");
    readonly CheckBox _attach = Check("녹화 영역 이동 시 메인 창도 같이 이동");
    readonly CheckBox _autoUpdate = Check("시작할 때 새 버전 자동 확인·업데이트");
    readonly CheckBox _shellMenu = Check("탐색기 우클릭 메뉴: 비디오 파일 \"MxCam: 용량 최적화 변환\"");
    // 비디오
    readonly NumericUpDown _fps = new() { Minimum = 1, Maximum = 60, Width = 70 };
    readonly ComboBox _level = Combo(200);
    readonly CheckBox _cursor = Check("녹화 시 커서 포함");
    // 오디오
    readonly ComboBox _rate = Combo(110, "장치 원본", "44100 Hz", "48000 Hz");
    readonly ComboBox _channels = Combo(110, "모노", "스테레오");
    // 효과
    readonly CheckBox _click = Check("마우스 클릭 효과 (왼쪽 노랑 / 오른쪽 파랑)");
    readonly TextBox _wm = new() { Width = 300, PlaceholderText = "PNG 이미지 (비우면 사용 안 함)" };
    readonly NumericUpDown _wmOpacity = new() { Minimum = 0, Maximum = 100, Width = 60 };
    readonly ComboBox _wmPos = Combo(100, "오른쪽 아래", "왼쪽 아래", "오른쪽 위", "왼쪽 위", "가운데");
    // 시간 제한
    readonly NumericUpDown _limitMin = new() { Minimum = 0, Maximum = 1440, Width = 60 };
    readonly NumericUpDown _limitSec = new() { Minimum = 0, Maximum = 59, Width = 50 };
    readonly ComboBox _limitAction = Combo(150, "아무 작업하지 않음", "새 녹화 시작", "프로그램 종료", "시스템 종료");
    // 단축키
    readonly ComboBox _kRec = KeyBox(), _kPause = KeyBox(), _kCap = KeyBox();

    internal Button OkButton { get; } = new() { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
    internal Button ResetButton { get; } = new() { Text = "기본값으로 되돌리기", AutoSize = true };

    static ComboBox Combo(int width, params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
        c.Items.AddRange(items);
        return c;
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    static ComboBox KeyBox()
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        c.Items.Add(Keys.None);
        for (var k = Keys.F1; k <= Keys.F12; k++) c.Items.Add(k);
        c.Items.Add(Keys.Pause);
        c.Items.Add(Keys.Scroll);
        return c;
    }

    public OptionsForm(Settings s)
    {
        _s = s;
        Text = $"MxCam {AppInfo.VersionText} - 옵션";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("맑은 고딕", 9f);

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(19, 40, 79), Margin = new Padding(0, 10, 0, 2) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 2);
        }
        void Row(string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(12, 7, 10, 3) });
            t.Controls.Add(c);
        }
        Control Browse(TextBox box, Action onClick)
        {
            var b = new Button { Text = "…", Width = 32 };
            b.Click += (_, _) => onClick();
            return Flow(box, b);
        }
        Label Lbl(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

        Section("저장");
        Row("저장 폴더", Browse(_out, () =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _out.Text };
            if (d.ShowDialog(this) == DialogResult.OK) _out.Text = d.SelectedPath;
        }));
        Row("파일 이름", Flow(_prefix, Lbl("_날짜_시간.확장자")));
        Row("캡처 이미지 형식", _shot);
        Row("ffmpeg 경로", Browse(_ff, () =>
        {
            using var d = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일|*.exe" };
            if (d.ShowDialog(this) == DialogResult.OK) _ff.Text = d.FileName;
        }));
        Row("", _openAfter);
        Row("", _minimize);
        Row("", _attach);
        Row("", _autoUpdate);
        Row("", _shellMenu);

        Section("비디오");
        Row("초당 프레임 (FPS)", _fps);
        Row("품질 단계", _level);
        Row("", _cursor);

        Section("오디오");
        Row("샘플레이트", _rate);
        Row("채널", _channels);

        Section("효과");
        Row("", _click);
        Row("워터마크", Browse(_wm, () =>
        {
            using var d = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog(this) == DialogResult.OK) _wm.Text = d.FileName;
        }));
        Row("", Flow(Lbl("불투명도"), _wmOpacity, Lbl("%   위치"), _wmPos));

        Section("녹화 시간 제한");
        Row("제한 시간", Flow(_limitMin, Lbl("분"), _limitSec, Lbl("초  (0 = 제한 없음)")));
        Row("제한 후 동작", _limitAction);

        Section("단축키");
        Row("", Flow(Lbl("녹화"), _kRec, Lbl("일시정지"), _kPause, Lbl("캡처"), _kCap));

        var cancel = new Button { Text = "닫기", DialogResult = DialogResult.Cancel, AutoSize = true };
        ResetButton.Click += (_, _) => Fill(new Settings { FfmpegPath = _ff.Text });
        var buttons = Flow(OkButton, cancel, ResetButton);
        buttons.Margin = new Padding(0, 12, 0, 0);
        t.Controls.Add(new Label());
        t.Controls.Add(buttons);
        Controls.Add(t);
        AcceptButton = OkButton;
        CancelButton = cancel;

        _level.Items.AddRange(Codecs.LevelNames);
        Fill(s);
        OkButton.Click += (_, _) => Save();
    }

    static FlowLayoutPanel Flow(params Control[] cs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        p.Controls.AddRange(cs);
        return p;
    }

    void Fill(Settings s)
    {
        _out.Text = s.OutputDir;
        _prefix.Text = s.FilePrefix;
        _ff.Text = s.FfmpegPath;
        _shot.SelectedItem = s.ScreenshotFormat;
        if (_shot.SelectedIndex < 0) _shot.SelectedIndex = 0;
        _openAfter.Checked = s.OpenFolderAfterRecord;
        _minimize.Checked = s.MinimizeWhileRecording;
        _attach.Checked = s.AttachToolbar;
        _autoUpdate.Checked = s.AutoUpdate;
        _shellMenu.Checked = s.ShellMenu;
        _fps.Value = Math.Clamp(s.Fps, 1, 60);
        _level.SelectedIndex = Math.Clamp(s.Level, 1, 5) - 1;
        _cursor.Checked = s.RecordCursor;
        _rate.SelectedIndex = s.AudioSampleRate switch { 44100 => 1, 48000 => 2, _ => 0 };
        _channels.SelectedIndex = s.AudioChannels == 1 ? 0 : 1;
        _click.Checked = s.ClickEffect;
        _wm.Text = s.WatermarkPath;
        _wmOpacity.Value = Math.Clamp(s.WatermarkOpacity, 0, 100);
        _wmPos.SelectedIndex = (int)s.WatermarkPosition;
        _limitMin.Value = Math.Min(1440, s.TimeLimitSeconds / 60);
        _limitSec.Value = s.TimeLimitSeconds % 60;
        _limitAction.SelectedIndex = (int)s.TimeLimitAction;
        _kRec.SelectedItem = s.RecordKey;
        _kPause.SelectedItem = s.PauseKey;
        _kCap.SelectedItem = s.CaptureKey;
    }

    void Save()
    {
        _s.OutputDir = string.IsNullOrWhiteSpace(_out.Text) ? new Settings().OutputDir : _out.Text.Trim();
        var prefix = string.Concat(_prefix.Text.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        _s.FilePrefix = prefix.Length == 0 ? "MxCam" : prefix;
        _s.FfmpegPath = _ff.Text.Trim();
        _s.ScreenshotFormat = (string)_shot.SelectedItem!;
        _s.OpenFolderAfterRecord = _openAfter.Checked;
        _s.MinimizeWhileRecording = _minimize.Checked;
        _s.AttachToolbar = _attach.Checked;
        _s.AutoUpdate = _autoUpdate.Checked;
        _s.ShellMenu = _shellMenu.Checked;
        _s.Fps = (int)_fps.Value;
        _s.Level = _level.SelectedIndex + 1;
        _s.RecordCursor = _cursor.Checked;
        _s.AudioSampleRate = _rate.SelectedIndex switch { 1 => 44100, 2 => 48000, _ => 0 };
        _s.AudioChannels = _channels.SelectedIndex == 0 ? 1 : 2;
        _s.ClickEffect = _click.Checked;
        _s.WatermarkPath = _wm.Text.Trim();
        _s.WatermarkOpacity = (int)_wmOpacity.Value;
        _s.WatermarkPosition = (WatermarkPos)_wmPos.SelectedIndex;
        _s.TimeLimitSeconds = (int)_limitMin.Value * 60 + (int)_limitSec.Value;
        _s.TimeLimitAction = (LimitAction)_limitAction.SelectedIndex;
        _s.RecordKey = (Keys)_kRec.SelectedItem!;
        _s.PauseKey = (Keys)_kPause.SelectedItem!;
        _s.CaptureKey = (Keys)_kCap.SelectedItem!;
    }
}
