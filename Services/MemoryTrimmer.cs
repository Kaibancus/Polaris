using System;
using System.Runtime.InteropServices;

namespace Polaris.Services;

/// <summary>Trims the process working set while Polaris is idle (both docks
/// hidden). Polaris is a WPF + <c>AllowsTransparency</c> tray app: its idle
/// footprint (~400&#160;MB) is almost entirely UNMANAGED — the WPF/MilCore
/// software-render surfaces, the native graphics stack (Direct2D/DirectWrite/
/// WIC), the loaded runtime/framework images and the hidden dock windows. The
/// managed heap is only a few MB, so a GC reclaims almost nothing.
///
/// <para>While both docks are hidden none of those pages are touched, so they
/// can be evicted from the resident set without hurting anything: this is the
/// same mechanism behind the familiar "tray app drops to a few MB once
/// minimised" behaviour. <see cref="EmptyWorkingSet"/> moves the process's
/// pages to the standby list, freeing the physical RAM for other apps (and
/// shrinking the figure shown in Task Manager) until the next summon faults
/// the few pages it needs back in.</para></summary>
internal static class MemoryTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // Pseudo-handle for the current process — always has the rights needed to
    // trim its own working set, so no OpenProcess is required.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Evicts the current process's resident pages to the standby list.
    /// Safe to call repeatedly; a no-op cost when there is nothing to trim.</summary>
    public static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(GetCurrentProcess()); }
        catch { /* psapi unavailable — best-effort only */ }
    }
}
