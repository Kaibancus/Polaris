using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DesktopPanel.Models;

namespace DesktopPanel.Views;

public partial class RadialIcon : UserControl
{
    private static readonly Duration Anim = new(TimeSpan.FromMilliseconds(150));
    private const double HoverScale = 1.7;
    private const double LabelWidth = 150;

    public AppEntry Entry { get; }

    /// <summary>Raised when the pointer enters / leaves so the parent ring can
    /// nudge the neighbouring icons aside.</summary>
    public event Action<RadialIcon>? HoverStarted;
    public event Action<RadialIcon>? HoverEnded;

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        InitializeComponent();

        // Centre the (zero-layout) name label below the icon.
        LabelChrome.Width = LabelWidth;
        Canvas.SetLeft(LabelChrome, (iconSize - LabelWidth) / 2.0);
        Canvas.SetTop(LabelChrome, iconSize + 8);

        MouseEnter += OnEnter;
        MouseLeave += OnLeave;
    }

    public BitmapSource? IconImage { get; }
    public double IconSize { get; }
    public Color GlowColor { get; }
    public Brush LabelBrush { get; }
    public string DisplayName { get; }

    private void OnEnter(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(HoverScale, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(HoverScale, Anim));
        Glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(24, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim));
        HoverStarted?.Invoke(this);
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, Anim));
        Glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(0, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        HoverEnded?.Invoke(this);
    }
}
