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

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush, bool dropletHover)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        _dropletHover = dropletHover;
        InitializeComponent();

        // Pick the hover style per theme: the water-droplet lens is exclusive to
        // the liquid-glass theme; every other theme (Saturn) gets the soft blue
        // bloom. Keep the unused layer collapsed so it never paints.
        if (_dropletHover)
        {
            BlueGlow.Visibility = Visibility.Collapsed;
            // Liquid-glass icon plate: a near-invisible "crystal" chip — the body
            // is almost fully transparent (far clearer than the dock backdrop)
            // so the icon appears to float on a sliver of glass, lifted by a
            // bright luminous rim, a diagonal sheen and a soft drop shadow.
            IconPlate.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x07, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x01, 0xD8, 0xE6, 0xFF), 1.0),
                },
            };
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
            // Faint blue bloom from the accent colour (alpha kept low so it reads
            // as a gentle halo rather than a hard wash). Drawn behind the icon so
            // only the background plate glows — the icon image stays crisp.
            BlueGlow.Visibility = Visibility.Visible;
            BlueGlow.Background = new SolidColorBrush(
                Color.FromArgb(0x5A, glowColor.R, glowColor.G, glowColor.B));
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
    private bool _isRunning;

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

    private void OnEnter(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(HoverScale, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(HoverScale, Anim));
        // Fade in the per-theme hover layer (Opacity is composited; the lens /
        // glow blurs are rasterised once via BitmapCache, so no per-frame
        // effect recompute).
        if (_dropletHover)
            HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, Anim));
        else
            BlueGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim));
        HoverStarted?.Invoke(this);

        // Schedule the multi-window preview after a short hover dwell.
        _preview.OnPointerEnter();
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, Anim));
        if (_dropletHover)
            HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        else
            BlueGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
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

