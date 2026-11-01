using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MxCam;

/// <summary>
/// 녹화 영역 표시 테두리 (oCam과 같은 조작).
///  · 점 8개: 드래그해서 크기 조절
///  · 테두리 선 / 가운데 이동 아이콘: 드래그해서 이동
///  · 나머지 안쪽: 창 모양(Region)에서 아예 잘라내서 클릭이 뒤 창으로 그대로 통과
/// 창 전체는 화면 캡처에서 제외되어 영상에 찍히지 않는다.
/// </summary>
public sealed class RegionFrame : Form
{
    public const int B = 4;          // 캡처 영역 바깥 테두리 두께
    const int Band = 2;              // 테두리 안쪽으로 더 잡히는 폭 (선을 잡기 쉽게)
    const int Dot = 9;               // 크기 조절 점 크기
    const int DotInset = 10;         // 점 중심이 바깥 가장자리에서 떨어진 거리
    static readonly Color Idle = Color.FromArgb(132, 204, 22);
    static readonly Color DotFill = Color.FromArgb(236, 252, 203);
    static readonly Color Rec = Color.FromArgb(239, 68, 68);
    static readonly Color Paused = Color.FromArgb(245, 158, 11);
    static readonly string IconFont = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    bool _recording, _paused;
    string _overlay = "";

    public event Action? AreaChanged;

    public RegionFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Idle;
        DoubleBuffered = true;
        MinimumSize = new Size(120 + 2 * B, 90 + 2 * B);
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
        UpdateShape();
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
        set { _recording = value; UpdateShape(); Invalidate(); }
    }

    public bool IsPaused
    {
        get => _paused;
        set { _paused = value; Invalidate(); }
    }

    public string OverlayText
    {
        get => _overlay;
        set { if (_overlay != value) { _overlay = value; UpdateShape(); Invalidate(); } }
    }

    // ───────────── 모양 ─────────────

    /// <summary>점 8개: 왼위, 위, 오른위, 오른, 오른아래, 아래, 왼아래, 왼 (HitTest 코드 순서와 같음).</summary>
    Rectangle[] Dots()
    {
        int w = Width, h = Height, c = DotInset, s = Dot;
        Rectangle At(int x, int y) => new(x - s / 2, y - s / 2, s, s);
        return new[]
        {
            At(c, c), At(w / 2, c), At(w - 1 - c, c), At(w - 1 - c, h / 2),
            At(w - 1 - c, h - 1 - c), At(w / 2, h - 1 - c), At(c, h - 1 - c), At(c, h / 2),
        };
    }
    static readonly int[] DotCodes = { 13, 12, 14, 11, 17, 15, 16, 10 }; // HTTOPLEFT, HTTOP, HTTOPRIGHT, HTRIGHT, HTBOTTOMRIGHT, HTBOTTOM, HTBOTTOMLEFT, HTLEFT

    Rectangle MoveIcon() => new(Width / 2 - 14, Height / 2 - 14, 28, 28);

    string SizeText()
    {
        var r = CaptureRect;
        string size = $"{r.Width & ~1} × {r.Height & ~1}";
        return string.IsNullOrEmpty(_overlay) ? size : $"{_overlay}   {size}";
    }

    Rectangle SizeLabel()
    {
        var sz = TextRenderer.MeasureText(SizeText(), Font);
        int w = sz.Width + 14, h = sz.Height + 6;
        return new Rectangle((Width - w) / 2, Height / 2 + 18, w, h);
    }

    Rectangle Inner() => new(B + Band, B + Band, Width - 2 * (B + Band), Height - 2 * (B + Band));

    /// <summary>창 모양 = 테두리 띠 + (대기 중) 점 8개 + 가운데 이동 아이콘·크기 표시. 나머지는 잘라냄 → 클릭 통과.</summary>
    void UpdateShape()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        var rgn = new Region(new Rectangle(0, 0, Width, Height));
        rgn.Exclude(Inner());
        if (!_recording)
        {
            foreach (var d in Dots()) rgn.Union(Rectangle.Inflate(d, 1, 1));
            rgn.Union(MoveIcon());
            rgn.Union(SizeLabel());
        }
        var old = Region;
        Region = rgn;
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = _recording ? (_paused ? Paused : Rec) : Idle;
        g.Clear(color);
        if (_recording) return;

        using (var fill = new SolidBrush(DotFill))
        using (var edge = new Pen(Color.FromArgb(77, 124, 15)))
            foreach (var d in Dots())
            {
                g.FillRectangle(fill, d);
                g.DrawRectangle(edge, d.X, d.Y, d.Width - 1, d.Height - 1);
            }

        // 가운데 이동 아이콘
        var mi = MoveIcon();
        using (var bg = new SolidBrush(Color.FromArgb(32, 32, 36))) g.FillRectangle(bg, mi);
        using (var icon = new Font(IconFont, 15f, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "", icon, mi, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // 크기 표시
        var sl = SizeLabel();
        using (var bg = new SolidBrush(Color.FromArgb(32, 32, 36))) g.FillRectangle(bg, sl);
        TextRenderer.DrawText(g, SizeText(), Font, sl, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ───────────── 마우스 ─────────────

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

    /// <summary>점 → 크기 조절, 테두리 선·이동 아이콘·크기 표시 → 이동, 그 밖(안쪽) → 통과.</summary>
    internal int HitTest(Point p)
    {
        const int HTTRANSPARENT = -1, HTCLIENT = 1, HTCAPTION = 2;
        if (_recording) return Inner().Contains(p) ? HTTRANSPARENT : HTCLIENT; // 녹화 중엔 영역 고정
        var dots = Dots();
        for (int i = 0; i < dots.Length; i++)
            if (Rectangle.Inflate(dots[i], 3, 3).Contains(p)) return DotCodes[i];
        if (MoveIcon().Contains(p) || SizeLabel().Contains(p)) return HTCAPTION;
        if (!Inner().Contains(p)) return HTCAPTION; // 테두리 선
        return HTTRANSPARENT;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        AreaChanged?.Invoke();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateShape();
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
