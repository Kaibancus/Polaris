using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;

namespace Polaris.Views;

/// <summary>A per-pixel-alpha drag ghost rendered through
/// <b>DirectComposition + Direct2D</b> instead of a WPF <c>AllowsTransparency</c>
/// layered window. A borderless <c>WS_EX_NOREDIRECTIONBITMAP</c> top-level window
/// hosts a composition swap chain the DWM composites on the GPU — no per-frame
/// CPU <c>UpdateLayeredWindow</c> upload. Implements <see cref="IDragGhost"/>.</summary>
internal sealed class DragGhostWindowGpu : IDragGhost
{
    private readonly IntPtr _hwnd;
    private readonly CompositionHost _host;
    private readonly ID2D1Bitmap _bitmap;
    private readonly int _pxW, _pxH;
    private readonly double _scale;     // device px per DIP
    private float _opacity = 1f;
    private bool _disposed;

    public DragGhostWindowGpu(ImageSource snapshot, double dipWidth, double dipHeight)
    {
        var src = (BitmapSource)snapshot;
        if (src.Format != PixelFormats.Pbgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);

        _pxW = Math.Max(1, src.PixelWidth);
        _pxH = Math.Max(1, src.PixelHeight);
        _scale = dipWidth > 0 ? _pxW / dipWidth : 1.0;

        _hwnd = CreateGhostWindow(_pxW, _pxH);
        _host = new CompositionHost(_hwnd, _pxW, _pxH);

        int stride = _pxW * 4;
        var pixels = new byte[stride * _pxH];
        src.CopyPixels(pixels, stride, 0);
        _bitmap = _host.CreateBitmap(_pxW, _pxH, pixels, stride);

        Render();
    }

    private void Render()
    {
        if (_disposed)
            return;
        _host.Render(ctx => ctx.DrawBitmap(_bitmap, _opacity, InterpolationMode.Linear));
    }

    public void MoveCenterTo(double dipX, double dipY)
    {
        if (_disposed)
            return;
        int x = (int)Math.Round(dipX * _scale - _pxW / 2.0);
        int y = (int)Math.Round(dipY * _scale - _pxH / 2.0);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOREDRAW);
    }

    public double GhostOpacity
    {
        set
        {
            float v = (float)Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(v - _opacity) < 0.001f)
                return;
            _opacity = v;
            Render();
        }
    }

    public void Show()
    {
        if (_disposed)
            return;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Render();   // present after the window is visible
    }

    public void Close()
    {
        if (_disposed)
            return;
        _disposed = true;
        _bitmap.Dispose();
        _host.Dispose();
        DestroyWindow(_hwnd);
    }

    // ---- Raw Win32 window (NOREDIRECTIONBITMAP, click-through, top-most) -------

    private static readonly WndProc s_wndProc = DefWindowProcW;
    private static ushort s_classAtom;
    private static readonly object s_classLock = new();

    private static IntPtr CreateGhostWindow(int w, int h)
    {
        EnsureClass();
        IntPtr hwnd = CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TRANSPARENT |
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, string.Empty, WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                "CreateWindowEx (GPU drag ghost) failed: " + Marshal.GetLastWin32Error());
        return hwnd;
    }

    private const string ClassName = "PolarisDragGhostGpu";

    private static void EnsureClass()
    {
        lock (s_classLock)
        {
            if (s_classAtom != 0)
                return;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            s_classAtom = RegisterClassExW(ref wc);
            if (s_classAtom == 0)
                throw new InvalidOperationException(
                    "RegisterClassEx (GPU drag ghost) failed: " + Marshal.GetLastWin32Error());
        }
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOREDRAW = 0x0008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
