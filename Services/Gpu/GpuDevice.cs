using System;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct2D1;

namespace Polaris.Services.Gpu;

/// <summary>
/// Process-wide GPU device shared by every <see cref="CompositionHost"/> (the main dock,
/// side dock, Saturn notch clock and drop ghost). Previously each host created its OWN
/// <c>ID3D11Device</c> + DXGI + D2D device; with 3-4 live surfaces that is 3-4 separate
/// hardware devices, each committing a large driver address space and spawning a driver
/// worker-thread pool. Sharing ONE device cuts that committed memory and thread count.
///
/// <para><b>Thread safety.</b> The docks each render on their own <see cref="RenderLoop"/>
/// thread, so the shared device is touched concurrently. The D3D11 device is created
/// multithread-capable and marked <c>SetMultithreadProtected(true)</c>, and the D2D factory
/// is <see cref="FactoryType.MultiThreaded"/>, so D3D11/D2D serialise internal access. Each
/// host still owns its OWN swap chain, DirectComposition device/target/visual and
/// <c>ID2D1DeviceContext</c> (DComp + device contexts have thread affinity and are used only
/// on their creating render thread).</para>
///
/// <para>The instance is lazily created on the first host (on whichever render thread builds
/// first; the lazy initialiser is thread-safe) and kept for the process lifetime — the OS
/// reclaims the COM objects on exit, which avoids any teardown ordering hazard with a render
/// thread that might still be mid-frame.</para>
/// </summary>
internal sealed class GpuDevice
{
    private static readonly Lazy<GpuDevice> _shared =
        new(() => new GpuDevice(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared device, created on first access.</summary>
    public static GpuDevice Shared => _shared.Value;

    public ID3D11Device D3D { get; }
    public IDXGIDevice Dxgi { get; }
    public IDXGIFactory2 Factory { get; }
    public ID2D1Device D2DDevice { get; }

    private readonly ID2D1Factory1 _d2dFactory;

    private GpuDevice()
    {
        // Prefer the hardware GPU; fall back to WARP so the app still runs in a VM / on a
        // machine without a usable D3D11 hardware device. BgraSupport (no SingleThreaded
        // flag) keeps the device multithread-capable.
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
        D3D = device!;

        // Serialise concurrent access from the per-dock render threads.
        try
        {
            using var mt = D3D.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);
        }
        catch { /* multithread protection unavailable — WARP/old driver; sharing still works for the typical single-active-dock case */ }

        Dxgi = D3D.QueryInterface<IDXGIDevice>();
        Factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        D2DDevice = _d2dFactory.CreateDevice(Dxgi);
    }
}
