using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace Polaris.Views;

/// <summary>Pauses the main dock's perpetual ambient animations (the glass orbit
/// light sweep and the running icons' sweep/breathing glow) whenever the cursor is
/// not actively moving.
///
/// <para>The liquid-glass main dock is a large (often full-screen) per-pixel-alpha
/// (<c>AllowsTransparency</c>) layered window: WPF software-composites it on the CPU
/// and, having no dirty-rect upload, re-uploads the ENTIRE window bitmap every time
/// any pixel changes. The forever-looping orbit light and running glows therefore
/// re-composite the whole surface every tick, pinning the process at a high idle CPU
/// and keeping its pages resident even when the user is not interacting — which also
/// defeats the idle working-set trim. Routing these loops through controllable clocks
/// lets us freeze them the instant the cursor stops moving (holding each at its
/// current value, so nothing disappears) and resume them on the next movement, so a
/// parked dock is fully static and cheap.</para></summary>
public partial class RadialWindow
{
    private bool _ambientPaused;
    private readonly List<ClockController> _ambientClocks = new();

    /// <summary>Creates a controllable clock for a perpetual ambient animation and
    /// registers it so it can be paused/resumed with the dock's attended state. The
    /// caller applies the returned clock to its target (a <see cref="UIElement"/> or
    /// an <see cref="System.Windows.Media.RotateTransform"/>, both
    /// <see cref="IAnimatable"/>).</summary>
    private AnimationClock RegisterAmbientClock(DoubleAnimation anim)
    {
        AnimationClock clock = anim.CreateClock();
        if (clock.Controller is { } ctrl)
        {
            if (_ambientPaused)
                ctrl.Pause();
            _ambientClocks.Add(ctrl);
        }
        return clock;
    }

    /// <summary>Convenience matching <see cref="RadialIcon.AmbientRegistrar"/>: applies
    /// a pausable ambient loop to an animatable target.</summary>
    private void RegisterAmbientLoop(IAnimatable target, DependencyProperty prop, DoubleAnimation anim)
        => target.ApplyAnimationClock(prop, RegisterAmbientClock(anim));

    /// <summary>Drops all registered ambient clocks (called when the dock visual
    /// tree — and its clocks — is torn down).</summary>
    private void ClearAmbientLoops() => _ambientClocks.Clear();

    /// <summary>Freezes (or resumes) every perpetual ambient loop. Driven by the
    /// cursor poll: <paramref name="paused"/> is true while the dock is shown but the
    /// cursor is not moving, false while it is moving. A no-op when unchanged.</summary>
    public void SetAmbientPaused(bool paused)
    {
        if (paused == _ambientPaused)
            return;
        _ambientPaused = paused;
        foreach (ClockController ctrl in _ambientClocks)
        {
            if (paused)
                ctrl.Pause();
            else
                ctrl.Resume();
        }
    }
}
