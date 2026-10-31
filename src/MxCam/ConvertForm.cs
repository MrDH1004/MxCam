namespace MxCam;

/// <summary>
/// 동영상 변환: 파일 선택(또는 끌어다 놓기) → 원본 분석 → 추천 형식 자동 선택 → 6초 샘플로 예상 용량·화질 확인 → 변환.
/// </summary>
internal sealed class ConvertForm : Form
{
    readonly Settings _s;
    readonly string _ffmpeg;
    readonly Label _drop = new()
    {
        Text = "여기에 동영상을 끌어다 놓거나 [파일 선택]을 누르세요", Dock = DockStyle.Top, Height = 54,
        TextAlign = ContentAlignment.MiddleCenter, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(246, 248, 252),
    };
    readonly Button _btnOpen = new() { Text = "파일 선택…", AutoSize = true };
    readonly Label _src = new() { AutoSize = false, Height = 40, Dock = DockStyle.Top, Padding = new Padding(4, 6, 4, 0) };
    readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    readonly TrackBar _level = new() { Minimum = 1, Maximum = 5, Width = 200, TickStyle = TickStyle.BottomRight, LargeChange = 1 };
    readonly Label _levelName = new() { AutoSize = true, Margin = new Padding(6, 12, 3, 3) };
    readonly Label _reason = new() { AutoSize = false, Height = 64, Dock = DockStyle.Top, Padding = new Padding(4, 4, 4, 0), ForeColor = Color.FromArgb(30, 90, 170) };
    readonly Label _estimate = new() { AutoSize = false, Height = 62, Dock = DockStyle.Top, Padding = new Padding(4, 2, 4, 0), Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };
    readonly TextBox _out = new() { Width = 470 };
    readonly CheckBox _autoStart = new() { Text = "분석이 끝나면 추천대로 바로 변환", AutoSize = true };
    readonly ProgressBar _bar = new() { Dock = DockStyle.Bottom, Height = 18 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(4, 3, 4, 0) };
    internal Button ConvertButton { get; } = new() { Text = "추천 형식으로 변환", AutoSize = true, Enabled = false, Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };

    MediaInfo? _info;
    ConvertPlan? _plan;
    CancellationTokenSource? _sampleCts, _convertCts;
    bool _filling;
    int _sampleSeq;

    internal string? LastOutput { get; private set; }
    internal SampleResult? LastSample { get; private set; }
    internal ConvertPreset? RecommendedPreset { get; private set; }
    internal event Action<bool>? ConvertFinished;
    internal event Action? SampleFinished;

    public ConvertForm(Settings s, string ffmpeg)
    {
        _s = s;
        _ffmpeg = ffmpeg;
        Text = "MxCam - 동영상 변환 (포맷 추천)";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 500);
        MinimumSize = new Size(680, 440);
        Font = new Font("맑은 고딕", 9f);
        AllowDrop = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        foreach (var (_, name) in Converter.Presets) _preset.Items.Add(name);
        _level.Value = Math.Clamp(s.Level, 1, 5);
        _autoStart.Checked = s.AutoConvert;

        var row1 = Flow(_btnOpen, _autoStart);
        var row2 = Flow(Lbl("변환 형식"), _preset);
        var row3 = Flow(Lbl("품질 단계"), _level, _levelName);
        var browse = new Button { Text = "…", Width = 32 };
        var row4 = Flow(Lbl("저장 위치"), _out, browse);
        var row5 = Flow(ConvertButton);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6) };
        top.Controls.AddRange(new Control[] { row1, row2, row3, row4, row5 });

        Controls.Add(_estimate);
        Controls.Add(_reason);
        Controls.Add(top);
        Controls.Add(_src);
        Controls.Add(_drop);
        Controls.Add(_status);
        Controls.Add(_bar);

        _btnOpen.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "동영상/소리|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.vob;*.3gp;*.mp3;*.wav;*.flac;*.m4a;*.wma;*.aac|모든 파일|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) _ = LoadFileAsync(d.FileName);
        };
        browse.Click += (_, _) =>
        {
            using var d = new SaveFileDialog { FileName = Path.GetFileName(_out.Text), InitialDirectory = Path.GetDirectoryName(_out.Text) };
            if (d.ShowDialog(this) == DialogResult.OK) _out.Text = d.FileName;
        };
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = LoadFileAsync(files[0]);
        };
        _preset.SelectedIndexChanged += (_, _) => { if (!_filling) Replan(); };
        _level.ValueChanged += (_, _) => { if (!_filling) Replan(); };
        _autoStart.CheckedChanged += (_, _) => _s.AutoConvert = _autoStart.Checked;
        ConvertButton.Click += async (_, _) => await ConvertAsync();
        FormClosing += (_, _) => { _sampleCts?.Cancel(); _convertCts?.Cancel(); };
        UpdateLevelName();
    }

    static Label Lbl(string t) => new() { Text = t, AutoSize = true, Width = 70, Margin = new Padding(3, 7, 6, 3) };

    static FlowLayoutPanel Flow(params Control[] cs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(cs);
        return p;
    }

    void UpdateLevelName() => _levelName.Text = Codecs.LevelNames[_level.Value - 1];

    internal async Task LoadFileAsync(string file)
    {
        if (_convertCts != null) return;
        _status.Text = "분석 중…";
        try
        {
            _info = await Task.Run(() => MediaInfo.Probe(_ffmpeg, file));
        }
        catch (Exception ex)
        {
            _info = null;
            _src.Text = "읽을 수 없는 파일: " + ex.Message.Split('\n')[0];
            _status.Text = "";
            ConvertButton.Enabled = false;
            return;
        }
        _drop.Text = Path.GetFileName(file);
        _src.Text = "원본: " + _info.Summary;
        var rec = Converter.Recommend(_info, _s, out var reason);
        RecommendedPreset = rec;
        _filling = true;
        _preset.SelectedIndex = Array.FindIndex(Converter.Presets, p => p.preset == rec);
        _filling = false;
        _reason.Text = "💡 " + reason;
        Replan(autoStart: _autoStart.Checked);
    }

    void Replan(bool autoStart = false)
    {
        UpdateLevelName();
        if (_info == null) return;
        var preset = Converter.Presets[_preset.SelectedIndex].preset;
        if (!_info.HasVideo && preset is not (ConvertPreset.Mp3 or ConvertPreset.Remux)) preset = ConvertPreset.Mp3;
        _plan = Converter.Plan(preset, _info, _s, _level.Value);
        _out.Text = Converter.DefaultOutput(_info, _plan);
        ConvertButton.Text = preset == RecommendedPreset ? "추천 형식으로 변환" : "변환 시작";
        bool alreadyFine = preset == ConvertPreset.Remux && _info.Container.Contains("mp4");
        ConvertButton.Enabled = !alreadyFine;
        if (alreadyFine)
        {
            _estimate.Text = "원본이 이미 최적 상태 — 변환할 필요가 없습니다.";
            _status.Text = "";
            return;
        }
        _ = RunSampleAsync(autoStart);
    }

    async Task RunSampleAsync(bool autoStart)
    {
        _sampleCts?.Cancel();
        var cts = _sampleCts = new CancellationTokenSource();
        int seq = ++_sampleSeq;
        var info = _info!;
        var plan = _plan!;
        _estimate.Text = $"→ {plan.Title}: 6초 샘플로 예상 용량·화질 측정 중…";
        try
        {
            var r = await Task.Run(() => Converter.Sample(_ffmpeg, info, plan, cts.Token), cts.Token);
            if (seq != _sampleSeq || IsDisposed) return;
            LastSample = r;
            if (r.Error != null)
            {
                _estimate.Text = "샘플 변환 실패: " + r.Error;
            }
            else
            {
                double srcMB = info.Size / 1048576.0;
                double pct = (1 - r.EstimatedMB / srcMB) * 100;
                string size = pct >= 0 ? $"{pct:0}% 작아짐" : $"{-pct:0}% 커짐";
                string quality = plan.Preset is ConvertPreset.Mp3 or ConvertPreset.Gif ? "" :
                    plan.Preset == ConvertPreset.Remux ? " · 화질 원본 그대로" : $" · 화질 원본 대비 {r.Ssim * 100:0.0}%{(r.Ssim >= 0.97 ? " (눈으로 구분 어려움)" : r.Ssim >= 0.94 ? " (양호)" : " (손실 보임 — 품질 단계를 올리세요)")}";
                string speed = r.Speed > 0 ? $" · 예상 시간 {TimeSpan.FromSeconds(info.Duration / r.Speed):m\\:ss}" : "";
                _estimate.Text = $"→ {plan.Title}: 예상 {r.EstimatedMB:0.0} MB (원본 {srcMB:0.0} MB, {size}){quality}{speed}";
                if (pct < 3 && plan.Preset == ConvertPreset.Recommended)
                    _estimate.Text += "\n   ※ 줄어드는 용량이 거의 없습니다. 'MP4로 포장만 변경'이나 원본 유지를 권장합니다.";
            }
            _status.Text = "";
            SampleFinished?.Invoke();
            if (autoStart && r.Error == null && ConvertButton.Enabled) await ConvertAsync();
        }
        catch (OperationCanceledException) { }
    }

    internal async Task ConvertAsync()
    {
        if (_info == null || _plan == null) return;
        if (_convertCts != null) { _convertCts.Cancel(); return; }
        string output = _out.Text.Trim();
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_info.Path), StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "원본 파일에 덮어쓸 수 없습니다. 저장 위치를 바꾸세요.";
            return;
        }
        _sampleCts?.Cancel();
        _convertCts = new CancellationTokenSource();
        ConvertButton.Text = "취소";
        _btnOpen.Enabled = _preset.Enabled = _level.Enabled = false;
        _bar.Value = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<double>(v =>
        {
            if (_convertCts == null) return; // 완료 후 늦게 도착한 진행률은 무시
            _bar.Value = (int)(v * 1000 / 10);
            var eta = v > 0.02 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / v * (1 - v)) : (TimeSpan?)null;
            _status.Text = $"변환 중… {v * 100:0}%{(eta != null ? $" · 남은 시간 약 {eta:m\\:ss}" : "")}";
        });
        var (ok, msg) = await Converter.RunAsync(_ffmpeg, _info, _plan, output, progress, _convertCts.Token);
        _convertCts.Dispose();
        _convertCts = null;
        if (IsDisposed) return;
        _btnOpen.Enabled = _preset.Enabled = _level.Enabled = true;
        ConvertButton.Text = _preset.SelectedIndex >= 0 && Converter.Presets[_preset.SelectedIndex].preset == RecommendedPreset ? "추천 형식으로 변환" : "변환 시작";
        if (ok)
        {
            LastOutput = output;
            long outSize = new FileInfo(output).Length;
            double pct = (1 - outSize / (double)_info.Size) * 100;
            _status.Text = $"완료: {Path.GetFileName(output)} — {outSize / 1048576.0:0.0} MB ({(pct >= 0 ? $"{pct:0}% 작아짐" : $"{-pct:0}% 커짐")}, {sw.Elapsed:m\\:ss} 소요)";
            _out.Text = Converter.DefaultOutput(_info, _plan);
        }
        else
        {
            _bar.Value = 0;
            _status.Text = msg.Split('\n')[0];
            if (msg.Contains('\n')) _estimate.Text = msg.Length > 300 ? msg[..300] : msg;
        }
        ConvertFinished?.Invoke(ok);
    }
}
