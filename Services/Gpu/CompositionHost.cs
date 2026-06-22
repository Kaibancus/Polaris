using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.Mathematics;
using DXGIAlphaMode = Vortice.DXGI.AlphaMode;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;

namespace Polaris.Services.Gpu;

/// <summary>GPU composition host for a per-pixel-alpha window (GPU-rendering spike):
/// D3D11 device → DXGI <b>composition</b> swap chain (premultiplied alpha) →
/// DirectComposition device/target/visual → a D2D device context bound to the back
/// buffer. Unlike a WPF <c>AllowsTransparency</c> layered window (Tier-0 software
/// composite + per-frame full-surface <c>UpdateLayeredWindow</c> upload), the DWM
/// composites this on the GPU and the back buffer lives in VRAM.</summary>
internal sealed class CompositionHost : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIFactory2 _factory;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionDevice _dcomp;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private readonly IDCompositionVisual3? _visual3;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2d;
    private ID2D1Bitmap1 _targetBitmap;
    private float _dpi;

    // Frame-latency waitable object (independent render-thread path only). When the
    // swap chain is created with FrameLatencyWaitableObject, the DWM/compositor signals
    // this handle when it is ready to accept the next frame; a render thread waits on it
    // (instead of a UI-thread timer) to pace itself to the DISPLAY refresh rate. NULL on
    // the default (UI-thread FrameClock) path, where pacing is CompositionTarget.Rendering.
    private readonly IDXGISwapChain2? _swapChain2;
    private readonly IntPtr _frameLatencyWaitable;
    private readonly SwapChainFlags _swapFlags;

    public ID2D1DeviceContext Context => _d2d;
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>True when this host owns a frame-latency waitable object (render-thread
    /// path): callers should pace frames with <see cref="WaitForVBlank"/> + Present(1).</summary>
    public bool IsWaitable => _frameLatencyWaitable != IntPtr.Zero;

    private const uint WAIT_TIMEOUT_MS = 1000;
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Device pixels per DIP for the monitor hosting <paramref name="hwnd"/>
    /// (1.0 at 100%, 1.5 at 150%). Layout math is in DIPs but the Win32 window and
    /// swap chain are physical-pixel; callers scale the window/swap-chain size by
    /// this and pass <c>96 * scale</c> as the host DPI so DIP-space drawing maps 1:1.</summary>
    public static double DpiScale(IntPtr hwnd)
    {
        // GetDpiForWindow is unreliable before the window is realized on a monitor
        // (it can return the 96-DPI system default), so query the monitor's effective
        // DPI directly — stable from the moment the window exists.
        try
        {
            const uint MONITOR_DEFAULTTONEAREST = 2;
            const int MDT_EFFECTIVE_DPI = 0;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint mx, out _) == 0 && mx >= 48)
                return mx / 96.0;
        }
        catch { /* Shcore unavailable — fall through */ }
        uint dpi = GetDpiForWindow(hwnd);
        return dpi >= 48 ? dpi / 96.0 : 1.0;
    }

    public CompositionHost(IntPtr hwnd, int width, int height, float dpi = 96f, bool waitable = false)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        // Prefer the hardware GPU; fall back to WARP so the spike still runs in a
        // VM / on a machine without a usable D3D11 hardware device.
        ID3D11Device? device = null;
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, null, out device).CheckError();
        }
        catch
        {
            device?.Dispose();
            D3D11.D3D11CreateDevice(null, DriverType.Warp,
                DeviceCreationFlags.BgraSupport, null, out device).CheckError();
        }
        _d3d = device!;

        _dxgiDevice = _d3d.QueryInterface<IDXGIDevice>();
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        // The render-thread path adds FrameLatencyWaitableObject so a dedicated thread can
        // block on the compositor's "ready for next frame" signal (refresh-rate paced),
        // instead of the UI-thread CompositionTarget.Rendering clock that caps ~38-53fps.
        _swapFlags = waitable ? SwapChainFlags.FrameLatencyWaitableObject : SwapChainFlags.None;
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = DXGIAlphaMode.Premultiplied,
            Flags = _swapFlags,
        };
        _swapChain = _factory.CreateSwapChainForComposition(_d3d, desc);
        if (waitable)
        {
            try
            {
                _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
                // Latency 1 = at most one frame queued, so each WaitForVBlank returns as
                // soon as the just-presented frame is consumed → minimal input-to-photon lag.
                _swapChain2.MaximumFrameLatency = 1;
                _frameLatencyWaitable = _swapChain2.FrameLatencyWaitableObject;
            }
            catch { _swapChain2 = null; _frameLatencyWaitable = IntPtr.Zero; }
        }

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(_swapChain);
        // IDCompositionVisual3 adds per-visual opacity (premultiplied content), used to
        // fade the whole dock in/out on the GPU compositor without re-rendering.
        try { _visual3 = _visual.QueryInterface<IDCompositionVisual3>(); } catch { _visual3 = null; }
        _target.SetRoot(_visual);
        _dcomp.Commit();

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        // The swap chain is premultiplied-alpha (transparent composition surface), where
        // ClearType subpixel AA is invalid; force grayscale AA so glyph edges are clean
        // rather than colour-fringed/soft.
        _d2d.TextAntialiasMode = TextAntialiasMode.Grayscale;

        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            dpi, dpi, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _targetBitmap = _d2d.CreateBitmapFromDxgiSurface(surface, props);
        _d2d.Target = _targetBitmap;
        // Setting the target bitmap's DPI does NOT change the device context's own
        // DPI (it defaults to 96), and the context DPI is what scales DIP-space
        // drawing to pixels. Without this the whole scene renders at 1.0x — too
        // small and anchored to the top-left of a physical-pixel window. Set it so
        // DIP drawing maps 1:1 to the physical back buffer.
        _d2d.SetDpi(dpi, dpi);
        _dpi = dpi;
    }

    /// <summary>Resizes the swap chain + D2D target bitmap in place (no window / device
    /// recreation), so a dock that grows/shrinks by an icon can relayout without the blank
    /// frame a full teardown produces. The DComp visual content stays bound to the same
    /// swap chain, so the composition tree is untouched.</summary>
    public void Resize(int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        if (w == Width && h == Height)
            return;
        // Release the D2D target that holds a back-buffer reference before resizing.
        _d2d.Target = null;
        _targetBitmap.Dispose();
        _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, _swapFlags).CheckError();
        Width = w; Height = h;
        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            _dpi, _dpi, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _targetBitmap = _d2d.CreateBitmapFromDxgiSurface(surface, props);
        _d2d.Target = _targetBitmap;
        _d2d.SetDpi(_dpi, _dpi);
    }

    /// <summary>Creates a static premultiplied-BGRA D2D bitmap from raw pixels
    /// (e.g. a WPF <c>Pbgra32</c> snapshot's bytes).</summary>
    public ID2D1Bitmap1 CreateBitmap(int pxWidth, int pxHeight, byte[] bgraPremultiplied, int stride)
    {
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None);
        var handle = GCHandle.Alloc(bgraPremultiplied, GCHandleType.Pinned);
        try
        {
            return _d2d.CreateBitmap(new SizeI(pxWidth, pxHeight),
                handle.AddrOfPinnedObject(), (uint)stride, props);
        }
        finally { handle.Free(); }
    }

    /// <summary>Re-binds the swap-chain back buffer as the device context's target
    /// (after a temporary target switch — e.g. rendering a shadow silhouette into a
    /// command list for the D2D <c>Shadow</c> effect).</summary>
    public void SetDefaultTarget() => _d2d.Target = _targetBitmap;

    /// <summary>Slides and fades the whole dock visual on the GPU compositor: sets the
    /// root visual's pixel offset (slide-in/out) and opacity (fade) then commits. Used
    /// by the side dock's show/hide animation so the panel eases in from its anchored
    /// edge instead of popping. Offsets are in physical pixels.</summary>
    public void SetIntro(float offsetX, float offsetY, float opacity)
    {
        _visual.SetOffsetX(offsetX);
        _visual.SetOffsetY(offsetY);
        _visual3?.SetOpacity(Math.Clamp(opacity, 0f, 1f));
        _dcomp.Commit();
    }

    /// <summary>Presents the swap chain (for callers that drive BeginDraw/EndDraw
    /// themselves, e.g. an interleaved command-list shadow pass).</summary>
    public void Present() => _swapChain.Present(1, PresentFlags.None);

    /// <summary>Blocks until the compositor is ready to accept the next frame
    /// (render-thread path only). With MaximumFrameLatency=1 this returns once per
    /// display refresh after the last present is consumed, so a render thread that
    /// loops <c>WaitForVBlank(); render; Present(1);</c> self-paces to the monitor's
    /// refresh rate — 60Hz, 144Hz, etc. — with no UI-thread timer. No-op (returns
    /// immediately) when this host has no waitable object.</summary>
    public void WaitForVBlank()
    {
        if (_frameLatencyWaitable != IntPtr.Zero)
            WaitForSingleObject(_frameLatencyWaitable, WAIT_TIMEOUT_MS);
    }

    /// <summary>Clears to fully transparent, runs the caller's draw, presents.</summary>
    public void Render(Action<ID2D1DeviceContext> draw)
    {
        _d2d.BeginDraw();
        _d2d.Clear(new Color4(0f, 0f, 0f, 0f));
        draw(_d2d);
        _d2d.EndDraw();
        _swapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        _targetBitmap?.Dispose();
        _d2d?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _visual?.Dispose();
        _visual3?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _swapChain2?.Dispose();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _dxgiDevice?.Dispose();
        _d3d?.Dispose();
    }
}
