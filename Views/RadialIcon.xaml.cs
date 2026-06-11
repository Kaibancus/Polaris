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
    private const double HoverScale = 1.7;
    private const double LabelWidth = 150;

    public AppEntry Entry { get; }

    /// <summary>Raised when the pointer enters / leaves so the parent ring can
    /// nudge the neighbouring icons aside.</summary>
    public event Action<RadialIcon>? HoverStarted;
    public event Action<RadialIcon>? HoverEnded;

    /// <summary>Raised after the user clicks one of the window previews, so the
    /// host panel can dismiss itself.</summary>
    public event Action? WindowActivated;

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush, bool dropletHover, bool leftDockStyle = false)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        _dropletHover = dropletHover;
        _leftDockStyle = leftDockStyle;
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
            IconPlate.Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
            IconPlate.BorderThickness = new Thickness(1.2);
            IconPlate.BorderBrush = new LinearGradientBrush
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
            };
            IconPlate.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.42,
                Color = Color.FromRgb(0x05, 0x0A, 0x14),
            };
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
            PlateSheen.Background = new LinearGradientBrush
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
            };
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
            BlueGlow.Background = new SolidColorBrush(
                Color.FromArgb(glowAlpha, glowColor.R, glowColor.G, glowColor.B));

            // Saturn tray: reduce the icon plate to an almost-fully-transparent
            // sliver (alpha ≈ 1%) so the icons appear to rest directly on the
            // black disc with virtually no visible tray.
            IconPlate.Background = new SolidColorBrush(Color.FromArgb(0x03, 0xFF, 0xFF, 0xFF));
        }

        // Left dock: no tray. Strip the glass plate (background / rim / shadow /
        // sheen) so the icon floats free, and size + place the breathing green
        // "running" dot at the icon's left edge.
        if (_leftDockStyle)
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

            double dot = Math.Max(3.0, iconSize * 0.075);
            double glow = dot * 2.3;
            RunDot.Width = RunDot.Height = dot;
            RunDotGlow.Width = RunDotGlow.Height = glow;
            double cy = iconSize / 2.0;
            double cx = dot * 0.05;   // hug the very left edge
            Canvas.SetLeft(RunDot, cx - dot / 2.0);
            Canvas.SetTop(RunDot, cy - dot / 2.0);
            Canvas.SetLeft(RunDotGlow, cx - glow / 2.0);
            Canvas.SetTop(RunDotGlow, cy - glow / 2.0);
        }

        // Centre the (zero-layout) name label below the icon.
        LabelChrome.Width = LabelWidth;
        Canvas.SetLeft(LabelChrome, (iconSize - LabelWidth) / 2.0);
        Canvas.SetTop(LabelChrome, iconSize + 8);

        // Multi-window hover-thumbnail popup. File Explorer is worth previewing
        // even with a single window (the user often has just one Explorer window
        // with several tabs); every other app needs at least two.
        int minWindows = IsFileExplorer(entry.Path, entry.Arguments) ? 1 : 2;
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
    private readonly bool _leftDockStyle;
    private bool _isRunning;

    /// <summary>When true the icon does NOT scale itself on hover; instead the
    /// host dock drives a coordinated magnification (a macOS-dock-style wave)
    /// via <see cref="Magnify"/>. The hover glow / label / preview still fire.</summary>
    public bool ExternalMagnify { get; set; }

    private static readonly Duration MagnifyAnim = new(TimeSpan.FromMilliseconds(150));

    /// <summary>Applies an external magnification: scales the icon about its
    /// centre and offsets it horizontally (the macOS-dock "pop out" toward the
    /// screen). Animated with an ease-out so neighbouring icons settle smoothly
    /// as the pointer moves along the dock.</summary>
    public void Magnify(double scale, double offsetX)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, MagnifyAnim) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, MagnifyAnim) { EasingFunction = ease });
        MagnifyTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(offsetX, MagnifyAnim) { EasingFunction = ease });
    }

    /// <summary>Sets the magnification DIRECTLY (no animation) for continuous,
    /// cursor-tracked wave updates. Clears any running <see cref="Magnify"/>
    /// animation first so the assigned values take effect immediately — the
    /// smoothness comes from the host driving this every frame.</summary>
    public void SetMagnify(double scale, double offsetX)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MagnifyTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        MagnifyTranslate.X = offsetX;
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
        if (_leftDockStyle)
        {
            UpdateRunningDot();
            return;
        }
        if (_isRunning)
        {
            int loopFps = App.AmbientFrameRate;
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
            int loopFps = App.AmbientFrameRate;
            RunDot.Visibility = Visibility.Visible;
            RunDotGlow.Visibility = Visibility.Visible;
            var pulse = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromSeconds(2.0)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(pulse, loopFps);
            RunDot.BeginAnimation(OpacityProperty, pulse);
            var glowPulse = new DoubleAnimation(0.25, 0.65, new Duration(TimeSpan.FromSeconds(2.0)))
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
        if (!_leftDockStyle)
        {
            if (_dropletHover)
                HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, Anim));
            else
                BlueGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, Anim));
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
        if (!_leftDockStyle)
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

    /// <summary>True when the app entry points at the genuine Windows File
    /// Explorer (explorer.exe with NO shell:AppsFolder launcher argument), which
    /// we preview even with a single open window. Packaged apps such as the new
    /// Teams / Outlook are also launched via explorer.exe but with a
    /// shell:AppsFolder argument — those are NOT File Explorer.</summary>
    private static bool IsFileExplorer(string path, string? arguments)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), "explorer.exe",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return WindowPreviewService.TryGetLauncherAumid(path, arguments) == null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Closes the preview popup if it is open (called by the host panel
    /// when it hides).</summary>
    public void ClosePreview() => _preview.Close();
}

