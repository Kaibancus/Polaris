using System;
using System.Runtime.InteropServices;

namespace Polaris.Services.Gpu;

/// <summary>Live window preview via the DWM Thumbnail API. Unlike a PrintWindow
/// capture (which returns a black/blank frame for GPU-composited windows such as
/// Edge/Chrome and cannot capture a minimized window at all), a DWM thumbnail
/// reuses the frame DWM already composites for the source window, so it shows live
/// content for GPU windows and keeps showing the last frame after the source is
/// minimized to the taskbar. Verified (spike) to render even into an
/// AllowsTransparency=true WPF popup's HWND.
///
/// The thumbnail is an opaque overlay composited by DWM ON TOP of the destination
/// rect — it is NOT part of the WPF visual tree, so it cannot be rounded, clipped
/// by WPF, or drawn under other WPF elements; anything that must sit over it (a
/// close button) has to be a separate topmost element. One instance owns one
/// registration: create it against the popup's HWND + source window, call
/// <see cref="SetDestination"/> whenever the tile's on-screen rect changes, and
/// <see cref="Dispose"/> on tile/popup teardown.</summary>
internal sealed class DwmThumbnail : IDisposable
{
    private readonly IntPtr _dest;
    private IntPtr _thumb;
    private bool _disposed;

    /// <summary>Source window's full pixel size (client area), as reported by DWM;
    /// (0,0) if the registration failed.</summary>
    public (int W, int H) SourceSize { get; private set; }

    /// <summary>True when the thumbnail registered successfully and can be shown.</summary>
    public bool IsValid => _thumb != IntPtr.Zero;

    private DwmThumbnail(IntPtr dest, IntPtr thumb)
    {
        _dest = dest;
        _thumb = thumb;
        if (DwmQueryThumbnailSourceSize(thumb, out SIZE s) == 0)
            SourceSize = (s.cx, s.cy);
    }

    /// <summary>Registers a thumbnail of <paramref name="source"/> into the
    /// <paramref name="destHwnd"/> window. Returns null if registration fails (the
    /// caller should fall back to a static PrintWindow capture).</summary>
    public static DwmThumbnail? Create(IntPtr destHwnd, IntPtr source)
    {
        if (destHwnd == IntPtr.Zero || source == IntPtr.Zero)
            return null;
        if (DwmRegisterThumbnail(destHwnd, source, out IntPtr thumb) != 0 || thumb == IntPtr.Zero)
            return null;
        return new DwmThumbnail(destHwnd, thumb);
    }

    /// <summary>Positions/shows the thumbnail at the given destination rect (in
    /// PHYSICAL pixels relative to the destination window's client area). The DWM
    /// scales the source into this rect preserving aspect ratio. Pass
    /// <paramref name="visible"/> false to hide without unregistering.</summary>
    public void SetDestination(int left, int top, int right, int bottom, bool visible = true)
    {
        if (_thumb == IntPtr.Zero)
            return;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TN_RECTDESTINATION | DWM_TN_VISIBLE | DWM_TN_SOURCECLIENTAREAONLY | DWM_TN_OPACITY,
            rcDestination = new RECT { Left = left, Top = top, Right = right, Bottom = bottom },
            opacity = 255,
            fVisible = visible,
            // Render the FULL window so the rendered aspect equals DwmQueryThumbnailSourceSize
            // (the tile is sized to that), giving an exact fill with no one-sided black band.
            // Client-area-only would render a different (unmeasurable for WebView2 apps) aspect
            // → mismatch band. Win11's thin invisible resize border is the only surplus.
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    /// <summary>Hides the thumbnail overlay without releasing the registration.</summary>
    public void Hide()
    {
        if (_thumb == IntPtr.Zero)
            return;
        var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TN_VISIBLE, fVisible = false };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_thumb != IntPtr.Zero)
        {
            try { DwmUnregisterThumbnail(_thumb); } catch { }
            _thumb = IntPtr.Zero;
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    private const int DWM_TN_RECTDESTINATION = 0x00000001;
    private const int DWM_TN_OPACITY = 0x00000004;
    private const int DWM_TN_VISIBLE = 0x00000008;
    private const int DWM_TN_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }
}
