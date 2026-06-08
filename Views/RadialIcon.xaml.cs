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

    // Window-preview popup tuning.
    internal const int PreviewThumbWidth = 220;   // px capture width per window
    private const double PreviewOpenDelayMs = 420;
    private const double PreviewCloseDelayMs = 220;

    public AppEntry Entry { get; }

    /// <summary>Raised when the pointer enters / leaves so the parent ring can
    /// nudge the neighbouring icons aside.</summary>
    public event Action<RadialIcon>? HoverStarted;
    public event Action<RadialIcon>? HoverEnded;

    /// <summary>Raised after the user clicks one of the window previews, so the
    /// host panel can dismiss itself.</summary>
    public event Action? WindowActivated;

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        InitializeComponent();

        // The hover glow is a solid colour block, blurred and cached; tint it
        // with this icon's accent/glow colour.
        HoverGlow.Background = new SolidColorBrush(GlowColor);

        // Centre the (zero-layout) name label below the icon.
        LabelChrome.Width = LabelWidth;
        Canvas.SetLeft(LabelChrome, (iconSize - LabelWidth) / 2.0);
        Canvas.SetTop(LabelChrome, iconSize + 8);

        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewOpenDelayMs) };
        _openTimer.Tick += OnOpenTimerTick;
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewCloseDelayMs) };
        _closeTimer.Tick += OnCloseTimerTick;

        MouseEnter += OnEnter;
        MouseLeave += OnLeave;
        Unloaded += (_, _) => ClosePreview();
    }

    public BitmapSource? IconImage { get; }
    public double IconSize { get; }
    public Color GlowColor { get; }
    public Brush LabelBrush { get; }
    public string DisplayName { get; }

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
            RunningBorder.Visibility = Visibility.Visible;
            RunningGlowBorder.Visibility = Visibility.Visible;
            // Sweep the bright spot continuously around the border. A linear
            // 0..360 rotation on the brush is GPU-composited, so it stays smooth.
            var sweep = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2.8)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            RunningSweep.BeginAnimation(RotateTransform.AngleProperty, sweep);
            // Gentle breathing glow on the static blurred border (Opacity is a
            // cheap, composited property — no per-frame bitmap-effect recompute).
            var pulse = new DoubleAnimation(0.35, 0.8, new Duration(TimeSpan.FromSeconds(1.6)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
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
        // Fade in the pre-blurred glow (Opacity is composited; no effect recompute).
        HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim));
        HoverStarted?.Invoke(this);

        // Schedule the multi-window preview after a short hover dwell.
        _pointerInside = true;
        _closeTimer.Stop();
        _openTimer.Stop();
        _openTimer.Start();
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, Anim));
        HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        HoverEnded?.Invoke(this);

        _pointerInside = false;
        _openTimer.Stop();
        // Defer closing so the pointer can travel from the icon onto the popup.
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    // ---- Multi-window preview popup --------------------------------------

    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private Popup? _previewPopup;
    private bool _pointerInside;
    private bool _pointerInPopup;
    private int _previewToken;

    // Maps a window handle to its tile's thumbnail host so a background capture
    // can swap in the fresh image once it finishes.
    private readonly Dictionary<IntPtr, Border> _tileHosts = new();

    /// <summary>True when the app entry points at Windows File Explorer
    /// (explorer.exe), which we preview even with a single open window.</summary>
    private static bool IsFileExplorer(string path)
    {
        try
        {
            return string.Equals(Path.GetFileName(path), "explorer.exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OnOpenTimerTick(object? sender, EventArgs e)
    {
        _openTimer.Stop();
        if (!_pointerInside)
            return;
        string path = Entry.Path;
        if (string.IsNullOrWhiteSpace(path))
            return;

        int token = ++_previewToken;
        Task.Run(() =>
        {
            var windows = WindowPreviewService.GetWindows(path);
            // Multi-window apps are worth a preview; for File Explorer a single
            // window is also worth previewing (the user often has just one
            // Explorer window — possibly with several tabs — and still expects a
            // hover peek), so allow a 1-window preview there.
            int minWindows = IsFileExplorer(path) ? 1 : 2;
            if (windows.Count < minWindows)
                return;

            // Seed each tile with any thumbnail we already cached from a previous
            // hover so the popup can pop up INSTANTLY (after the open delay)
            // instead of waiting for every slow PrintWindow capture to finish.
            foreach (var w in windows)
                w.Thumbnail = WindowPreviewService.TryGetCachedThumbnail(w.Handle);

            Dispatcher.BeginInvoke(() =>
            {
                if (token != _previewToken || !_pointerInside)
                    return;
                ShowPreview(windows);
            });

            // Capture fresh frames in the background and swap each tile's image
            // in as it completes, so a stale/empty tile updates without blocking
            // the popup's appearance.
            foreach (var w in windows)
            {
                var fresh = WindowPreviewService.CaptureThumbnail(w.Handle, PreviewThumbWidth);
                if (fresh == null)
                    continue;
                IntPtr handle = w.Handle;
                Dispatcher.BeginInvoke(() =>
                {
                    if (token != _previewToken)
                        return;
                    UpdateTileThumbnail(handle, fresh);
                });
            }
        });
    }

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        if (!_pointerInside && !_pointerInPopup)
            ClosePreview();
    }

    private void ShowPreview(List<WindowPreview> windows)
    {
        // Close any existing popup UI WITHOUT bumping _previewToken — the
        // background capture loop that opened this preview is still running and
        // matches the current token; bumping here would reject its tile updates.
        ClosePopupUi();
        _tileHosts.Clear();

        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var w in windows)
            strip.Children.Add(BuildTile(w));

        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.55,
                Color = Colors.Black,
            },
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxWidth = 920,
                Content = strip,
            },
        };

        _previewPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Centre the popup horizontally over the icon and sit it just
                // above the icon's top edge.
                double x = (targetSize.Width - popupSize.Width) / 2.0;
                double y = -popupSize.Height - 6;
                return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal) };
            },
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            Child = shell,
        };
        shell.MouseEnter += (_, _) => { _pointerInPopup = true; _closeTimer.Stop(); };
        shell.MouseLeave += (_, _) => { _pointerInPopup = false; _closeTimer.Stop(); _closeTimer.Start(); };

        _previewPopup.IsOpen = true;
    }

    private UIElement BuildTile(WindowPreview w)
    {
        var inner = new StackPanel { Orientation = Orientation.Vertical, Width = PreviewThumbWidth };

        var thumbHost = new Border
        {
            Width = PreviewThumbWidth,
            Height = PreviewThumbWidth * 0.62,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
            ClipToBounds = true,
        };
        _tileHosts[w.Handle] = thumbHost;
        if (w.Thumbnail != null)
        {
            thumbHost.Child = new Image
            {
                Source = w.Thumbnail,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            thumbHost.Child = new TextBlock
            {
                Text = "最小化",
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
        }
        inner.Children.Add(thumbHost);

        inner.Children.Add(new TextBlock
        {
            Text = w.Title,
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(2, 6, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = PreviewThumbWidth,
        });

        var tile = new Border
        {
            Margin = new Thickness(4, 2, 4, 2),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = inner,
        };
        IntPtr handle = w.Handle;
        tile.MouseEnter += (_, _) =>
            tile.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowPreviewService.Activate(handle);
            ClosePreview();
            WindowActivated?.Invoke();
        };
        return tile;
    }

    /// <summary>Swaps a freshly-captured thumbnail into its tile (called on the
    /// UI thread once a background capture finishes).</summary>
    private void UpdateTileThumbnail(IntPtr handle, BitmapSource thumb)
    {
        if (!_tileHosts.TryGetValue(handle, out var host))
            return;
        host.Child = new Image
        {
            Source = thumb,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Closes and disposes the preview popup if it is open.</summary>
    public void ClosePreview()
    {
        _previewToken++;            // invalidate any in-flight capture
        _openTimer.Stop();
        _closeTimer.Stop();
        _pointerInPopup = false;
        ClosePopupUi();
    }

    /// <summary>Tears down the popup window only, leaving _previewToken and the
    /// timers untouched (used by ShowPreview when replacing a prior popup while
    /// the same capture batch keeps streaming in fresh thumbnails).</summary>
    private void ClosePopupUi()
    {
        _tileHosts.Clear();
        if (_previewPopup != null)
        {
            _previewPopup.IsOpen = false;
            _previewPopup.Child = null;
            _previewPopup = null;
        }
    }
}
