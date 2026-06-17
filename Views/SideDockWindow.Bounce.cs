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
    // ---- Launch -----------------------------------------------------------

    /// <summary>The (dx, dy) "hop" vector for a launch bounce: the icon leaps up
    /// off the dock surface and falls back. Vertical (Left/Right) and bottom docks
    /// jump up the screen; a top dock jumps down into the screen.</summary>
    private (double x, double y) BounceLift()
    {
        double amp = GIcon * 0.6;
        return _side == DockSide.Top ? (0.0, amp) : (0.0, -amp);
    }

    /// <summary>Cancels every open or pending hover thumbnail preview the instant a
    /// launch is committed, so no preview can pop while the dock plays its bounce and
    /// dismiss fade (the cursor is still parked over the clicked icon).</summary>
    private void SuppressHoverForLaunch()
    {
        HideHoverLabel();
        ClearRunPopups();
        foreach (var ic in _pinnedIcons)
            ic.ClosePreview();
    }

    private void Launch(AppEntry entry, RadialIcon? icon = null)
    {
        if (icon != null)
        {
            // Play the macOS-style hop FIRST while the dock is held visible, then
            // launch + dismiss when it finishes. Launching first would bring the
            // target window to the foreground over the dock, hiding the bounce.
            ResetWave();   // settle the wave so it can't fight the bounce transform
            SuppressHoverForLaunch();   // kill any open/pending thumbnail preview
            _bounceHold = true;
            Panel.SetZIndex(icon, 5000);   // hop above its neighbours
            var (lx, ly) = BounceLift();
            int idx = _pinnedIcons.IndexOf(icon);
            double centerMain = idx >= 0 && idx < _pinnedSlots.Count
                ? _pinnedSlots[idx].Y - _pinnedScroll
                : _colCenterCross;
            double maxLift = ly != 0 ? ly : lx;
            StartBounceFlame(icon.HopTransform, ly != 0, maxLift, centerMain);
            icon.PlayLaunchBounce(lx, ly, () =>
            {
                _bounceHold = false;
                _dismissing = true;   // block hover from re-magnifying during the fade
                Panel.SetZIndex(icon, 0);
                StopBounceFlame();
                AppLauncher.Launch(entry, null);
                SetEdgeShown(false);
            });
        }
        else
        {
            AppLauncher.Launch(entry, () => SetEdgeShown(false));
        }
    }

    /// <summary>Plays the launch bounce on a running-strip tile (a plain Grid
    /// whose inner visual carries the wave's scale + translate transforms), then
    /// runs <paramref name="onDone"/>. Falls back to running it immediately if the
    /// tile has no transform group.</summary>
    private void PlayRunTileBounce(FrameworkElement tileRoot, Action onDone)
    {
        ScaleTransform? scale = null;
        TranslateTransform? trans = null;
        if (tileRoot.Tag is FrameworkElement visual && visual.RenderTransform is TransformGroup tg)
        {
            foreach (var t in tg.Children)
            {
                if (t is ScaleTransform st) scale = st;
                else if (t is TranslateTransform tt) trans = tt;
            }
        }
        if (scale == null || trans == null)
        {
            onDone();
            return;
        }
        ResetWave();   // settle the wave so it can't fight the bounce transform
        SuppressHoverForLaunch();   // kill any open/pending thumbnail preview
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        trans.BeginAnimation(TranslateTransform.XProperty, null);
        trans.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = scale.ScaleY = 1.0;
        trans.X = trans.Y = 0.0;

        Panel.SetZIndex(tileRoot, 5000);   // hop above its neighbours
        var (lx, ly) = BounceLift();
        int ridx = _runTiles.IndexOf(tileRoot);
        double centerMain = ridx >= 0 && ridx < _runCenterMain.Count
            ? _runCenterMain[ridx]
            : _colCenterCross;
        double maxLift = ly != 0 ? ly : lx;
        StartBounceFlame(trans, ly != 0, maxLift, centerMain);
        var tx = DockBounce.BuildTranslate(lx);
        var ty = DockBounce.BuildTranslate(ly);
        var sx = DockBounce.BuildScale();
        var sy = DockBounce.BuildScale();
        _bounceHold = true;
        sy.Completed += (_, _) =>
        {
            _bounceHold = false;
            _dismissing = true;   // block hover from re-magnifying during the fade
            Panel.SetZIndex(tileRoot, 60);
            StopBounceFlame();
            onDone();
        };
        trans.BeginAnimation(TranslateTransform.XProperty, tx);
        trans.BeginAnimation(TranslateTransform.YProperty, ty);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    /// <summary>Starts feeding the Saturn flame from a live launch-bounce hop:
    /// each frame it reads how far the bouncing item has jumped and drives the
    /// flame intensity to match, so the tongue leaps up with the icon. No-op for
    /// non-Saturn (glass) docks. Stopped via StopBounceFlame when the bounce ends.</summary>
    private void StartBounceFlame(TranslateTransform trans, bool axisY, double maxLift, double centerMain)
    {
        if (!_darkDock || _waveBulge == null)
            return;
        _bounceFlameTrans = trans;
        _bounceFlameAxisY = axisY;
        _bounceFlameMaxLift = Math.Max(0.0001, Math.Abs(maxLift));
        _bounceFlameCenterMain = centerMain;
        _bounceFlameAmp = 0.0;
        if (!_bounceFlameActive)
        {
            _bounceFlameActive = true;
            _bounceFlameLastTick = TimeSpan.Zero;
            CompositionTarget.Rendering += OnBounceFlameTick;
        }
    }

    private void StopBounceFlame()
    {
        if (!_bounceFlameActive)
            return;
        _bounceFlameActive = false;
        _bounceFlameAmp = 0.0;
        _bounceFlameTrans = null;
        CompositionTarget.Rendering -= OnBounceFlameTick;
        UpdateWaveBulge();   // collapse the flame back to rest
    }

    private void OnBounceFlameTick(object? sender, EventArgs e)
    {
        var trans = _bounceFlameTrans;
        if (trans == null)
        {
            StopBounceFlame();
            return;
        }
        // Cap the bulge updates at the active profile's frame rate; this render
        // loop otherwise fires once per composited frame (the monitor refresh).
        if (e is RenderingEventArgs rea)
        {
            if (_bounceFlameLastTick > TimeSpan.Zero)
            {
                double since = (rea.RenderingTime - _bounceFlameLastTick).TotalSeconds;
                double budget = 1.0 / Math.Max(1, App.AnimationFrameRate) - 0.0008;
                if (since < budget)
                    return;
            }
            _bounceFlameLastTick = rea.RenderingTime;
        }
        double cur = _bounceFlameAxisY ? trans.Y : trans.X;
        _bounceFlameAmp = Math.Clamp(Math.Abs(cur) / _bounceFlameMaxLift, 0.0, 1.0);
        UpdateWaveBulge();
    }

    /// <summary>Sprinkles a small, faint starfield across the Saturn dock's black
    /// slab (pinned column + running strip), a few of them slowly twinkling — the
    /// same look as the main dock's planet backdrop, so the side dock reads as the
    /// same patch of space. Purely decorative; rebuilt with the slab each Layout.</summary>
    private void DrawDockStarfield(double baseEdge)
    {
        if (_slabMainLen <= 0)
            return;
        double s = Math.Max(0.5, _uiScale);
        double mainLo = _slabMain + GIcon * 0.1;
        double mainHi = _slabMain + _slabMainLen - GIcon * 0.1;
        double mainSpan = mainHi - mainLo;
        double crossLo = _bodyCross + GIcon * 0.08;
        double crossSpan = Math.Max(1.0, baseEdge - crossLo);
        if (mainSpan <= 0)
            return;

        var layer = new Canvas { IsHitTestVisible = false };
        Panel.SetZIndex(layer, -9);   // on the black slab, beneath the rubble + icons
        PanelCanvas.Children.Add(layer);

        var rng = new Random(0x2B17F3);
        int count = Math.Max(14, (int)(mainSpan / (19.0 * s)));
        for (int i = 0; i < count; i++)
        {
            double main = mainLo + rng.NextDouble() * mainSpan;
            double cross = crossLo + rng.NextDouble() * crossSpan;
            var p = LogicalPoint(main, cross);
            double sz = (0.6 + 1.7 * rng.NextDouble()) * s;
            byte br = (byte)(50 + 140 * rng.NextDouble());
            var star = new System.Windows.Shapes.Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = new SolidColorBrush(Color.FromArgb(br, 255, 255, 250)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(star, p.X - sz / 2.0);
            Canvas.SetTop(star, p.Y - sz / 2.0);
            layer.Children.Add(star);

            if (rng.NextDouble() > 0.62)   // a subset slowly twinkles
            {
                double full = br / 255.0;
                var tw = new DoubleAnimation(full * 0.28, full,
                    TimeSpan.FromSeconds(1.5 + 2.4 * rng.NextDouble()))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(2.2 * rng.NextDouble()),
                };
                Timeline.SetDesiredFrameRate(tw, App.AmbientFrameRate);
                star.BeginAnimation(OpacityProperty, tw);
            }
        }
    }

    // ---- Saturn debris belt ----------------------------------------------

    /// <summary>Scatters a field of tiny irregular asteroids/rubble across the
    /// Saturn dock — densest as a belt along the interior edge, but also strewn
    /// through the dock body (centre) and down toward the screen-edge side — so the
    /// dark slab reads as a band of space debris, like a slice of Saturn's rings.
    /// Each rock registers a live transform so the magnification wave shoves it
    /// outward as the bulge sweeps past. Rebuilt with the slab each Layout.</summary>
    private void DrawDebrisBelt(double baseEdge)
    {
        _debris.Clear();
        if (_slabMainLen <= 0)
            return;
        double s = Math.Max(0.5, _uiScale);
        double mainLo = _slabMain + GIcon * 0.15;
        double mainHi = _slabMain + _slabMainLen - GIcon * 0.15;
        double span = mainHi - mainLo;
        if (span <= 0)
            return;

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        var layer = new Canvas
        {
            IsHitTestVisible = false,
            Opacity = Math.Clamp(opacity, 0.0, 1.0),
            // A whisper of blur softens the rubble so it sits in the dock's haze
            // rather than reading as crisp UI shapes.
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0.7,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            },
        };
        Panel.SetZIndex(layer, -8);   // above slab + flame, below the icons
        PanelCanvas.Children.Add(layer);

        // Fixed seed → the belt is stable across rebuilds (no distracting reshuffle).
        var rng = new Random(0x9C34A1);
        double innerCross = _bodyCross + GIcon * 0.08;   // toward the screen edge
        double beltCross = baseEdge;                     // the dock's interior edge

        // Two populations: a dense rubble BELT straddling the interior edge, and a
        // sparser scattering of grains spread through the whole dock body so the
        // centre and the screen-edge side aren't bare.
        int beltCount = Math.Max(16, (int)(span / (6.0 * s)));
        int bodyCount = Math.Max(20, (int)(span / (6.0 * s)));

        for (int i = 0; i < beltCount; i++)
        {
            double main = mainLo + rng.NextDouble() * span;
            double g = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble()) / 3.0 - 0.5;
            double cross = beltCross + g * GIcon * 1.05 - GIcon * 0.05;
            double r = (1.4 + rng.NextDouble() * rng.NextDouble() * 5.9) * s;
            double alpha = 0.16 + rng.NextDouble() * 0.44;
            AddDebrisRock(layer, main, cross, r, alpha, rng);
        }
        for (int i = 0; i < bodyCount; i++)
        {
            double main = mainLo + rng.NextDouble() * span;
            // Uniform across the body, from the screen-edge side out to the belt.
            double cross = innerCross + rng.NextDouble() * Math.Max(1.0, beltCross - innerCross);
            double r = (1.2 + rng.NextDouble() * rng.NextDouble() * 4.1) * s;
            double alpha = 0.12 + rng.NextDouble() * 0.34;
            AddDebrisRock(layer, main, cross, r, alpha, rng);
        }

        // Almost-imperceptible drift along the belt so it feels alive, like rubble
        // slowly orbiting in space.
        var drift = new TranslateTransform();
        layer.RenderTransform = drift;
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var anim = new DoubleAnimation(-1.6 * s, 1.6 * s, new Duration(TimeSpan.FromSeconds(16)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        drift.BeginAnimation(prop, anim);
    }

    /// <summary>Builds one rock at logical (<paramref name="main"/>,
    /// <paramref name="cross"/>), adds it to <paramref name="layer"/>, and registers
    /// it for wave displacement. Smaller/fainter grains get less parallax so the
    /// field has depth.</summary>
    private void AddDebrisRock(Canvas layer, double main, double cross, double r, double alpha, Random rng)
    {
        var rock = MakeRock(LogicalPoint(main, cross), r, rng, alpha);
        var tr = new TranslateTransform();
        rock.RenderTransform = tr;
        layer.Children.Add(rock);
        _debris.Add(new DebrisRock
        {
            Main = main,
            // Bigger, bolder rocks ride the bulge further (foreground parallax).
            Parallax = 0.35 + Math.Clamp(r / (7.0 * Math.Max(0.5, _uiScale)), 0.0, 1.0) * 0.65,
            Tr = tr,
        });
    }

    /// <summary>Eases every debris rock toward the cross-axis push implied by the
    /// magnification wave at its main coordinate, so the rubble field bulges out
    /// under the cursor and relaxes behind it. Returns the largest pending delta so
    /// the wave loop keeps ticking until the rubble has settled.</summary>
    private double UpdateDebrisWave(double k)
    {
        if (_debris.Count == 0)
            return 0.0;
        bool active = !double.IsNaN(_waveCursorY);
        double denom = Math.Max(0.0001, HoverScale - 1.0);
        double maxPush = GIcon * 0.5;
        double maxDelta = 0.0;
        foreach (var d in _debris)
        {
            double target = 0.0;
            if (active)
            {
                double a = Math.Clamp((WaveScaleAt(_waveCursorY, d.Main) - 1.0) / denom, 0.0, 1.0);
                target = a * maxPush * d.Parallax;
            }
            double cur = d.Cur + (target - d.Cur) * k;
            d.Cur = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            var (dx, dy) = PopOffset(cur);
            d.Tr.X = dx;
            d.Tr.Y = dy;
        }
        return maxDelta;
    }

    /// <summary>Builds one irregular, faceted "rock": a jittered polygon shaded from
    /// a lit upper-left facet to a dark lower-right so it reads as a 3D pebble.</summary>
    private static System.Windows.Shapes.Path MakeRock(Point c, double r, Random rng, double alpha)
    {
        int verts = 6 + rng.Next(3);
        var fig = new PathFigure { IsClosed = true, IsFilled = true };
        double a0 = rng.NextDouble() * Math.PI * 2.0;
        for (int k = 0; k < verts; k++)
        {
            double ang = a0 + (Math.PI * 2.0) * k / verts + (rng.NextDouble() - 0.5) * 0.55;
            double rad = r * (0.58 + rng.NextDouble() * 0.42);
            var p = new Point(c.X + Math.Cos(ang) * rad, c.Y + Math.Sin(ang) * rad);
            if (k == 0)
                fig.StartPoint = p;
            else
                fig.Segments.Add(new LineSegment(p, true));
        }
        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        byte b = (byte)(70 + rng.Next(60));   // mid-grey base value
        byte Lit(int add) => (byte)Math.Min(255, b + add);
        byte Dark(double mul) => (byte)Math.Max(0, b * mul);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.2, 0.1),
            EndPoint = new Point(0.85, 0.95),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(Lit(58), Lit(54), Lit(50)), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(b, b, Lit(6)), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(Dark(0.32), Dark(0.32), Dark(0.4)), 1.0));
        brush.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = brush,
            Opacity = Math.Clamp(alpha, 0.0, 1.0),
            IsHitTestVisible = false,
        };
    }

}
