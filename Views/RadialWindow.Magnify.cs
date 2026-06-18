using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Polaris.Views;

public partial class RadialWindow
{
    // ---- Cursor-distance magnification for the MAIN dock -------------------
    //
    // High performance mode only: instead of the binary per-icon hover zoom
    // (enter => 1.7x, leave => 1.0x), every icon is magnified continuously by a
    // smooth function of its 2-D distance from the cursor — the icon nearest the
    // pointer grows the most and the effect eases to nothing further away (a
    // fisheye / macOS-dock feel that works for both the Saturn rings and the
    // liquid-glass grid). The icon directly under the pointer stays put (its
    // outward push is ~0), so it never slides out from under the cursor.
    //
    // This adds a per-frame render loop while the pointer is over the dock, so it
    // is gated to High mode; Low mode keeps the cheaper, event-driven hover zoom.

    private double[] _magCur = Array.Empty<double>();
    private double[] _magOffX = Array.Empty<double>();
    private double[] _magOffY = Array.Empty<double>();
    // The scale/offsets last PUSHED to each icon, and its last applied z-index.
    // We only call SetMagnify / SetZIndex when a value has moved past a small
    // epsilon, so icons sitting at rest (far from the cursor) cost nothing per
    // frame instead of re-writing four transform properties + a z-order sort.
    private double[] _magAppScale = Array.Empty<double>();
    private double[] _magAppX = Array.Empty<double>();
    private double[] _magAppY = Array.Empty<double>();
    private int[] _magAppZ = Array.Empty<int>();
    private bool _magTicking;
    private TimeSpan _magLastTick;
    // Cursor in CONTENT coordinates while the wave is active; NaN eases to rest.
    private Point _magCursor = new(double.NaN, double.NaN);

    /// <summary>Peak magnification directly under the cursor (matches the legacy
    /// hover zoom so High and Low modes reach the same maximum size).</summary>
    private const double MagnifyPeak = DockTuning.HoverScale;

    /// <summary>True when the main dock should use the continuous cursor-distance
    /// magnification (High performance mode). Low mode keeps the event-driven
    /// per-icon hover zoom.</summary>
    private bool MainMagnifyEnabled =>
        _config.Settings.PerformanceMode == Models.PerformanceMode.High;

    /// <summary>Influence radius of the wave in DIPs; icons further than this from
    /// the cursor stay at rest. A raised cosine falls off smoothly to the edge.</summary>
    private double MagnifySupport => EffectiveIconSize * 1.3;

    private double MagnifyScaleAt(double dist)
    {
        double s = MagnifySupport;
        if (dist >= s)
            return 1.0;
        double f = 0.5 * (1.0 + Math.Cos(Math.PI * dist / s));
        return 1.0 + (MagnifyPeak - 1.0) * f;
    }

    /// <summary>Feeds the live pointer (in PanelCanvas coordinates) to the wave.
    /// Activates only while the pointer is near an icon so the loop can stop once
    /// the cursor leaves the dock and everything has settled back to rest.</summary>
    private void UpdateMagnifyFromPointer(Point pInPanel)
    {
        if (!MainMagnifyEnabled || _pressedIcon != null || _iconElements.Count == 0)
        {
            _magCursor = new Point(double.NaN, double.NaN);
            return;
        }

        Point content = GlassToContent(pInPanel);
        double nearest = double.MaxValue;
        for (int i = 0; i < _iconElements.Count && i < _slotPositions.Count; i++)
            nearest = Math.Min(nearest, (_slotPositions[i] - content).Length);

        if (nearest <= MagnifySupport)
        {
            _magCursor = content;
            EnsureMagTicking();
        }
        else
        {
            _magCursor = new Point(double.NaN, double.NaN);
        }
    }

    /// <summary>Clears the wave immediately (used on hide / drag start / rebuild).</summary>
    private void ResetMagnify()
    {
        _magCursor = new Point(double.NaN, double.NaN);
        for (int i = 0; i < _iconElements.Count; i++)
        {
            _iconElements[i].SetMagnify(1.0, 0.0, 0.0);
            if (_iconElements[i] != _pressedIcon)
                Panel.SetZIndex(_iconElements[i], 0);
        }
        if (_magCur.Length != 0)
            Array.Clear(_magCur);
        if (_magOffX.Length != 0)
            Array.Clear(_magOffX);
        if (_magOffY.Length != 0)
            Array.Clear(_magOffY);
        if (_magAppScale.Length != 0)
            Array.Clear(_magAppScale);
        if (_magAppX.Length != 0)
            Array.Clear(_magAppX);
        if (_magAppY.Length != 0)
            Array.Clear(_magAppY);
        if (_magAppZ.Length != 0)
            Array.Clear(_magAppZ);
        StopMagTicking();
    }

    private void EnsureMagTicking()
    {
        if (_magTicking)
            return;
        _magTicking = true;
        _magLastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnMagTick;
    }

    private void StopMagTicking()
    {
        if (!_magTicking)
            return;
        CompositionTarget.Rendering -= OnMagTick;
        _magTicking = false;
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        // The pointer left the dock window: let the wave ease everything back to
        // rest (the tick loop stops itself once it has settled).
        if (_magTicking)
        {
            _magCursor = new Point(double.NaN, double.NaN);
            EnsureMagTicking();
        }
    }

    private void OnMagTick(object? sender, EventArgs e)
    {
        int n = _iconElements.Count;
        if (n == 0 || !MainMagnifyEnabled)
        {
            StopMagTicking();
            return;
        }
        if (_magCur.Length != n)
        {
            var old = _magCur; var ox = _magOffX; var oy = _magOffY;
            _magCur = new double[n];
            _magOffX = new double[n];
            _magOffY = new double[n];
            for (int i = 0; i < n; i++)
            {
                _magCur[i] = i < old.Length ? old[i] : 1.0;
                _magOffX[i] = i < ox.Length ? ox[i] : 0.0;
                _magOffY[i] = i < oy.Length ? oy[i] : 0.0;
            }
            // Resize the "last applied" trackers; seed to impossible sentinels
            // (NOT NaN — any comparison with NaN is false, which would suppress
            // the first push) so the first tick after a resize always pushes
            // each icon once.
            _magAppScale = new double[n];
            _magAppX = new double[n];
            _magAppY = new double[n];
            _magAppZ = new int[n];
            for (int i = 0; i < n; i++)
            {
                _magAppScale[i] = -1.0;
                _magAppX[i] = double.MinValue;
                _magAppY[i] = double.MinValue;
                _magAppZ[i] = int.MinValue;
            }
        }

        double dt = 1.0 / 60.0;
        if (e is RenderingEventArgs rea)
        {
            if (_magLastTick > TimeSpan.Zero)
            {
                double since = (rea.RenderingTime - _magLastTick).TotalSeconds;
                // Throttle to the active performance profile so a high-refresh
                // panel doesn't tick the wave far above the target frame rate.
                double budget = 1.0 / Math.Max(1, App.AnimationFrameRate) - 0.0008;
                if (since < budget)
                    return;
                dt = Math.Clamp(since, 0.001, 0.05);
            }
            _magLastTick = rea.RenderingTime;
        }
        double k = 1.0 - Math.Exp(-dt / 0.045);

        bool active = !double.IsNaN(_magCursor.X);

        // The focal icon is the one nearest the cursor (the most magnified). It
        // stays anchored at its slot; every OTHER icon is shoved away from that
        // FIXED slot to make room — so the focal icon never slides as the pointer
        // wanders inside its cell, yet the neighbours visibly part around it.
        int focal = -1;
        if (active)
        {
            double best = double.MaxValue;
            for (int i = 0; i < n && i < _slotPositions.Count; i++)
            {
                if (_iconElements[i] == _pressedIcon)
                    continue;
                double dd = (_slotPositions[i] - _magCursor).Length;
                if (dd < best) { best = dd; focal = i; }
            }
        }
        double focalF = 0.0;
        Point fp = default;
        if (focal >= 0)
        {
            double fd = (_slotPositions[focal] - _magCursor).Length;
            focalF = (MagnifyScaleAt(fd) - 1.0) / (MagnifyPeak - 1.0);  // 0..1 intensity
            fp = _slotPositions[focal];
        }

        // Match the low-performance hover spread exactly (SpreadNeighbours), in
        // raw icon units; both read the shared DockTuning spread constants.
        double iconSize = _config.Settings.IconSize;
        double influence = iconSize * DockTuning.SpreadInfluence;
        double push = iconSize * DockTuning.SpreadPush;
        double maxDelta = 0.0;

        for (int i = 0; i < n; i++)
        {
            if (_iconElements[i] == _pressedIcon || i >= _slotPositions.Count)
            {
                _magCur[i] = 1.0;
                continue;
            }

            double d = active ? (_slotPositions[i] - _magCursor).Length : 0.0;
            double target = active ? MagnifyScaleAt(d) : 1.0;
            double cur = _magCur[i] + (target - _magCur[i]) * k;
            _magCur[i] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));

            // Spread target: shove this icon away from the focal icon's slot,
            // falling off with distance and scaled by the focal magnification.
            // The focal icon itself (and the dragged icon) get zero spread.
            double tx = 0.0, ty = 0.0;
            if (active && focal >= 0 && i != focal && focalF > 0.001)
            {
                Vector v = _slotPositions[i] - fp;
                double vd = v.Length;
                if (vd > 0.01 && vd < influence)
                {
                    double amt = push * (1.0 - vd / influence) * focalF;
                    tx = v.X / vd * amt;
                    ty = v.Y / vd * amt;
                }
            }
            _magOffX[i] += (tx - _magOffX[i]) * k;
            _magOffY[i] += (ty - _magOffY[i]) * k;
            maxDelta = Math.Max(maxDelta, Math.Abs(tx - _magOffX[i]));
            maxDelta = Math.Max(maxDelta, Math.Abs(ty - _magOffY[i]));

            // Only touch the icon when something actually moved past a small
            // epsilon: icons resting at 1.0x far from the cursor are left alone
            // instead of re-writing four transform properties every frame.
            if (Math.Abs(cur - _magAppScale[i]) > 1e-4 ||
                Math.Abs(_magOffX[i] - _magAppX[i]) > 1e-3 ||
                Math.Abs(_magOffY[i] - _magAppY[i]) > 1e-3)
            {
                _iconElements[i].SetMagnify(cur, _magOffX[i], _magOffY[i]);
                _magAppScale[i] = cur;
                _magAppX[i] = _magOffX[i];
                _magAppY[i] = _magOffY[i];
            }
            int z = cur > 1.001 ? 3000 + (int)(cur * 1000) : 0;
            if (z != _magAppZ[i])
            {
                Panel.SetZIndex(_iconElements[i], z);
                _magAppZ[i] = z;
            }
        }

        if (!active && maxDelta < 0.0015)
        {
            for (int i = 0; i < n; i++)
            {
                if (_iconElements[i] == _pressedIcon)
                    continue;
                _iconElements[i].SetMagnify(1.0, 0.0, 0.0);
                _magCur[i] = 1.0;
                _magOffX[i] = 0.0;
                _magOffY[i] = 0.0;
                _magAppScale[i] = 1.0;
                _magAppX[i] = 0.0;
                _magAppY[i] = 0.0;
                Panel.SetZIndex(_iconElements[i], 0);
                _magAppZ[i] = 0;
            }
            StopMagTicking();
        }
    }
}
