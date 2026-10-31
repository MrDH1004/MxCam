namespace MxCam;

/// <summary>
/// 코덱 추천 창: 5단계 품질 + 이 PC 인코더별 용량/화질/속도 비교 + 실측 테스트로 최적 코덱 추천.
/// </summary>
internal sealed class RecommendForm : Form
{
    readonly Settings _s;
    readonly string _ffmpeg;
    readonly Rectangle _region;
    readonly Label _title = new() { AutoSize = false, Height = 54, Dock = DockStyle.Top, Padding = new Padding(10, 8, 10, 0) };
    readonly TrackBar _level = new() { Minimum = 1, Maximum = 5, TickStyle = TickStyle.BottomRight, Width = 260, LargeChange = 1 };
    readonly Label _levelName = new() { AutoSize = true, Margin = new Padding(8, 12, 3, 3) };
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    readonly Label _progress = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10, 4, 10, 0) };
    readonly Button _btnBench = new() { Text = "실측 테스트 (4초)", AutoSize = true };
    readonly Button _btnApply = new() { Text = "추천 코덱으로 자동 설정", AutoSize = true };
    readonly Button _btnUse = new() { Text = "선택한 코덱 사용", AutoSize = true };
    readonly Button _btnClose = new() { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
    CancellationTokenSource? _cts;

    public RecommendForm(Settings s, string ffmpeg, Rectangle region)
    {
        _s = s;
        _ffmpeg = ffmpeg;
        _region = region;
        Text = "MxCam - 코덱 추천 (용량은 작게, 화질은 좋게)";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(920, 560);
        MinimumSize = new Size(760, 420);
        Font = new Font("맑은 고딕", 9f);
        _title.Font = new Font("맑은 고딕", 12f, FontStyle.Bold);

        var levelRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(6, 4, 6, 0), WrapContents = false };
        levelRow.Controls.Add(new Label { Text = "인코딩 품질 단계 (자동 5단계):", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        levelRow.Controls.Add(_level);
        levelRow.Controls.Add(_levelName);
        _level.Value = Math.Clamp(s.Level, 1, 5);
        _level.ValueChanged += (_, _) => { _s.Level = _level.Value; Refill(); };

        _list.Columns.Add("코덱", 200);
        _list.Columns.Add("용량 효율", 80);
        _list.Columns.Add("속도", 70);
        _list.Columns.Add("호환성", 70);
        _list.Columns.Add("예상 MB/분", 85, HorizontalAlignment.Right);
        _list.Columns.Add("실측 MB/분", 85, HorizontalAlignment.Right);
        _list.Columns.Add("실측 화질", 75, HorizontalAlignment.Right);
        _list.Columns.Add("같은 화질 환산", 105, HorizontalAlignment.Right);
        _list.Columns.Add("실측 속도", 70, HorizontalAlignment.Right);
        _list.Columns.Add("판정", 150);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { _btnClose, _btnUse, _btnApply, _btnBench });

        Controls.Add(_list);
        Controls.Add(_progress);
        Controls.Add(buttons);
        Controls.Add(levelRow);
        Controls.Add(_title);
        CancelButton = _btnClose;

        _btnBench.Click += async (_, _) => await RunBench();
        _btnApply.Click += (_, _) =>
        {
            _s.CodecId = Codecs.AutoId;
            if (_s.Mode == RecordMode.Gif) _s.Mode = RecordMode.Screen;
            _progress.Text = $"자동 모드로 설정됨 → {Codecs.Recommended(_s, W, H, _s.Fps).Label}, {Codecs.LevelNames[_s.Level - 1]}";
            Refill();
        };
        _btnUse.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not VKind k) return;
            var p = Codecs.All.FirstOrDefault(p => p.Video == k && p.Ext == "mp4" && Codecs.IsAvailable(p))
                    ?? Codecs.All.FirstOrDefault(p => p.Video == k && Codecs.IsAvailable(p));
            if (p == null) return;
            _s.CodecId = p.Id;
            if (_s.Mode == RecordMode.Gif) _s.Mode = RecordMode.Screen;
            _progress.Text = "코덱 설정됨: " + p.Name;
        };
        FormClosing += (_, _) => _cts?.Cancel();
        Refill();
    }

    int W => _region.Width & ~1;
    int H => _region.Height & ~1;

    void Refill()
    {
        _levelName.Text = Codecs.LevelNames[_s.Level - 1];
        var rec = Codecs.Recommended(_s, W, H, _s.Fps);
        var bestBench = Recommender.Decide(_s.Bench, _s.Level);
        string basis = bestBench?.Kind == rec.Kind
            ? $"실측 결과 기준 ({_s.BenchAt:MM-dd HH:mm}) · 같은 화질에서 용량 최소"
            : "하드웨어/성능 기준 (실측 테스트를 하면 더 정확)";
        string auto = bestBench?.Kind == rec.Kind ? $" · 자동 녹화 시 보정 단계 {Codecs.AutoLevel(_s, rec.Kind):0.0}" : "";
        _title.Text = $"★ 추천: {rec.Label}\n   {basis} · {W}×{H} {_s.Fps}fps · {Codecs.LevelNames[_s.Level - 1]}{auto}";

        _list.BeginUpdate();
        _list.Items.Clear();
        int li = _s.Level - 1;
        var rows = Codecs.AllVideoInfos
            .Where(i => i.Kind is not (VKind.Gif or VKind.DivX or VKind.OpenH264) && Codecs.IsVideoAvailable(i.Kind))
            .OrderByDescending(i => i.Kind == rec.Kind).ThenByDescending(i => i.Efficiency).ThenByDescending(i => i.Hardware);
        foreach (var i in rows)
        {
            var b = _s.Bench.FirstOrDefault(x => x.Kind == i.Kind);
            string verdict = i.Kind == rec.Kind ? "★ 추천" : "";
            if (b?.Error != null) verdict = "실측 실패";
            else if (b != null && !b.Realtime) verdict = "실시간 녹화엔 느림";
            else if (b != null && b.State > 0) verdict = "5단계도 기준 화질 미달";
            else if (b != null && b.State < 0 && verdict == "") verdict = "최저 단계도 고화질 (용량 상한)";
            else if (verdict == "" && i.Speed <= 2 && !i.Hardware) verdict = "CPU 부담 큼";
            else if (verdict == "" && i.Compat <= 3) verdict = i.Compat <= 2 ? "구형 기기 재생 어려움" : "일부 플레이어 코덱 필요";
            var item = new ListViewItem(new[]
            {
                i.Label + (i.Hardware ? "  (GPU)" : ""),
                Codecs.Stars((i.Efficiency + 1) / 2),
                Codecs.Stars(i.Speed),
                Codecs.Stars(i.Compat),
                Codecs.EstimateMBPerMin(_s, i, _s.Level, W, H, _s.Fps).ToString("0.0"),
                b == null || b.Error != null ? "-" : b.Mb[li].ToString("0.00"),
                b == null || b.Error != null ? "-" : (b.Ssim[li] * 100).ToString("0.00") + "%",
                b == null || b.Error != null || b.MBAtTarget <= 0 ? "-" : (b.State < 0 ? "≤ " : "") + b.MBAtTarget.ToString("0.00") + " MB/분",
                b == null || b.Error != null ? "-" : $"x{b.Speed:0.0}",
                verdict,
            }) { Tag = i.Kind };
            if (i.Kind == rec.Kind)
            {
                item.Font = new Font(_list.Font, FontStyle.Bold);
                item.BackColor = Color.FromArgb(255, 249, 219);
            }
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (string.IsNullOrEmpty(_progress.Text))
            _progress.Text = "실측 화질 = 원본 대비 SSIM (100%가 원본과 동일). 실측은 지금 녹화 영역의 화면으로 측정하므로, 평소 녹화할 화면(게임·영상·문서)을 띄운 채 실행하세요.";
    }

    async Task RunBench()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        _btnBench.Text = "취소";
        _btnApply.Enabled = _btnUse.Enabled = false;
        var progress = new Progress<string>(m => _progress.Text = m);
        try
        {
            var results = await Recommender.RunAsync(_s, _ffmpeg, _region, 4, progress, _cts.Token);
            _s.Bench = results;
            _s.BenchAt = DateTime.Now;
            _s.BenchPixelRate = (long)W * H * _s.Fps;
            var best = Recommender.Decide(results, _s.Level);
            _progress.Text = best == null
                ? "실시간 녹화가 가능한 코덱을 찾지 못했습니다. 녹화 영역이나 FPS를 줄여 보세요."
                : $"완료 → {best.Label}: x264와 같은 화질로 맞췄을 때 용량이 가장 작음 (환산 {best.MBAtTarget:0.00} MB/분). '추천 코덱으로 자동 설정'을 누르면 적용됩니다.";
            _s.Save();
        }
        catch (OperationCanceledException)
        {
            _progress.Text = "취소됨";
        }
        catch (Exception ex)
        {
            _progress.Text = "실측 실패: " + ex.Message.Split('\n')[0];
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                _btnBench.Text = "실측 테스트 (4초)";
                _btnApply.Enabled = _btnUse.Enabled = true;
                Refill();
            }
        }
    }
}
