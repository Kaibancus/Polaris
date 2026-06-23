using System;
using System.Windows;
using System.Windows.Threading;
using Polaris.Models;

namespace Polaris.Views;

/// <summary>
/// The surface the host (App) drives the primary "main dock" through, implemented
/// by the GPU <see cref="MainDockWindowGpu"/>.
/// </summary>
internal interface IMainDock
{
    /// <summary>Realise the dock window once (stays shown, fully transparent)
    /// to avoid show/hide flicker.</summary>
    void Realize();

    /// <summary>Re-read config / theme and rebuild the icon layout in place.</summary>
    void RefreshFromConfig();

    /// <summary>Summon the panel from the hotkey (transient — hides on release).</summary>
    void ShowPanel();

    /// <summary>Summon the panel pinned open (from the tray / Ctrl+4).</summary>
    void ShowPinned();

    /// <summary>Dismiss the panel (fade out + retract).</summary>
    void HidePanel();

    /// <summary>Dismiss the panel, invoking <paramref name="onFaded"/> once the
    /// fade-out completes (used to open settings after the dock clears).</summary>
    void HidePanel(Action? onFaded);

    /// <summary>Hide unless the panel is pinned open.</summary>
    void HideIfNotPinned();

    /// <summary>Freeze / resume perpetual ambient animations when the cursor is
    /// parked still or away (a CPU/RAM saver; may be a no-op).</summary>
    void SetAmbientPaused(bool paused);

    /// <summary>Tear the dock down on shutdown.</summary>
    void Close();

    /// <summary>True while the panel is currently shown.</summary>
    bool IsShown { get; }

    /// <summary>UI-thread dispatcher used to marshal host callbacks.</summary>
    Dispatcher Dispatcher { get; }

    /// <summary>Raised when the dock asks the host to open the settings window.</summary>
    event Action? RequestOpenSettings;

    /// <summary>Raised whenever the panel hides, so the host can retract the side
    /// dock together with the main dock.</summary>
    event Action? PanelDismissed;

    /// <summary>Host hook: pin a dragged glass icon onto the left-edge dock.
    /// Returns true when the entry was pinned there.</summary>
    Func<Point, AppEntry, bool>? DropToSideDock { get; set; }

    /// <summary>Host hook: height (DIP) the side dock reserves at the bottom edge
    /// so the main dock can lift clear of it.</summary>
    Func<double>? BottomDockReserve { get; set; }

    /// <summary>Host hook: raised after the dock mutates its app list.</summary>
    Action? AppsChanged { get; set; }

    /// <summary>Host hook: raised while a glass icon is being dragged.</summary>
    Action<bool>? GlassDragActiveChanged { get; set; }
}
