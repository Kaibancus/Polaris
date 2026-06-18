using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace Polaris.Views;

/// <summary>Pauses the side dock's perpetual "breathing" animations (running-dot
/// pulses on the pinned column and the running strip, plus the dark-dock star
/// twinkle) whenever the cursor is not over the dock.
///
/// <para>The side dock is a per-pixel-alpha (<c>AllowsTransparency</c>) layered
/// window: WPF software-composites it on the CPU and every opacity tick re-uploads
/// the whole window surface. A handful of forever-looping opacity animations
/// therefore pin the process at a high idle CPU (~60%) and keep its pages resident
/// even when nobody is looking, which also defeats the idle working-set trim. By
/// driving these loops through controllable clocks we can freeze them the moment
/// the cursor leaves (holding each element at its current opacity, so the green
/// running dots stay visible — only their motion stops) and resume them when the
/// cursor returns. While frozen the window is static, so the CPU drops to idle and
/// the trim can reclaim the RAM.</para></summary>
public partial class SideDockWindow
{
    private bool _ambientPaused;
    private readonly List<ClockController> _ambientClocks = new();

    /// <summary>Applies a perpetual ambient animation as a controllable clock and
    /// registers it so it can be paused/resumed with the dock's attended state.
    /// Used by the running-strip dots, the star twinkle and (via
    /// <see cref="RadialIcon.AmbientRegistrar"/>) the pinned icons' running dots.</summary>
    private void RegisterAmbientLoop(IAnimatable target, DependencyProperty prop, DoubleAnimation anim)
    {
        AnimationClock clock = anim.CreateClock();
        target.ApplyAnimationClock(prop, clock);
        if (clock.Controller is { } ctrl)
        {
            if (_ambientPaused)
                ctrl.Pause();
            _ambientClocks.Add(ctrl);
        }
    }

    /// <summary>Drops all registered ambient clocks (called when the dock is
    /// rebuilt, since the old visual tree — and its clocks — are discarded).</summary>
    private void ClearAmbientLoops() => _ambientClocks.Clear();

    /// <summary>Freezes (or resumes) every perpetual ambient loop. Called from the
    /// cursor poll: <paramref name="paused"/> is true while the dock is shown but
    /// the cursor is away from it, false while the cursor is over (or summoning)
    /// it. A no-op when the state is unchanged.</summary>
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
