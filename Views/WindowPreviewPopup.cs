using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Services;

namespace Polaris.Views;

/// <summary>Side of the target a <see cref="WindowPreviewPopup"/> opens toward.</summary>
internal enum PreviewPlacement { Above, Below, Right, Left }

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
    private const double PreviewOpenDelayMs = 180;
    private const double PreviewCloseDelayMs = 300;

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

    /// <summary>Which side of the target the popup opens toward. Defaults to
    /// Above (the main radial dock). A side dock sets this so the preview opens
    /// toward the screen interior: Below for a Top dock, Right for a Left dock,
    /// Left for a Right dock.</summary>
    public PreviewPlacement Placement { get; set; } = PreviewPlacement.Above;

    /// <summary>Extra upward offset (px) applied only to an Above-opening preview, so the
    /// main dock can lift its preview a little higher above the icon. 0 for side docks.</summary>
    public double ExtraTopLift { get; set; }

    // Maps a window handle to its tile's thumbnail host so a background capture
    // can swap in the fresh image once it finishes.
    private readonly Dictionary<IntPtr, Border> _tileHosts = new();
    // Maps a window handle to its live DWM thumbnail (the preferred preview: works
    // for GPU-composited and minimized windows). Null entry = DWM registration
    // failed and the tile uses the PrintWindow still fallback instead.
    private readonly Dictionary<IntPtr, Polaris.Services.Gpu.DwmThumbnail?> _dwmThumbs = new();
    // HWND of the open popup's content (the DWM thumbnail destination); the tile
    // rects are mapped relative to this window's client area.
    private IntPtr _popupHwnd;

    // Maps a window handle to its whole tile element so the in-header close button
    // can remove the correct tile from the strip.
    private readonly Dictionary<IntPtr, UIElement> _tiles = new();

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
        // Switching to a DIFFERENT icon: the pointer is now on another icon (it
        // cannot also be over the old popup), so close the old preview RIGHT NOW
        // instead of letting its window — and its live DWM overlay — linger through
        // the close-delay + fade while the new one's open-delay runs. A live DWM
        // preview is vivid, so any residual frame is very noticeable.
        if (_previewPopup != null)
            Close();
        _openTimer.Stop();
        _openTimer.Start();
        PrewarmThumbnails();
    }

    /// <summary>During the open-delay dwell, enumerate the target's windows and capture their
    /// thumbnails into the shared cache off the UI thread, so when the popup actually opens its
    /// tiles render the FRESH frame immediately (TryGetCachedThumbnail hits) instead of popping
    /// blank/stale and only sharpening once the post-open background capture finishes. Cheap and
    /// idempotent — the capture loop already writes _thumbCache (thread-safe), and a hover-away
    /// before the dwell elapses just leaves a warmed cache for next time.</summary>
    private void PrewarmThumbnails()
    {
        Task.Run(() =>
        {
            if (!_pointerInside)
                return;
            try
            {
                var windows = _getWindows();
                if (windows.Count < _minWindows)
                    return;
                foreach (var w in windows)
                {
                    if (!_pointerInside)
                        return;   // pointer left — stop warming the cache
                    WindowPreviewService.CaptureThumbnail(w.Handle, PreviewThumbWidth);
                }
            }
            catch { /* best-effort warm-up */ }
        });
    }

    /// <summary>Call from the target's MouseLeave.</summary>
    public void OnPointerLeave()
    {
        _pointerInside = false;
        _openTimer.Stop();
        // Defer closing so the pointer can travel from the target onto the popup. Keep the DWM
        // thumbnails shown through the close delay so a plain hover-away lingers for the full
        // delay instead of looking like an instant close (the thumbnails are dropped when the
        // popup actually closes). An icon SWITCH still hides them at once via OnPointerEnter→Close.
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
            {
                // The pointer moved onto a target with nothing to preview (e.g. a pinned
                // app that isn't running). A prior OnPointerEnter for this target stopped
                // the close timer, so without this the previous icon's popup would stay
                // stuck open. Close it (unless the pointer is now over the popup itself).
                _target.Dispatcher.BeginInvoke(() =>
                {
                    if (token == _previewToken && !_pointerInPopup)
                        Close();
                });
                return;
            }

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
        const double TileWidth = PreviewThumbWidth + 14; // 220 content + tile padding + margin
        int columns = Math.Min(windows.Count, 6);
        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            MaxWidth = Math.Max(1, columns) * TileWidth,
        };
        foreach (var w in windows)
            strip.Children.Add(BuildTile(w));

        bool shellLight = SystemTheme.IsLight;
        var shell = new Border
        {
            Background = new SolidColorBrush(shellLight ? Color.FromArgb(0xF4, 0xF3, 0xF3, 0xF6)
                                                        : Color.FromArgb(0xF2, 0x1E, 0x1E, 0x22)),
            BorderBrush = new SolidColorBrush(shellLight ? Color.FromArgb(0x22, 0x00, 0x00, 0x00)
                                                         : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
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

        // Transparent "bridge" wrapping the card on the side facing the dock: it makes the
        // popup window's hit area reach back over the gap toward the icon, so the moment the
        // cursor leaves the icon toward the preview it is already "inside" the popup (no neighbour
        // icon switch / close can fire). Fixes diagonal travel collapsing the preview. The visible
        // card is unchanged; the placement callback subtracts the bridge so the card stays put.
        double bridge = ExtraTopLift + 40;   // gap(3) + lift + ~icon: corridor reaches the icon, with extra slack
        var outer = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,   // transparent but hit-testable
            Padding = Placement switch
            {
                PreviewPlacement.Below => new Thickness(0, bridge, 0, 0),
                PreviewPlacement.Right => new Thickness(bridge, 0, 0, 0),
                PreviewPlacement.Left => new Thickness(0, 0, bridge, 0),
                _ => new Thickness(0, 0, 0, bridge),   // Above: corridor hangs down toward the icon
            },
            Child = shell,
        };

        _previewPopup = new Popup
        {
            PlacementTarget = _target,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Open the popup toward the screen interior relative to the dock
                // edge: centred above/below for a horizontal dock, or centred to
                // the right/left for a vertical (Left/Right) side dock. A small
                // uniform gap on every side keeps the preview hugging the dock /
                // screen edge it springs from; verified the popup window is z-top
                // above the drop-shim, so a tight gap is not occluded.
                const double gap = 3;
                double x, y;
                PopupPrimaryAxis axis;
                switch (Placement)
                {
                    case PreviewPlacement.Below:
                        x = (targetSize.Width - popupSize.Width) / 2.0;
                        y = targetSize.Height + gap - bridge;   // bridge corridor reaches up to the icon
                        axis = PopupPrimaryAxis.Horizontal;
                        break;
                    case PreviewPlacement.Right:
                        x = targetSize.Width + gap - bridge;
                        y = (targetSize.Height - popupSize.Height) / 2.0;
                        axis = PopupPrimaryAxis.Vertical;
                        break;
                    case PreviewPlacement.Left:
                        x = -popupSize.Width - gap + bridge;
                        y = (targetSize.Height - popupSize.Height) / 2.0;
                        axis = PopupPrimaryAxis.Vertical;
                        break;
                    default: // Above
                        x = (targetSize.Width - popupSize.Width) / 2.0;
                        y = -popupSize.Height - gap - ExtraTopLift + bridge;   // card unchanged; corridor hangs to icon
                        axis = PopupPrimaryAxis.Horizontal;
                        break;
                }
                return new[] { new CustomPopupPlacement(new Point(x, y), axis) };
            },
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            Child = outer,
        };
        outer.MouseEnter += (_, _) => { _pointerInPopup = true; _closeTimer.Stop(); };
        outer.MouseLeave += (_, _) => { _pointerInPopup = false; _closeTimer.Stop(); _closeTimer.Start(); };

        _previewPopup.IsOpen = true;

        // Once the popup is realised it has its own HWND; register a live DWM
        // thumbnail per tile into it (the preferred preview — works for GPU and
        // minimized windows). Tiles whose registration fails keep the PrintWindow
        // still / icon fallback already placed in their host. Re-place the
        // thumbnails on every layout pass so they track the tiles as the popup
        // sizes/moves; positions are cheap idempotent DwmUpdateThumbnailProperties.
        shell.LayoutUpdated += OnPopupLayoutUpdated;
        shell.Dispatcher.BeginInvoke(new Action(() => SetupDwmThumbnails(shell)),
            DispatcherPriority.Loaded);
    }

    /// <summary>After the popup is realised, register a live DWM thumbnail for each
    /// tile into the popup's HWND. Called once on open; tiles that fail keep their
    /// PrintWindow/icon fallback.</summary>
    private void SetupDwmThumbnails(Visual shell)
    {
        if (PresentationSource.FromVisual(shell) is not HwndSource src)
            return;
        _popupHwnd = src.Handle;
        _hwndRoot = src.RootVisual as UIElement;
        foreach (var kv in _tileHosts)
        {
            IntPtr handle = kv.Key;
            if (_dwmThumbs.ContainsKey(handle))
                continue;
            var thumb = Polaris.Services.Gpu.DwmThumbnail.Create(_popupHwnd, handle);
            _dwmThumbs[handle] = thumb;   // may be null (registration failed → fallback shows)
            if (thumb is { IsValid: true })
            {
                // A live DWM preview covers the host. Clear the placeholder child
                // (PrintWindow still / "已最小化" / icon) and the dark background:
                // when the overlay is hidden on hover-away the host must reveal
                // NOTHING (so the old preview vanishes cleanly) rather than flashing
                // the "已最小化" fallback that sat underneath the overlay.
                kv.Value.Child = null;
                kv.Value.Background = Brushes.Transparent;
                // Size the tile to the FULL-window aspect (DwmQueryThumbnailSourceSize) and
                // render the full window (see DwmThumbnail.SetDestination). Both derive from
                // the same full-window measurement, so the thumbnail fills the tile EXACTLY
                // with no one-sided black band — unlike client-area-only, whose true client
                // aspect can't be measured for WebView2/UWP-shell apps (new Outlook reports a
                // 217x29 stub), causing a mismatch band.
                var (sw, sh) = thumb.SourceSize;
                if (sw > 0 && sh > 0)
                    kv.Value.Height = Math.Clamp(PreviewThumbWidth * (double)sh / sw,
                        PreviewThumbWidth * 0.32, PreviewThumbWidth * 1.10);
            }
        }
        PlaceDwmThumbnails();
    }

    private void OnPopupLayoutUpdated(object? sender, EventArgs e) => PlaceDwmThumbnails();

    /// <summary>Maps each tile's thumbnail host to its on-screen rect (physical
    /// pixels, relative to the popup window's client area) and pushes it to the DWM.
    /// No-op for tiles without a valid thumbnail (they show the WPF fallback).</summary>
    private void PlaceDwmThumbnails()
    {
        if (_popupHwnd == IntPtr.Zero || _dwmThumbs.Count == 0)
            return;
        foreach (var kv in _dwmThumbs)
        {
            if (kv.Value is not { IsValid: true } thumb)
                continue;
            if (!_tileHosts.TryGetValue(kv.Key, out var host) || !host.IsVisible)
            {
                thumb.Hide();
                continue;
            }
            try
            {
                // Host rect in its own DIP space → popup-window DIP → physical px.
                // Floor the top-left and ceil the bottom-right so the opaque thumbnail
                // ALWAYS fully covers the host: Math.Round on all four could leave the dest
                // rect a sub-pixel short, exposing the dark shell as a thin black line along
                // a (white) window's edge.
                var topLeft = host.TranslatePoint(new Point(0, 0), _hwndRoot ?? host);
                var dpi = VisualTreeHelper.GetDpi(host);
                int l = (int)Math.Floor(topLeft.X * dpi.DpiScaleX);
                int t = (int)Math.Floor(topLeft.Y * dpi.DpiScaleY);
                int r = (int)Math.Ceiling((topLeft.X + host.ActualWidth) * dpi.DpiScaleX);
                int b = (int)Math.Ceiling((topLeft.Y + host.ActualHeight) * dpi.DpiScaleY);
                if (r > l && b > t)
                    thumb.SetDestination(l, t, r, b);
            }
            catch { thumb.Hide(); }
        }
    }

    /// <summary>Root visual of the popup HWND, used to translate tile coordinates
    /// into the destination window's client space for the DWM thumbnail rect.</summary>
    private UIElement? _hwndRoot;

    /// <summary>Releases every DWM thumbnail registration.</summary>
    private void DisposeDwmThumbnails()
    {
        foreach (var t in _dwmThumbs.Values)
            t?.Dispose();
        _dwmThumbs.Clear();
        _popupHwnd = IntPtr.Zero;
        _hwndRoot = null;
    }

    /// <summary>Immediately hides the live DWM overlays without unregistering, so a
    /// hover-away / icon switch makes the old preview vanish at once instead of
    /// lingering for the open-delay (the overlays don't participate in the WPF
    /// fade). They are fully released later in ClosePopupUi / the next ShowPreview.</summary>
    private void HideDwmThumbnails()
    {
        foreach (var t in _dwmThumbs.Values)
            t?.Hide();
    }

    private UIElement BuildTile(WindowPreview w)
    {
        bool light = Polaris.Services.SystemTheme.IsLight;
        var fgBrush = new SolidColorBrush(light ? Color.FromArgb(0xF0, 0x1B, 0x1B, 0x1F)
                                                : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        var subBrush = new SolidColorBrush(light ? Color.FromArgb(0x99, 0x00, 0x00, 0x00)
                                                 : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var fallbackBg = new SolidColorBrush(light ? Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEE)
                                                   : Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
        IntPtr handle = w.Handle;
        double fs = Polaris.Services.FontScale.Current;

        var inner = new StackPanel { Orientation = Orientation.Vertical, Width = PreviewThumbWidth };

        // --- Windows-taskbar-style header: app icon + title (left), close X (right),
        //     ABOVE the thumbnail. Because it doesn't overlap the thumbnail it is NOT
        //     covered by the DWM thumbnail overlay, so the close button is a plain WPF
        //     element again (no separate topmost popup needed). ---
        const double HeaderH = 24;
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, HeaderH, 0),   // leave room for the right-aligned close button
        };
        var hdrIcon = WindowPreviewService.GetWindowAppIcon(handle);
        if (hdrIcon != null)
            titleRow.Children.Add(new Image
            {
                Source = hdrIcon, Width = 16, Height = 16,
                Margin = new Thickness(2, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        titleRow.Children.Add(new TextBlock
        {
            Text = w.Title,
            Foreground = fgBrush,
            FontSize = 12 * fs,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = PreviewThumbWidth - HeaderH - (hdrIcon != null ? 24 : 4),
        });

        var closeGlyph = new TextBlock
        {
            Text = "✕", FontSize = 11, Foreground = fgBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var closeBtn = new Border
        {
            Width = HeaderH, Height = HeaderH,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = closeGlyph,
        };
        closeBtn.MouseEnter += (_, _) => { closeBtn.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)); closeGlyph.Foreground = Brushes.White; };
        closeBtn.MouseLeave += (_, _) => { closeBtn.Background = Brushes.Transparent; closeGlyph.Foreground = fgBrush; };
        closeBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseTileWindow(handle); };

        var header = new Grid { Height = HeaderH, Margin = new Thickness(2, 0, 2, 3) };
        header.Children.Add(titleRow);
        header.Children.Add(closeBtn);
        inner.Children.Add(header);

        // --- Live thumbnail host (the DWM overlay is composited over THIS, below the header) ---
        var thumbHost = new Border
        {
            Width = PreviewThumbWidth,
            Height = PreviewThumbWidth * 0.62,
            CornerRadius = new CornerRadius(4),
            Background = fallbackBg,
            ClipToBounds = true,
        };
        _tileHosts[handle] = thumbHost;
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
        else if (WindowPreviewService.IsWindowMinimized(handle))
        {
            // A genuinely minimized window can't be PrintWindow-captured (a live DWM
            // thumbnail, if it registers, replaces this placeholder anyway).
            thumbHost.Child = new TextBlock
            {
                Text = "已最小化", Foreground = subBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12 * fs,
            };
        }
        else
        {
            var ic = WindowPreviewService.GetWindowAppIcon(handle);
            if (ic != null)
                thumbHost.Child = new Image
                {
                    Source = ic, Width = 56, Height = 56, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            else
                thumbHost.Child = new TextBlock
                {
                    Text = "无预览", Foreground = subBrush,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12 * fs,
                };
        }
        inner.Children.Add(thumbHost);

        var tile = new Border
        {
            Margin = new Thickness(3),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = inner,
        };
        _tiles[handle] = tile;
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(
            light ? Color.FromArgb(0x14, 0x00, 0x00, 0x00) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowPreviewService.Activate(handle);
            Close();
            _onActivated?.Invoke();
        };
        return tile;
    }

    /// <summary>Closes the given window and removes its tile, closing the whole popup
    /// once the last tile is gone.</summary>
    private void CloseTileWindow(IntPtr handle)
    {
        WindowPreviewService.CloseWindow(handle);
        _tileHosts.Remove(handle);
        if (_dwmThumbs.TryGetValue(handle, out var dt))
        {
            dt?.Dispose();
            _dwmThumbs.Remove(handle);
        }
        if (_tiles.TryGetValue(handle, out var tileEl))
        {
            _tiles.Remove(handle);
            if (tileEl is FrameworkElement fe && fe.Parent is Panel parent)
            {
                parent.Children.Remove(tileEl);
                if (parent.Children.Count == 0)
                    Close();
            }
        }
    }

    /// <summary>Swaps a freshly-captured thumbnail into its tile (called on the
    /// UI thread once a background capture finishes).</summary>
    private void UpdateTileThumbnail(IntPtr handle, BitmapSource thumb)
    {
        // A live DWM thumbnail (if it registered) is the preferred preview and sits
        // as an overlay above this host, so don't bother swapping in the slower
        // PrintWindow still — it would only show if the DWM overlay later failed.
        if (_dwmThumbs.TryGetValue(handle, out var dt) && dt is { IsValid: true })
            return;
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

    /// <summary>True while the thumbnail popup is currently shown.</summary>
    public bool IsOpen => _previewPopup != null;

    /// <summary>True while the pointer is over the floating preview itself (tracked from the
    /// popup shell's MouseEnter/Leave). Lets the dock ignore the geometric icon-hover that the
    /// preview overlaps in the Saturn ring layout, so resting on the preview never switches it
    /// to — or closes it in favour of — the icon underneath the cursor.</summary>
    public bool PointerInPopup => _pointerInPopup;

    /// <summary>Closes and disposes the preview popup if it is open.</summary>
    public void Close()
    {
        _previewToken++;            // invalidate any in-flight capture
        _openTimer.Stop();
        _closeTimer.Stop();
        _pointerInPopup = false;
        ClosePopupUi();
    }

    /// <summary>
    /// Dismisses the popup while letting its fade-out animation actually play
    /// (unlike <see cref="Close"/>, which blanks the popup instantly), then
    /// invokes <paramref name="onClosed"/> once the fade has visibly finished.
    /// Used so a right-click context menu only appears after the thumbnail
    /// preview has animated away rather than overlapping it.
    /// </summary>
    public void CloseAnimated(Action onClosed)
    {
        _previewToken++;            // invalidate any in-flight capture
        _openTimer.Stop();
        _closeTimer.Stop();
        _pointerInPopup = false;

        var popup = _previewPopup;
        if (popup == null)
        {
            onClosed();
            return;
        }
        // Detach so a concurrent hover can't reuse this fading popup, but keep
        // its Child intact so PopupAnimation.Fade has something to fade out.
        _previewPopup = null;
        // DWM thumbnails are opaque overlays that do NOT participate in the WPF
        // fade-out, so release them immediately rather than leaving a live preview
        // hanging over a fading popup.
        _tiles.Clear();
        DisposeDwmThumbnails();
        _tileHosts.Clear();

        popup.IsOpen = false;       // begins the fade-out

        // PopupAnimation.Fade runs for ~200 ms; wait that out (plus a little
        // margin) before tearing the popup down and signalling completion.
        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(230) };
        done.Tick += (_, _) =>
        {
            done.Stop();
            popup.Child = null;
            onClosed();
        };
        done.Start();
    }

    /// <summary>Tears down the popup window only, leaving _previewToken and the
    /// timers untouched (used by ShowPreview when replacing a prior popup while
    /// the same capture batch keeps streaming in fresh thumbnails).</summary>
    private void ClosePopupUi()
    {
        _tiles.Clear();
        DisposeDwmThumbnails();
        _tileHosts.Clear();
        if (_previewPopup != null)
        {
            _previewPopup.IsOpen = false;
            _previewPopup.Child = null;
            _previewPopup = null;
        }
    }
}
