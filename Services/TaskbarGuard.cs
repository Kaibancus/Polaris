using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Polaris.Services;

/// <summary>
/// Masks the system auto-hide taskbar's reveal trigger under the bottom
/// side-dock's centre-50% activation band. The auto-hide bar only slides up when
/// the cursor reaches the monitor's bottom-edge pixels; holding the cursor a few
/// rows above that edge across the dock's centre band (and swallowing the edge
/// move) lets the dock pop there while the taskbar stays hidden. The outer 50% at
/// each end is untouched, so the taskbar can still be summoned there.
///
/// The hook lives on its OWN thread with a private message loop, isolated from
/// the WPF UI thread: a low-level hook proc that misses the LowLevelHooksTimeout
/// (~300ms) is silently skipped for that event, and the UI thread can stall that
/// long while compositing the fullscreen glass layer or animating — which is
/// exactly when (other windows open, system busier) the guard would leak.
///
/// The owner toggles <see cref="Active"/> (taskbar auto-hide AND bottom dock) from
/// its edge poll; the hot paths read only that simple volatile flag and never
/// touch WPF objects across threads.
/// </summary>
internal sealed class TaskbarGuard
{
    private Thread? _guardThread;
    private Thread? _guardPollThread;
    private uint _guardThreadId;
    private volatile bool _guardStop;
    private IntPtr _taskbarGuardHook = IntPtr.Zero;
    private LowLevelMouseProc? _taskbarGuardProc;   // keep alive while hooked

    private volatile bool _active;

    // Snapshot of every monitor's bounds, refreshed periodically by the poll
    // thread (and once at start). The hot paths use it to decide whether a
    // monitor's bottom edge is a TRUE outer edge (guarded) or has another display
    // adjacent below it (not guarded, so the cursor can cross down). Volatile
    // reference swap keeps reads lock-free.
    private volatile RECT[] _monitorRects = Array.Empty<RECT>();
    private int _lastMonitorScanTick;

    /// <summary>Set by the owner's edge poll: true when the taskbar is auto-hide
    /// AND the side dock is anchored to the bottom. The hot paths only act while
    /// this is set.</summary>
    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public void Start()
    {
        if (_guardThread != null)
            return;
        _guardStop = false;
        // Seed the monitor layout before the hook starts so the very first mouse
        // moves are evaluated against a valid snapshot.
        RefreshMonitorSnapshot();
        _guardThread = new Thread(TaskbarGuardThread)
        {
            IsBackground = true,
            Name = "PolarisTaskbarGuard",
        };
        _guardThread.SetApartmentState(ApartmentState.STA);
        _guardThread.Start();

        // A low-level hook is silently bypassed while an ELEVATED (admin) window
        // is in the foreground (UIPI) — e.g. an admin Terminal — so the clamp
        // would leak there. GetCursorPos/SetCursorPos are NOT UIPI-gated (they act
        // on the global cursor, not a window), so a parallel polling clamp covers
        // exactly that gap without elevating Polaris (which would break drag-add
        // from Explorer and launch every app elevated).
        _guardPollThread = new Thread(TaskbarGuardPollThread)
        {
            IsBackground = true,
            Name = "PolarisTaskbarGuardPoll",
        };
        _guardPollThread.Start();
    }

    public void Stop()
    {
        _guardStop = true;
        var pt = _guardPollThread;
        var t = _guardThread;
        if (t == null && pt == null)
            return;
        if (_guardThreadId != 0)
            PostThreadMessage(_guardThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t?.Join(500);
        pt?.Join(500);
        _guardThread = null;
        _guardPollThread = null;
        _guardThreadId = 0;
    }

    private void TaskbarGuardPollThread()
    {
        bool clipped = false;
        void Release()
        {
            if (clipped)
            {
                ClipCursor(IntPtr.Zero);
                clipped = false;
            }
        }

        while (!_guardStop)
        {
            if (!_active)
            {
                Release();
                Thread.Sleep(50);
                continue;
            }
            try
            {
                // Refresh the monitor layout at most ~once a second so display
                // hot-plug / rearrangement is reflected without per-tick cost.
                int tick = Environment.TickCount;
                if (_monitorRects.Length == 0 || tick - _lastMonitorScanTick >= 1000)
                {
                    RefreshMonitorSnapshot();
                    _lastMonitorScanTick = tick;
                }

                if (GetCursorPos(out POINT pt))
                {
                    IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        // Guard a monitor's bottom edge only when it is a TRUE outer
                        // edge (no display connected below it in the centre band). If
                        // another monitor sits below, leave the edge open so the cursor
                        // can cross down into it.
                        if (!HasNeighborBelow(mi.rcMonitor))
                        {
                            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            double bandStart = mi.rcMonitor.Left + w * CentreBandEdgeFraction;
                            double bandEnd = mi.rcMonitor.Right - w * CentreBandEdgeFraction;
                            int floor = mi.rcMonitor.Bottom - TaskbarGuardRows;
                            // PROACTIVELY confine the cursor above the bottom edge
                            // whenever it is within the centre band's X range. A
                            // reactive SetCursorPos runs only AFTER the cursor has
                            // already touched the edge — too late, the taskbar has
                            // revealed and our bounce-back lands inside the now-shown
                            // bar. ClipCursor makes the bottom rows physically
                            // unreachable in the band, so the edge is never touched
                            // and the bar never reveals; it is not UIPI-gated, so it
                            // works even when an elevated window is in the
                            // foreground. The clip is full monitor width (only the
                            // bottom is cut) so horizontal motion stays free, and it
                            // is released the moment the cursor leaves the band so
                            // the outer 50% can still summon the taskbar.
                            //
                            // The clip's TOP is the virtual-screen top, not this
                            // monitor's top: a single ClipCursor rect would otherwise
                            // trap the cursor against the primary monitor's top edge
                            // in the centre band, blocking it from crossing UP into a
                            // monitor positioned above the primary. Extending the top
                            // to the whole virtual desktop leaves the upward path free
                            // (the cursor is still kept on real display surface) while
                            // only the bottom edge stays clamped.
                            if (pt.X >= bandStart && pt.X <= bandEnd)
                            {
                                const int SM_YVIRTUALSCREEN = 77;
                                var clip = new RECT
                                {
                                    Left = mi.rcMonitor.Left,
                                    Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
                                    Right = mi.rcMonitor.Right,
                                    Bottom = floor,
                                };
                                ClipCursor(ref clip);
                                clipped = true;
                            }
                            else
                            {
                                Release();
                            }
                        }
                        else
                        {
                            Release();
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug("TaskbarGuard", "cursor clip/release iteration failed", ex); }
            Thread.Sleep(10);
        }
        Release();
    }

    private void TaskbarGuardThread()
    {
        _guardThreadId = GetCurrentThreadId();
        _taskbarGuardProc = TaskbarGuardProc;   // keep alive while hooked
        IntPtr hMod = GetModuleHandle(null);
        _taskbarGuardHook = SetWindowsHookEx(WH_MOUSE_LL, _taskbarGuardProc, hMod, 0);
        if (_taskbarGuardHook == IntPtr.Zero)
        {
            Log.Warn("TaskbarGuard", "SetWindowsHookEx failed; auto-hide guard inactive. " +
                "Win32 error " + Marshal.GetLastWin32Error());
            return;
        }

        // Pump this thread's message queue so the hook callback is dispatched
        // independently of the WPF UI thread. WM_QUIT (posted on shutdown) ends it.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_taskbarGuardHook);
        _taskbarGuardHook = IntPtr.Zero;
        _taskbarGuardProc = null;
    }

    private IntPtr TaskbarGuardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEMOVE && _active)
        {
            try
            {
                // Read only the fields we need straight from the struct (x@0, y@4,
                // flags@12) to keep the hot path allocation-free and fast.
                int px = Marshal.ReadInt32(lParam, 0);
                int py = Marshal.ReadInt32(lParam, 4);
                uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                // Skip our own SetCursorPos-injected moves (prevents recursion).
                if ((flags & LLMHF_INJECTED) == 0)
                {
                    var pt = new POINT { X = px, Y = py };
                    IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        // Mask the taskbar reveal only on a TRUE outer bottom edge.
                        // If another display is connected below this monitor, its
                        // bottom edge is left open so the cursor can cross down.
                        if (!HasNeighborBelow(mi.rcMonitor))
                        {
                            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            double bandStart = mi.rcMonitor.Left + w * CentreBandEdgeFraction;
                            double bandEnd = mi.rcMonitor.Right - w * CentreBandEdgeFraction;
                            // Hold the cursor clear of the very edge across the
                            // centre band, and SWALLOW the original edge event
                            // (return non-zero): on a fast flick a single move
                            // lands directly at the edge, and if the shell sees
                            // that coordinate it reveals the taskbar before our
                            // reposition registers. Discarding it means the shell
                            // only ever sees our injected, clamped position.
                            if (px >= bandStart && px <= bandEnd
                                && py >= mi.rcMonitor.Bottom - TaskbarGuardRows)
                            {
                                SetCursorPos(px, mi.rcMonitor.Bottom - TaskbarGuardRows);
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug("TaskbarGuard", "mouse hook handler failed", ex); }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Re-reads the current monitor layout into <see cref="_monitorRects"/>.
    /// Cheap (a handful of monitors) and called at most ~once a second from the poll
    /// loop, plus once at start, so display hot-plug / rearrangement is picked up.</summary>
    private void RefreshMonitorSnapshot()
    {
        var rects = new List<RECT>(4);
        bool Collect(IntPtr hMon, IntPtr hdc, ref RECT lprc, IntPtr data)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
                rects.Add(mi.rcMonitor);
            return true;
        }
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
            _monitorRects = rects.ToArray();
        }
        catch (Exception ex) { Log.Debug("Monitors", "monitor snapshot enumeration failed", ex); }
    }

    /// <summary>True when another display sits directly below <paramref name="m"/>'s
    /// bottom edge and overlaps the guarded centre band — i.e. the cursor should be
    /// allowed to cross downward there, so no taskbar-guard wall is placed. False for
    /// a true outer bottom edge (nothing below in the band), which IS guarded.</summary>
    private bool HasNeighborBelow(in RECT m)
    {
        int w = m.Right - m.Left;
        double bandStart = m.Left + w * CentreBandEdgeFraction;
        double bandEnd = m.Right - w * CentreBandEdgeFraction;
        const int tol = 2;   // tolerate a 1-2px seam between adjacent monitors
        var rects = _monitorRects;
        foreach (var r in rects)
        {
            // A monitor whose TOP meets this monitor's BOTTOM (self never matches,
            // since its own Top != its own Bottom) and whose horizontal span covers
            // any part of the guarded band counts as "connected below".
            if (Math.Abs(r.Top - m.Bottom) <= tol && r.Right > bandStart && r.Left < bandEnd)
                return true;
        }
        return false;
    }

    private const int WH_MOUSE_LL = 14;
    // Fraction of a monitor's width excluded at EACH end when defining the guarded
    // "centre band": 0.25 leaves the central 50% guarded and the outer 50% free to
    // summon the taskbar. Must mirror the side dock's bottom-edge trigger band.
    private const double CentreBandEdgeFraction = 0.25;
    // Pixel rows above the monitor's bottom edge that the cursor is held clear of
    // inside the centre band, so the auto-hide taskbar's reveal tolerance never
    // fires there. Stays below the dock's edge reach so the dock still pops.
    private const int TaskbarGuardRows = 3;
    private const int WM_MOUSEMOVE = 0x0200;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint WM_QUIT = 0x0012;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

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
