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

        if (IsIconic(h))
        {
            // Un-minimize while preserving the prior state: if the window was
            // maximized (full-screen) before minimizing, restore it maximized;
            // otherwise restore it to its normal windowed size.
            var pl = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            bool wasMaximized = GetWindowPlacement(h, ref pl)
                && (pl.flags & WPF_RESTORETOMAXIMIZED) != 0;
            ShowWindow(h, wasMaximized ? SW_MAXIMIZE : SW_RESTORE);
        }
        else
        {
            // Already visible — just bring it forward without changing whether
            // it is maximized or windowed (SW_SHOW preserves the current state).
            ShowWindow(h, SW_SHOW);
        }
        SetForegroundWindow(h);
        return true;
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
