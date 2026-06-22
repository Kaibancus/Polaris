using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    private static readonly WndProc s_wndProc = WndProcImpl;
    private static ushort s_atom;

    private IntPtr _owner;
    private readonly Func<uint, IntPtr, int, int, (bool handled, IntPtr result)> _forward;
    private readonly Action<List<string>, byte[]?, int, int> _onDrop;
    private readonly Action<(int x, int y)?>? _onDragMove;
    private OleDropTarget? _ole;
    private IntPtr _hwnd;

    /// <summary>Re-point the shim at a new owner window (the dock recreates its HWND on
    /// rebuild). Keeping the single shim alive across rebuilds avoids a dispose/create gap
    /// during which an external drag would find no drop target.</summary>
    public void SetOwner(IntPtr owner) => _owner = owner;

    public DropShimWindow(IntPtr owner,
        Func<uint, IntPtr, int, int, (bool handled, IntPtr result)> forward,
        Action<List<string>, byte[]?, int, int> onDrop,
        Action<(int x, int y)?>? onDragMove = null)
    {
        _owner = owner;
        _forward = forward;
        _onDrop = onDrop;
        _onDragMove = onDragMove;
        Create();
    }

    private void Create()
    {
        if (s_atom == 0)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),   // standard arrow, not the busy/AppStarting cursor
                hbrBackground = GetStockObject(BLACK_BRUSH),   // alpha 1 over black ≈ invisible
                lpszClassName = "PolarisDropShim",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "PolarisDropShim", string.Empty, WS_POPUP,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
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
        _ole = new OleDropTarget(_hwnd, _onDrop) { OnDragMove = pt => _onDragMove?.Invoke(pt) };
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
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, Math.Max(1, w), Math.Max(1, h), SWP_NOACTIVATE);
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_HIDE);
    }

    public void Dispose()
    {
        _ole?.Revoke(); _ole = null;
        if (_hwnd != IntPtr.Zero)
        {
            s_instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (!s_instances.TryGetValue(h, out var self))
            return DefWindowProcW(h, msg, w, l);
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
                if (GetWindowRect(h, out RECT r))
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
        return DefWindowProcW(h, msg, w, l);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x00000008,
        WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205,
        WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_DROPFILES = 0x0233;
    private const int MA_NOACTIVATE = 3, LWA_ALPHA = 2, SW_SHOWNOACTIVATE = 4, SW_HIDE = 0, BLACK_BRUSH = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    private static readonly IntPtr IDC_ARROW = (IntPtr)32512;
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, System.Text.StringBuilder? buf, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out POINT pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);
}
