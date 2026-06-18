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
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2d;
    private readonly ID2D1Bitmap1 _targetBitmap;

    public ID2D1DeviceContext Context => _d2d;
    public int Width { get; }
    public int Height { get; }

    public CompositionHost(IntPtr hwnd, int width, int height, float dpi = 96f)
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
            Flags = SwapChainFlags.None,
        };
        _swapChain = _factory.CreateSwapChainForComposition(_d3d, desc);

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(_swapChain);
        _target.SetRoot(_visual);
        _dcomp.Commit();

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            dpi, dpi, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _targetBitmap = _d2d.CreateBitmapFromDxgiSurface(surface, props);
        _d2d.Target = _targetBitmap;
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

    /// <summary>Presents the swap chain (for callers that drive BeginDraw/EndDraw
    /// themselves, e.g. an interleaved command-list shadow pass).</summary>
    public void Present() => _swapChain.Present(1, PresentFlags.None);

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
        _target?.Dispose();
        _dcomp?.Dispose();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _dxgiDevice?.Dispose();
        _d3d?.Dispose();
    }
}
