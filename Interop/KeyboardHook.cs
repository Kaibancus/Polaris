using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Polaris.Interop;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) that detects press and release of a
/// single trigger key. RegisterHotKey cannot report key-up, so a hook is
/// required for the "hold to show, release to hide" behavior.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>Raised on the trigger key's first key-down (auto-repeat suppressed).</summary>
    public event Action? KeyPressed;

    /// <summary>Raised on the trigger key's key-up.</summary>
    public event Action? KeyReleased;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11;

    private readonly int _triggerVk;
    private readonly bool _suppress;
    private readonly bool _requireCtrl;
    private readonly LowLevelKeyboardProc _proc;
    private readonly Dispatcher _dispatcher;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDown;

    /// <param name="triggerVirtualKey">Virtual-key code, e.g. 0x78 for F9.</param>
    /// <param name="suppressKey">When true the trigger key is swallowed so it does
    /// not reach other applications (e.g. to stop Caps Lock from toggling).</param>
    /// <param name="requireCtrl">When true the press only fires while either Ctrl
    /// key is held down, turning the trigger into a Ctrl+key chord.</param>
    public KeyboardHook(int triggerVirtualKey, bool suppressKey = false, bool requireCtrl = false)
    {
        _triggerVk = triggerVirtualKey;
        _suppress = suppressKey;
        _requireCtrl = requireCtrl;
        _proc = HookCallback;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>True if the hook is currently installed.</summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>Last Win32 error from a failed Start(), or 0 on success.</summary>
    public int LastError { get; private set; }

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
            return;

        // For low-level hooks the system dispatches the callback on the installing
        // thread, so hMod can be the EXE base handle. GetModuleHandle(null) returns
        // the handle of the file used to create the process (works in single-file).
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

        if (_hookId == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();

            // Fallback: some single-file/self-extract layouts return a NULL base
            // handle from GetModuleHandle(null); retry with the main module name.
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                using var module = process.MainModule!;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                    GetModuleHandle(module.ModuleName), 0);
                if (_hookId == IntPtr.Zero)
                    LastError = Marshal.GetLastWin32Error();
            }
            catch
            {
                // ignore; LastError already captured
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is first field

            if (vk == _triggerVk)
            {
                // For a Ctrl+key chord, only react while a Ctrl key is held.
                // GetAsyncKeyState reads the real-time key state, which stays
                // accurate even when the chord is injected (e.g. a trackpad
                // gesture remapped to Ctrl+4) rather than physically typed.
                bool ctrlOk = !_requireCtrl ||
                    (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (!_isDown && ctrlOk)
                    {
                        _isDown = true;
                        _dispatcher.BeginInvoke(() => KeyPressed?.Invoke());
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (_isDown)
                    {
                        _isDown = false;
                        _dispatcher.BeginInvoke(() => KeyReleased?.Invoke());
                    }
                }

                // Swallow the key so it never reaches other apps (prevents the
                // Caps Lock LED/state from toggling when used as a trigger).
                if (_suppress && _isDown)
                    return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int nVirtKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}