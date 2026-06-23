using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Polaris.Interop;

namespace Polaris.Services.Gpu;

/// <summary>
/// A near-invisible top-level overlay that sits directly above a DirectComposition dock
/// window to receive OLE drag-drop and the initial mouse press, then forwards them back
/// to the dock. Composition-only windows (WS_EX_NOREDIRECTIONBITMAP) cannot be OLE drop
/// targets nor receive WM_DROPFILES — the OS never routes drag events to them — so a
/// normal redirected window is needed to catch drags from Explorer / the desktop. This
/// mirrors the "legacy window" trick browsers use over their composition surfaces.
///
/// The shim hit-tests by forwarding WM_NCHITTEST to the owner (so it is HTCLIENT exactly
/// where the dock is interactive and HTTRANSPARENT elsewhere, passing clicks through),
/// forwards mouse messages to the owner's handler (the dock's SetCapture then routes the
/// rest of a drag straight to the dock), and forwards drops to the owner.
/// </summary>
internal sealed class DropShimWindow : IDisposable
{
    // Static WndProc + instance map (same pattern as MainDockWindowGpu) so the class's
    // lpfnWndProc delegate is never collected — a per-instance delegate would dangle when
    // a shim is disposed on dock rebuild and crash the next window created with the class.
    private static readonly Dictionary<IntPtr, DropShimWindow> s_instances = new();
    private static ushort s_atom;

    private IntPtr _owner;
    private readonly Func<uint, IntPtr, int, int, (bool handled, IntPtr result)> _forward;
    private readonly Action<List<string>, byte[]?, int, int> _onDrop;
    private readonly Action<(int x, int y)?, string?>? _onDragMove;
    private OleDropTarget? _ole;
    private IntPtr _hwnd;

    /// <summary>Re-point the shim at a new owner window (the dock recreates its HWND on
    /// rebuild). Keeping the single shim alive across rebuilds avoids a dispose/create gap
    /// during which an external drag would find no drop target.</summary>
    public void SetOwner(IntPtr owner) => _owner = owner;

    public DropShimWindow(IntPtr owner,
        Func<uint, IntPtr, int, int, (bool handled, IntPtr result)> forward,
        Action<List<string>, byte[]?, int, int> onDrop,
        Action<(int x, int y)?, string?>? onDragMove = null)
    {
        _owner = owner;
        _forward = forward;
        _onDrop = onDrop;
        _onDragMove = onDragMove;
        Create();
    }

    private void Create()
    {
        _hwnd = Win32.CreateWindow("PolarisDropShim",
            Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            10, 10, s_wndProc, ref s_atom,
            Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),   // standard arrow, not the busy/AppStarting cursor
            Win32.GetStockObject(Win32.BLACK_BRUSH));          // alpha 1 over black ≈ invisible
        if (_hwnd == IntPtr.Zero)
            return;
        s_instances[_hwnd] = this;
        // alpha 1/255: effectively invisible but the window still hit-tests + accepts
        // input/drops (a fully-transparent layered window would be click-through).
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);
        // The shim is a normal redirected window (unlike the composition dock, which the OS
        // refuses to route drag-drop to), so it can host a full OLE IDropTarget. OLE gives
        // real-time DragEnter/DragOver/Drop — needed for the drag-follow preview and for
        // CFSTR_SHELLIDLIST shell items (This PC / Recycle Bin / File Explorer) that the
        // legacy WM_DROPFILES path can't carry.
        Allow(_hwnd);
        _ole = new OleDropTarget(_hwnd, _onDrop) { OnDragMove = (pt, src) => _onDragMove?.Invoke(pt, src) };
        _ole.Register();
    }

    /// <summary>Let drag-drop messages through UIPI (no-op if same integrity).</summary>
    private static void Allow(IntPtr h)
    {
        foreach (uint m in new uint[] { WM_DROPFILES, 0x004A /*WM_COPYDATA*/, 0x0049 /*WM_COPYGLOBALDATA*/ })
            try { ChangeWindowMessageFilterEx(h, m, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero); } catch { }
    }

    /// <summary>Position the shim exactly over the owner's interactive box (physical px).</summary>
    public void SetBounds(int x, int y, int w, int h)
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, Math.Max(1, w), Math.Max(1, h), Win32.SWP_NOACTIVATE);
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
    }

    public void Dispose()
    {
        _ole?.Revoke(); _ole = null;
        if (_hwnd != IntPtr.Zero)
        {
            s_instances.Remove(_hwnd);
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (!s_instances.TryGetValue(h, out var self))
            return Win32.DefWindowProcW(h, msg, w, l);
        switch (msg)
        {
            case WM_NCHITTEST:
                // Mirror the owner's hit region (screen coords are window-independent).
                return SendMessageW(self._owner, WM_NCHITTEST, IntPtr.Zero, l);
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
            case WM_MOUSEMOVE:
            {
                int cx = unchecked((short)((long)l & 0xFFFF));
                int cy = unchecked((short)(((long)l >> 16) & 0xFFFF));
                if (Win32.GetWindowRect(h, out Win32.RECT r))
                {
                    var (handled, res) = self._forward(msg, w, r.Left + cx, r.Top + cy);
                    if (handled) return res;
                }
                return IntPtr.Zero;
            }
            case WM_MOUSEWHEEL:
            {
                int sx = unchecked((short)((long)l & 0xFFFF));
                int sy = unchecked((short)(((long)l >> 16) & 0xFFFF));
                var (handled, res) = self._forward(msg, w, sx, sy);
                if (handled) return res;
                return IntPtr.Zero;
            }
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    private static readonly Win32.WndProc s_wndProc = WndProcImpl;

    private const uint WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205,
        WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_DROPFILES = 0x0233;
    private const int MA_NOACTIVATE = 3, LWA_ALPHA = 2;

    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, int flags);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, System.Text.StringBuilder? buf, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out Win32.POINT pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);
}
