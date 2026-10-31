using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MxCam;

/// <summary>
/// GDI 화면 캡처 (BitBlt).
/// top-down 32bit DIB section에 직접 BitBlt 하므로 GetDIBits 복사가 한 번 줄어든다.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    readonly IntPtr _screenDc, _memDc, _bitmap, _oldBitmap, _bits;
    readonly Rectangle _src;
    readonly bool _drawCursor;
    readonly bool _clickEffect;
    readonly Dictionary<IntPtr, (int x, int y)> _hotspots = new();
    static readonly Brush LeftClickBrush = new SolidBrush(Color.FromArgb(120, 255, 214, 0));
    static readonly Brush RightClickBrush = new SolidBrush(Color.FromArgb(120, 0, 160, 255));

    /// <summary>테스트용: true면 마우스 버튼이 눌린 것으로 간주.</summary>
    public static volatile bool ForceClickForTest;

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    public ScreenCapturer(Rectangle region, bool drawCursor, bool clickEffect = false)
    {
        _clickEffect = clickEffect;
        // yuv420 인코딩을 위해 가로/세로를 짝수로
        Width = Math.Max(2, region.Width & ~1);
        Height = Math.Max(2, region.Height & ~1);
        _src = new Rectangle(region.X, region.Y, Width, Height);
        _drawCursor = drawCursor;

        _screenDc = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(_screenDc);
        if (_screenDc == IntPtr.Zero || _memDc == IntPtr.Zero) throw new InvalidOperationException("화면 DC를 만들 수 없습니다.");
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("캡처 버퍼를 만들 수 없습니다.");
        _oldBitmap = Native.SelectObject(_memDc, _bitmap);
    }

    public Rectangle Source => _src;

    public void Capture()
    {
        // DWM 합성 환경에서는 SRCCOPY만 사용 (CAPTUREBLT는 커서 깜빡임/지연 유발)
        Native.BitBlt(_memDc, 0, 0, Width, Height, _screenDc, _src.X, _src.Y, Native.SRCCOPY);
        if (_clickEffect) DrawClickEffect();
        if (_drawCursor) DrawCursor();
        Native.GdiFlush();
    }

    /// <summary>마우스 클릭 효과: 왼쪽 버튼 노란 원, 오른쪽 버튼 파란 원.</summary>
    void DrawClickEffect()
    {
        bool left = ForceClickForTest || Native.GetAsyncKeyState(0x01) < 0;
        bool right = Native.GetAsyncKeyState(0x02) < 0;
        if (!left && !right) return;
        if (!Native.GetCursorPos(out var pt)) return;
        int x = pt.X - _src.X, y = pt.Y - _src.Y;
        const int r = 22;
        if (x < -r || y < -r || x > Width + r || y > Height + r) return;
        using var g = Graphics.FromHdc(_memDc);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillEllipse(left ? LeftClickBrush : RightClickBrush, x - r, y - r, r * 2, r * 2);
    }

    void DrawCursor()
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (!_hotspots.TryGetValue(ci.hCursor, out var hs))
        {
            if (Native.GetIconInfo(ci.hCursor, out var ii))
            {
                hs = (ii.xHotspot, ii.yHotspot);
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }
            _hotspots[ci.hCursor] = hs;
        }
        int x = ci.ptScreenPos.X - _src.X - hs.x;
        int y = ci.ptScreenPos.Y - _src.Y - hs.y;
        if (x > Width || y > Height || x < -64 || y < -64) return;
        Native.DrawIconEx(_memDc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
    }

    public void CopyTo(byte[] buffer) => Marshal.Copy(_bits, buffer, 0, FrameBytes);

    public Bitmap ToBitmap()
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(_bits + y * Width * 4, row, 0, row.Length);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>현재 캡처 버퍼의 (x,y) 픽셀 (테스트용).</summary>
    public Color PixelAt(int x, int y)
    {
        int v = Marshal.ReadInt32(_bits, (y * Width + x) * 4);
        return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    public void Dispose()
    {
        Native.SelectObject(_memDc, _oldBitmap);
        Native.DeleteObject(_bitmap);
        Native.DeleteDC(_memDc);
        Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
