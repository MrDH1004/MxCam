namespace MxCam;

/// <summary>
/// 내 PC 분석: 사양 파악 → 하드웨어 인코더 확인 → 지금 화면 3초 실측 → 최적 코덱 추천.
/// 처음 실행 때와 그래픽카드·ffmpeg가 바뀌었을 때 자동으로 열린다.
/// </summary>
internal sealed class PcAnalysisForm : Form
{
    readonly Settings _s;
    readonly string _ffmpeg;
    readonly Label _head = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 8, 10, 0), Font = new Font("맑은 고딕", 11f, FontStyle.Bold), Text = "내 PC 분석 중…" };
    readonly ListView _spec = new() { View = View.Details, Dock = DockStyle.Top, Height = 132, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true };
    readonly Label _result = new() { Dock = DockStyle.Top, Height = 104, Padding = new Padding(10, 6, 10, 0), Font = new Font("맑은 고딕", 10f), ForeColor = Color.FromArgb(19, 40, 79) };
    readonly ListView _bench = new() { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 4, 8, 0) };
    readonly Button _btnAgain = new() { Text = "다시 분석", AutoSize = true, Enabled = false };
    readonly Button _btnClose = new() { Text = "닫기", AutoSize = true };
    CancellationTokenSource? _cts;

    internal bool Finished { get; private set; }
    internal VKind? BestKind { get; private set; }
    internal PcSpec? Spec { get; private set; }

    public PcAnalysisForm(Settings s, string ffmpeg, bool automatic)
    {
        _s = s;
        _ffmpeg = ffmpeg;
        Text = "MxCam - 내 PC 분석";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 600);
        MinimumSize = new Size(640, 520);
        Font = new Font("맑은 고딕", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _spec.Columns.Add("항목", 150);
        _spec.Columns.Add("내 PC", 560);
        _bench.Columns.Add("코덱", 210);
        _bench.Columns.Add("실측 MB/분", 90, HorizontalAlignment.Right);
        _bench.Columns.Add("화질", 70, HorizontalAlignment.Right);
        _bench.Columns.Add("같은 화질 환산", 110, HorizontalAlignment.Right);
        _bench.Columns.Add("속도", 60, HorizontalAlignment.Right);
        _bench.Columns.Add("판정", 160);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { _btnClose, _btnAgain });
        Controls.Add(_bench);
        Controls.Add(_result);
        Controls.Add(_spec);
        Controls.Add(_head);
        Controls.Add(_status);
        Controls.Add(buttons);

        if (automatic) _result.Text = "처음 실행이거나 PC 구성이 바뀌어서, 이 PC에 가장 맞는 녹화 코덱을 찾고 있습니다. (약 30초)";
        _btnClose.Click += (_, _) => Close();
        _btnAgain.Click += async (_, _) => await RunAsync();
        FormClosing += (_, _) => _cts?.Cancel();
        Shown += async (_, _) => await RunAsync();
    }

    void Spec_(string k, string v) => _spec.Items.Add(new ListViewItem(new[] { k, v }));

    internal async Task RunAsync()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        Finished = false;
        _btnAgain.Enabled = false;
        _bench.Items.Clear();
        _spec.Items.Clear();
        try
        {
            _status.Text = "사양 확인 중…";
            var pc = await Task.Run(() => PcAnalyzer.Collect(_ffmpeg));
            Spec = pc;
            Spec_("CPU", $"{pc.Cpu}  ({pc.Threads}스레드)");
            Spec_("메모리", $"{pc.RamGB:0.#} GB");
            Spec_("그래픽카드", pc.GpuText);
            Spec_("화면", pc.Display);

            _status.Text = "하드웨어 인코더 확인 중…";
            await Task.Run(() => Codecs.Probe(_ffmpeg), _cts.Token);
            string Hw(string name, params VKind[] k) => $"{name}: " + string.Join(" · ", k.Select(x => $"{Codecs.Video(x).Label.Split(' ').Last()} {(Codecs.IsVideoAvailable(x) ? "✓" : "✗")}"));
            Spec_("하드웨어 인코더", string.Join("   ", new[]
            {
                Hw("NVIDIA", VKind.Nvenc264, VKind.NvencHevc, VKind.NvencAv1),
                Hw("Intel", VKind.Qsv264, VKind.QsvHevc),
                Hw("AMD", VKind.Amf264, VKind.AmfHevc),
            }));
            Spec_("ffmpeg", _ffmpeg);

            // 녹화에 흔한 크기(최대 1920×1080)로 지금 화면을 3초 무손실 캡처해 후보별 실측
            var scr = Screen.PrimaryScreen!.Bounds;
            var region = new Rectangle(scr.X, scr.Y, Math.Min(1920, scr.Width), Math.Min(1080, scr.Height));
            var cand = PcAnalyzer.Candidates(pc);
            var progress = new Progress<string>(m => _status.Text = m);
            var results = await Recommender.RunAsync(_s, _ffmpeg, region, 3, progress, _cts.Token, cand);
            _s.Bench = results;
            _s.BenchAt = DateTime.Now;
            _s.BenchPixelRate = (long)region.Width * region.Height * _s.Fps;
            var best = Recommender.Decide(results, _s.Level);
            var bestInfo = best != null ? Codecs.Video(best.Kind) : Codecs.BestVideo(region.Width, region.Height, _s.Fps);
            BestKind = bestInfo.Kind;
            _s.PcFingerprint = pc.Fingerprint;
            _s.PcSummary = $"{pc.Cpu} · {pc.GpuText} · {pc.RamGB:0.#}GB";
            _s.PcBest = bestInfo.Kind;
            _s.Save();

            _head.Text = $"★ 이 PC 추천 코덱: {bestInfo.Label}";
            int hwCount = new[] { VKind.Nvenc264, VKind.NvencHevc, VKind.NvencAv1, VKind.Qsv264, VKind.QsvHevc, VKind.Amf264, VKind.AmfHevc }.Count(Codecs.IsVideoAvailable);
            _result.Text =
                $"어떻게 골랐나요?  ① 사양 파악: {pc.Cpu} · {pc.GpuText}\n" +
                $"② 하드웨어 인코더 {hwCount}개 사용 가능 확인   ③ 지금 화면 3초로 코덱 {results.Count}개를 같은 화질 기준 용량·속도 실측\n" +
                $"→ {PcAnalyzer.Explain(pc, bestInfo, results, _s.Level)}\n" +
                "자동 모드가 이 결과로 녹화합니다. 다시 분석하려면 [옵션] → [내 PC 다시 분석].";
            foreach (var r in results.OrderBy(r => r.Error != null).ThenBy(r => r.MBAtTarget <= 0 ? double.MaxValue : r.MBAtTarget))
            {
                int li = _s.Level - 1;
                string verdict = r.Kind == bestInfo.Kind ? "★ 추천" : r.Error != null ? "실패" : !r.Realtime ? "실시간 녹화엔 느림" : r.State > 0 ? "목표 화질 미달" : "";
                var item = new ListViewItem(new[]
                {
                    Codecs.Video(r.Kind).Label + (Codecs.Video(r.Kind).Hardware ? "  (GPU)" : ""),
                    r.Error != null ? "-" : r.Mb[li].ToString("0.00"),
                    r.Error != null ? "-" : $"{r.Ssim[li] * 100:0.0}%",
                    r.MBAtTarget > 0 ? $"{r.MBAtTarget:0.00} MB/분" : "-",
                    r.Error != null ? "-" : $"x{r.Speed:0.0}",
                    verdict,
                });
                if (r.Kind == bestInfo.Kind) { item.Font = new Font(_bench.Font, FontStyle.Bold); item.BackColor = Color.FromArgb(255, 249, 219); }
                _bench.Items.Add(item);
            }
            _status.Text = $"분석 완료 ({DateTime.Now:HH:mm}) — 다음부터는 자동으로 하지 않습니다. PC 구성이 바뀌면 상태 줄로 알려 드립니다.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "취소됨";
        }
        catch (Exception ex)
        {
            _status.Text = "분석 실패: " + ex.Message.Split('\n')[0];
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Finished = true;
            if (!IsDisposed) _btnAgain.Enabled = true;
        }
    }
}
