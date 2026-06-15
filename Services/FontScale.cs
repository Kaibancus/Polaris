namespace Polaris.Services;

/// <summary>
/// Global multiplier applied to every piece of dock text (icon name labels, the
/// glass clock / weather line, the right-click menu, window-preview titles, …) so
/// the user can scale all dock typography from one "字体大小" setting. The slider is
/// expressed in percent where 50% maps to the original sizes (<see cref="Current"/>
/// == 1.0); render sites multiply their base font size by <see cref="Current"/>.
/// </summary>
public static class FontScale
{
    private static double _scale = 1.0;

    /// <summary>The current text multiplier (1.0 == the original sizes).</summary>
    public static double Current => _scale;

    /// <summary>Sets the multiplier from the slider percentage (50 == 1.0×). The
    /// value is clamped to a sane band so text never disappears or explodes.</summary>
    public static void SetFromPercent(double percent)
    {
        double scale = percent / 50.0;
        _scale = scale < 0.2 ? 0.2 : (scale > 3.0 ? 3.0 : scale);
    }
}
