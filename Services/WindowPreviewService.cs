using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Polaris.Services;

/// <summary>One top-level window belonging to an application.</summary>
public sealed class WindowPreview
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public BitmapSource? Thumbnail { get; set; }
}

/// <summary>A distinct application that currently owns a taskbar (alt-tab)
/// window, identified by its executable path and (for packaged apps) its
/// Application User Model ID, plus a representative window to activate.</summary>
public sealed class TaskbarApp
{
    public string Path { get; init; } = "";
    public string? Aumid { get; init; }
    public IntPtr Window { get; init; }
}

/// <summary>
/// Enumerates the visible top-level windows owned by the process(es) behind an
/// app entry, captures a still thumbnail of each (so multi-window apps such as
/// browsers / Explorer can show per-window previews on hover), and activates a
/// chosen window.
/// </summary>
public static class WindowPreviewService
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess,
        ref uint applicationUserModelIdLength, StringBuilder? applicationUserModelId);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToStringAlloc(ref PROPVARIANT propvar, out IntPtr ppszOut);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    private static Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // PKEY_AppUserModel_ID: fmtid {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5.
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Most recent successful thumbnail per window handle. Lets us fall
    /// back to the last good capture when a window is minimized (PrintWindow
    /// cannot render a minimized window). Values are frozen, so cross-thread safe.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, BitmapSource> _thumbCache = new();

    /// <summary>
    /// Returns the visible, alt-tab-style top-level windows owned by the
    /// running process(es) launched from <paramref name="exePath"/>.
    /// </summary>
    public static List<WindowPreview> GetWindows(string exePath)
    {
        var result = new List<WindowPreview>();
        var pids = GetPidsForExe(exePath);
        if (pids.Count == 0)
            return result;

        int ownPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (GetWindowThreadProcessId(hWnd, out uint pid) == 0)
                return true;
            if (pid == ownPid || !pids.Contains((int)pid))
                return true;
            if (!IsAltTabWindow(hWnd))
                return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            result.Add(new WindowPreview { Handle = hWnd, Title = title });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Returns the previewable windows for an app entry, resolving packaged
    /// (MSIX/UWP) apps correctly. Such apps are pinned as
    /// <c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>, so matching by the
    /// explorer.exe process would wrongly return File Explorer's windows. When
    /// the entry is one of these launchers we match by the window's Application
    /// User Model ID instead; otherwise we fall back to process-path matching.
    /// </summary>
    public static List<WindowPreview> GetWindowsForEntry(string path, string? arguments)
    {
        string? aumid = TryGetLauncherAumid(path, arguments);
        return aumid != null ? GetWindowsByAumid(aumid) : GetWindows(path);
    }

    /// <summary>
    /// If <paramref name="path"/>/<paramref name="arguments"/> describe a
    /// packaged-app launcher (<c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>),
    /// returns the AUMID; otherwise null.
    /// </summary>
    public static string? TryGetLauncherAumid(string path, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;
        try
        {
            if (!string.Equals(Path.GetFileName(path), "explorer.exe",
                    StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch
        {
            return null;
        }

        const string token = "shell:AppsFolder\\";
        int i = arguments.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;

        string aumid = arguments.Substring(i + token.Length).Trim().Trim('"');
        int ws = aumid.IndexOf(' ');
        if (ws > 0)
            aumid = aumid.Substring(0, ws);
        return string.IsNullOrWhiteSpace(aumid) ? null : aumid;
    }

    /// <summary>
    /// Returns the visible, alt-tab-style top-level windows whose Application
    /// User Model ID matches <paramref name="aumid"/> (used for packaged apps
    /// such as the new Teams / Outlook).
    /// </summary>
    public static List<WindowPreview> GetWindowsByAumid(string aumid)
    {
        var result = new List<WindowPreview>();
        int ownPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                return true;
            if (!IsAltTabWindow(hWnd))
                return true;

            string? winAumid = GetWindowAumid(hWnd);
            if (winAumid == null ||
                !string.Equals(winAumid, aumid, StringComparison.OrdinalIgnoreCase))
                return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            result.Add(new WindowPreview { Handle = hWnd, Title = title });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Reads a window's Application User Model ID from its property
    /// store, or null if it has none (most non-packaged windows).</summary>
    private static string? GetWindowAumid(IntPtr hWnd)
    {
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hWnd, ref iid, out IPropertyStore? store) != 0
                || store == null)
                return null;
            try
            {
                var key = PKEY_AppUserModel_ID;
                if (store.GetValue(ref key, out PROPVARIANT pv) != 0)
                    return null;
                try
                {
                    if (PropVariantToStringAlloc(ref pv, out IntPtr str) != 0
                        || str == IntPtr.Zero)
                        return null;
                    try { return Marshal.PtrToStringUni(str); }
                    finally { Marshal.FreeCoTaskMem(str); }
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates the distinct applications that currently own a taskbar
    /// (alt-tab) window — one entry per executable / packaged app — so the panel
    /// can surface running programs that are not pinned into Polaris. Excludes
    /// our own process. Each entry carries a representative window for activation.
    /// </summary>
    public static List<TaskbarApp> GetTaskbarApps()
    {
        var result = new List<TaskbarApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ownPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            // Only genuine alt-tab windows: this excludes background/tray apps
            // whose only top-level window is a WS_EX_TOOLWINDOW (e.g. PixPin,
            // Snipaste) as well as the Windows shell surfaces. The running strip
            // therefore lists only apps the user actually has a real window for.
            if (!IsAltTabWindow(hWnd))
                return true;
            if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                return true;
            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            string? path = GetProcessInfo(pid, out string? procAumid);
            if (string.IsNullOrWhiteSpace(path))
                return true;

            // Prefer the process's packaged identity (reliable for Win32-hosted
            // packaged apps like new Teams/Outlook, whose windows do not expose
            // an AUMID via the window property store), then the window AUMID.
            string? aumid = procAumid;
            if (string.IsNullOrWhiteSpace(aumid))
                aumid = GetWindowAumid(hWnd);
            if (string.IsNullOrWhiteSpace(aumid))
                aumid = null;
            // Distinct by packaged identity when present, else by exe path, so a
            // multi-window app shows a single tile.
            string key = aumid ?? path;
            if (!seen.Add(key))
                return true;

            result.Add(new TaskbarApp { Path = path, Aumid = aumid, Window = hWnd });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Titles of every File-Explorer (explorer.exe) alt-tab window that
    /// is currently open. Used to light the running indicator on pinned
    /// shell-namespace items (This PC, Recycle Bin…) whose "process" is the
    /// shared explorer.exe and can only be told apart by their window title.</summary>
    public static List<string> GetExplorerWindowTitles()
    {
        var titles = new List<string>();
        int ownPid = Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            if (!IsAltTabWindow(hWnd))
                return true;
            if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                return true;
            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;
            string? path = GetProcessInfo(pid, out string? _aumid);
            if (string.IsNullOrWhiteSpace(path))
                return true;
            if (string.Equals(Path.GetFileName(path), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return titles;
    }


    /// <summary>Full executable path and packaged AUMID (null when unpackaged)
    /// of a process id; path is null if the process is inaccessible.</summary>
    private static string? GetProcessInfo(uint pid, out string? aumid)
    {
        aumid = null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                path = sb.ToString();

            uint len = 0;
            int rc = GetApplicationUserModelId(h, ref len, null);
            if (rc == ERROR_INSUFFICIENT_BUFFER && len > 0)
            {
                var aumidBuf = new StringBuilder((int)len);
                if (GetApplicationUserModelId(h, ref len, aumidBuf) == 0)
                    aumid = aumidBuf.ToString();
            }

            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// Background "warm-up": for every running window of the given apps, refresh
    /// the cached thumbnail while the window is still visible. This way, once a
    /// window is minimized (and can no longer be captured), the hover preview can
    /// fall back to the most recent frame captured just before it was minimized.
    /// Minimized windows are skipped (CaptureThumbnail keeps their cached frame).
    /// Entries for windows that no longer exist are pruned.
    /// </summary>
    public static void WarmCache(IEnumerable<(string Path, string? Arguments)> apps, int maxWidth)
    {
        var live = new HashSet<IntPtr>();
        foreach (var (path, args) in apps)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            foreach (var w in GetWindowsForEntry(path, args))
            {
                live.Add(w.Handle);
                // Captures + caches when visible; returns the cached frame (no
                // overwrite) when the window is minimized.
                CaptureThumbnail(w.Handle, maxWidth);
            }
        }

        foreach (var key in _thumbCache.Keys)
            if (!live.Contains(key))
                _thumbCache.TryRemove(key, out _);
    }

    /// <summary>Returns the last captured thumbnail for this window from the
    /// cache without performing a (slow) fresh PrintWindow capture, or null if
    /// none is cached yet. Lets the preview popup render instantly with the
    /// previous frame while a fresh capture runs in the background.</summary>
    public static BitmapSource? TryGetCachedThumbnail(IntPtr hWnd)
        => _thumbCache.TryGetValue(hWnd, out var cached) ? cached : null;

    /// <summary>
    /// Captures a still bitmap of the window, scaled so its width is at most
    /// <paramref name="maxWidth"/>. Returns a frozen <see cref="BitmapSource"/>
    /// (safe to hand to the UI thread) or null if capture failed.
    /// </summary>
    public static BitmapSource? CaptureThumbnail(IntPtr hWnd, int maxWidth)
    {
        // A minimized window is not rendered, so PrintWindow would return a blank
        // or garbage bitmap. Fall back to the last good capture we cached while
        // the window was visible (e.g. from an earlier hover).
        if (IsIconic(hWnd))
            return _thumbCache.TryGetValue(hWnd, out var cachedMin) ? cachedMin : null;

        if (!GetWindowRect(hWnd, out RECT r))
            return _thumbCache.TryGetValue(hWnd, out var cachedNoRect) ? cachedNoRect : null;

        // Our process is PerMonitorV2-aware, so GetWindowRect reports the true
        // PHYSICAL pixel size. PrintWindow, however, asks the target window to
        // paint itself at *its own* DPI awareness: a system-DPI-aware app on a
        // scaled monitor renders at its smaller logical size and would only fill
        // a corner of a physical-sized bitmap (the rest left black — the "only a
        // corner shows" bug). Size the capture bitmap to the window's actual
        // render resolution = physical × (windowDpi / monitorDpi) so PrintWindow
        // fills it exactly. For per-monitor-aware windows windowDpi == monitorDpi
        // (and at 100% scaling both are 96), so this is a no-op there.
        int physW = r.Right - r.Left;
        int physH = r.Bottom - r.Top;
        if (physW <= 0 || physH <= 0 || physW > 20000 || physH > 20000)
            return _thumbCache.TryGetValue(hWnd, out var cachedBad) ? cachedBad : null;

        double renderScale = 1.0;
        try
        {
            uint winDpi = GetDpiForWindow(hWnd);
            IntPtr hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (winDpi > 0 && hMon != IntPtr.Zero
                && GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint monDpi, out _) == 0
                && monDpi > 0)
            {
                renderScale = (double)winDpi / monDpi;
            }
        }
        catch
        {
            // DPI APIs unavailable — fall back to the physical size.
        }

        int w = Math.Max(1, (int)Math.Round(physW * renderScale));
        int h = Math.Max(1, (int)Math.Round(physH * renderScale));

        try
        {
            using var full = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                bool ok;
                try
                {
                    ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
                if (!ok)
                    return _thumbCache.TryGetValue(hWnd, out var cachedFail) ? cachedFail : null;
            }

            double scale = Math.Min(1.0, (double)maxWidth / w);
            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));

            using var thumb = new Drawing.Bitmap(tw, th, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, tw, th);
            }

            IntPtr hbitmap = thumb.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _thumbCache[hWnd] = src;   // remember for when the window is later minimized
                return src;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch
        {
            return _thumbCache.TryGetValue(hWnd, out var cachedEx) ? cachedEx : null;
        }
    }

    /// <summary>Brings a specific window to the foreground, restoring it first
    /// if it is minimized.</summary>
    public static void Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;
        try
        {
            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Requests that the window close (posts WM_CLOSE, same as clicking
    /// its title-bar X). The app may prompt to save; we don't force-kill.</summary>
    public static void CloseWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;
        try
        {
            SendMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Best effort.
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static bool IsAltTabWindow(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd))
            return false;

        // Skip DWM-cloaked windows (e.g. background UWP / virtual-desktop hidden).
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
            return false;

        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        bool isAppWindow = (ex & WS_EX_APPWINDOW) != 0;
        bool isToolWindow = (ex & WS_EX_TOOLWINDOW) != 0;
        if (isToolWindow)
            return false;

        // Owned windows (dialogs/popups) are excluded unless explicitly flagged
        // as app windows — matches the Alt+Tab inclusion rule.
        IntPtr owner = GetWindow(hWnd, GW_OWNER);
        if (owner != IntPtr.Zero && !isAppWindow)
            return false;

        return true;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLengthW(hWnd);
        if (len <= 0)
            return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static HashSet<int> GetPidsForExe(string exePath)
    {
        var pids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(exePath))
            return pids;

        string fullTarget;
        string targetName;
        try
        {
            fullTarget = Path.GetFullPath(exePath);
            targetName = Path.GetFileNameWithoutExtension(exePath);
        }
        catch
        {
            return pids;
        }
        if (string.IsNullOrEmpty(targetName))
            return pids;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(targetName);
        }
        catch
        {
            return pids;
        }

        foreach (var p in candidates)
        {
            try
            {
                // Prefer exact module-path match; fall back to name-only when the
                // module path is inaccessible (elevated / bitness mismatch).
                string? modulePath = null;
                try { modulePath = p.MainModule?.FileName; } catch { /* denied */ }

                if (modulePath == null ||
                    string.Equals(Path.GetFullPath(modulePath), fullTarget,
                        StringComparison.OrdinalIgnoreCase))
                {
                    pids.Add(p.Id);
                }
            }
            catch
            {
                // Process exited mid-inspection.
            }
            finally
            {
                p.Dispose();
            }
        }
        return pids;
    }
}
