using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Services;

namespace Polaris.Views;

/// <summary>
/// Reusable hover-thumbnail popup. Attach it to any <see cref="FrameworkElement"/>
/// (a ring icon or a taskbar tile); when the pointer dwells over the target it
/// shows a floating panel of live window thumbnails for the associated program,
/// with click-to-activate. The element provides the windows via a delegate so
/// the same popup serves both pinned apps and taskbar-only apps.
/// </summary>
internal sealed class WindowPreviewPopup
{
    internal const int PreviewThumbWidth = 220;   // px capture width per window
    private const double PreviewOpenDelayMs = 420;
    private const double PreviewCloseDelayMs = 220;

    private readonly FrameworkElement _target;
    private readonly Func<List<WindowPreview>> _getWindows;
    private readonly int _minWindows;
    private readonly Action? _onActivated;

    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private Popup? _previewPopup;
    private bool _pointerInside;
    private bool _pointerInPopup;
    private int _previewToken;

    // Maps a window handle to its tile's thumbnail host so a background capture
    // can swap in the fresh image once it finishes.
    private readonly Dictionary<IntPtr, Border> _tileHosts = new();

    /// <param name="target">Element to anchor the popup to and centre it over.</param>
    /// <param name="getWindows">Returns the previewable windows (runs off the UI thread).</param>
    /// <param name="minWindows">Only show the popup when at least this many windows exist.</param>
    /// <param name="onActivated">Invoked after the user clicks a thumbnail.</param>
    public WindowPreviewPopup(FrameworkElement target, Func<List<WindowPreview>> getWindows,
        int minWindows, Action? onActivated)
    {
        _target = target;
        _getWindows = getWindows;
        _minWindows = minWindows;
        _onActivated = onActivated;

        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewOpenDelayMs) };
        _openTimer.Tick += OnOpenTimerTick;
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewCloseDelayMs) };
        _closeTimer.Tick += OnCloseTimerTick;
    }

    /// <summary>Call from the target's MouseEnter.</summary>
    public void OnPointerEnter()
    {
        _pointerInside = true;
        _closeTimer.Stop();
        _openTimer.Stop();
        _openTimer.Start();
    }

    /// <summary>Call from the target's MouseLeave.</summary>
    public void OnPointerLeave()
    {
        _pointerInside = false;
        _openTimer.Stop();
        // Defer closing so the pointer can travel from the target onto the popup.
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private void OnOpenTimerTick(object? sender, EventArgs e)
    {
        _openTimer.Stop();
        if (!_pointerInside)
            return;

        int token = ++_previewToken;
        Task.Run(() =>
        {
            var windows = _getWindows();
            if (windows.Count < _minWindows)
                return;

            // Seed each tile with any thumbnail we already cached from a previous
            // hover so the popup can pop up INSTANTLY (after the open delay)
            // instead of waiting for every slow PrintWindow capture to finish.
            foreach (var w in windows)
                w.Thumbnail = WindowPreviewService.TryGetCachedThumbnail(w.Handle);

            _target.Dispatcher.BeginInvoke(() =>
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
                _target.Dispatcher.BeginInvoke(() =>
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
            Close();
    }

    private void ShowPreview(List<WindowPreview> windows)
    {
        // Close any existing popup UI WITHOUT bumping _previewToken — the
        // background capture loop that opened this preview is still running and
        // matches the current token; bumping here would reject its tile updates.
        ClosePopupUi();
        _tileHosts.Clear();

        // Lay tiles out in a wrapping grid so ALL windows are visible (a single
        // horizontal row would overflow the popup width and, with the scrollbar
        // hidden, silently clip the extra tiles). Cap the row width at up to 6
        // columns; further windows wrap onto additional rows.
        const double TileWidth = PreviewThumbWidth + 24; // 220 content + padding + margin
        int columns = Math.Min(windows.Count, 6);
        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            MaxWidth = Math.Max(1, columns) * TileWidth,
        };
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
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 720,
                Content = strip,
            },
        };

        _previewPopup = new Popup
        {
            PlacementTarget = _target,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Centre the popup horizontally over the target and sit it just
                // above the target's top edge.
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

        // A close ("×") button in the thumbnail's top-right corner, shown only
        // while the tile is hovered, that closes the underlying window.
        var closeBtn = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 4, 4, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xC0, 0x3A, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var thumbArea = new Grid();
        thumbArea.Children.Add(thumbHost);
        thumbArea.Children.Add(closeBtn);
        inner.Children.Add(thumbArea);

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
        {
            tile.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            closeBtn.Visibility = Visibility.Visible;
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Background = Brushes.Transparent;
            closeBtn.Visibility = Visibility.Collapsed;
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowPreviewService.Activate(handle);
            Close();
            _onActivated?.Invoke();
        };
        closeBtn.MouseEnter += (_, _) =>
            closeBtn.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0x4A, 0x3C));
        closeBtn.MouseLeave += (_, _) =>
            closeBtn.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xC0, 0x3A, 0x2E));
        closeBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;   // don't fall through to the tile's activate handler
            WindowPreviewService.CloseWindow(handle);
            _tileHosts.Remove(handle);
            // Remove this tile from the strip; close the whole popup once empty.
            if (tile.Parent is Panel parent)
            {
                parent.Children.Remove(tile);
                if (parent.Children.Count == 0)
                    Close();
            }
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
    public void Close()
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
