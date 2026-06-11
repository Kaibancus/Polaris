using System.Collections.Generic;

namespace Polaris.Models;

/// <summary>
/// Root configuration object persisted to disk as JSON.
/// </summary>
public sealed class AppConfig
{
    public AppSettings Settings { get; set; } = new();

    /// <summary>Ordered list of apps; list order == ring order.</summary>
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>
    /// Ordered list of apps pinned to the left-edge vertical dock. Every entry
    /// here is also present in <see cref="Apps"/> (adding to the left dock always
    /// adds to the main dock); removing from the left dock leaves the main dock
    /// entry intact.
    /// </summary>
    public List<AppEntry> LeftDockApps { get; set; } = new();
}
