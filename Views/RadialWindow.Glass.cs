using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Polaris.Services;

namespace Polaris.Views;

// Non-Saturn theme rendering: the translucent "liquid glass" panel and the
// minimal centre button used by plain grid themes.
public partial class RadialWindow
{
    /// <summary>A minimal centre control used by non-Saturn themes: a small
    /// circular settings button placed above the grid.</summary>
    private void DrawSimpleCenterButton()
    {
        double s = Math.Max(40, EffectiveIconSize * 0.82);
        var btn = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(s / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x2A, 0x6C, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.55,
                Color = Color.FromRgb(0x0A, 0x10, 0x1C),
            },
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = s * 0.54,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };

        double gridTop = _center.Y - 2 * (EffectiveIconSize * 2.1);
        Canvas.SetLeft(btn, _center.X - s / 2);
        Canvas.SetTop(btn, gridTop - s - EffectiveIconSize * 0.6);
        Panel.SetZIndex(btn, 2000);
        PanelCanvas.Children.Add(btn);
    }

    /// <summary>
    /// Draws the "液态玻璃" backdrop: a translucent, frosted rounded-rectangle
    /// panel sized to enclose the 6-column icon grid (6×3 by default, growing to
    /// 6×5). Its overall opacity follows the user's panel-transparency setting.
    /// </summary>
    private void DrawGlassPanel()
    {
        double icon = EffectiveIconSize;
        double cellW = icon * 2.15;
        double cellH = icon * 2.35;
        int rows = LiquidGlassTheme.RowsFor(_config.Apps.Count);
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double gridH = (rows - 1) * cellH;

        double padX = icon * 1.15;
        double padY = icon * 1.15;
        // Extra band reserved at the very top of the slab for the clock / gear.
        // The icon grid stays centred on GlassDockCenter; only the slab grows
        // upward, so the date-time sits well clear of the first icon row even
        // when a top-row icon zooms up on hover.
        double topInset = icon * 0.55;
        double w = gridW + icon + padX * 2;
        double h = gridH + icon + padY * 2;
        double left = _center.X - w / 2.0;
        double gridTop = GlassDockCenter.Y - h / 2.0;   // panel top around the icon grid
        double top = gridTop - topInset;                // actual slab top (includes clock band)

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        const double radius = 28;

        // One continuous glass panel: the slab extends past the icon grid by the
        // taskbar strip height so the dock and the running-app row are a single
        // uninterrupted piece of glass. A thin etched seam line is engraved
        // across the junction (see DrawGlassSeam) to visually divide the two
        // sections without breaking the glass.
        double dockBottom = gridTop + h;
        double totalH = h + topInset + GlassTaskbarStripHeight;
        DrawGlassSlab(left, top, w, totalH, radius, opacity);
        DrawGlassSeam(left, dockBottom, w, opacity);

        // Settings gear in the panel's top-right corner.
        double gs = Math.Max(40, icon * 0.72);
        var gear = new Border
        {
            Width = gs,
            Height = gs,
            CornerRadius = new CornerRadius(gs / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.5,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = gs * 0.56,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        gear.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };
        Canvas.SetLeft(gear, left + w - gs - 14);
        Canvas.SetTop(gear, top + 14);
        Panel.SetZIndex(gear, 2000);
        PanelCanvas.Children.Add(gear);

        // System date / time in the panel's top-left (the "settings row"),
        // year-month-day and time all on one line.
        var clockTime = new TextBlock
        {
            FontSize = Math.Max(17, icon * 0.34),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.55,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
        };
        Canvas.SetLeft(clockTime, left + 18);
        Canvas.SetTop(clockTime, top + 14);
        Panel.SetZIndex(clockTime, 2000);
        PanelCanvas.Children.Add(clockTime);
        _glassClockTime = clockTime;
        _glassClockDate = null;
        UpdateGlassClock();
    }

    /// <summary>Draws an Apple-style "Liquid Glass" slab (translucent body, edge
    /// rim, specular dome, glare and base shade). When <paramref name="track"/> is
    /// supplied, every created element is added to it so the caller can remove the
    /// slab later.</summary>
    internal void DrawGlassSlab(double left, double top, double w, double h, double radius, double opacity,
        System.Collections.Generic.List<FrameworkElement>? track = null)
    {
        // Body: clear, lightly cool-tinted glass sheet with a soft floating shadow.
        var glass = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x1C, 0xEA, 0xF2, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x2A, 0xCE, 0xDC, 0xF2), 1.0),
                },
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 48,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.42,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
            IsHitTestVisible = false,
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        Canvas.SetLeft(glass, left);
        Canvas.SetTop(glass, top);
        Panel.SetZIndex(glass, -12);
        PanelCanvas.Children.Add(glass);
        track?.Add(glass);

        // Base shade: soft dark pool at the bottom for volume.
        var baseShade = new Border
        {
            Width = w,
            Height = h * 0.4,
            CornerRadius = new CornerRadius(0, 0, radius, radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x0A, 0x12, 0x20), 0.0),
                    new GradientStop(Color.FromArgb(0x30, 0x0A, 0x12, 0x20), 1.0),
                },
            },
        };
        Canvas.SetLeft(baseShade, left);
        Canvas.SetTop(baseShade, top + h * 0.6);
        Panel.SetZIndex(baseShade, -11);
        PanelCanvas.Children.Add(baseShade);
        track?.Add(baseShade);

        // Top specular dome: blurred bright highlight along the upper edge.
        var topCap = new Border
        {
            Width = w * 0.86,
            Height = h * 0.55,
            CornerRadius = new CornerRadius(w * 0.43),
            Opacity = opacity,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(22, h * 0.07) },
            CacheMode = new System.Windows.Media.BitmapCache(),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.12),
                Center = new Point(0.5, 0.12),
                RadiusX = 0.62,
                RadiusY = 0.95,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(topCap, left + w * 0.07);
        Canvas.SetTop(topCap, top + 2);
        Panel.SetZIndex(topCap, -9);
        PanelCanvas.Children.Add(topCap);
        track?.Add(topCap);

        // Diagonal glare streak: a faint tilted bright bar clipped to the panel.
        var glareClip = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            ClipToBounds = true,
            CacheMode = new System.Windows.Media.BitmapCache(),
            Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius),
        };
        var glareCanvas = new Canvas { Width = w, Height = h };
        var glare = new System.Windows.Shapes.Rectangle
        {
            Width = w * 1.7,
            Height = h * 0.16,
            RadiusX = h * 0.08,
            RadiusY = h * 0.08,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(18, h * 0.05) },
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-20),
        };
        Canvas.SetLeft(glare, -w * 0.35);
        Canvas.SetTop(glare, h * 0.06);
        glareCanvas.Children.Add(glare);
        glareClip.Child = glareCanvas;
        Canvas.SetLeft(glareClip, left);
        Canvas.SetTop(glareClip, top);
        Panel.SetZIndex(glareClip, -8);
        PanelCanvas.Children.Add(glareClip);
        track?.Add(glareClip);

        // Luminous edge rim: bright hairline catching light around the slab,
        // brightest top-left. On top of every other layer so the edge stays crisp.
        var rim = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(1.1),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), 0.4),
                    new GradientStop(Color.FromArgb(0x30, 0xC8, 0xDA, 0xF5), 0.62),
                    new GradientStop(Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(rim, left);
        Canvas.SetTop(rim, top);
        Panel.SetZIndex(rim, -6);
        PanelCanvas.Children.Add(rim);
        track?.Add(rim);

        // Inner refraction glow: soft bright ring just inside the rim.
        var innerGlow = new Border
        {
            Width = w - 2,
            Height = h - 2,
            CornerRadius = new CornerRadius(radius - 1),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(2.2),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
            CacheMode = new System.Windows.Media.BitmapCache(),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x2A, 0xDC, 0xEA, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(innerGlow, left + 1);
        Canvas.SetTop(innerGlow, top + 1);
        Panel.SetZIndex(innerGlow, -7);
        PanelCanvas.Children.Add(innerGlow);
        track?.Add(innerGlow);
    }

    /// <summary>
    /// Engraves a thin horizontal seam across the single glass panel at
    /// <paramref name="seamY"/>, dividing the dock from the taskbar strip without
    /// breaking the glass (a dark recess hairline plus a bright highlight below).
    /// </summary>
    private void DrawGlassSeam(double left, double seamY, double w, double opacity)
    {
        double inset = 16;
        double lineW = Math.Max(1, w - inset * 2);
        double lineLeft = left + inset;

        // Dark recess line.
        var groove = new System.Windows.Shapes.Rectangle
        {
            Width = lineW,
            Height = 1.5,
            Opacity = opacity,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x06, 0x0B, 0x16), 0.0),
                    new GradientStop(Color.FromArgb(0x99, 0x06, 0x0B, 0x16), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0x06, 0x0B, 0x16), 1.0),
                },
            },
        };
        Canvas.SetLeft(groove, lineLeft);
        Canvas.SetTop(groove, seamY - 0.75);
        Panel.SetZIndex(groove, -5);
        PanelCanvas.Children.Add(groove);

        // Bright highlight catching the lip just below the recess.
        var highlight = new System.Windows.Shapes.Rectangle
        {
            Width = lineW,
            Height = 1.5,
            Opacity = opacity,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(highlight, lineLeft);
        Canvas.SetTop(highlight, seamY + 0.75);
        Panel.SetZIndex(highlight, -5);
        PanelCanvas.Children.Add(highlight);
    }

    // ---- Desktop background blur (liquid-glass summon) -------------------

    private System.Windows.Controls.Image? _desktopBlur;
    // The frosted backdrop captured at summon time, kept so a mid-panel Rebuild
    // (e.g. after a drag-reorder) can re-add the layer without re-capturing the
    // screen — a fresh capture would grab the now-visible panel itself.
    private BitmapSource? _desktopBlurSource;

    /// <summary>
    /// Captures the desktop currently behind the (still-transparent, Opacity 0)
    /// overlay, blurs it, and shows it as the bottom-most layer so the
    /// liquid-glass panel appears to frost the wallpaper behind it. A radial
    /// opacity mask feathers the blur out toward the window edges so there is no
    /// hard rectangular seam against the un-blurred desktop. Called once per
    /// summon (a fresh capture each time reflects the current desktop).
    /// </summary>
    private void ShowDesktopBlur()
    {
        HideDesktopBlur();
        // Capture at half resolution and blur the pixels directly (see
        // CaptureScreenBehindWindow). The returned bitmap is ALREADY frosted, so
        // the displayed Image needs no live WPF BlurEffect — that effect is
        // computed lazily on the render thread the first frame after the window
        // shows, which lands a frame or two into the fade and reads as a
        // "sharp -> blur" pop. A pre-blurred bitmap composites instantly.
        var shot = CaptureScreenBehindWindow(0.5);
        if (shot == null)
            return;

        _desktopBlurSource = shot;
        AddDesktopBlurLayer(shot);
    }

    /// <summary>Re-adds the cached frosted backdrop after a Rebuild cleared the
    /// canvas, without re-capturing (which would grab the visible panel).</summary>
    private void RestoreDesktopBlur()
    {
        if (_desktopBlurSource != null)
            AddDesktopBlurLayer(_desktopBlurSource);
    }

    private void AddDesktopBlurLayer(BitmapSource shot)
    {
        double w = Width > 0 ? Width : ActualWidth;
        double h = Height > 0 ? Height : ActualHeight;

        var img = new System.Windows.Controls.Image
        {
            Source = shot,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            // Bitmap is pre-blurred; rasterise once so the fade is a pure GPU
            // composite with zero per-frame work.
            CacheMode = new BitmapCache(),
        };
        Canvas.SetLeft(img, 0);
        Canvas.SetTop(img, 0);
        Panel.SetZIndex(img, -100);   // behind every glass layer
        PanelCanvas.Children.Add(img);
        _desktopBlur = img;
    }

    private void HideDesktopBlur()
    {
        if (_desktopBlur != null)
        {
            PanelCanvas.Children.Remove(_desktopBlur);
            _desktopBlur = null;
        }
        _desktopBlurSource = null;
    }

    /// <summary>
    /// Grabs the screen pixels under the overlay's rectangle. Because the
    /// layered window is fully transparent (Opacity 0) at capture time, the
    /// framebuffer there shows the desktop / windows behind, so a plain BitBlt
    /// from the screen DC returns exactly the backdrop to frost.
    /// </summary>
    private BitmapSource? CaptureScreenBehindWindow(double scale = 1.0)
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int px = (int)Math.Round(Left * sx);
            int py = (int)Math.Round(Top * sy);
            int pw = (int)Math.Round(Width * sx);
            int ph = (int)Math.Round(Height * sy);
            if (pw <= 0 || ph <= 0)
                return null;

            scale = Math.Clamp(scale, 0.1, 1.0);
            int dw = Math.Max(1, (int)Math.Round(pw * scale));
            int dh = Math.Max(1, (int)Math.Round(ph * scale));

            // Grab the desktop with GDI+ CopyFromScreen, which reliably reads the
            // composited screen on DWM/DirectComposition setups where a manual
            // BitBlt/StretchBlt from the screen DC returns a blank (white) frame.
            // Capture at native size into a temp bitmap, then downscale with
            // high-quality interpolation into the working bitmap.
            using var full = new System.Drawing.Bitmap(pw, ph,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                g.CopyFromScreen(px, py, 0, 0,
                    new System.Drawing.Size(pw, ph),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            System.Drawing.Bitmap small;
            if (dw == pw && dh == ph)
            {
                small = full;
            }
            else
            {
                small = new System.Drawing.Bitmap(dw, dh,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = System.Drawing.Graphics.FromImage(small);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, dw, dh);
            }

            // Bake the frost into the pixels so the displayed Image needs no live
            // WPF BlurEffect (which would compute lazily on first paint and pop in
            // a frame late). Three box-blur passes approximate a Gaussian; the
            // radius is in capture (half-res) pixels and scales with capture size
            // so the on-screen blur strength is resolution-independent.
            int blurRadius = Math.Max(2, (int)Math.Round(dw * 0.009));
            BoxBlurBitmap(small, blurRadius, 3);

            IntPtr hBmp = small.GetHbitmap();
            try
            {
                var result = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                result.Freeze();
                return result;
            }
            finally
            {
                DeleteObject(hBmp);
                if (!ReferenceEquals(small, full))
                    small.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// In-place separable box blur on a 32bpp ARGB bitmap. Runs <paramref
    /// name="passes"/> horizontal+vertical passes (3 ≈ Gaussian) with a running
    /// sum, so cost is O(pixels) per pass independent of radius. Done on the
    /// quarter-res capture, this is a sub-millisecond way to pre-frost the
    /// backdrop without a live WPF effect.
    /// </summary>
    private static void BoxBlurBitmap(System.Drawing.Bitmap bmp, int radius, int passes)
    {
        if (radius < 1)
            return;
        int w = bmp.Width, h = bmp.Height;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect,
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int bytes = stride * h;
            byte[] buf = new byte[bytes];
            byte[] tmp = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, bytes);

            for (int p = 0; p < passes; p++)
            {
                BoxBlurPass(buf, tmp, w, h, stride, radius, horizontal: true);
                BoxBlurPass(tmp, buf, w, h, stride, radius, horizontal: false);
            }

            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>One separable box-blur pass (horizontal or vertical) over BGRA
    /// bytes using a sliding window sum. Writes into <paramref name="dst"/>.</summary>
    private static void BoxBlurPass(byte[] src, byte[] dst, int w, int h,
        int stride, int radius, bool horizontal)
    {
        int window = radius * 2 + 1;
        if (horizontal)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                // Prime the window with the clamped left edge.
                for (int k = -radius; k <= radius; k++)
                {
                    int x = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    int i = row + x * 4;
                    sumB += src[i]; sumG += src[i + 1]; sumR += src[i + 2]; sumA += src[i + 3];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    dst[o] = (byte)(sumB / window);
                    dst[o + 1] = (byte)(sumG / window);
                    dst[o + 2] = (byte)(sumR / window);
                    dst[o + 3] = (byte)(sumA / window);
                    int addX = x + radius + 1; addX = addX >= w ? w - 1 : addX;
                    int subX = x - radius; subX = subX < 0 ? 0 : subX;
                    int ai = row + addX * 4, si = row + subX * 4;
                    sumB += src[ai] - src[si];
                    sumG += src[ai + 1] - src[si + 1];
                    sumR += src[ai + 2] - src[si + 2];
                    sumA += src[ai + 3] - src[si + 3];
                }
            }
        }
        else
        {
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int y = k < 0 ? 0 : (k >= h ? h - 1 : k);
                    int i = y * stride + col;
                    sumB += src[i]; sumG += src[i + 1]; sumR += src[i + 2]; sumA += src[i + 3];
                }
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride + col;
                    dst[o] = (byte)(sumB / window);
                    dst[o + 1] = (byte)(sumG / window);
                    dst[o + 2] = (byte)(sumR / window);
                    dst[o + 3] = (byte)(sumA / window);
                    int addY = y + radius + 1; addY = addY >= h ? h - 1 : addY;
                    int subY = y - radius; subY = subY < 0 ? 0 : subY;
                    int ai = addY * stride + col, si = subY * stride + col;
                    sumB += src[ai] - src[si];
                    sumG += src[ai + 1] - src[si + 1];
                    sumR += src[ai + 2] - src[si + 2];
                    sumA += src[ai + 3] - src[si + 3];
                }
            }
        }
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}

