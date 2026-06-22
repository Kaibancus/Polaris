using System;
using System.Windows;
using System.Windows.Media;

namespace Polaris.Services;

/// <summary>Render-quality tier. <see cref="High"/> equals the original values
/// for every knob, so a machine pinned to High is visually identical to before
/// this adaptive system existed.</summary>
public enum QualityTier
{
    High = 0,
    Medium = 1,
    Low = 2,
}

/// <summary>
/// Central render-quality governor. The liquid-glass and Saturn themes render
/// into <c>AllowsTransparency</c> (software-composited, layered) windows whose
/// per-frame cost scales with composited-pixel area and the number of animated /
/// effect-bearing visuals. On a weak or very-high-DPI machine that cost can pull
/// the real frame rate below 60.
///
/// This classifies the machine into a <see cref="QualityTier"/> once at startup
/// (from the WPF render tier, CPU width and the primary monitor's device-pixel
/// area) and then watches the live frame time while a dock is shown, stepping the
/// tier DOWN if the static guess proves too optimistic. It never steps up within
/// a session, so a capable machine stays pinned to <see cref="QualityTier.High"/>
/// — where every knob equals the original value, guaranteeing zero visual change
/// on hardware that was already smooth.
///
/// The exposed knobs drive:
///   C  loop frame rates + Saturn decorative micro-detail density
///   D  BitmapCache render scale (composited-pixel reduction on the heavy caches)
///   E  whether the heaviest always-on animated blur (glass orbit light) is built
/// </summary>
public static class RenderProfile
{
    /// <summary>The active quality tier (read across the app when (re)building).</summary>
    public static QualityTier Tier { get; private set; } = QualityTier.High;

    /// <summary>Raised when the live governor steps the tier down. The app
    /// re-applies the frame-rate fields; geometry-scaled knobs (Saturn detail,
    /// cache scale) take effect on the next rebuild / summon.</summary>
    public static event Action? Changed;

    // ---- Per-tier knobs --------------------------------------------------
    // High == the original values, so a High-tier machine sees no change.

    /// <summary>Always-on background loop rate (planet/Saturn spin, glass
    /// shimmer, running pulses). Capable HW keeps the smooth 60; weaker tiers
    /// throttle to 30 to free the software-composition budget.</summary>
    public static int LoopFrameRate => Tier == QualityTier.High ? 60 : 30;

    /// <summary>Very slow ambient drift rate (the glass orbit light, one rev /
    /// minute). 60 on High; 30 below — visually identical at that speed but half
    /// the layered-window uploads.</summary>
    public static int SlowDriftFrameRate => Tier == QualityTier.High ? 60 : 30;

    /// <summary>Multiplier on the heavy BitmapCache render scale (the scroll and
    /// summon-rise grid caches). Lower = fewer rasterised pixels per frame on
    /// weak / high-DPI panels, where those caches dominate the upload.</summary>
    public static double CacheRenderScale => Tier switch
    {
        QualityTier.High => 1.0,
        QualityTier.Medium => 0.85,
        _ => 0.70,
    };

    /// <summary>Density multiplier for Saturn's purely-decorative micro detail
    /// (denser starfield + icy speckle). The ring STRUCTURE is unaffected, so the
    /// dock still reads as Saturn at every tier.</summary>
    public static double SaturnDetailFactor => Tier switch
    {
        QualityTier.High => 1.0,
        QualityTier.Medium => 0.75,
        _ => 0.5,
    };

    /// <summary>Whether the discrete "extra" Saturn detail groups (extra ringlets,
    /// a second shimmer/spoke/clump per ring) are built. Kept on except at the
    /// lowest tier, which falls back to the lighter baseline ring set.</summary>
    public static bool SaturnExtraDetail => Tier != QualityTier.Low;

    /// <summary>Whether the heaviest always-on animated blur (the glass orbit
    /// light sprite) is built. Dropped on the lowest tier, where re-compositing
    /// its blurred sprite every frame is the single biggest continuous cost.</summary>
    public static bool HeavyBlurEnabled => Tier != QualityTier.Low;

    // ---- Detection -------------------------------------------------------

    private static bool _detected;

    /// <summary>Classifies the machine once. Safe to call repeatedly.</summary>
    public static void Detect()
    {
        if (_detected)
            return;
        _detected = true;

        // Debug/QA override: POLARIS_TIER=High|Medium|Low forces a tier so the
        // FPS profiler can measure a degraded tier on capable hardware. Ignored
        // when unset or unrecognised.
        string? forced = Environment.GetEnvironmentVariable("POLARIS_TIER");
        if (!string.IsNullOrWhiteSpace(forced) &&
            Enum.TryParse<QualityTier>(forced.Trim(), ignoreCase: true, out var t))
        {
            Tier = t;
            return;
        }

        int renderTier = RenderCapability.Tier >> 16;   // 0 = software, 1/2 = GPU
        int cores = Environment.ProcessorCount;
        double area = PrimaryPixelArea();
        const double fhd = 1920.0 * 1080.0;

        if (renderTier == 0)
        {
            // No hardware acceleration: the layered window is fully software
            // composited every frame — start conservative.
            Tier = QualityTier.Low;
        }
        else if (cores <= 4 && area > fhd * 1.2)
        {
            // A modest CPU pushing a larger-than-FHD surface. The layered-window
            // upload is CPU-bound, so start one notch down. A powerful CPU stays
            // High at ANY resolution (incl. 4K) — resolution alone never demotes;
            // the live governor steps a machine down only if it truly can't hold
            // 60.
            Tier = QualityTier.Medium;
        }
        else
        {
            Tier = QualityTier.High;
        }
    }

    private static double PrimaryPixelArea()
    {
        try
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            if (b is { Width: > 0, Height: > 0 } r)
                return (double)r.Width * r.Height;   // physical pixels
        }
        catch { /* fall through to a safe default */ }
        return 1920.0 * 1080.0;
    }

    // ---- Live governor ---------------------------------------------------

    private static double _emaMs;
    private static double _slowSeconds;
    private static TimeSpan _last = TimeSpan.MinValue;
    private static bool _watching;

    /// <summary>Starts watching the live frame time. Call when a dock is shown
    /// and animating; cheap — it only accumulates an EMA off the render tick the
    /// shown dock is already driving.</summary>
    public static void BeginWatch()
    {
        if (_watching)
            return;
        _watching = true;
        _last = TimeSpan.MinValue;
        _slowSeconds = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Stops watching the live frame time (call when the dock is
    /// dismissed) so an idle app pays nothing.</summary>
    public static void EndWatch()
    {
        if (!_watching)
            return;
        _watching = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re)
            return;
        TimeSpan t = re.RenderingTime;
        if (_last == TimeSpan.MinValue)
        {
            _last = t;
            return;
        }
        if (t == _last)
            return;
        double dt = (t - _last).TotalSeconds;
        _last = t;
        if (dt <= 0 || dt > 0.5)
            return;                       // ignore pauses / outliers

        double ms = dt * 1000.0;
        _emaMs = _emaMs <= 0 ? ms : _emaMs * 0.9 + ms * 0.1;

        // 60 fps == 16.7 ms. Allow headroom: a sustained EMA above 22 ms
        // (~<45 fps) for ~2 s of shown animation steps the tier down once.
        if (_emaMs > 22.0)
        {
            _slowSeconds += dt;
            if (_slowSeconds >= 2.0 && Tier != QualityTier.Low)
            {
                Tier = (QualityTier)((int)Tier + 1);
                _emaMs = 0;
                _slowSeconds = 0;
                Changed?.Invoke();
            }
        }
        else
        {
            _slowSeconds = Math.Max(0, _slowSeconds - dt);
        }
    }
}
