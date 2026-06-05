using System.Collections.Generic;

namespace DesktopPanel.Models;

/// <summary>
/// User-configurable appearance and behavior settings.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Panel background opacity, 0.0 - 1.0.</summary>
    public double PanelOpacity { get; set; } = 0.85;

    /// <summary>Panel background color (hex, e.g. "#1E1E1E").</summary>
    public string PanelColor { get; set; } = "#1E1E1E";

    /// <summary>Accent / ring color (hex).</summary>
    public string AccentColor { get; set; } = "#3D7EFF";

    /// <summary>Icon label font color (hex).</summary>
    public string FontColor { get; set; } = "#FFFFFF";

    /// <summary>Active visual theme id (see <c>ThemeRegistry</c>). Controls the
    /// icon layout and the panel background/animation. Global effects (icon
    /// hover zoom, running-app glow) are independent of the theme.</summary>
    public string Theme { get; set; } = "saturn";

    /// <summary>Whether to launch on Windows startup.</summary>
    public bool RunAtStartup { get; set; } = true;

    /// <summary>
    /// One-time migration flag: false in configs written before "run at startup"
    /// defaulted to on. On first load with this false we force RunAtStartup to
    /// true (default-enable), then set this so the user's later choice is kept.
    /// </summary>
    public bool StartupDefaultApplied { get; set; } = false;

    /// <summary>Diameter of a single icon in device-independent pixels.</summary>
    public double IconSize { get; set; } = 56;

    /// <summary>Maximum icons allowed on a single ring.</summary>
    public int MaxIconsPerRing { get; set; } = 12;

    /// <summary>
    /// Number of icons on the inner ring. 0 = auto (fill the inner ring up to
    /// 12, remaining icons go to the outer ring). Adjusted as the user drags
    /// icons between the two rings.
    /// </summary>
    public int Ring0Count { get; set; } = 0;

    /// <summary>
    /// Virtual-key code of the hold-to-show trigger key. Default 0xA5 (Right Alt).
    /// </summary>
    public int TriggerKey { get; set; } = 0xA5;
}
