using System.Drawing.Imaging;

namespace MxCam;

/// <summary>
/// MxCam.exe --manualshots [폴더] : 매뉴얼용 화면 이미지를 만든다.
/// 화면 캡처가 아니라 각 창을 직접 그려(DrawToBitmap) 저장하므로 바탕화면을 건드리지 않는다.
/// </summary>
public sealed partial class MainForm
{
    static void Save(Control c, string path)
    {
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        bmp.Save(path, ImageFormat.Png);
    }

    async Task SaveMenu(ToolStripDropDownButton b, string path, string? openSub = null)
    {
        b.ShowDropDown();
        await Task.Delay(300);
        Save(b.DropDown, path);
        if (openSub != null && b.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text!.StartsWith(openSub)) is { } sub)
        {
            sub.ShowDropDown();
            await Task.Delay(300);
            Save(sub.DropDown, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_sub.png"));
        }
        b.HideDropDown();
        await Task.Delay(100);
    }

    internal async Task CaptureManualShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        _s.OutputDir = Path.Combine(dir, "out");
        await Until(() => Codecs.Probed, 30000);
        UpdateHint();
        await Task.Delay(300);
        Save(this, Path.Combine(dir, "main.png"));
        await SaveMenu(_btnCodec, Path.Combine(dir, "menu_codec.png"), "MP4");
        await SaveMenu(_btnSize, Path.Combine(dir, "menu_size.png"));
        await SaveMenu(_btnSound, Path.Combine(dir, "menu_sound.png"));
        await SaveMenu(_btnMode, Path.Combine(dir, "menu_mode.png"));

        using (var pf = new PcAnalysisForm(_s, _ffmpeg!, automatic: true))
        {
            pf.Show(this);
            await Until(() => pf.Finished, 240000);
            await Task.Delay(300);
            Save(pf, Path.Combine(dir, "pc_analysis.png"));
            File.WriteAllText(Path.Combine(dir, "pc_check.txt"), $"fingerprint={_s.PcFingerprint}\nsummary={_s.PcSummary}\nbest={_s.PcBest}\nbasis={Codecs.RecommendBasis(_s, 1920, 1080, 30)}\nauto={Codecs.AutoProfile(_s, 1920, 1080, 30).Name}");
        }
        UpdateHint();
        Save(this, Path.Combine(dir, "main.png"));
        using (var opt = new OptionsForm(_s, _ffmpeg))
        {
            opt.Show(this);
            await Task.Delay(400);
            Save(opt, Path.Combine(dir, "options.png"));
        }
        using (var rf = new RecommendForm(_s, _ffmpeg!, _frame.CaptureRect))
        {
            rf.Show(this);
            await Task.Delay(400);
            Save(rf, Path.Combine(dir, "recommend.png"));
        }

        // 녹화 중 화면 (툴바 상태)
        _btnRecord.PerformClick();
        await Until(() => _rec != null, 3000);
        await Task.Delay(2500);
        Save(this, Path.Combine(dir, "main_recording.png"));
        await StopRecordingAsync();

        // 변환 / 최적화 창: 예제 동영상으로
        string sample = Path.Combine(dir, "예제_영상.mp4");
        Ffmpeg.RunCapture(_ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=30", "-f", "lavfi", "-i", "sine=f=440:r=48000",
            "-t", "12", "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", sample }, 120000);
        using (var cf = new ConvertForm(_s, _ffmpeg!))
        {
            cf.Show(this);
            bool sampled = false;
            cf.SampleFinished += () => sampled = true;
            await cf.LoadFileAsync(sample);
            await Until(() => sampled, 60000);
            await Task.Delay(200);
            Save(cf, Path.Combine(dir, "convert.png"));
        }
        using (var of = new OptimizeForm(_s, sample) { TestMode = true })
        {
            of.Show(this);
            await Until(() => of.Finished, 300000);
            await Task.Delay(200);
            Save(of, Path.Combine(dir, "optimize.png"));
        }
        // 녹화 영역 테두리
        Save(_frame, Path.Combine(dir, "frame.png"));
    }
}
