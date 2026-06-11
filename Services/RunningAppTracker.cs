using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Polaris.Services;

/// <summary>
/// Detects whether the executable behind an <see cref="Models.AppEntry"/> is
/// currently running, and can bring its existing window to the foreground
/// instead of launching a second instance.
/// </summary>
public static class RunningAppTracker
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public System.Drawing.Point ptMinPosition;
        public System.Drawing.Point ptMaxPosition;
        public System.Drawing.Rectangle rcNormalPosition;
    }

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;

    private const int WPF_RESTORETOMAXIMIZED = 0x0002;

    /// <summary>
    /// Returns true if at least one running process was started from
    /// <paramref name="exePath"/> and currently owns a visible main window.
    /// </summary>
    public static bool IsRunning(string exePath)
    {
        return FindWindowProcess(exePath) != null;
    }

    /// <summary>
    /// Snapshot of the processes that currently own a visible main window:
    /// full executable paths where the module path is readable, plus the
    /// base-names of the remaining processes whose path could not be read
    /// (e.g. elevated or cross-architecture processes).
    /// </summary>
    public sealed class RunningSnapshot
    {
        public HashSet<string> Paths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NamesWithoutPath { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Full executable paths of processes that are running but do
        /// NOT currently own a visible main window (e.g. apps minimized to the
        /// system tray, which hide their window). Matched by exact path only.</summary>
        public HashSet<string> PathsNoWindow { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes, on the calling (ideally background) thread, a snapshot of the
    /// running processes. Processes that own a visible main window are recorded
    /// in <see cref="RunningSnapshot.Paths"/> (or <see cref="RunningSnapshot.NamesWithoutPath"/>
    /// when their path can't be read). Processes without a visible main window
    /// but with a readable path are recorded in <see cref="RunningSnapshot.PathsNoWindow"/>,
    /// so apps minimized to the system tray are still detected. Recording full
    /// executable paths (rather than base-names only) prevents two different
    /// programs that share a base name from being mistaken for one another.
    /// </summary>
    public static RunningSnapshot SnapshotRunning()
    {
        var snapshot = new RunningSnapshot();
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return snapshot;
        }

        foreach (var p in all)
        {
            try
            {
                bool hasWindow = p.MainWindowHandle != IntPtr.Zero;

                string? modulePath = null;
                try
                {
                    modulePath = p.MainModule?.FileName;
                }
                catch
                {
                    // Access denied / 32-vs-64 — fall back to a name match.
                }

                if (hasWindow)
                {
                    if (!string.IsNullOrEmpty(modulePath))
                        snapshot.Paths.Add(Path.GetFullPath(modulePath));
                    else
                        snapshot.NamesWithoutPath.Add(p.ProcessName);
                }
                else if (!string.IsNullOrEmpty(modulePath))
                {
                    // No visible window (possibly tray-minimized). Only trust an
                    // exact path match here; name-only matching for windowless
                    // processes would over-report background helpers.
                    snapshot.PathsNoWindow.Add(Path.GetFullPath(modulePath));
                }
            }
            catch
            {
                // Process exited mid-enumeration; ignore.
            }
            finally
            {
                p.Dispose();
            }
        }
        return snapshot;
    }

    /// <summary>
    /// Tests <paramref name="exePath"/> against a running snapshot. Matches on
    /// the full executable path when available, and only falls back to a
    /// base-name match for processes whose path could not be read — so a
    /// same-named program at a different path is not reported as running.
    /// </summary>
    public static bool IsRunningInSnapshot(string exePath, RunningSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(exePath) || snapshot == null)
            return false;
        try
        {
            string full;
            try { full = Path.GetFullPath(exePath); }
            catch { full = exePath; }

            if (snapshot.Paths.Contains(full))
                return true;

            // Tray-minimized (windowless) processes: exact path match only.
            if (snapshot.PathsNoWindow.Contains(full))
                return true;

            if (snapshot.NamesWithoutPath.Count == 0)
                return false;

            string name = Path.GetFileNameWithoutExtension(exePath);
            return !string.IsNullOrEmpty(name)
                && snapshot.NamesWithoutPath.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a shell-namespace pin (This PC,
    /// Recycle Bin… — a "::{CLSID}" or "shell:" target, not a real exe) and a
    /// File-Explorer window matching its display <paramref name="name"/> is
    /// currently open. Such items all run inside the shared explorer.exe and so
    /// can only be told apart by their window title.
    /// </summary>
    public static bool IsShellItemRunning(string name, string path, IReadOnlyList<string> explorerTitles)
    {
        if (explorerTitles == null || explorerTitles.Count == 0)
            return false;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            return false;
        bool isShell = path.StartsWith("::", StringComparison.Ordinal)
            || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        if (!isShell)
            return false;
        foreach (var t in explorerTitles)
        {
            if (!string.IsNullOrEmpty(t) && t.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Brings the running instance of <paramref name="exePath"/> to the
    /// foreground (restoring it if minimized). Returns true on success.
    /// </summary>
    public static bool ActivateExisting(string exePath)
    {
        var proc = FindWindowProcess(exePath);
        if (proc == null)
            return false;

        IntPtr h = proc.MainWindowHandle;
        if (h == IntPtr.Zero)
            return false;

        ActivateWindow(h);
        return true;
    }

    /// <summary>
    /// Brings the running instance of the app entry to the foreground. Packaged
    /// apps (the new Teams / Outlook, Store apps…) are pinned as
    /// <c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>; matching those by the
    /// "explorer.exe" path would activate the desktop shell instead of the app,
    /// so when the entry is such a launcher we locate the window by its
    /// Application User Model ID. Returns true when a window was activated.
    /// </summary>
    public static bool ActivateExisting(string path, string? arguments)
    {
        string? aumid = WindowPreviewService.TryGetLauncherAumid(path, arguments);
        if (aumid != null)
        {
            var windows = WindowPreviewService.GetWindowsByAumid(aumid);
            if (windows.Count == 0)
                return false;
            ActivateWindow(windows[0].Handle);
            return true;
        }

        return ActivateExisting(path);
    }

    /// <summary>Restores (preserving the maximized state) and foregrounds the
    /// window <paramref name="h"/>.</summary>
    private static void ActivateWindow(IntPtr h)
    {
        if (h == IntPtr.Zero)
            return;

        // Read the window's placement so the maximized state is preserved across
        // the activation. Relying solely on IsIconic + ShowWindow(SW_RESTORE)
        // can drop a window out of its maximized state into a normal "windowed"
        // size on some apps, which is exactly the "opens windowed" symptom.
        var pl = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        bool gotPlacement = GetWindowPlacement(h, ref pl);
        bool iconic = IsIconic(h);

        // The window is (or was, before minimizing) maximized when either it is
        // currently shown maximized, or it is minimized with the
        // "restore-to-maximized" flag set.
        bool maximized = gotPlacement &&
            (pl.showCmd == SW_SHOWMAXIMIZED ||
             (iconic && (pl.flags & WPF_RESTORETOMAXIMIZED) != 0));

        if (iconic)
        {
            // Explicitly re-maximize when that was the prior state; otherwise a
            // plain restore to the normal windowed size.
            ShowWindow(h, maximized ? SW_SHOWMAXIMIZED : SW_RESTORE);
        }
        else if (maximized)
        {
            // Already visible and maximized — keep it maximized (SW_SHOW would
            // also preserve it, but SW_SHOWMAXIMIZED guards against any app that
            // misreports its state).
            ShowWindow(h, SW_SHOWMAXIMIZED);
        }
        else
        {
            ShowWindow(h, SW_SHOW);
        }

        ForceForeground(h);
    }

    /// <summary>Reliably brings <paramref name="h"/> to the foreground. A plain
    /// <c>SetForegroundWindow</c> is frequently refused by Windows when the call
    /// comes from a background process (the foreground-lock); attaching to the
    /// current foreground thread's input queue lifts that restriction.</summary>
    private static void ForceForeground(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == h)
            return;

        uint targetThread = GetWindowThreadProcessId(h, out _);
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint thisThread = GetCurrentThreadId();

        bool attachedFg = fgThread != 0 && fgThread != thisThread &&
            AttachThreadInput(thisThread, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != thisThread &&
            targetThread != fgThread &&
            AttachThreadInput(thisThread, targetThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(thisThread, targetThread, false);
            if (attachedFg)
                AttachThreadInput(thisThread, fgThread, false);
        }
    }

    /// <summary>
    /// Watches a freshly-launched process and, if its main window first appears
    /// minimized, restores it to a normal window and brings it forward.
    ///
    /// Polaris runs as a background tray process, so apps it launches can
    /// inherit a minimized show state (via STARTUPINFO/SW_SHOWDEFAULT) and open
    /// minimized to the taskbar instead of as a normal window. This nudges such
    /// windows back to normal. Windows that open normally are left untouched.
    /// </summary>
    public static void EnsureRestoredWhenReady(Process? proc)
    {
        if (proc == null)
            return;

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // Poll for up to ~4s for the app's main window to appear.
                for (int i = 0; i < 40; i++)
                {
                    await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);

                    try
                    {
                        proc.Refresh();
                        if (proc.HasExited)
                            return;
                    }
                    catch
                    {
                        return;
                    }

                    IntPtr h = proc.MainWindowHandle;
                    if (h == IntPtr.Zero)
                        continue;

                    // The window exists. Only intervene if it came up minimized.
                    if (IsIconic(h))
                    {
                        ShowWindow(h, SW_RESTORE);
                        SetForegroundWindow(h);
                    }
                    return;
                }
            }
            catch
            {
                // Best-effort only — never let a launch fail because of this.
            }
        });
    }

    /// <summary>
    /// Finds a running process whose executable matches <paramref name="exePath"/>
    /// and that has a non-zero main window handle.
    /// </summary>
    private static Process? FindWindowProcess(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return null;

        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(exePath);
        }
        catch
        {
            fullTarget = exePath;
        }

        string targetName;
        try
        {
            targetName = Path.GetFileNameWithoutExtension(exePath);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(targetName))
            return null;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(targetName);
        }
        catch
        {
            return null;
        }

        Process? fallback = null;
        foreach (var p in candidates)
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero)
                    continue;

                // Prefer an exact path match when the module path is readable;
                // otherwise fall back to the name-only match (access to
                // MainModule can be denied for elevated processes).
                string? modulePath = null;
                try
                {
                    modulePath = p.MainModule?.FileName;
                }
                catch
                {
                    // Access denied / 32-vs-64 — keep as a name-only fallback.
                }

                if (modulePath != null &&
                    string.Equals(Path.GetFullPath(modulePath), fullTarget,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }

                fallback ??= p;
            }
            catch
            {
                // Process exited between enumeration and inspection.
            }
        }

        return fallback;
    }
}
