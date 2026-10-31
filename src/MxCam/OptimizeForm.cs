using System.Diagnostics;

namespace MxCam;

/// <summary>
/// 우클릭 "용량 최적화 변환" 창: 분석 → 자가 학습 탐색 → 변환을 자동으로 진행.
/// </summary>
internal sealed class OptimizeForm : Form
{
    readonly Settings _s;
    readonly string _file;
    readonly Label _title = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0), Font = new Font("맑은 고딕", 10f, FontStyle.Bold) };
    readonly Label _src = new() { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 2, 8, 0) };
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window };
    readonly ProgressBar _bar = new() { Dock = DockStyle.Bottom, Height = 16, Maximum = 1000 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 4, 8, 0) };
    readonly Button _btnFolder = new() { Text = "폴더 열기", AutoSize = true, Enabled = false };
    readonly Button _btnClose = new() { Text = "취소", AutoSize = true };
    readonly CancellationTokenSource _cts = new();

    internal string? Output { get; private set; }
    internal OptimizeDecision? Decision { get; private set; }
    internal bool Finished { get; private set; }
    internal bool Succeeded { get; private set; }
    internal bool TestMode { get; init; }

    public OptimizeForm(Settings s, string file)
    {
        _s = s;
        _file = file;
        Text = "MxCam - 용량 최적화 변환";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(620, 400);
        Font = new Font("맑은 고딕", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _title.Text = Path.GetFileName(file);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { _btnClose, _btnFolder });
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        pad.Controls.Add(_log);
        Controls.Add(pad);
        Controls.Add(_src);
        Controls.Add(_title);
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(buttons);
        _btnClose.Click += (_, _) => { if (Finished) Close(); else _cts.Cancel(); };
        _btnFolder.Click += (_, _) =>
        {
            if (Output != null && File.Exists(Output)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Output}\"") { UseShellExecute = true });
        };
        FormClosing += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await RunAsync();
    }

    void Log(string line)
    {
        _log.AppendText(line + Environment.NewLine);
    }

    internal async Task RunAsync()
    {
        var ffmpeg = Ffmpeg.Resolve(_s.FfmpegPath);
        try
        {
            if (ffmpeg == null) throw new InvalidOperationException("ffmpeg.exe를 찾을 수 없습니다. MxCam 옵션에서 경로를 지정하세요.");
            _status.Text = "코덱 확인 중…";
            await Task.Run(() => Codecs.Probe(ffmpeg));
            var info = await Task.Run(() => MediaInfo.Probe(ffmpeg, _file));
            _src.Text = "원본: " + info.Summary;
            if (!info.HasVideo) throw new InvalidOperationException("영상이 없는 파일입니다.");

            int level = _s.Level;
            Log($"목표 화질: 원본 대비 SSIM {Optimizer.TargetSsim(level) * 100:0.0}% 이상 ({Codecs.LevelNames[level - 1]})");
            Log($"영상 분류: {Learning.ClassOf(info)}");
            var progress = new Progress<string>(m => _status.Text = m);
            var d = await Task.Run(() => Optimizer.Decide(ffmpeg, info, _s, level, progress, _cts.Token));
            Decision = d;
            Log(d.UsedHistory
                ? $"학습 데이터 {d.HistoryCount}건 활용 → 성적 좋은 후보 위주로 시험"
                : $"이 분류의 학습 데이터 {d.HistoryCount}건 → 후보 전체 탐색 (다음부터 빨라짐)");
            foreach (var t in d.Trials)
                Log($"  {Codecs.Video(t.Kind).Label,-20} {t.Level:0.0}단계 → 예상 {t.MB,7:0.0} MB · 화질 {t.Ssim * 100:0.00}% · x{t.Speed:0.0}");
            double srcMB = info.Size / 1_000_000.0;
            var best = Codecs.Video(d.Kind);
            Log($"선택: {best.Label} {d.Level:0.0}단계 — 예상 {d.EstMB:0.0} MB (원본 {srcMB:0.0} MB, {(1 - d.EstMB / srcMB) * 100:0}% 감소), 화질 {d.Ssim * 100:0.00}%");

            if (d.NoGain)
            {
                Log("→ 원본이 이미 충분히 최적화되어 있어 변환하지 않았습니다. (줄어드는 용량 8% 미만)");
                _status.Text = "변환할 필요 없음 — 원본 유지";
                Succeeded = true;
                return;
            }

            var plan = new ConvertPlan { Preset = ConvertPreset.Recommended, Video = d.Kind, Ext = "mp4", Title = best.Label, Level = level, VideoLevel = d.Level };
            Output = Converter.DefaultOutput(info, plan);
            var sw = Stopwatch.StartNew();
            var bar = new Progress<double>(v =>
            {
                _bar.Value = (int)(v * 1000);
                var eta = v > 0.02 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / v * (1 - v)) : (TimeSpan?)null;
                _status.Text = $"변환 중… {v * 100:0}%{(eta != null ? $" · 남은 시간 약 {eta:m\\:ss}" : "")}";
            });
            var (ok, msg) = await Converter.RunAsync(ffmpeg, info, plan, Output, bar, _cts.Token);
            if (!ok) throw new InvalidOperationException(msg);

            long outSize = new FileInfo(Output).Length;
            double pct = (1 - outSize / (double)info.Size) * 100;
            // 실제 결과도 학습에 반영 (전체 파일 기준 용량비)
            Learning.Add(new[] { new LearnRecord { Class = Learning.ClassOf(info), Kind = d.Kind, Level = d.Level, Ssim = d.Ssim,
                SizeRatio = outSize / (double)info.Size, Speed = info.Duration / Math.Max(0.001, sw.Elapsed.TotalSeconds), At = DateTime.Now } });
            if (outSize >= info.Size)
            {
                File.Delete(Output);
                Output = null;
                Log("→ 결과가 원본보다 커서 삭제했습니다. 원본을 그대로 두세요.");
                _status.Text = "원본 유지 (변환 결과가 더 큼)";
            }
            else
            {
                Log($"완료: {Path.GetFileName(Output)} — {outSize / 1048576.0:0.0} MB ({pct:0}% 감소, {sw.Elapsed:m\\:ss} 소요)");
                _status.Text = $"완료 — {pct:0}% 줄었습니다";
                _btnFolder.Enabled = true;
            }
            Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "취소됨";
        }
        catch (Exception ex)
        {
            _status.Text = "실패";
            Log("오류: " + ex.Message);
        }
        finally
        {
            Finished = true;
            if (!IsDisposed)
            {
                _btnClose.Text = "닫기";
                _bar.Value = Succeeded ? 1000 : 0;
            }
        }
    }
}
