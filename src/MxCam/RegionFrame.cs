using System.Drawing.Drawing2D;

namespace MxCam;

/// <summary>
/// 녹화 영역 표시 테두리. 안쪽은 색상 키로 투명 + 클릭 통과, 창 전체는 화면 캡처에서 제외되어 영상에 찍히지 않는다.
/// 테두리/모서리 핸들로 크기 조절, 가운데 크기 표시를 잡고 이동.
/// </summary>
public sealed class RegionFrame : Form
{
    public const int B = 4;          // 캡처 영역 바깥 테두리 두께
    const int Grip = 12;
    static readonly Color Key = Color.FromArgb(255, 1, 254);
    static readonly Color Idle = Color.FromArgb(34, 197, 94);
    static readonly Color Rec = Color.FromArgb(239, 68, 68);
    static readonly Color Paused = Color.FromArgb(245, 158, 11);

    bool _recording, _paused;
    string _overlay = "";

    public event Action? AreaChanged;

    public RegionFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Key;
        TransparencyKey = Key;
        DoubleBuffered = true;
        MinimumSize = new Size(64 + 2 * B, 48 + 2 * B);
        Text = "MxCam 녹화 영역";
        Font = new Font("Segoe UI", 9f, FontStyle.Bold);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: Alt+Tab에서 숨김
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ExcludeFromCapture(Handle);
    }

    /// <summary>실제로 녹화되는 화면 좌표 영역 (테두리 안쪽).</summary>
    public Rectangle CaptureRect
    {
        get => new(Left + B, Top + B, Width - 2 * B, Height - 2 * B);
        set => Bounds = new Rectangle(value.X - B, value.Y - B, value.Width + 2 * B, value.Height + 2 * B);
    }

    public bool Recording
    {
        get => _recording;
        set { _recording = value; Invalidate(); }
    }

    public bool IsPaused
    {
        get => _paused;
        set { _paused = value; Invalidate(); }
    }

    public string OverlayText
    {
        get => _overlay;
        set { if (_overlay != value) { _overlay = value; Invalidate(LabelRect()); } }
    }

    Rectangle LabelRect()
    {
        var text = LabelText();
        var size = TextRenderer.MeasureText(text, Font);
        int w = size.Width + 16, h = size.Height + 8;
        return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
    }

    string LabelText()
    {
        var r = CaptureRect;
        string size = $"{r.Width & ~1} × {r.Height & ~1}";
        return string.IsNullOrEmpty(_overlay) ? size : $"{_overlay}   {size}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var color = _recording ? (_paused ? Paused : Rec) : Idle;
        using (var pen = new Pen(color, B) { Alignment = PenAlignment.Inset })
            g.DrawRectangle(pen, 0, 0, Width, Height);

        if (!_recording)
        {
            using var br = new SolidBrush(color);
            foreach (var h in Handles()) g.FillRectangle(br, h);
        }

        // 가운데 크기/상태 표시 (녹화 중엔 화면을 가리지 않게 숨김)
        if (!_recording)
        {
            var lr = LabelRect();
            using var bg = new SolidBrush(Color.FromArgb(32, 32, 36));
            g.FillRectangle(bg, lr);
            TextRenderer.DrawText(g, LabelText(), Font, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    IEnumerable<Rectangle> Handles()
    {
        int w = Width, h = Height, s = Grip;
        yield return new Rectangle(0, 0, s, s);
        yield return new Rectangle(w - s, 0, s, s);
        yield return new Rectangle(0, h - s, s, s);
        yield return new Rectangle(w - s, h - s, s, s);
        yield return new Rectangle((w - s) / 2, 0, s, B + 3);
        yield return new Rectangle((w - s) / 2, h - B - 3, s, B + 3);
        yield return new Rectangle(0, (h - s) / 2, B + 3, s);
        yield return new Rectangle(w - B - 3, (h - s) / 2, B + 3, s);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST)
        {
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            m.Result = (IntPtr)HitTest(p);
            return;
        }
        base.WndProc(ref m);
    }

    int HitTest(Point p)
    {
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
            HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        if (_recording) return HTCLIENT; // 녹화 중엔 영역 고정
        if (LabelRect().Contains(p)) return HTCAPTION;
        int g = Grip;
        bool l = p.X < g, r = p.X >= Width - g, t = p.Y < g, b = p.Y >= Height - g;
        if (t && l) return HTTOPLEFT;
        if (t && r) return HTTOPRIGHT;
        if (b && l) return HTBOTTOMLEFT;
        if (b && r) return HTBOTTOMRIGHT;
        if (l) return HTLEFT;
        if (r) return HTRIGHT;
        if (t) return HTTOP;
        if (b) return HTBOTTOM;
        return HTCAPTION;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        AreaChanged?.Invoke();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Invalidate();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Invalidate();
    }
}

/// <summary>마우스로 녹화할 창을 고르는 전체 화면 오버레이.</summary>
public sealed class WindowPicker : Form
{
    readonly Rectangle _virtual = SystemInformation.VirtualScreen;
    readonly HashSet<IntPtr> _ignore;
    Rectangle _hover;
    public Rectangle? Picked { get; private set; }

    public WindowPicker(IEnumerable<IntPtr> ignore)
    {
        _ignore = new HashSet<IntPtr>(ignore);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.35;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ignore.Add(Handle);
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var r = WindowAt(Cursor.Position);
        if (r != _hover)
        {
            _hover = r;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Close(); return; }
        var r = WindowAt(Cursor.Position);
        if (!r.IsEmpty) Picked = Rectangle.Intersect(r, _virtual);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, "녹화할 창을 클릭하세요 (Esc 취소)", new Font("Segoe UI", 16f, FontStyle.Bold),
            new Point(Cursor.Position.X - _virtual.X + 20, Cursor.Position.Y - _virtual.Y + 20), Color.White);
        if (_hover.IsEmpty) return;
        var r = _hover;
        r.Offset(-_virtual.X, -_virtual.Y);
        using var pen = new Pen(Color.Lime, 6);
        e.Graphics.DrawRectangle(pen, r);
    }

    internal Rectangle WindowAt(Point pt)
    {
        Rectangle found = Rectangle.Empty;
        uint myPid = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (_ignore.Contains(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == myPid) return true;
            var r = Native.GetVisibleBounds(h);
            if (r.Width < 40 || r.Height < 40 || !r.Contains(pt)) return true;
            if (Native.GetWindowTextLength(h) == 0 && r != Screen.FromPoint(pt).Bounds) return true;
            found = r;
            return false; // EnumWindows는 위쪽(z-order) 창부터 → 처음 맞는 창
        }, IntPtr.Zero);
        return found;
    }
}
