using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Interop;

namespace Polaris.Services;

/// <summary>
/// Mirrors the Windows taskbar's "needs attention" behaviour for the dock.
///
/// Windows apps signal a new message the same way the taskbar surfaces it: they
/// call <c>FlashWindowEx</c> on their window, which makes the taskbar button flash
/// for attention. There is no public API to poll "is this window flashing", but
/// the shell broadcasts it: a window registered with <c>RegisterShellHookWindow</c>
/// receives an <c>HSHELL_FLASH</c> notification (carrying the flashing window's
/// handle) on every flash cycle, and an <c>HSHELL_WINDOWACTIVATED</c> when a window
/// is brought to the foreground (the user has seen it). We track the set of
/// currently-flashing top-level windows from those notifications so the docks can
/// badge the matching app icon and clear it once the window is activated.
///
/// The numeric unread count (e.g. WeChat / Outlook showing "5") is an app-private
/// taskbar overlay icon that Windows does NOT expose to other processes, so it
/// cannot be read directly. As a cross-app approximation we parse the window title,
/// where many messaging / mail apps surface the same count (e.g. "(5) WhatsApp",
/// "Inbox (3) - Outlook") — the same text the taskbar tooltip shows.
/// </summary>
public static class AttentionService
{
    private const int HSHELL_WINDOWACTIVATED   = 4;
    private const int HSHELL_WINDOWDESTROYED   = 2;
    private const int HSHELL_RUDEAPPACTIVATED  = 0x8004;
    private const int HSHELL_FLASH             = 0x8006;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterShellHookWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DeregisterShellHookWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    private static readonly object _lock = new();
    private static readonly HashSet<IntPtr> _flashing = new();
    private static HwndSource? _src;
    private static int _shellMsg;
    private static bool _started;

    /// <summary>Raised (on the UI thread) whenever the flashing-window set changes,
    /// so the docks can refresh their badges promptly instead of waiting for the
    /// next poll.</summary>
    public static event Action? Changed;

    /// <summary>Installs the shell hook on a hidden top-level window. Idempotent;
    /// must be called on the UI thread once a Dispatcher / message loop exists.</summary>
    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            // A real (not message-only) top-level window is required to receive
            // shell hook notifications. Keep it 0-size, off-screen and never shown.
            var p = new HwndSourceParameters("PolarisAttentionSink")
            {
                Width = 0,
                Height = 0,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            };
            _src = new HwndSource(p);
            _src.AddHook(WndProc);
            _shellMsg = RegisterWindowMessage("SHELLHOOK");
            RegisterShellHookWindow(_src.Handle);
        }
        catch
        {
            // Best effort: without the hook the dock simply shows no new-message
            // badges; everything else keeps working.
            _started = false;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_shellMsg != 0 && msg == _shellMsg)
        {
            switch (wParam.ToInt32())
            {
                case HSHELL_FLASH:
                    AddFlashing(lParam);
                    break;
                // The user looked at this window (or it closed): clear attention.
                case HSHELL_WINDOWACTIVATED:
                case HSHELL_RUDEAPPACTIVATED:
                case HSHELL_WINDOWDESTROYED:
                    RemoveFlashing(lParam);
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private static void AddFlashing(IntPtr h)
    {
        if (h == IntPtr.Zero)
            return;
        bool changed;
        lock (_lock) changed = _flashing.Add(h);
        if (changed)
            Changed?.Invoke();
    }

    private static void RemoveFlashing(IntPtr h)
    {
        if (h == IntPtr.Zero)
            return;
        bool changed;
        lock (_lock) changed = _flashing.Remove(h);
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>A snapshot copy of the currently-flashing top-level window handles.</summary>
    public static HashSet<IntPtr> SnapshotFlashing()
    {
        lock (_lock)
            return new HashSet<IntPtr>(_flashing);
    }

    // Matches a parenthesised / bracketed unread count that messaging and mail
    // apps put in their title: a leading "(5) …" / "[5] …", or "… (3) - App"
    // where the count is followed by a separator. Avoids matching incidental
    // numbers mid-title (e.g. a document named "report (2).docx").
    private static readonly Regex UnreadLeading =
        new(@"^\s*[\(\[](\d{1,4})[\)\]]", RegexOptions.Compiled);
    private static readonly Regex UnreadBeforeSep =
        new(@"[\(\[](\d{1,4})[\)\]]\s*[-–—|·]", RegexOptions.Compiled);

    /// <summary>Best-effort unread count parsed from a window title, or 0 when the
    /// title carries no recognisable count.</summary>
    public static int ParseUnread(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0;
        var m = UnreadLeading.Match(title);
        if (!m.Success)
            m = UnreadBeforeSep.Match(title);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0)
            return n;
        return 0;
    }

    public static void Stop()
    {
        if (_src == null)
            return;
        try
        {
            DeregisterShellHookWindow(_src.Handle);
            _src.RemoveHook(WndProc);
            _src.Dispose();
        }
        catch { /* shutting down */ }
        _src = null;
        _started = false;
    }
}
