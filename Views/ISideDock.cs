using System;
using System.Windows;
using Polaris.Models;

namespace Polaris.Views;

/// <summary>
/// The surface the host (App) drives the secondary "side dock" through, so the
/// WPF (<see cref="SideDockWindow"/>) and GPU (<see cref="SideDockWindowGpu"/>)
/// implementations are interchangeable behind the <c>POLARIS_GPU_SIDEDOCK</c>
/// flag — the same A/B pattern used for the drag ghost and notch clock.
/// </summary>
internal interface ISideDock
{
    /// <summary>Realise the dock window once, hidden, ready to be summoned.</summary>
    void Realize();

    /// <summary>Force-dismiss the dock by clearing every show reason at once.</summary>
    void HideAll();

    /// <summary>Re-sync after the main dock changed its resident region.</summary>
    void RefreshFromConfig();

    /// <summary>Re-read the dock-position setting and re-anchor / rebuild.</summary>
    void RefreshLayout();

    /// <summary>Keep the dock shown while an icon is dragged from the main dock.</summary>
    void SetDragActive(bool active);

    /// <summary>Show / hide together with the Ctrl+4 pinned panel toggle.</summary>
    void SetPinnedShown(bool shown);

    /// <summary>Show / hide in step with the main hotkey dock.</summary>
    void SetMainShown(bool shown);

    /// <summary>Show / hide from the screen-edge mouse trigger.</summary>
    void SetEdgeShown(bool shown);

    /// <summary>Freeze / resume perpetual ambient animations when the cursor is
    /// parked still or away (a CPU/RAM saver; may be a no-op).</summary>
    void SetAmbientPaused(bool paused);

    /// <summary>Test a DEVICE-pixel screen point against the dock and pin the
    /// entry there when it lands over the column. Returns true when accepted.</summary>
    bool TryAcceptDrop(Point screenDevicePoint, AppEntry entry);

    /// <summary>Screen-coordinate (DIP) rectangle of the visible glass slab.</summary>
    Rect GetDockScreenBounds();

    /// <summary>True while the dock is currently shown.</summary>
    bool DockVisible { get; }

    /// <summary>The edge the dock is anchored to.</summary>
    DockSide DockSidePosition { get; }

    /// <summary>Raised when the dock mutates the shared main-dock app list.</summary>
    event Action? MainDockChanged;

    /// <summary>Invoked when the dock's Polaris tile asks to toggle the pinned docks.</summary>
    Action? ToggleDocks { get; set; }
}
