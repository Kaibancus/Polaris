using System.Windows.Media;

namespace Polaris.Services;

/// <summary>Small colour helpers shared across the dock views.</summary>
public static class ColorUtil
{
    /// <summary>Parses a hex / named colour string, returning
    /// <paramref name="fallback"/> when the string is blank or unparseable.</summary>
    public static Color Parse(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) &&
                ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch { /* ignore */ }
        return fallback;
    }
}
