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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string pszPath, IntPtr pbc,
        int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToStringAlloc(ref PROPVARIANT propvar, out IntPtr ppszOut);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        // A real PROPVARIANT is 24 bytes on x64 (2-byte VARTYPE + 6 reserved =
        // 8-byte header, then a 16-byte value union that can hold two pointers).
        // Under-declaring it (e.g. only 16 bytes) lets IPropertyStore::GetValue
        // overflow the managed buffer, corrupting the value so the AUMID reads
        // back empty — which silently disables packaged-app window detection.
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(16)] public IntPtr p2;
    }

    private static Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // PKEY_AppUserModel_ID: fmtid {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5.
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    // PKEY_Link_TargetParsingPath: fmtid {B9B4B3FC-2B51-4A42-B5D8-324146AFCF25}, pid 2.
    // An shell:AppsFolder item exposes the path of the program it launches here.
    private static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new()
    {
        fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"),
        pid = 2,
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

    /// <summary>Cache of app icons keyed by the owning process's exe path, used
    /// as a preview fallback for windows that cannot be captured by PrintWindow
    /// (GPU/DirectX-composited windows such as Windows Terminal render nothing to
    /// a GDI device, so their thumbnail is always blank).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _winIconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the window is currently minimized (iconic).</summary>
    public static bool IsWindowMinimized(IntPtr hWnd) => hWnd != IntPtr.Zero && IsIconic(hWnd);

    /// <summary>Returns the application icon of the process that owns
    /// <paramref name="hWnd"/>, or null when it cannot be resolved. Used as a
    /// preview placeholder when a live thumbnail cannot be captured.</summary>
    public static BitmapSource? GetWindowAppIcon(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero || GetWindowThreadProcessId(hWnd, out uint pid) == 0)
                return null;
            string? path = GetProcessInfo(pid, out string? _ignore);
            if (string.IsNullOrWhiteSpace(path))
                return null;
            return _winIconCache.GetOrAdd(path, p => IconExtractor.GetIcon(p));
        }
        catch
        {
            return null;
        }
    }

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
        if (aumid != null)
        {
            var byAumid = GetWindowsByAumid(aumid);
            if (byAumid.Count > 0)
                return byAumid;

            // The AUMID matched no window. Many shell:AppsFolder entries are not
            // genuine packaged apps (e.g. VS Code, iQiyi): they launch a plain
            // Win32 exe whose windows carry no AUMID. Resolve the real target
            // executable and match those windows by process instead.
            string? exe = TryResolveAppsFolderExe(aumid);
            if (!string.IsNullOrEmpty(exe))
            {
                var byExe = GetWindows(exe);
                if (byExe.Count > 0)
                    return byExe;
                // Multi-process apps (e.g. iQiyi) own their visible window from a
                // sibling/child process in the same install folder, so the exact
                // exe match finds nothing — fall back to a folder match.
                return GetWindowsByExeFolder(exe);
            }

            return byAumid;
        }

        // Protocol launchers (e.g. "explorer.exe ms-settings:") open a UWP app
        // that is usually hosted by ApplicationFrameHost — its window has no
        // readable AUMID and is not owned by explorer.exe. Matching by the
        // explorer.exe process would wrongly surface File Explorer's windows, so
        // we show no previews for these; clicking simply (re)launches the
        // protocol, which activates the single-instance app.
        if (IsProtocolLauncher(path, arguments))
            return new List<WindowPreview>();

        var byPath = GetWindows(path);
        if (byPath.Count > 0)
            return byPath;
        // Multi-process apps (Steam's window is owned by steamwebhelper.exe in a
        // subfolder; Baidu Netdisk and similar spawn a helper that owns the UI)
        // expose no window from the pinned exe itself. Match any visible window
        // whose process lives under the pinned exe's install folder.
        return GetWindowsByExeFolder(path);
    }

    /// <summary>Visible alt-tab windows whose owning process executable lives in
    /// (or under) the install folder of <paramref name="exePath"/>. This catches
    /// multi-process apps whose UI window belongs to a helper/child process with
    /// a different name or path than the pinned launcher (Steam → steamwebhelper
    /// in a subfolder, Baidu Netdisk, iQiyi…). Guarded against over-broad folders
    /// (a drive root, Windows, or a Program Files root) so it never sweeps up
    /// every program that happens to share a generic parent directory.</summary>
    private static List<WindowPreview> GetWindowsByExeFolder(string exePath)
    {
        var result = new List<WindowPreview>();
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty; }
        catch { return result; }
        if (string.IsNullOrEmpty(dir) || IsTooBroadFolder(dir))
            return result;

        string prefix = dir.TrimEnd('\\') + "\\";
        int ownPid = Environment.ProcessId;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                    return true;
                if (!IsAltTabWindow(hWnd))
                    return true;
                string title = GetWindowTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title))
                    return true;
                string? p = GetProcessInfo(pid, out string? _ignore);
                if (string.IsNullOrWhiteSpace(p))
                    return true;
                string full;
                try { full = Path.GetFullPath(p); }
                catch { return true; }
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(new WindowPreview { Handle = hWnd, Title = title });
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Best effort; an empty list just means no folder-matched preview.
        }
        return result;
    }

    /// <summary>True when <paramref name="dir"/> is too generic to use for a
    /// folder match — a drive root, the Windows directory, System32, or a
    /// Program Files root — where many unrelated programs live side by side.</summary>
    private static bool IsTooBroadFolder(string dir)
    {
        try
        {
            string d = dir.TrimEnd('\\');
            string? root = Path.GetPathRoot(d)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(d) || string.Equals(d, root, StringComparison.OrdinalIgnoreCase))
                return true;
            string[] broad =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            foreach (var b in broad)
                if (!string.IsNullOrEmpty(b) &&
                    string.Equals(d, b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the entry launches a URI protocol via Explorer (e.g.
    /// <c>explorer.exe ms-settings:</c>) rather than a file or a
    /// <c>shell:AppsFolder</c> packaged-app launcher. These open a separate host
    /// process, so they must not be matched against explorer.exe's own windows.
    /// </summary>
    public static bool IsProtocolLauncher(string path, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return false;
        try
        {
            if (!string.Equals(Path.GetFileName(path), "explorer.exe",
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
            return false;
        }

        string arg = arguments.Trim().Trim('"');
        // A URI scheme: a letter followed by letters/digits/+/-/. then ':'. We
        // exclude drive paths ("C:\") — those have a single-letter scheme
        // followed by a path separator — and the shell: namespace (handled
        // elsewhere / not a windowed protocol app).
        int colon = arg.IndexOf(':');
        if (colon <= 0)
            return false;
        string scheme = arg.Substring(0, colon);
        if (scheme.Length == 1)
            return false;   // drive letter like C:
        if (scheme.StartsWith("shell", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (char c in scheme)
        {
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// If <paramref name="path"/>/<paramref name="arguments"/> describe a
    /// packaged-app launcher (<c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>),
    /// returns the AUMID; otherwise null.
    /// </summary>
    public static string? TryGetLauncherAumid(string path, string? arguments)
    {
        const string token = "shell:AppsFolder\\";

        // New form: the entry's Path itself is "shell:AppsFolder\<AUMID>" (a
        // packaged app dragged from the Start menu, normalized on import) with no
        // arguments. Older pins stored it as explorer.exe + the token in the
        // arguments, so accept both.
        string? source = null;
        if (!string.IsNullOrWhiteSpace(path) &&
            path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            source = path;
        }
        else if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                if (string.Equals(Path.GetFileName(path), "explorer.exe",
                        StringComparison.OrdinalIgnoreCase))
                    source = arguments;
            }
            catch
            {
                return null;
            }
        }

        if (source == null)
            return null;

        int i = source.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;

        string aumid = source.Substring(i + token.Length).Trim().Trim('"');
        int ws = aumid.IndexOf(' ');
        if (ws > 0)
            aumid = aumid.Substring(0, ws);
        return string.IsNullOrWhiteSpace(aumid) ? null : aumid;
    }

    /// <summary>
    /// True when the entry is an AppsFolder launcher whose target is hosted by
    /// the shell process — its resolved executable is explorer.exe (e.g. File
    /// Explorer = <c>shell:AppsFolder\Microsoft.Windows.Explorer</c>, whose
    /// AppsFolder target is a <c>::{CLSID}</c> shell location). "Activating" the
    /// already-running explorer.exe shell window does nothing visible, so these
    /// items must always launch a fresh window instead of being brought forward.
    /// </summary>
    public static bool IsShellHostedLauncher(string path, string? arguments)
    {
        string? aumid = TryGetLauncherAumid(path, arguments);
        if (string.IsNullOrEmpty(aumid))
            return false;
        string? exe = TryResolveAppsFolderExe(aumid);
        if (string.IsNullOrEmpty(exe))
            return false;
        try
        {
            return string.Equals(Path.GetFileName(exe), "explorer.exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the entry is the genuine Windows File Explorer — explorer.exe
    /// with NO <c>shell:AppsFolder</c> launcher argument (packaged apps such as
    /// the new Teams / Outlook are also launched via explorer.exe but carry such
    /// an argument). explorer.exe is also the desktop shell, so callers treat
    /// File Explorer specially: always open a fresh window rather than activate.
    /// Single source of truth for this test across the docks and the preview.
    /// </summary>
    public static bool IsFileExplorer(string path, string? arguments)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), "explorer.exe",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return TryGetLauncherAumid(path, arguments) == null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the entry launches the generic Windows File Explorer — either a
    /// bare explorer.exe pin (<see cref="IsFileExplorer"/>) or the AppsFolder
    /// form <c>shell:AppsFolder\Microsoft.Windows.Explorer</c>. Unlike specific
    /// shell folders (This PC, Recycle Bin…), File Explorer has no single window
    /// title to match — it is "running" whenever ANY Explorer file window is
    /// open. Single source of truth for the green-light test on both docks.
    /// </summary>
    public static bool IsFileExplorerLauncher(string path, string? arguments)
    {
        try
        {
            if (IsFileExplorer(path, arguments))
                return true;
            string? aumid = TryGetLauncherAumid(path, arguments);
            return !string.IsNullOrEmpty(aumid)
                && aumid.IndexOf("Microsoft.Windows.Explorer",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the visible, alt-tab-style top-level windows whose Application
    /// User Model ID matches <paramref name="aumid"/> (used for packaged apps
    /// such as the new Teams / Outlook).
    /// </summary>
    public static List<WindowPreview> GetWindowsByAumid(string aumid)
    {        var result = new List<WindowPreview>();
        int ownPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                return true;
            if (!IsAltTabWindow(hWnd))
                return true;

            // Prefer the window's own AUMID; some packaged apps (the new Outlook)
            // expose an empty / missing per-window AUMID, so fall back to the
            // owning process's AUMID, which carries the same package family name.
            string? winAumid = GetWindowAumid(hWnd);
            if (string.IsNullOrEmpty(winAumid))
                GetProcessInfo(pid, out winAumid);
            if (!AumidMatches(winAumid, aumid))
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
    /// Enumerates alt-tab windows once and returns the set of package-family
    /// names that currently own a window. Used to light the "running" glow on
    /// pinned packaged-app (UWP / Store) icons without an EnumWindows pass per
    /// icon. Match an entry with <see cref="IsAumidInSnapshot"/>.
    /// </summary>
    public static HashSet<string> SnapshotRunningAumids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ownPid = Environment.ProcessId;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                    return true;
                if (!IsAltTabWindow(hWnd))
                    return true;

                string? winAumid = GetWindowAumid(hWnd);
                if (string.IsNullOrEmpty(winAumid))
                    GetProcessInfo(pid, out winAumid);
                if (!string.IsNullOrEmpty(winAumid))
                    set.Add(PackageFamilyOf(winAumid));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Best-effort; an empty set just means no UWP glow this tick.
        }
        return set;
    }

    /// <summary>True when the packaged app identified by <paramref name="aumid"/>
    /// has a window in <paramref name="runningAumids"/> (compared by package
    /// family name, matching <see cref="AumidMatches"/>).</summary>
    public static bool IsAumidInSnapshot(string? aumid, HashSet<string>? runningAumids)
    {
        if (string.IsNullOrEmpty(aumid) || runningAumids == null || runningAumids.Count == 0)
            return false;
        string family = PackageFamilyOf(aumid);
        foreach (var running in runningAumids)
            if (FamilyMatches(family, running))
                return true;
        return false;
    }

    /// <summary>True when a window's Application User Model ID
    /// <paramref name="winAumid"/> identifies the same packaged app as the pinned
    /// launcher's <paramref name="target"/> AUMID. Some packaged apps (e.g. the
    /// new Outlook) report a window AUMID whose app-id portion differs from the
    /// launcher's, so a match on the package family name (the part before the
    /// <c>!</c>) is accepted in addition to an exact match.</summary>
    private static bool AumidMatches(string? winAumid, string target)
    {
        if (string.IsNullOrEmpty(winAumid))
            return false;
        if (string.Equals(winAumid, target, StringComparison.OrdinalIgnoreCase))
            return true;

        string winPfn = PackageFamilyOf(winAumid);
        string targetPfn = PackageFamilyOf(target);
        return winPfn.Length > 0 && FamilyMatches(winPfn, targetPfn);
    }

    /// <summary>Compares two package-family names, tolerating a profile/variant
    /// suffix. Edge, for instance, is launched as app-id <c>MSEdge</c> but its
    /// windows report <c>MSEdge.UserData.Profile1</c>; treat one as matching the
    /// other when it is the same string or a dotted extension of it.</summary>
    private static bool FamilyMatches(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        return a.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when two AUMIDs identify the same packaged app, comparing by
    /// package family name with the same profile/variant-suffix tolerance as
    /// <see cref="FamilyMatches"/>. Used to exclude a pinned packaged app (whose
    /// stored AUMID may be the bare launcher id, e.g. <c>MSEdge</c>) from the
    /// running strip even when its live window reports a profile-suffixed AUMID
    /// (e.g. <c>MSEdge.UserData.Profile1</c>).</summary>
    public static bool AumidFamilyMatches(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        return FamilyMatches(PackageFamilyOf(a), PackageFamilyOf(b));
    }

    /// <summary>Returns the package-family-name portion of an AUMID (everything
    /// before the <c>!</c>), or the whole string when it has no <c>!</c>.</summary>
    private static string PackageFamilyOf(string aumid)
    {
        int bang = aumid.IndexOf('!');
        return bang > 0 ? aumid.Substring(0, bang) : aumid;
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

    /// <summary>Caches the <see cref="TryResolveAppsFolderExe"/> result per AUMID
    /// (AppsFolder targets are static for the session), so the COM property-store
    /// lookup runs at most once per app rather than every running-state tick.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _appsFolderExeCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves an <c>shell:AppsFolder\&lt;AUMID&gt;</c> pseudo-AUMID to the full
    /// path of the executable it actually launches, by reading the AppsFolder
    /// item's <c>System.Link.TargetParsingPath</c>. Many Start-menu entries (e.g.
    /// Visual Studio Code = <c>Microsoft.VisualStudioCode</c>, File Explorer =
    /// <c>Microsoft.Windows.Explorer</c>) are NOT genuine packaged apps: their
    /// windows carry no AUMID, so they can only be matched to a running process
    /// by exe path. A shell-location target (<c>::{CLSID}</c>, e.g. File
    /// Explorer's Home) is hosted by the shell, so it maps to
    /// <c>%WINDIR%\explorer.exe</c>. Returns null for a true packaged app (whose
    /// windows DO carry the AUMID) or when the target cannot be resolved.
    /// </summary>
    public static string? TryResolveAppsFolderExe(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return null;
        return _appsFolderExeCache.GetOrAdd(aumid, ResolveAppsFolderExeUncached);
    }

    private static string? ResolveAppsFolderExeUncached(string aumid)
    {
        try
        {
            string parsing = "shell:AppsFolder\\" + aumid;
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreFromParsingName(parsing, IntPtr.Zero, 0, ref iid, out IPropertyStore? store) != 0
                || store == null)
                return null;
            try
            {
                var key = PKEY_Link_TargetParsingPath;
                if (store.GetValue(ref key, out PROPVARIANT pv) != 0)
                    return null;
                try
                {
                    if (PropVariantToStringAlloc(ref pv, out IntPtr str) != 0 || str == IntPtr.Zero)
                        return null;
                    string? target;
                    try { target = Marshal.PtrToStringUni(str); }
                    finally { Marshal.FreeCoTaskMem(str); }
                    if (string.IsNullOrWhiteSpace(target))
                        return null;
                    // A shell location ("::{CLSID}") runs inside the shell process.
                    if (target.StartsWith("::", StringComparison.Ordinal))
                    {
                        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        return Path.Combine(win, "explorer.exe");
                    }
                    return File.Exists(target) ? target : null;
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

            // ApplicationFrameHost.exe merely hosts UWP windows (Settings, some
            // Store apps); its window exposes no usable AUMID, so it would show
            // up as a generic host tile that duplicates the real (often pinned)
            // app. Skip it so the running strip never lists the host process.
            if (string.Equals(Path.GetFileName(path), "ApplicationFrameHost.exe",
                    StringComparison.OrdinalIgnoreCase))
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
    /// if it is minimized. Delegates to the robust activation in
    /// <see cref="RunningAppTracker.ActivateWindow"/>, which attaches to the
    /// foreground thread's input queue to defeat Windows' foreground lock — a
    /// plain SetForegroundWindow from a background/topmost app (like the dock) is
    /// frequently refused, leaving the target window merely flashing in the
    /// taskbar instead of coming forward.</summary>
    public static void Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;
        try
        {
            RunningAppTracker.ActivateWindow(hWnd);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Finds the first VISIBLE alt-tab window owned by a process whose
    /// executable FILE NAME matches <paramref name="exePath"/> and brings it to
    /// the foreground. Used to activate a desktop-bridge app (e.g. iQiyi) whose
    /// real window is owned by a differently-pathed instance of the launcher exe.
    /// Returns true when a window was activated.</summary>
    public static bool ActivateWindowByExeName(string exePath)
    {
        string targetName;
        try { targetName = Path.GetFileName(exePath); }
        catch { return false; }
        if (string.IsNullOrEmpty(targetName))
            return false;

        IntPtr found = IntPtr.Zero;
        int ownPid = Environment.ProcessId;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsAltTabWindow(hWnd))
                    return true;
                if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid == ownPid)
                    return true;
                if (string.IsNullOrWhiteSpace(GetWindowTitle(hWnd)))
                    return true;
                string? path = GetProcessInfo(pid, out string? _procAumid);
                if (string.IsNullOrWhiteSpace(path))
                    return true;
                try
                {
                    if (string.Equals(Path.GetFileName(path), targetName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;   // stop enumerating
                    }
                }
                catch { /* ignore a malformed path */ }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return false;
        }

        if (found == IntPtr.Zero)
            return false;
        RunningAppTracker.ActivateWindow(found);
        return true;
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
