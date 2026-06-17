using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    /// Ordered list of apps pinned to the side (edge) vertical dock. Every entry
    /// here is also present in <see cref="Apps"/> (adding to the side dock always
    /// adds to the main dock); removing from the side dock leaves the main dock
    /// entry intact.
    /// </summary>
    /// <remarks>The JSON key stays "LeftDockApps" (the historical name) so configs
    /// written by older versions still load after the SideDock rename.</remarks>
    [JsonPropertyName("LeftDockApps")]
    public List<AppEntry> SideDockApps { get; set; } = new();
}
