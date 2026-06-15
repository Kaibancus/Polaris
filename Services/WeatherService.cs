using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Polaris.Services;

/// <summary>
/// Fetches the current weather + city for the dock's clock line, key-free:
/// location (lat/lon + Chinese city name) is resolved by IP through a small
/// provider chain so that one blocked or slow endpoint doesn't kill the weather
/// line, and the weather itself comes from Open-Meteo. The result is cached and
/// refreshed at most every <see cref="RefreshInterval"/>; callers poll
/// <see cref="Summary"/> and subscribe to <see cref="Updated"/>. All failures are
/// swallowed so an offline machine simply keeps showing the clock without weather.
/// </summary>
public sealed class WeatherService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The formatted "weather location" line, e.g. "晴 23°  北京", or null
    /// until the first successful fetch.</summary>
    public string? Summary { get; private set; }

    /// <summary>Raised (on a background thread) whenever <see cref="Summary"/>
    /// changes. Handlers must marshal to the UI thread themselves.</summary>
    public event Action? Updated;

    private DateTime _lastFetch = DateTime.MinValue;
    private bool _busy;

    /// <summary>Refreshes the cached weather if it is stale (or <paramref name="force"/>).
    /// Safe to call frequently — it self-throttles and never throws.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (_busy)
            return;
        if (!force && Summary != null && DateTime.Now - _lastFetch < RefreshInterval)
            return;
        _busy = true;
        try
        {
            // 1. Locate by IP through a provider chain (no key, Chinese place
            //    names). A single blocked/slow endpoint won't kill the weather
            //    line — the next provider is tried instead.
            var location = await ResolveLocationAsync().ConfigureAwait(false);
            if (location is not (double lat, double lon, string city))
                return;

            // 2. Current weather (Open-Meteo, no key). Invariant culture so the
            //    decimal point in the coordinates is a dot, not a comma.
            string wUrl = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.0000}&longitude={1:0.0000}&current=temperature_2m,weather_code",
                lat, lon);
            var wJson = await Http.GetStringAsync(wUrl).ConfigureAwait(false);
            using var wDoc = JsonDocument.Parse(wJson);
            var cur = wDoc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();

            string desc = WmoToChinese(code);
            string s = $"{desc} {Math.Round(temp)}°   {city}".Trim();
            if (s != Summary)
            {
                Summary = s;
                Updated?.Invoke();
            }
            _lastFetch = DateTime.Now;
        }
        catch
        {
            // Offline / API hiccup — keep whatever we last had.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Resolves the current position (lat/lon + Chinese city) by IP,
    /// trying each provider in turn until one succeeds. Returns null (so the caller
    /// silently keeps the previous weather) when every provider fails. Never throws.</summary>
    private async Task<(double lat, double lon, string city)?> ResolveLocationAsync()
    {
        foreach (var provider in new Func<Task<(double, double, string)?>>[]
                 {
                     LocateViaIpWhoIs,   // HTTPS, fast, localized; primary.
                     LocateViaIpApi,     // HTTP fallback (blocked in some regions).
                 })
        {
            try
            {
                if (await provider().ConfigureAwait(false) is { } loc)
                    return loc;
            }
            catch
            {
                // Try the next provider.
            }
        }
        return null;
    }

    /// <summary>Geolocation via ipwho.is (HTTPS, key-free, Chinese place names).</summary>
    private static async Task<(double, double, string)?> LocateViaIpWhoIs()
    {
        var json = await Http.GetStringAsync("https://ipwho.is/?lang=zh-CN").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            return null;
        double lat = root.GetProperty("latitude").GetDouble();
        double lon = root.GetProperty("longitude").GetDouble();
        string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(city) && root.TryGetProperty("region", out var rn))
            city = rn.GetString() ?? "";
        return (lat, lon, city.Trim());
    }

    /// <summary>Geolocation via ip-api.com (HTTP, key-free, Chinese place names).</summary>
    private static async Task<(double, double, string)?> LocateViaIpApi()
    {
        var json = await Http.GetStringAsync(
            "http://ip-api.com/json/?lang=zh-CN&fields=status,city,regionName,lat,lon")
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("status", out var st) || st.GetString() != "success")
            return null;
        double lat = root.GetProperty("lat").GetDouble();
        double lon = root.GetProperty("lon").GetDouble();
        string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(city) && root.TryGetProperty("regionName", out var rn))
            city = rn.GetString() ?? "";
        return (lat, lon, city.Trim());
    }

    /// <summary>Maps a WMO weather-interpretation code (as used by Open-Meteo) to a
    /// short Chinese description.</summary>
    private static string WmoToChinese(int code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "雪粒",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷暴冰雹",
        _ => "—",
    };
}
