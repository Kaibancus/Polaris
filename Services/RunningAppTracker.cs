using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopPanel.Services;

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
    /// Computes, on the calling (ideally background) thread, the set of process
    /// base-names that currently own a visible main window. Callers can then test
    /// each entry cheaply with <see cref="IsRunningByName"/> without enumerating
    /// the process list once per icon on the UI thread.
    /// </summary>
    public static HashSet<string> SnapshotRunningWindowNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return names;
        }

        foreach (var p in all)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    names.Add(p.ProcessName);
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
        return names;
    }

    /// <summary>Tests <paramref name="exePath"/> against a name snapshot.</summary>
    public static bool IsRunningByName(string exePath, HashSet<string> runningNames)
    {
        if (string.IsNullOrWhiteSpace(exePath) || runningNames.Count == 0)
            return false;
        try
        {
            string name = Path.GetFileNameWithoutExtension(exePath);
            return !string.IsNullOrEmpty(name) && runningNames.Contains(name);
        }
        catch
        {
            return false;
        }
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
