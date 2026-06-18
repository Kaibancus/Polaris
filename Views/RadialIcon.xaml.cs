using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class RadialIcon : UserControl
{
    private static readonly Duration Anim = new(TimeSpan.FromMilliseconds(110));
    private const double HoverScale = DockTuning.HoverScale;
    private const double LabelWidth = 90;

    // The icon plate's decorative brushes / shadow are identical for every icon
    // of a theme and are never mutated or animated (animations target element
    // Opacity / transforms, not these objects). Building them once as frozen,
    // shared resources skips per-icon allocation + change-notification plumbing
    // and lets WPF reuse a single GPU resource across all icons.
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static readonly Brush GlassPlateBg =
        Frozen(new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SaturnPlateBg =
        Frozen(new SolidColorBrush(Color.FromArgb(0x03, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GlassRimBrush = Frozen(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF), 0.0),
            new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.45),
            new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.6),
            new GradientStop(Color.FromArgb(0x70, 0xD8, 0xEC, 0xFF), 1.0),
        },
    });
    private static readonly Brush GlassSheenBrush = Frozen(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0.85, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.0),
            new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.22),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
        },
    });
    private static readonly System.Windows.Media.Effects.DropShadowEffect GlassPlateShadow =
        Frozen(new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.42,
            Color = Color.FromRgb(0x05, 0x0A, 0x14),
        });

    public AppEntry Entry { get; }

    /// <summary>Raised when the pointer enters / leaves so the parent ring can
    /// nudge the neighbouring icons aside.</summary>
    public event Action<RadialIcon>? HoverStarted;
    public event Action<RadialIcon>? HoverEnded;

    /// <summary>Raised after the user clicks one of the window previews, so the
    /// host panel can dismiss itself.</summary>
    public event Action? WindowActivated;

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush, bool dropletHover, bool sideDockStyle = false)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        _dropletHover = dropletHover;
        _sideDockStyle = sideDockStyle;
        InitializeComponent();

        // Pick the hover style per theme: the water-droplet lens is exclusive to
        // the liquid-glass theme; every other theme (Saturn) gets the soft blue
        // bloom. Keep the unused layer collapsed so it never paints.
        if (_dropletHover)
        {
            BlueGlow.Visibility = Visibility.Collapsed;
            // Liquid-glass icon plate: a faint "crystal" chip at 95% transparency
            // (alpha ≈ 5% = 0x0D) so the icon floats on a barely-there sliver of
            // glass, lifted by a bright luminous rim, a diagonal sheen and a soft
            // drop shadow.
            IconPlate.Background = GlassPlateBg;
            IconPlate.BorderThickness = new Thickness(1.2);
            IconPlate.BorderBrush = GlassRimBrush;
            IconPlate.Effect = GlassPlateShadow;
            // Bake the plate (icon image + drop shadow) into a GPU bitmap cache
            // rendered at 2x so the 1.7x hover zoom upscales a cached texture
            // instead of re-rasterising the drop shadow on the CPU every frame.
            // RenderAtScale 2.0 keeps the icon crisp at full zoom + DPI headroom.
            IconPlate.CacheMode = new System.Windows.Media.BitmapCache(2.0)
            {
                SnapsToDevicePixels = true,
            };
            // Diagonal glossy sheen across the top-left — the crystal highlight.
            PlateSheen.Visibility = Visibility.Visible;
            PlateSheen.Background = GlassSheenBrush;
        }
        else
        {
            HoverGlow.Visibility = Visibility.Collapsed;
            // Faint bloom from the accent colour (alpha kept low so it reads as a
            // gentle halo rather than a hard wash). Drawn behind the icon so only
            // the background plate glows — the icon image stays crisp. When the
            // supplied glow colour is already translucent (alpha < 0xFF) its own
            // alpha is honoured, so themes can request a softer bloom.
            BlueGlow.Visibility = Visibility.Visible;
            byte glowAlpha = glowColor.A < 0xFF ? glowColor.A : (byte)0x5A;
            BlueGlow.Background = Frozen(new SolidColorBrush(
                Color.FromArgb(glowAlpha, glowColor.R, glowColor.G, glowColor.B)));

            // Saturn tray: reduce the icon plate to an almost-fully-transparent
            // sliver (alpha ≈ 1%) so the icons appear to rest directly on the
            // black disc with virtually no visible tray.
            IconPlate.Background = SaturnPlateBg;
        }

        // Left dock: no tray. Strip the glass plate (background / rim / shadow /
        // sheen) so the icon floats free, and size + place the breathing green
        // "running" dot at the icon's left edge.
        if (_sideDockStyle)
        {
            IconPlate.Background = Brushes.Transparent;
            IconPlate.BorderThickness = new Thickness(0);
            IconPlate.BorderBrush = null;
            IconPlate.Effect = null;
            IconPlate.CacheMode = null;
            PlateSheen.Visibility = Visibility.Collapsed;
            // No water-droplet lens on hover either — that glassy overlay reads
            // as a tray. The macOS-style magnify wave is the only hover feedback.
            HoverGlow.Visibility = Visibility.Collapsed;

            double dot = Math.Max(2.6, iconSize * 0.07);
            double glow = dot * 2.3;
            RunDot.Width = RunDot.Height = dot;
            RunDotGlow.Width = RunDotGlow.Height = glow;
            PositionRunDot();
        }

        // Centre the (zero-layout) name label below the icon.
        LabelChrome.Width = LabelWidth;
        Label.FontSize = 11.5 * Polaris.Services.FontScale.Current;
        Canvas.SetLeft(LabelChrome, (iconSize - LabelWidth) / 2.0);
        Canvas.SetTop(LabelChrome, iconSize + 8);

        // Multi-window hover-thumbnail popup. Show a live thumbnail even with a
        // single open window so the user can preview/peek any running app.
        int minWindows = 1;
        _preview = new WindowPreviewPopup(
            this,
            () => WindowPreviewService.GetWindowsForEntry(Entry.Path, Entry.Arguments),
            minWindows,
            onActivated: () => WindowActivated?.Invoke());

        MouseEnter += OnEnter;
        MouseLeave += OnLeave;
        Unloaded += (_, _) => _preview.Close();
    }

    public BitmapSource? IconImage { get; }
    public double IconSize { get; }
    public Color GlowColor { get; }
    public Brush LabelBrush { get; }
    public string DisplayName { get; }

    private readonly bool _dropletHover;
    private readonly bool _sideDockStyle;
    private bool _isRunning;
    private DockSide _dockEdge = DockSide.Left;

    /// <summary>Tells a left-dock icon which screen edge its dock is anchored
    /// to, so the running dot hugs the outer (screen-edge) side and the
    /// window-preview popup opens toward the screen interior.</summary>
    public void ApplyDockEdge(DockSide side)
    {
        _dockEdge = side;
        if (_sideDockStyle)
            PositionRunDot();
        if (_preview != null)
            _preview.Placement = side switch
            {
                DockSide.Top => PreviewPlacement.Below,
                DockSide.Left => PreviewPlacement.Right,
                DockSide.Right => PreviewPlacement.Left,
                _ => PreviewPlacement.Above,   // Bottom dock → above the icon
            };
    }

    /// <summary>Positions the breathing green "running" dot against the dock's
    /// outer (screen-edge) side, centred along the other axis.</summary>
    private void PositionRunDot()
    {
        double dot = RunDot.Width;
        double glow = RunDotGlow.Width;
        double near = dot * 0.05;
        double far = IconSize - dot * 0.05;
        double mid = IconSize / 2.0;
        (double cx, double cy) = _dockEdge switch
        {
            DockSide.Right => (far, mid),
            DockSide.Top => (mid, near),
            DockSide.Bottom => (mid, far),
            _ => (near, mid),
        };
        Canvas.SetLeft(RunDot, cx - dot / 2.0);
        Canvas.SetTop(RunDot, cy - dot / 2.0);
        Canvas.SetLeft(RunDotGlow, cx - glow / 2.0);
        Canvas.SetTop(RunDotGlow, cy - glow / 2.0);
    }

    /// <summary>When true the icon does NOT scale itself on hover; instead the
    /// host dock drives a coordinated magnification (a macOS-dock-style wave)
    /// via <see cref="Magnify"/>. The hover glow / label / preview still fire.</summary>
    public bool ExternalMagnify { get; set; }

    private static readonly Duration MagnifyAnim = new(TimeSpan.FromMilliseconds(150));

    /// <summary>Applies an external magnification: scales the icon about its
    /// centre and offsets it toward the screen interior (the macOS-dock "pop
    /// out"). The offset axis depends on the dock edge — horizontal for a
    /// Left/Right dock, vertical for a Top/Bottom dock. Animated with an
    /// ease-out so neighbouring icons settle smoothly as the pointer moves.</summary>
    public void Magnify(double scale, double offsetX, double offsetY = 0)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, MagnifyAnim) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, MagnifyAnim) { EasingFunction = ease });
        MagnifyTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(offsetX, MagnifyAnim) { EasingFunction = ease });
        MagnifyTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offsetY, MagnifyAnim) { EasingFunction = ease });
    }

    /// <summary>Sets the magnification DIRECTLY (no animation) for continuous,
    /// cursor-tracked wave updates. Clears any running <see cref="Magnify"/>
    /// animation first so the assigned values take effect immediately — the
    /// smoothness comes from the host driving this every frame.</summary>
    public void SetMagnify(double scale, double offsetX, double offsetY = 0)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MagnifyTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        MagnifyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        MagnifyTranslate.X = offsetX;
        MagnifyTranslate.Y = offsetY;
    }

    /// <summary>The live hop translation while a launch bounce plays (the animated
    /// MagnifyTranslate). Lets the dock read the current jump height so the Saturn
    /// flame can rise in sync with the icon.</summary>
    public TranslateTransform HopTransform => MagnifyTranslate;

    /// <summary>Plays a one-off macOS-dock-style launch bounce: the icon hops by
    /// (<paramref name="liftX"/>, <paramref name="liftY"/>) and falls back with a
    /// landing bounce, swelling slightly at the apex. Invokes
    /// <paramref name="onCompleted"/> when it finishes. The caller should settle
    /// any magnification wave first so the frame loop doesn't fight it.</summary>
    public void PlayLaunchBounce(double liftX, double liftY, Action? onCompleted = null)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MagnifyTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        MagnifyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        Scale.ScaleX = 1.0;
        Scale.ScaleY = 1.0;
        MagnifyTranslate.X = 0.0;
        MagnifyTranslate.Y = 0.0;

        var tx = DockBounce.BuildTranslate(liftX);
        var ty = DockBounce.BuildTranslate(liftY);
        var sx = DockBounce.BuildScale();
        var sy = DockBounce.BuildScale();
        if (onCompleted != null)
            sy.Completed += (_, _) => onCompleted();
        MagnifyTranslate.BeginAnimation(TranslateTransform.XProperty, tx);
        MagnifyTranslate.BeginAnimation(TranslateTransform.YProperty, ty);
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    /// <summary>
    /// When true the icon shows a flowing blue light around its square border,
    /// indicating the target program is currently running.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
                return;
            _isRunning = value;
            UpdateRunningVisual();
        }
    }

    private void UpdateRunningVisual()
    {
        if (_sideDockStyle)
        {
            UpdateRunningDot();
            return;
        }
        if (_isRunning)
        {
            // In the liquid-glass theme the whole panel is a fullscreen per-pixel-
            // alpha layered window, so EVERY glow tick re-uploads the entire screen.
            // Throttle the slow running glow there to GlassLoopFrameRate (the value
            // designed for exactly this) instead of the higher ambient rate — the
            // sweep/breathe are slow enough that 30 fps is indistinguishable, and it
            // halves the per-tick full-screen upload. Saturn (not a layered window)
            // keeps the ambient rate.
            int loopFps = _dropletHover ? App.GlassLoopFrameRate : App.AmbientFrameRate;
            RunningBorder.Visibility = Visibility.Visible;
            RunningGlowBorder.Visibility = Visibility.Visible;
            // Sweep the bright spot continuously around the border. A linear
            // 0..360 rotation on the brush is GPU-composited, so it stays smooth.
            // Tick at the oversampled rate so the slow rotation does not beat
            // against the panel's 59.94 Hz present (which reads as judder).
            var sweep = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(4.2)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(sweep, loopFps);
            RunningSweep.BeginAnimation(RotateTransform.AngleProperty, sweep);
            // Gentle breathing glow on the static blurred border (Opacity is a
            // cheap, composited property — no per-frame bitmap-effect recompute).
            var pulse = new DoubleAnimation(0.35, 0.8, new Duration(TimeSpan.FromSeconds(2.2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(pulse, loopFps);
            RunningGlowBorder.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            RunningSweep.BeginAnimation(RotateTransform.AngleProperty, null);
            RunningGlowBorder.BeginAnimation(OpacityProperty, null);
            RunningBorder.Visibility = Visibility.Collapsed;
            RunningGlowBorder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Left-dock running indicator: a small green dot at the icon's left
    /// edge that gently breathes (opacity pulse), with a soft halo behind it.</summary>
    private void UpdateRunningDot()
    {
        if (_isRunning)
        {
            int loopFps = App.GlassLoopFrameRate;
            RunDot.Visibility = Visibility.Visible;
            RunDotGlow.Visibility = Visibility.Visible;
            var pulse = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromSeconds(2.0)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(pulse, loopFps);
            RunDot.BeginAnimation(OpacityProperty, pulse);
            var glowPulse = new DoubleAnimation(0.15, 0.42, new Duration(TimeSpan.FromSeconds(2.0)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(glowPulse, loopFps);
            RunDotGlow.BeginAnimation(OpacityProperty, glowPulse);
        }
        else
        {
            RunDot.BeginAnimation(OpacityProperty, null);
            RunDotGlow.BeginAnimation(OpacityProperty, null);
            RunDot.Visibility = Visibility.Collapsed;
            RunDotGlow.Visibility = Visibility.Collapsed;
        }
    }

    private bool _attentionFlashing;

    /// <summary>Shows / hides the new-message attention dot (lower-left corner),
    /// mirroring the taskbar's flash. <paramref name="flashing"/> means the app is
    /// actively requesting attention (its taskbar button would be flashing); the
    /// dot then pulses. <paramref name="count"/> is accepted for source
    /// compatibility but no longer rendered as a number.</summary>
    public void SetAttention(bool flashing, int count)
    {
        if (_attentionFlashing == flashing)
            return;
        _attentionFlashing = flashing;
        UpdateAttentionVisual();
    }

    private void UpdateAttentionVisual()
    {
        if (!_attentionFlashing)
        {
            AttentionBadge.BeginAnimation(OpacityProperty, null);
            AttentionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AttentionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AttentionBadge.Opacity = 0;
            AttentionBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // A compact red dot, nudged toward the icon's lower-left so it hugs the
        // corner rather than overhanging.
        AttentionCount.Text = "";
        AttentionCount.Visibility = Visibility.Collapsed;
        double d = Math.Max(5.0, Math.Min(10.0, IconSize * 0.12));
        AttentionBadge.Height = d;
        AttentionBadge.CornerRadius = new CornerRadius(d / 2.0);
        AttentionBadge.MinWidth = d;
        AttentionBadge.Padding = new Thickness(0);
        AttentionShift.X = -4;
        AttentionShift.Y = 4;

        AttentionBadge.Visibility = Visibility.Visible;
        AttentionBadge.Opacity = 1;

        if (_attentionFlashing)
        {
            // Pulse to draw the eye, like the taskbar's flash.
            int fps = App.AmbientFrameRate;
            var pulse = new DoubleAnimation(1.0, 1.18, new Duration(TimeSpan.FromSeconds(0.7)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(pulse, fps);
            AttentionScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            AttentionScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        else
        {
            AttentionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AttentionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AttentionScale.ScaleX = AttentionScale.ScaleY = 1;
        }
    }

    private void OnEnter(object sender, MouseEventArgs e)
    {
        FpsProfiler.Push("HoverZoom");
        // When the host dock drives a coordinated wave, it owns the scale; the
        // icon only lights its hover layer / label here.
        if (!ExternalMagnify)
        {
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(HoverScale, Anim));
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(HoverScale, Anim));
        }
        // Fade in the per-theme hover layer (Opacity is composited; the lens /
        // glow blurs are rasterised once via BitmapCache, so no per-frame
        // effect recompute). The left dock has no hover tray/lens at all.
        if (!_sideDockStyle)
        {
            if (_dropletHover)
                HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, Anim));
            else
                // Glass theme: the magnify glow sits a touch dimmer than full.
                BlueGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, Anim));
        }
        // Glass theme shows a floating label (hosted unclipped by the host) so
        // the bottom row's name isn't cut by the scroll clip; suppress the
        // built-in label there to avoid a duplicate.
        if (!_dropletHover)
            LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim));
        HoverStarted?.Invoke(this);

        // Schedule the multi-window preview after a short hover dwell.
        _preview.OnPointerEnter();
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        FpsProfiler.Pop("HoverZoom");
        if (!ExternalMagnify)
        {
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, Anim));
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, Anim));
        }
        if (!_sideDockStyle)
        {
            if (_dropletHover)
                HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
            else
                BlueGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        }
        if (!_dropletHover)
            LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        HoverEnded?.Invoke(this);

        _preview.OnPointerLeave();
    }

    // ---- Multi-window preview popup --------------------------------------

    private readonly WindowPreviewPopup _preview;

    /// <summary>Closes the preview popup if it is open (called by the host panel
    /// when it hides).</summary>
    public void ClosePreview() => _preview.Close();

    /// <summary>True while this icon's hover thumbnail preview is shown.</summary>
    public bool IsPreviewOpen => _preview.IsOpen;

    /// <summary>If the hover preview is open, fades it out and invokes
    /// <paramref name="onClosed"/> once the animation finishes, returning true.
    /// Returns false (without calling back) when no preview is showing.</summary>
    public bool TryFadePreview(Action onClosed)
    {
        if (!_preview.IsOpen)
            return false;
        _preview.CloseAnimated(onClosed);
        return true;
    }
}

