using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Polaris.Services;

/// <summary>
/// Dismisses the docks when the user left-clicks anywhere outside every Polaris
/// window while the main dock is open. A WH_MOUSE_LL hook watches for a left
/// button-down and resolves the window under the cursor: if that window belongs
/// to ANOTHER process (the desktop, another app, the taskbar…) the click is
/// "outside" and <see cref="ClickedOutside"/> fires. Clicks landing on any
/// Polaris-owned window — the dock slabs, hover previews, right-click menus —
/// share our process id and are ignored, so the dock's own interactions keep
/// working. Because the docks are per-pixel-alpha layered windows, a click on a
/// transparent region passes straight through to the window behind it (a
/// different process), so empty space around the slab also dismisses correctly.
///
/// The hook lives on its OWN thread with a private message loop (mirroring
/// <see cref="TaskbarGuard"/>), isolated from the WPF UI thread so a busy UI
/// frame can't make the low-level hook miss the LowLevelHooksTimeout. The owner
/// toggles <see cref="Active"/> when the main dock shows/hides; the hot path
/// reads only that volatile flag and never touches WPF objects across threads.
/// </summary>
internal sealed class ClickAwayWatcher
{
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook = IntPtr.Zero;
    private LowLevelMouseProc? _proc;          // keep alive while hooked
    private volatile bool _active;
    private readonly uint _ownPid;

    /// <summary>Raised (on the hook thread) when a left button-down lands on a
    /// window that does not belong to this process while <see cref="Active"/> is
    /// set. Handlers must marshal to the UI thread before touching WPF.</summary>
    public event Action? ClickedOutside;

    public ClickAwayWatcher()
    {
        _ownPid = GetCurrentProcessId();
    }

    /// <summary>Set by the owner: true only while the main dock is open. The hot
    /// path acts on a click only while this is set.</summary>
    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public void Start()
    {
        if (_thread != null)
            return;
        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "PolarisClickAwayWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        var t = _thread;
        if (t == null)
            return;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(500);
        _thread = null;
        _threadId = 0;
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookProc;                       // keep alive while hooked
        IntPtr hMod = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Warn("ClickAwayWatcher", "SetWindowsHookEx failed; click-away dismiss inactive. " +
                "Win32 error " + Marshal.GetLastWin32Error());
            return;
        }

        // Pump this thread's message queue so the hook callback runs independently
        // of the WPF UI thread. WM_QUIT (posted on Stop) ends the loop.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_LBUTTONDOWN && _active)
        {
            try
            {
                // MSLLHOOKSTRUCT: pt.x@0, pt.y@4, mouseData@8, flags@12.
                int px = Marshal.ReadInt32(lParam, 0);
                int py = Marshal.ReadInt32(lParam, 4);
                uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                // Ignore programmatically-injected clicks (e.g. our own).
                if ((flags & LLMHF_INJECTED) == 0)
                {
                    var pt = new POINT { X = px, Y = py };
                    IntPtr hwnd = WindowFromPoint(pt);
                    IntPtr root = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
                    uint pid = 0;
                    if (root != IntPtr.Zero)
                        GetWindowThreadProcessId(root, out pid);
                    // A click resolving to no window, or to a window owned by
                    // another process, is outside every Polaris surface. A click on
                    // one of our own windows still counts as "outside" when it lands
                    // in the transparent headroom around the dock (the GPU docks are
                    // a large HWND whose padding hit-tests as HTTRANSPARENT), so the
                    // dock dismisses when the user clicks just outside the visible slab.
                    bool ownSurface = root != IntPtr.Zero && pid == _ownPid
                        && SendMessage(root, WM_NCHITTEST, IntPtr.Zero, MakeLParam(px, py)) != (IntPtr)HTTRANSPARENT;
                    if (!ownSurface)
                        ClickedOutside?.Invoke();   // non-blocking: handler marshals to UI
                }
            }
            catch (Exception ex) { Log.Debug("ClickAwayWatcher", "mouse hook handler failed", ex); }
        }
        // Never swallow the click — the user's click should still reach whatever
        // they clicked; we only dismiss the dock alongside it.
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint WM_QUIT = 0x0012;
    private const uint GA_ROOT = 2;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    private static IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        // Short timeout so a busy UI thread never stalls the mouse hook.
        return SendMessageTimeout(hwnd, msg, w, l, SMTO_ABORTIFHUNG, 60, out var res) == IntPtr.Zero
            ? IntPtr.Zero : res;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
}
