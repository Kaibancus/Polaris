using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class SideDockWindow
{
    // ---- macOS-dock magnification wave (continuous + frame-smoothed) -------

    /// <summary>Drives the wave + hover label from the live window pointer
    /// position. Activation is tested against the STATIC pinned viewport rect —
    /// never against the per-icon hit areas — so the pop-out (which slides an
    /// icon out from under the cursor) cannot create an enter/leave feedback
    /// loop. That loop was the source of the hover flicker/jump.</summary>
    private void UpdateWaveFromPointer(Point p)
    {
        // While a launch bounce is playing, or while the dock is dismissing after a
        // launch, keep the wave fully quiescent — a wave restart would call
        // SetMagnify (re-magnifying the icon under the still-stationary cursor) and
        // could cancel the bounce animation.
        if (_bounceHold || _dismissing)
            return;

        bool valid = !double.IsNaN(p.X) && !double.IsNaN(p.Y);
        // The wave activates over the whole dock body (pinned column + running
        // strip) so it stays continuous as the cursor crosses the seam.
        bool inDock = valid
            && (_pinnedIcons.Count > 0 || _runCenterMain.Count > 0)
            && _waveHitRect.Contains(p);
        if (inDock)
        {
            _waveCursorY = MainOf(p);
            EnsureWaveTicking();
        }
        else
        {
            _waveCursorY = double.NaN;   // the tick loop eases everything back to rest
            EnsureWaveTicking();         // re-arm in case the loop had stopped while
                                         // the cursor sat still on the dock, so the
                                         // ease-back to rest always runs
        }

        // The pinned hover label is still gated on the pinned viewport only; the
        // running tiles drive their own label/preview from their fixed hit areas.
        bool inColumn = valid && _pinnedIcons.Count > 0 && _pinnedViewport.Contains(p);
        if (inColumn)
        {
            int idx = NearestVisibleIconIndex(MainOf(p));
            if (idx != _labelIdx)
            {
                _labelIdx = idx;
                if (idx >= 0)
                    ShowHoverLabel(_pinnedIcons[idx], idx);
                else
                    HideHoverLabel();
            }
        }
        else
        {
            if (_labelIdx >= 0)
            {
                _labelIdx = -1;
                // Don't fight a running-strip tile that is currently showing its
                // own hover label (the pointer is over the running area below the
                // pinned viewport).
                if (!_runLabelShown)
                    HideHoverLabel();
            }
        }
    }

    /// <summary>Pinned icon whose (scrolled) centre is closest to <paramref name="y"/>.</summary>
    private int NearestVisibleIconIndex(double y)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _pinnedIcons.Count; i++)
        {
            double d = Math.Abs((_pinnedSlots[i].Y - _pinnedScroll) - y);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    // Influence radius of the wave, in icon-cell units. Icons within this many
    // cells of the cursor are magnified; the falloff is a smooth raised cosine.
    // A smaller support makes the wave sharper/peakier (neighbours fall off to
    // rest faster), so the bulge is concentrated under the pointer.
    private const double WaveSupport = 2.3;

    /// <summary>Continuous magnification for an icon, as a smooth function of its
    /// distance (in cells) from the cursor — peaks at <see cref="HoverScale"/>
    /// directly under the pointer and eases to 1.0 at the support edge.</summary>
    private double WaveScaleAt(double cursorY, double iconCenterY)
    {
        double d = Math.Abs(cursorY - iconCenterY) / CellH;
        if (d >= WaveSupport)
            return 1.0;
        double f = 0.5 * (1.0 + Math.Cos(Math.PI * d / WaveSupport));
        return 1.0 + (HoverScale - 1.0) * f;
    }

    /// <summary>Horizontal pop-out (toward the screen) for a given magnification,
    /// growing with the scale so larger icons jut out further — the macOS feel.</summary>
    private double WavePop(double scale) => (scale - 1.0) * GIcon * 1.18;

    // Black-flame geometry (in cross-axis units): how far the tallest tongue
    // licks past the resting edge. The base roots at the slab's edge side so the
    // opaque pedestal spans the full slab thickness and fuses with the body.
    private double FlameMaxHeight => GIcon * 1.05;

    private void EnsureWaveTicking()
    {
        if (_waveTicking)
            return;
        _waveTicking = true;
        _waveLastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnWaveTick;
    }

    /// <summary>Per-frame smoothing of the wave toward the cursor-driven target.
    /// Running this off the render clock (rather than restarting a per-icon
    /// animation on every MouseMove) is what makes the slide fluid: the cursor
    /// supplies the target, the frame loop eases every icon toward it.</summary>
    private void OnWaveTick(object? sender, EventArgs e)
    {
        int n = _pinnedIcons.Count;
        if (_waveCur.Length != n)
        {
            var old = _waveCur;
            _waveCur = new double[n];
            for (int i = 0; i < n; i++)
                _waveCur[i] = i < old.Length ? old[i] : 1.0;
        }

        // Frame-rate-independent easing (tau = 45 ms) so the feel is identical
        // on 60 Hz and high-refresh panels.
        double dt = 1.0 / 60.0;
        if (e is RenderingEventArgs rea)
        {
            if (_waveLastTick > TimeSpan.Zero)
            {
                double since = (rea.RenderingTime - _waveLastTick).TotalSeconds;
                // Throttle to the active performance profile's frame rate. This
                // render loop fires once per composited frame (the monitor refresh),
                // so without this cap a high-refresh panel ticks the wave far above
                // the profile target — the reason low-performance mode didn't appear
                // to limit the frame rate. Skip frames until the budget elapses.
                double budget = 1.0 / Math.Max(1, App.AnimationFrameRate) - 0.0008;
                if (since < budget)
                    return;
                dt = Math.Clamp(since, 0.001, 0.05);
            }
            _waveLastTick = rea.RenderingTime;
        }
        double k = 1.0 - Math.Exp(-dt / 0.045);

        bool active = !double.IsNaN(_waveCursorY);
        double maxDelta = 0.0;
        for (int i = 0; i < n; i++)
        {
            double iconCenterY = _pinnedSlots[i].Y - _pinnedScroll;
            double target = active ? WaveScaleAt(_waveCursorY, iconCenterY) : 1.0;
            double cur = _waveCur[i] + (target - _waveCur[i]) * k;
            _waveCur[i] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            var (pdx, pdy) = PopOffset(WavePop(cur));
            _pinnedIcons[i].SetMagnify(cur, pdx, pdy);
            // Larger icons sit on top so the bulge overlaps its neighbours cleanly.
            Panel.SetZIndex(_pinnedIcons[i], cur > 1.001 ? 3000 + (int)(cur * 1000) : 0);
        }

        // Same wave, applied to the running strip below the seam, so a hovered
        // running icon magnifies — and its neighbours ripple — exactly like the
        // pinned column. The cursor coordinate is shared, so the bulge glides
        // seamlessly across the divider.
        int rn = _runCenterMain.Count;
        if (_runWaveCur.Length != rn)
        {
            var old = _runWaveCur;
            _runWaveCur = new double[rn];
            for (int j = 0; j < rn; j++)
                _runWaveCur[j] = j < old.Length ? old[j] : 1.0;
        }
        for (int j = 0; j < rn; j++)
        {
            double target = active ? WaveScaleAt(_waveCursorY, _runCenterMain[j]) : 1.0;
            double cur = _runWaveCur[j] + (target - _runWaveCur[j]) * k;
            _runWaveCur[j] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            var (pdx, pdy) = PopOffset(WavePop(cur));
            _runScale[j].ScaleX = _runScale[j].ScaleY = cur;
            _runTrans[j].X = pdx;
            _runTrans[j].Y = pdy;
            Panel.SetZIndex(_runTiles[j], cur > 1.001 ? 3000 + (int)(cur * 1000) : 60);
        }

        // Deform the black Saturn background so its interior edge follows the wave.
        maxDelta = Math.Max(maxDelta, UpdateDebrisWave(k));
        UpdateWaveBulge();

        // Once the wave has converged to its CURRENT target, stop the render loop.
        // Previously it only stopped when the cursor had fully left (active=false);
        // while the cursor sat still ON the dock the loop kept firing every vsync
        // (re-running SetMagnify on every icon and rebuilding the flame geometry),
        // which is what pinned the side dock at ~60% CPU during a stationary hover.
        // A later MouseMove re-arms the loop (EnsureWaveTicking) and re-eases toward
        // the new target, so the motion is visually identical.
        if (maxDelta < 0.0015)
        {
            if (!active)
            {
                // Settled all the way back to rest — normalise everything to 1.0.
                for (int i = 0; i < n; i++)
                {
                    _pinnedIcons[i].SetMagnify(1.0, 0.0, 0.0);
                    Panel.SetZIndex(_pinnedIcons[i], 0);
                }
                for (int j = 0; j < rn; j++)
                {
                    _runScale[j].ScaleX = _runScale[j].ScaleY = 1.0;
                    _runTrans[j].X = _runTrans[j].Y = 0.0;
                    _runWaveCur[j] = 1.0;
                    Panel.SetZIndex(_runTiles[j], 60);
                }
                foreach (var d in _debris)
                {
                    d.Cur = 0.0;
                    d.Tr.X = 0.0;
                    d.Tr.Y = 0.0;
                }
                UpdateWaveBulge();   // flatten the background bulge back to rest
            }
            CompositionTarget.Rendering -= OnWaveTick;
            _waveTicking = false;
        }
    }

    /// <summary>
    /// Rebuilds the Saturn dark dock's "black flame": a single large tongue that
    /// rides the magnification wave (centred on the wave's weighted peak, sized by
    /// its intensity) rather than one spike per icon. The silhouette is a smooth,
    /// Catmull-Rom flame profile — wide bellied base tapering to a leaning,
    /// flickering tip — rooted deep in the solid slab so its base fuses with the
    /// background and dissolves at the tip via the fill gradient. Pinches to
    /// nothing at rest; no-op for every non-Saturn (clear-glass) theme.
    /// </summary>
    private void UpdateWaveBulge()
    {
        var path = _waveBulge;
        if (path == null || !_darkDock)
            return;

        int n = _pinnedIcons.Count;
        bool pinnedOk = n > 0 && _waveCur.Length == n;
        bool runOk = _runCenterMain.Count > 0 && _runWaveCur.Length == _runCenterMain.Count;
        if (!pinnedOk && !runOk && !_bounceFlameActive)
        {
            path.Data = null;
            return;
        }

        double denom = Math.Max(0.0001, HoverScale - 1.0);

        // Collapse the per-icon wave into a single flame: peak intensity and the
        // magnification-weighted centre, so one tongue glides between icons as the
        // cursor sweeps instead of several fixed spikes. The pinned column AND the
        // running strip feed the SAME accumulator, so the tongue flows continuously
        // across the seam exactly as the cursor crosses it.
        double peak = 0.0, wsum = 0.0, csum = 0.0;
        if (pinnedOk)
        {
            for (int i = 0; i < n; i++)
            {
                double a = Math.Clamp((_waveCur[i] - 1.0) / denom, 0.0, 1.0);
                if (a <= 0.0)
                    continue;
                double w = a * a;
                double main = _pinnedSlots[i].Y - _pinnedScroll;
                wsum += w;
                csum += w * main;
                if (a > peak)
                    peak = a;
            }
        }
        if (runOk)
        {
            for (int j = 0; j < _runCenterMain.Count; j++)
            {
                double a = Math.Clamp((_runWaveCur[j] - 1.0) / denom, 0.0, 1.0);
                if (a <= 0.0)
                    continue;
                double w = a * a;
                double main = _runCenterMain[j];   // running tiles are not scrolled
                wsum += w;
                csum += w * main;
                if (a > peak)
                    peak = a;
            }
        }

        if (peak < 0.05 || wsum <= 0.0)
        {
            // No wave, but a launch bounce may still want its own tongue.
            if (_bounceFlameActive && _bounceFlameAmp > 0.01)
            {
                double ba = Math.Clamp(_bounceFlameAmp, 0.0, 1.0);
                peak = ba;
                wsum = ba * ba;
                csum = wsum * _bounceFlameCenterMain;
            }
            else
            {
                path.Data = null;
                return;
            }
        }
        else if (_bounceFlameActive && _bounceFlameAmp > 0.01)
        {
            // Blend the hop tongue in alongside any residual wave.
            double ba = Math.Clamp(_bounceFlameAmp, 0.0, 1.0);
            double w = ba * ba;
            wsum += w;
            csum += w * _bounceFlameCenterMain;
            if (ba > peak)
                peak = ba;
        }

        double cm = csum / wsum;                       // flame centre (main axis)
        double baseEdge = _bodyCross + _bodyCrossLen;  // resting interior edge
        // Root just inside the solid slab core — below the interior feather band
        // (so the blurred base is buried in solid black and leaves no seam) but
        // NOT down at the screen edge (so the flame never spills past the dock).
        double rootC = Math.Max(_bodyCross, baseEdge - _flameFeather - GIcon * 0.55);
        double t = _waveLastTick.TotalSeconds;         // flicker clock

        double half = CellH * (2.05 + 1.85 * peak);   // wide footprint: long, gradual flanks
        double flick = Math.Sin(t * 8.5) * 0.5 + Math.Sin(t * 5.3 + 1.1) * 0.5;
        double H = Math.Pow(peak, 1.1) * FlameMaxHeight * 0.90 * (0.88 + 0.12 * flick);
        double lean = (0.18 * Math.Sin(t * 3.7) + 0.10 * Math.Sin(t * 6.1 + 0.7)) * H;

        // Sample the flame silhouette as a height profile over the footprint. The
        // envelope blends a sharp central peak with a wide bell, raised to a gamma so
        // the flanks ease down to zero with a flat tangent. Because the height never
        // dips below the resting edge, the flanks GRAZE the dock surface tangentially
        // (no transversal crossing, no visible corner) and merge into it. A small
        // surface ripple and a height-weighted lateral lean make the tip curl and
        // flicker organically; the buried base anchors the shape inside the slab.
        const int M = 30;
        // Smooth taper so the flame fades out before the dock's rounded main-axis
        // ends — it must never jut past the corner where there's no slab beneath it.
        double mainLo = _slabMain, mainHi = _slabMain + _slabMainLen;
        double endPad = GIcon * 0.45, endRamp = GIcon * 1.00;
        static double SS(double e) { e = Math.Clamp(e, 0.0, 1.0); return e * e * (3.0 - 2.0 * e); }
        var q = _flameSilhouette ??= new Point[M + 1];   // reused silhouette buffer
        for (int k = 0; k <= M; k++)
        {
            double x = -1.0 + 2.0 * k / M;
            double b = 0.5 * (1.0 + Math.Cos(Math.PI * x));
            double env = Math.Pow(0.40 * b * b + 0.60 * b, 1.6);   // sharp peak; flanks ease flat into the dock
            double protUp = H * env * (1.0 + 0.05 * Math.Sin(3.5 * Math.PI * x + t * 5.0));
            double up = Math.Pow(Math.Clamp(protUp / Math.Max(1e-6, H), 0.0, 1.0), 1.3);
            double m = cm + x * half + lean * up;
            double edgeFac = SS((m - (mainLo + endPad)) / endRamp) * SS(((mainHi - endPad) - m) / endRamp);
            protUp *= edgeFac;
            q[k] = new Point(m, baseEdge + protUp);
        }

        // Build a closed figure: straight up the left base from the buried root,
        // a smooth Catmull-Rom curve over the silhouette, straight down the right
        // base, and an implicit straight base across the slab interior.
        Point CR1(int i) => new Point(
            q[i].X + (q[Math.Min(M, i + 1)].X - q[Math.Max(0, i - 1)].X) / 6.0,
            q[i].Y + (q[Math.Min(M, i + 1)].Y - q[Math.Max(0, i - 1)].Y) / 6.0);
        Point CR2(int i) => new Point(
            q[i + 1].X - (q[Math.Min(M, i + 2)].X - q[i].X) / 6.0,
            q[i + 1].Y - (q[Math.Min(M, i + 2)].Y - q[i].Y) / 6.0);

        // Reuse a single unfrozen StreamGeometry, rewriting it in place each
        // frame, instead of allocating + freezing a fresh geometry per frame.
        // Output is pixel-identical; it just removes the per-frame allocation.
        var geo = _flameGeo ??= new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToLocal(cm - half, rootC), true, true);
            ctx.LineTo(ToLocal(q[0].X, q[0].Y), false, false);
            for (int i = 0; i < M; i++)
                ctx.BezierTo(
                    ToLocal(CR1(i).X, CR1(i).Y),
                    ToLocal(CR2(i).X, CR2(i).Y),
                    ToLocal(q[i + 1].X, q[i + 1].Y),
                    false, true);
            ctx.LineTo(ToLocal(cm + half, rootC), false, false);
        }
        if (!ReferenceEquals(path.Data, geo))
            path.Data = geo;
    }

    /// <summary>Hard, immediate reset of the wave (used on rebuild / drag start).</summary>
    private void ResetWave()
    {
        _waveCursorY = double.NaN;
        if (_labelIdx >= 0)
        {
            _labelIdx = -1;
            HideHoverLabel();
        }
        foreach (var ic in _pinnedIcons)
        {
            ic.SetMagnify(1.0, 0.0, 0.0);
            Panel.SetZIndex(ic, 0);
        }
        for (int j = 0; j < _runScale.Count; j++)
        {
            _runScale[j].ScaleX = _runScale[j].ScaleY = 1.0;
            _runTrans[j].X = _runTrans[j].Y = 0.0;
            if (j < _runTiles.Count)
                Panel.SetZIndex(_runTiles[j], 60);
        }
        for (int j = 0; j < _runWaveCur.Length; j++)
            _runWaveCur[j] = 1.0;
        if (_waveTicking)
        {
            CompositionTarget.Rendering -= OnWaveTick;
            _waveTicking = false;
        }
        _waveCur = Array.Empty<double>();
        if (_waveBulge != null)
            _waveBulge.Data = null;
    }

    private void ShowHoverLabel(RadialIcon ic, int idx)
    {
        double mainC = _pinnedSlots[idx].Y - _pinnedScroll;
        double crossExtent = GIcon / 2.0 * HoverScale + WavePop(HoverScale);
        ShowHoverLabelCore(ic.Entry.Name, mainC, crossExtent);
    }

    /// <summary>Shared hover-label renderer used by both the pinned icons and the
    /// running strip, so both use the same font, size and styling.</summary>
    private void ShowHoverLabelCore(string name, double mainCenter, double crossExtent)
    {
        if (_hoverLabel == null)
        {
            _hoverLabelText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                // A dark drop shadow gives the name depth (more "3D") AND a dark
                // halo so light text stays legible even over a white window behind
                // the translucent label.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 5,
                    ShadowDepth = 1.4,
                    Direction = 315,
                    Opacity = 0.9,
                },
            };
            _hoverLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x05, 0x1A, 0x1A, 0x1A)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 4, 10, 4),
                IsHitTestVisible = false,
                Opacity = 0,
                Child = _hoverLabelText,
            };
            Panel.SetZIndex(_hoverLabel, 4000);
            PanelCanvas.Children.Add(_hoverLabel);
        }

        _hoverLabelText!.Text = name;

        // Cross position of the label's near edge: right at the hover-enlarged
        // icon's outer edge (no extra gap) so the name sits as close to the icon
        // as possible without being covered by it.
        double crossPos = _colCenterCross + crossExtent;
        double thickness = IsVertical ? WinW : WinH;
        double mainExtent = IsVertical ? WinH : WinW;

        // Auto-shrink the font so the WHOLE name fits without ellipsis. For
        // vertical docks the label grows along the cross axis toward the far
        // edge; for horizontal docks it grows along the main axis, centred on
        // the icon, so the budget is whichever side has less room.
        double maxFont = 10.5 * HoverScale * Polaris.Services.FontScale.Current;
        double minFont = 7.5 * HoverScale * Polaris.Services.FontScale.Current;
        double horizPad = 20 * _uiScale;                 // matches Border padding (10 + 10)
        double avail = IsVertical
            ? Math.Max(40 * _uiScale, thickness - crossPos - horizPad - 6 * _uiScale)
            : Math.Max(40 * _uiScale, 2 * Math.Min(mainCenter, mainExtent - mainCenter) - horizPad - 6 * _uiScale);
        _hoverLabelText.FontSize = FitFontSize(name, maxFont, minFont, avail);

        _hoverLabel.InvalidateMeasure();
        _hoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = _hoverLabel.DesiredSize.Width;
        double lh = _hoverLabel.DesiredSize.Height;

        // Anchor per edge so the label hangs off the icon toward the interior,
        // centred on the icon along the main axis.
        double left, top;
        switch (_side)
        {
            case DockSide.Right:
                left = WinW - crossPos - lw;       // grows left (interior)
                top = mainCenter - lh / 2.0;
                break;
            case DockSide.Top:
                left = mainCenter - lw / 2.0;
                top = crossPos;                    // grows down (interior)
                break;
            case DockSide.Bottom:
                left = mainCenter - lw / 2.0;
                top = WinH - crossPos - lh;        // grows up (interior)
                break;
            case DockSide.Left:
            default:
                left = crossPos;                   // grows right (interior)
                top = mainCenter - lh / 2.0;
                break;
        }
        Canvas.SetLeft(_hoverLabel, left);
        Canvas.SetTop(_hoverLabel, top);

        _hoverLabel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_hoverLabel.Opacity, 1, TimeSpan.FromMilliseconds(110)));
    }

    private void HideHoverLabel()
    {
        if (_hoverLabel == null)
            return;
        _hoverLabel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_hoverLabel.Opacity, 0, TimeSpan.FromMilliseconds(110)));
    }

    /// <summary>Largest font size in [<paramref name="minFont"/>,
    /// <paramref name="maxFont"/>] at which <paramref name="text"/> renders within
    /// <paramref name="availW"/> device-independent pixels, so the whole name
    /// shows without ellipsis. Falls back to the floor when even that overflows.</summary>
    private double FitFontSize(string text, double maxFont, double minFont, double availW)
    {
        if (string.IsNullOrEmpty(text) || availW <= 0)
            return maxFont;

        var tf = new Typeface(
            _hoverLabelText!.FontFamily, _hoverLabelText.FontStyle,
            _hoverLabelText.FontWeight, _hoverLabelText.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double Width(double fs)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, tf, fs, Brushes.White, dpi);
            return ft.WidthIncludingTrailingWhitespace;
        }

        if (Width(maxFont) <= availW)
            return maxFont;

        // Binary search for the largest fitting size.
        double lo = minFont, hi = maxFont;
        for (int i = 0; i < 12; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (Width(mid) <= availW)
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

}
