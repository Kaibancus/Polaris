using System.Collections.Generic;

namespace Polaris.Models;

/// <summary>Edge of the screen the quick-launch (side) dock is anchored to.</summary>
public enum DockSide
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// User-configurable appearance and behavior settings.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Panel background transparency, 0.0 (opaque) - 1.0 (invisible).
    /// This is the <i>active</i> value for the current theme; each theme keeps
    /// its own remembered value in <see cref="ThemeAppearances"/>.</summary>
    public double PanelTransparency { get; set; } = 0.10;

    /// <summary>Panel background color (hex, e.g. "#1E1E1E").</summary>
    public string PanelColor { get; set; } = "#1E1E1E";

    /// <summary>Accent / ring color (hex).</summary>
    public string AccentColor { get; set; } = "#3D7EFF";

    /// <summary>Icon label font color (hex).</summary>
    public string FontColor { get; set; } = "#FFFFFF";

    /// <summary>Active visual theme id (see <c>ThemeRegistry</c>). Controls the
    /// icon layout and the panel background/animation. Global effects (icon
    /// hover zoom, running-app glow) are independent of the theme.</summary>
    public string Theme { get; set; } = "liquidglass";

    /// <summary>Whether to launch on Windows startup.</summary>
    public bool RunAtStartup { get; set; } = true;

    /// <summary>
    /// One-time migration flag: false in configs written before "run at startup"
    /// defaulted to on. On first load with this false we force RunAtStartup to
    /// true (default-enable), then set this so the user's later choice is kept.
    /// </summary>
    public bool StartupDefaultApplied { get; set; } = false;

    /// <summary>
    /// One-time migration flag: false in configs written before the resident /
    /// inner-ring count became per-theme. On first load with this false we seed
    /// the active theme's <see cref="ThemeAppearance.Ring0Count"/> from the
    /// legacy shared <see cref="Ring0Count"/>, then set this so each theme keeps
    /// its own count from then on.
    /// </summary>
    public bool ResidentCountDecoupled { get; set; } = false;

    /// <summary>Diameter of a single icon in device-independent pixels.</summary>
    public double IconSize { get; set; } = 56;

    /// <summary>Maximum icons allowed on a single ring.</summary>
    public int MaxIconsPerRing { get; set; } = 12;

    /// <summary>
    /// Number of icons on the inner ring. 0 = auto (fill the inner ring up to
    /// 12, remaining icons go to the outer ring). Adjusted as the user drags
    /// icons between the two rings. This is the <i>active</i> value for the
    /// current theme; each theme keeps its own remembered value in
    /// <see cref="ThemeAppearances"/>.
    /// </summary>
    public int Ring0Count { get; set; } = 0;

    /// <summary>
    /// Virtual-key code of the hold-to-show trigger key. Default 0xA5 (Right Alt).
    /// </summary>
    public int TriggerKey { get; set; } = 0xA5;

    /// <summary>
    /// Virtual-key code of the toggle (sticky open/close) hotkey, always combined
    /// with Ctrl. One of 0x30..0x39, i.e. Ctrl+0 .. Ctrl+9. Default 0x34 (Ctrl+4).
    /// </summary>
    public int ToggleKey { get; set; } = 0x34;

    /// <summary>Screen edge the quick-launch (side) dock is anchored to.
    /// Defaults to the bottom edge.</summary>
    public DockSide DockPosition { get; set; } = DockSide.Bottom;

    /// <summary>Whether the quick-launch dock is shown on every monitor.
    /// Defaults to on.</summary>
    public bool DockOnAllMonitors { get; set; } = true;

    /// <summary>Per-theme remembered appearance (transparency + icon size),
    /// keyed by theme id. Lets each theme restore its own look when re-selected;
    /// missing entries fall back to the theme's built-in defaults.</summary>
    public Dictionary<string, ThemeAppearance> ThemeAppearances { get; set; } = new();

    /// <summary>Global dock-text size, as a slider percentage where 50 maps to the
    /// original sizes (1.0×). Applied across all dock typography. Default 50.</summary>
    public double FontSizePercent { get; set; } = 50;
}

/// <summary>Remembered per-theme appearance values.</summary>
public sealed class ThemeAppearance
{
    /// <summary>Panel background transparency, 0.0 (opaque) - 1.0 (invisible).</summary>
    public double Transparency { get; set; }

    /// <summary>Icon diameter in device-independent pixels.</summary>
    public double IconSize { get; set; }

    /// <summary>Resident / inner-ring count for this theme (0 = auto). Lets the
    /// Saturn and liquid-glass themes keep independent resident-app counts.</summary>
    public int Ring0Count { get; set; }
}
