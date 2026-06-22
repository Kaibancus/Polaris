using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class RadialWindow
{
    public void ShowPanel()
    {
        IsPinned = false;
        ShowFaded(pinned: false);
    }

    /// <summary>
    /// Shows the panel in pinned mode (stays open until explicitly closed),
    /// so the user can drag desktop shortcuts onto the ring.
    /// </summary>
    public void ShowPinned()
    {
        ShowFaded(pinned: true);
    }

    /// <summary>True while the overlay is faded in and interactive.</summary>
    public bool IsShown => _shown;

    /// <summary>
    /// Realises the transparent overlay once at startup and keeps it shown but
    /// fully transparent. Re-showing an <c>AllowsTransparency</c> window with
    /// Hide()/Show() recreates its layered surface and flashes a frame every
    /// time; staying shown and only fading the opacity avoids that flicker.
    /// While Opacity is 0 the layered window is fully transparent, so mouse
    /// clicks pass straight through to the desktop beneath.
    /// </summary>
    public void Realize()
    {
        if (_realized) return;
        _realized = true;
        _suppressRebuild = true;
        SizeToActiveContent();
        RootGrid.Opacity = 0;
        Show();                         // one-time; the window then stays shown
        _suppressRebuild = false;
        Rebuild();
        _runningTimer.Stop();           // nothing to poll while hidden
        // Collapse the content while dismissed so WPF stops rendering (and thus
        // ticking) every continuous animation — orbits, running sweeps, twinkle
        // — that would otherwise burn GPU/CPU behind the invisible (Opacity 0)
        // layered window. ShowFaded makes it Visible again before each Rebuild.
        PanelCanvas.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Fades the already-realised overlay in. No Hide()/Show() is involved, so
    /// there is no layered-surface flash.
    /// </summary>
    private void ShowFaded(bool pinned)
    {
        Realize();                      // safety: ensure the window exists
        _shown = true;
        if (pinned)
            IsPinned = true;

        PanelCanvas.Visibility = Visibility.Visible;   // resume rendering/animation
        SizeToActiveContent();
        _suppressRebuild = false;
        Rebuild();                      // pick up any config changes
        // Watch the live frame rate while shown so the quality governor can step
        // weak machines down a tier if they can't hold 60 (no-op on capable HW).
        Polaris.Services.RenderProfile.BeginWatch();
        // Reset the content opacity to 0 before fading in so a stale frame from
        // the previous summon can never flash.
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 0;
        // The liquid-glass dock no longer frosts the desktop behind it — the
        // panel sits on the clear wallpaper. (Desktop-blur capture removed.)
        if (_theme.ShowGlassPanel)
        {
            AnimateGlassRise();         // slide the dock up from the screen bottom
            // The glass dock runs the orbit light + running-app sweeps
            // continuously while shown; mark it the base profiling scene until
            // the panel hides (mirrors SaturnIdle).
            Polaris.Services.FpsProfiler.Push("GlassIdle");
            _profilingGlass = true;
        }
        else
        {
            PanelCanvas.RenderTransform = Transform.Identity;  // clear any glass rise
            AnimateRingsExpand();       // grow the rings out from the centre
            // The Saturn theme orbits + spins continuously while shown; mark it
            // the base profiling scene until the panel hides.
            Polaris.Services.FpsProfiler.Push("SaturnIdle");
            _profilingSaturn = true;
        }
        Topmost = true;
        Activate();
        _runningTimer.Start();
        UpdateGlassClock();
        ShowNotchIfSaturn();
        _clockTimer.Start();
        WarmPreviewCache();             // prime previews for this show, then poll
        _previewWarmTimer.Start();
        _ = _weather.RefreshAsync();   // fetch weather promptly on show

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Liquid-glass summon: slide the whole dock up from below the
    /// bottom screen edge into its docked position, with a soft ease-out.</summary>
    private void AnimateGlassRise()
    {
        // Start fully below the dock's resting bottom so it rises into view.
        double rise = GlassDockTotalHeight + GlassDockBottomMargin + EffectiveIconSize;
        // Anchor at the bottom-centre so the squash/stretch grows out of the
        // bottom edge — reads as a fluid blob settling rather than a rigid slide.
        var tt = new TranslateTransform(0, rise);
        var sc = new ScaleTransform(1.0, 0.94);   // vertically compressed, springs to full height
        var grp = new TransformGroup();
        grp.Children.Add(sc);
        grp.Children.Add(tt);
        PanelCanvas.RenderTransformOrigin = new Point(0.5, 1.0);
        PanelCanvas.RenderTransform = grp;

        // Gentle overshoot so the dock eases past its resting line and settles.
        var slideEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
        var slide = new DoubleAnimation(rise, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = slideEase,
        };
        // Vertical stretch springs slightly beyond full and relaxes back.
        var stretchEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
        var stretch = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = stretchEase,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slide, App.AnimationFrameRate);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(stretch, App.AnimationFrameRate);
        // The squash/stretch scales the whole panel; without a cache WPF would
        // re-rasterise every blurred glass-chrome layer each frame (heavy ->
        // dropped frames). Cache the entire panel to one GPU texture for the
        // duration so the scale just stretches that texture, then drop the cache
        // when the motion settles so live content (clock, hover) renders crisply.
        var riseCache = new System.Windows.Media.BitmapCache
        {
            SnapsToDevicePixels = false,
            // 1.0 on capable hardware (unchanged); lower on weak / high-DPI panels
            // so the squash/stretch rasterises fewer pixels per frame.
            RenderAtScale = Polaris.Services.RenderProfile.CacheRenderScale,
        };
        PanelCanvas.CacheMode = riseCache;
        Polaris.Services.FpsProfiler.Push("GlassRise");
        slide.Completed += (_, _) =>
        {
            PanelCanvas.CacheMode = null;
            Polaris.Services.FpsProfiler.Pop("GlassRise");
        };
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
    }

    /// <summary>Animates the two ring layers growing out from the planet, the
    /// inner band leading and the outer band following, for a "summon" feel.</summary>
    private void AnimateRingsExpand()
    {
        ExpandLayer(_innerBandLayer, 0.0);
        ExpandLayer(_innerOrbitLayer, 0.0);
        ExpandLayer(_outerBandLayer, 0.11);
        ExpandLayer(_outerOrbitLayer, 0.11);
        // The expand burst runs ~110ms delay + 280ms grow; pop a bit after.
        Polaris.Services.FpsProfiler.Push("RingsExpand");
        var done = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        done.Tick += (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            Polaris.Services.FpsProfiler.Pop("RingsExpand");
        };
        done.Start();
    }

    private void ExpandLayer(Canvas? layer, double delaySeconds)
    {
        if (layer == null)
            return;

        layer.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform(0.55, 0.55);
        layer.RenderTransform = sc;

        var begin = TimeSpan.FromSeconds(delaySeconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());

        var fadeIn = new DoubleAnimation(0, 0.78, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        layer.BeginAnimation(OpacityProperty, fadeIn);
    }

    public void HidePanel()
    {
        HidePanel(null);
    }

    /// <summary>Hides the panel, optionally invoking <paramref name="onFaded"/>
    /// once the fade-out animation has fully completed (used so the settings
    /// window only appears after the dock has finished disappearing).</summary>
    public void HidePanel(Action? onFaded)
    {
        _shown = false;
        IsPinned = false;
        CancelDrag();
        _runningTimer.Stop();
        _clockTimer.Stop();
        _previewWarmTimer.Stop();       // no thumbnail polling needed while hidden
        // Free the (large) window-thumbnail bitmaps that can be re-captured next
        // show; keeps only minimized windows' last-good frames. Cuts idle memory.
        Polaris.Services.WindowPreviewService.TrimThumbCacheForHide();
        _notch?.HideNotch();
        ResetMagnify();
        // Stop watching the live frame rate so an idle app pays nothing.
        Polaris.Services.RenderProfile.EndWatch();

        // Let the host retract the left-edge dock together with the main dock
        // (e.g. when an icon launch hides the panel).
        PanelDismissed?.Invoke();

        // End the Saturn continuous-orbit profiling scene if it was active.
        if (_profilingSaturn)
        {
            _profilingSaturn = false;
            Polaris.Services.FpsProfiler.Pop("SaturnIdle");
        }
        // End the glass idle profiling scene if it was active.
        if (_profilingGlass)
        {
            _profilingGlass = false;
            Polaris.Services.FpsProfiler.Pop("GlassIdle");
        }

        // Reset the glass grid scroll so the next summon starts at the top row.
        StopGlassScrollAnimation();
        _glassScroll = 0;

        // Dismiss any open window-preview popups so they don't linger on screen.
        foreach (var ic in _iconElements)
            ic?.ClosePreview();

        // Fade the content out, then collapse it so the fully-transparent
        // layered window passes clicks straight through to the desktop (a
        // layered window's transparent pixels are not hit-testable). Capture the
        // current (animated) opacity BEFORE replacing the animation so we start
        // the fade from what's on screen.
        double from = RootGrid.Opacity;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) =>
        {
            // Only collapse if still hidden (a fast re-show may have intervened).
            if (!_shown)
            {
                PanelCanvas.Visibility = Visibility.Collapsed;
                // Release the whole dock visual tree (and its BitmapCache bitmaps,
                // the bulk of the dock's render memory) now that it's hidden. The
                // next ShowFaded calls Rebuild() which recreates everything, so
                // this is free apart from that rebuild we'd do on show anyway. Big
                // idle (hidden-dock) memory saving. Deferred to a Background pass
                // so it never blocks the final collapsed frame from presenting.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_shown)
                    {
                        ClearVisualTree();
                        // Shrink the layered window to 1x1 while hidden so WPF frees
                        // the large software-composited surface (AllowsTransparency
                        // keeps a per-pixel-alpha buffer ~ window-area×4 bytes plus
                        // render scratch — the bulk of the idle footprint). The next
                        // ShowFaded's SizeToActiveContent restores the real size
                        // before Rebuild, so the user never sees the 1x1 window.
                        Width = 1;
                        Height = 1;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            // Defer the callback to a LATER dispatcher pass (Background) instead
            // of running it inline on the fade's final frame. The callback may
            // build a heavy window (the settings UI), and doing that synchronously
            // here blocks the UI thread before the collapsed/transparent frame is
            // presented — which reads as the dock flashing/stuttering as it
            // disappears. Letting the collapse render first keeps the fade smooth.
            if (onFaded != null)
                Dispatcher.BeginInvoke(onFaded, System.Windows.Threading.DispatcherPriority.Background);
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Hides the panel only if it is not pinned (used on key-release).</summary>
    public void HideIfNotPinned()
    {
        if (!IsPinned)
            HidePanel();
    }

}
