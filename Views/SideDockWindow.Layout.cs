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
    // ---- Layout + build --------------------------------------------------

    private void Layout()
    {
        double icon = EffectiveIconSize;
        // Length available along the anchored edge (the main axis).
        double mainExtent = IsVertical
            ? (Height > 0 ? Height : MonitorLayout.ActiveBounds.Height)
            : (Width > 0 ? Width : MonitorLayout.ActiveBounds.Width);

        double crossGap = 1 * _uiScale;
        // Slab is only as thick as the icon at its hover-enlarged size (plus a
        // hair of breathing room), so the glass background is a snug strip.
        double padCross = GIcon * (HoverScale - 1.0) / 2.0 + icon * 0.12;
        _slabCrossLen = GIcon + padCross * 2.0;
        _slabCross = crossGap;
        // Bias the resting icon column toward the screen edge so the icons hug
        // it. The hover wave pops icons toward the interior, so the edge-side
        // half of the slab's hover-reserve is unused at rest — shifting the
        // column edge-ward there gives the pop-out more room without clipping.
        double edgeBias = GIcon * (HoverScale - 1.0) * 0.30;
        _colCenterCross = _slabCross + _slabCrossLen / 2.0 - edgeBias;

        double startPad = icon * 0.7;
        double endPad = icon * 0.7;
        double seam = _hasRunningArea ? icon * 0.55 : 0;

        // Keep clear of the edges and the taskbar. WorkArea already excludes a
        // docked taskbar, but an AUTO-HIDE taskbar leaves WorkArea at full size,
        // so reserve an explicit band at each end the dock never enters. The
        // taskbar runs along the bottom, i.e. ACROSS a vertical dock's main
        // (top→bottom) axis, so only a vertical dock needs the larger end
        // reserve; a horizontal dock's main axis runs left→right with no taskbar
        // in its path, so it uses symmetric reserves to stay truly centred
        // (an asymmetric band would shift the slab toward the smaller end).
        double startReserve = 12 * _uiScale;
        double endReserve = IsVertical ? 56 * _uiScale : 12 * _uiScale;
        double usableMain = mainExtent - startReserve - endReserve;

        // The dock shows the resident region (up to 14 icons) AND the running
        // strip (up to RunningMaxComplete + overflow tiles), with one uniform
        // cell size for both so the spacing is identical above and below the
        // divider. Size that shared cell from the comfortable default and, only
        // if the combined column would overflow the usable band, shrink it just
        // enough to fit every row (down to a snug floor so icons never overlap).
        int pinnedCount = _config.SideDockApps.Count;
        int runSlots = _hasRunningArea ? CurrentRunSlots() : 0;
        int totalCells = pinnedCount + runSlots;
        double fixedChrome = startPad + endPad + (_hasRunningArea ? seam : 0);
        double availForCells = usableMain - fixedChrome;

        _cellH = DefaultCellH;
        if (totalCells > 0 && totalCells * _cellH > availForCells)
        {
            double floorCell = GIcon * 1.04;   // keep a hair of gap between icons
            _cellH = Math.Max(floorCell, availForCells / totalCells);
        }

        double runningBlockH = runSlots * CellH;

        // Cap visible pinned rows only if the floor was hit and the column still
        // overflows (so the dock never spills past the usable band).
        int maxVisible = Math.Max(1, (int)Math.Floor((availForCells - runningBlockH) / CellH));
        _pinnedVisible = Math.Min(pinnedCount, maxVisible);

        double pinnedBlockH = _pinnedVisible * CellH;

        _slabMainLen = startPad + pinnedBlockH
               + (_hasRunningArea ? seam + runningBlockH : 0)
               + endPad;

        // Centre the VISIBLE ICON CLUSTER (not the slab box) on the usable band.
        // The seam gap between the pinned and running groups, combined with their
        // unequal icon counts, pulls the icons' centre of mass off the slab's
        // geometric centre — so centring the slab box alone leaves the icons
        // looking shifted toward the larger (usually pinned) group, i.e. slightly
        // toward the leading side on a top/bottom dock. Position the slab so the
        // icon centroid lands on the band centre instead, then clamp to the
        // reserved end margins so the dock never spills past them.
        int visibleCells = _pinnedVisible + runSlots;
        double centroidFromSlab = startPad
            + (visibleCells > 0 ? CellH * visibleCells / 2.0 : 0)
            + (_hasRunningArea && visibleCells > 0 ? seam * runSlots / (double)visibleCells : 0);
        _slabMain = (startReserve + usableMain / 2.0) - centroidFromSlab;
        if (_slabMain + _slabMainLen > mainExtent - endReserve)
            _slabMain = mainExtent - endReserve - _slabMainLen;
        if (_slabMain < startReserve)
            _slabMain = startReserve;

        _pinnedAreaMain = _slabMain + startPad;
        _runAreaMain = _pinnedAreaMain + pinnedBlockH + seam;

        // Divider line: the true midpoint between the LAST pinned icon's far edge
        // and the FIRST running tile's near edge, so the drawn seam matches where
        // the icons actually break.
        double lastPinnedEnd = _pinnedAreaMain + pinnedBlockH - (CellH - GIcon) / 2.0;
        double firstRunStart = _runAreaMain + (RunStep - GIcon) / 2.0;
        _seamMain = _pinnedVisible > 0
            ? (lastPinnedEnd + firstRunStart) / 2.0
            : _runAreaMain - seam / 2.0;

        // Clip rect for the pinned scroll layer. Kept INSIDE the glass slab at
        // the near end (so icons never spill past the glass while scrolling) yet
        // wide/long enough that a hovered icon's wave (1.7x zoom + pop-out toward
        // the interior) is not cut.
        double margin = icon * 0.9;
        double clipMainLo = _slabMain + icon * 0.12;
        double clipMainHi = _hasRunningArea
            ? _seamMain - icon * 0.05
            : _slabMain + _slabMainLen - icon * 0.12;
        // The hovered icon pops out toward the interior; extend the clip's
        // interior cross edge to cover its enlarged half-width plus the pop.
        double popInterior = GIcon / 2.0 * HoverScale + WavePop(HoverScale) + icon * 0.2;
        double clipCrossLo = _colCenterCross - GIcon / 2.0 - margin;
        double clipCrossHi = _colCenterCross + popInterior;
        _pinnedViewport = LogicalRect(
            clipMainLo,
            clipCrossLo,
            Math.Max(0, clipMainHi - clipMainLo),
            Math.Max(0, clipCrossHi - clipCrossLo));

        // The wave activates over the WHOLE dock body — pinned column AND running
        // strip — so dragging the cursor across the seam keeps one continuous wave
        // instead of two disjoint ones. Spans to the slab's far main end (which
        // already includes the running block) with the same cross band as above.
        double waveMainHi = _slabMain + _slabMainLen - icon * 0.12;
        _waveHitRect = LogicalRect(
            clipMainLo,
            clipCrossLo,
            Math.Max(0, waveMainHi - clipMainLo),
            Math.Max(0, clipCrossHi - clipCrossLo));
    }

    private int _runSlotsCached;
    private int CurrentRunSlots() => _runSlotsCached;

    private double PinnedScrollMax =>
        Math.Max(0, (_config.SideDockApps.Count - _pinnedVisible)) * CellH;

    private bool PinnedScrollable => PinnedScrollMax > 0.5;

    private void Rebuild()
    {
        Layout();

        // The pinned icon set is about to be rebuilt; tear down any live wave so
        // it can't reference stale icons / slot positions.
        ResetWave();

        PanelCanvas.Children.Clear();
        _pinnedIcons.Clear();
        _pinnedSlots.Clear();
        ClearAmbientLoops();
        _scrollLayer = null;
        _scrollTransform = null;
        _hoverLabel = null;
        _hoverLabelText = null;
        // The canvas was just wiped, so any previously-drawn running tiles are
        // gone; drop their references and force ApplyRunning to redraw them
        // (otherwise the signature guard would skip the redraw and leave the
        // reserved running area blank).
        _runTiles.Clear();
        _runScale.Clear();
        _runTrans.Clear();
        _runCenterMain.Clear();
        _runWaveCur = Array.Empty<double>();
        _runSignature = null;
        _debris.Clear();
        ClearRunPopups();
        PruneIconCache();

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        // Corner radius matches the main dock's resident-region border
        // (main-icon * 0.42); EffectiveIconSize / SideDockScale recovers the
        // main dock's icon size from this scaled-down tray.
        double trayRadius = EffectiveIconSize / SideDockScale * 0.42;
        // The Saturn theme uses a black smoked-glass side dock; every other
        // theme keeps the clear "liquid glass" body.
        bool darkSlab = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        _darkDock = darkSlab;
        _waveBulge = null;
        if (darkSlab)
        {
            // Draw the black tray snug around the icon column, bleeding its
            // edge-side feather off-screen so the solid black sits flush against
            // the screen edge.
            double darkPad = GIcon * 0.5;
            double darkBleed = GIcon * 0.4;
            _bodyCross = _slabCross - darkBleed;
            _bodyCrossLen = (_colCenterCross - _bodyCross) + GIcon / 2.0 + darkPad;
            var r = LogicalRect(_slabMain, _bodyCross, _slabMainLen, _bodyCrossLen);

            // Slab + flame share ONE opacity group AND one feather: each is drawn
            // fully opaque with hard edges inside the group, then a single blur is
            // applied to the whole group and the panel transparency once. Feathering
            // the union (rather than each element separately) is what makes the
            // flame's edge softness identical to the dock's — they are literally the
            // same blurred silhouette — and fuses them into one black mass instead
            // of two stacked semi-transparent layers.
            double slabFeather = Math.Max(16.0, Math.Min(_slabMainLen, _bodyCrossLen) * 0.24);
            _flameFeather = slabFeather;
            var darkGroup = new Canvas
            {
                Opacity = opacity,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = Math.Max(12.0, slabFeather),
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                },
            };
            Panel.SetZIndex(darkGroup, -12);
            PanelCanvas.Children.Add(darkGroup);

            // Slab drawn opaque with hard edges (no per-slab feather mask) — the
            // group blur above feathers it uniformly with the flame.
            GlassChrome.DrawSlab(darkGroup, r.X, r.Y, r.Width, r.Height, trayRadius, 1.0, track: null, frosted: false, dark: true, featherMask: false);

            // Dynamic "black flame" tongue that licks up from the slab and rides
            // the magnification wave. Opaque, hard-edged, same black as the slab
            // rim; the group blur gives it the dock's exact edge feather.
            _bulgeOpacity = opacity;
            double maxBulge = WavePop(HoverScale) + GIcon * HoverScale / 2.0 + GIcon;
            double baseEdge = _bodyCross + _bodyCrossLen;
            double bulgeCrossHi = _colCenterCross + maxBulge;
            // Clip the flame so it can never spill past the dock's ROUNDED corners.
            // The lower band (the buried skirt) is clipped to the slab's exact rounded
            // rectangle; the upper band (the tongue) is clipped to an inset rectangle
            // so the tip stays clear of the corner radius. The union of the two is the
            // dock silhouette grown upward only in the middle.
            var slabRound = new RectangleGeometry(r, trayRadius, trayRadius);
            var upperRect = LogicalRect(
                _slabMain + trayRadius, baseEdge,
                Math.Max(0, _slabMainLen - 2.0 * trayRadius),
                Math.Max(0, bulgeCrossHi - baseEdge));
            var clipGeo = new GeometryGroup { FillRule = FillRule.Nonzero };
            clipGeo.Children.Add(slabRound);
            clipGeo.Children.Add(new RectangleGeometry(upperRect));

            _waveBulge = new System.Windows.Shapes.Path
            {
                // Pure black to match the slab body's rim colour; fully opaque and
                // hard-edged — feathering is done once by the group blur. Brushes.Black
                // is a shared frozen system brush (no per-rebuild allocation).
                Fill = Brushes.Black,
                Opacity = 1.0,
                IsHitTestVisible = false,
                Clip = clipGeo,
            };
            Panel.SetZIndex(_waveBulge, -10);
            darkGroup.Children.Add(_waveBulge);

            // Saturn's signature: scatter a belt of tiny asteroids/rubble along the
            // dock's interior edge so the dark slab reads as a ring of space debris.
            DrawDebrisBelt(baseEdge);

            // Faint starfield over the black slab, matching the main dock's planet
            // backdrop, so the side dock feels like the same patch of space.
            DrawDockStarfield(baseEdge);
        }
        else
        {
            // Hug the icon column with only a modest interior margin instead of
            // the full hover-reserve thickness, so the liquid-glass panel doesn't
            // leave a large empty block beside the icons.
            double glassPad = GIcon * 0.30;
            _bodyCross = _slabCross;
            _bodyCrossLen = (_colCenterCross - _bodyCross) + GIcon / 2.0 + glassPad;
            var r = LogicalRect(_slabMain, _bodyCross, _slabMainLen, _bodyCrossLen);
            GlassChrome.DrawSlab(PanelCanvas, r.X, r.Y, r.Width, r.Height, trayRadius, opacity, track: null, frosted: false, dark: false, frostStrength: GlassChrome.FrostStrengthFor(_config.Settings.PanelTransparency));
            // Give the clear-glass side dock a raised, chiselled edge so it reads
            // as a 3-D slab rather than a flat sheet.
            DrawGlassBevel(r.X, r.Y, r.Width, r.Height, trayRadius, opacity);
            // Cool light source orbiting the slab centre (one revolution / minute).
            // Dropped on the lowest quality tier to spare its per-frame blur.
            if (Polaris.Services.RenderProfile.HeavyBlurEnabled)
                BuildGlassOrbitLight(r, trayRadius);
        }
        if (_hasRunningArea)
            DrawSeam(_seamMain, opacity);

        // Pinned column inside a clipped scrolling layer.
        _pinnedScroll = Math.Clamp(_pinnedScroll, 0, PinnedScrollMax);
        _scrollTransform = IsVertical ? new TranslateTransform(0, -_pinnedScroll) : new TranslateTransform(-_pinnedScroll, 0);
        _scrollLayer = new Canvas { RenderTransform = _scrollTransform };
        var clip = new Canvas { Clip = new RectangleGeometry(_pinnedViewport) };
        clip.Children.Add(_scrollLayer);
        PanelCanvas.Children.Add(clip);

        var apps = _config.SideDockApps;
        for (int i = 0; i < apps.Count; i++)
        {
            double mainC = _pinnedAreaMain + i * CellH + CellH / 2.0;
            var slot = new Point(_colCenterCross, mainC);   // X = cross, Y = main
            _pinnedSlots.Add(slot);

            var icon = CreateIcon(apps[i], GIcon);
            PlaceLogical(icon, slot);
            _scrollLayer.Children.Add(icon);
            _pinnedIcons.Add(icon);
        }

        RefreshRunning();
    }

    /// <summary>Centres an icon at a window-local point (used while dragging).</summary>
    private void PlaceCentered(RadialIcon icon, Point center)
    {
        Canvas.SetLeft(icon, center.X - icon.IconSize / 2);
        Canvas.SetTop(icon, center.Y - icon.IconSize / 2);
    }

    /// <summary>Centres an icon at a logical (X = cross, Y = main) slot.</summary>
    private void PlaceLogical(RadialIcon icon, Point logical)
        => PlaceCentered(icon, ToLocal(logical.Y, logical.X));

    private RadialIcon CreateIcon(AppEntry entry, double size)
    {
        var bmp = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
        var icon = new RadialIcon(entry, bmp, size, AccentColor, LabelBrush, dropletHover: true, sideDockStyle: true);
        icon.ApplyDockEdge(_side);
        icon.ExternalMagnify = true;   // the dock drives a coordinated macOS-style wave
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        icon.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowPinnedIconMenu(icon);
        };
        return icon;
    }

    private void PruneIconCache()
        => IconExtractor.PruneCache(_iconCache, _config.SideDockApps);

    private void DrawSeam(double seamMain, double opacity)
    {
        // The divider runs perpendicular to the main axis (across the body), at
        // main = seamMain. Compute its two window-local endpoints.
        Point pa = ToLocal(seamMain, _bodyCross + 10 * _uiScale);
        Point pb = ToLocal(seamMain, _bodyCross + _bodyCrossLen - 10 * _uiScale);

        bool isSaturn = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;

        // A soft cool glow plus a bright glassy highlight form the divider. The
        // old near-black groove line is omitted so the seam reads as a light
        // split with no dark edge.
        double glowThk   = isSaturn ? 2.2  : 4.0;
        byte   glowA     = isSaturn ? (byte)0x55 : (byte)0xB0;
        int    glowBlur  = isSaturn ? 4    : 5;
        double shineThk  = isSaturn ? 0.5  : 0.9;
        byte   shineA    = isSaturn ? (byte)0x80 : (byte)0xDD;

        var glow = new System.Windows.Shapes.Line
        {
            X1 = pa.X, X2 = pb.X, Y1 = pa.Y, Y2 = pb.Y,
            StrokeThickness = glowThk,
            Stroke = new SolidColorBrush(Color.FromArgb(glowA, 0xBF, 0xE0, 0xFF)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = glowBlur },
        };
        var shine = new System.Windows.Shapes.Line
        {
            X1 = pa.X, X2 = pb.X, Y1 = pa.Y, Y2 = pb.Y,
            StrokeThickness = shineThk,
            Stroke = new SolidColorBrush(Color.FromArgb(shineA, 0xEA, 0xF4, 0xFF)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Panel.SetZIndex(glow, -5);
        Panel.SetZIndex(shine, -4);
        PanelCanvas.Children.Add(glow);
        PanelCanvas.Children.Add(shine);
    }

    /// <summary>Overlays a raised, chiselled bevel on the clear-glass side dock so
    /// its edge reads as a 3-D slab. A directional rim (bright along the top/left,
    /// shaded along the bottom/right) fakes a single top-left light source, an
    /// inner soft groove adds depth (ambient occlusion just inside the rim), and a
    /// thin bright top-left hairline crisps the lit corner. Glass theme only.</summary>
    private void DrawGlassBevel(double left, double top, double w, double h, double radius, double opacity)
    {
        if (w <= 1 || h <= 1)
            return;

        // Directional bevel rim: bright top-left highlight melting into a dark
        // bottom-right shade, which is what makes the slab look raised.
        var bevel = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(1.8),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.0),  // lit top-left
                    new GradientStop(Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF), 0.34),
                    new GradientStop(Color.FromArgb(0x28, 0x0A, 0x14, 0x24), 0.6),
                    new GradientStop(Color.FromArgb(0x96, 0x04, 0x09, 0x12), 1.0),  // shaded bottom-right
                },
            },
        };
        Canvas.SetLeft(bevel, left);
        Canvas.SetTop(bevel, top);
        Panel.SetZIndex(bevel, -5);
        PanelCanvas.Children.Add(bevel);

        // Inner occlusion groove: a soft dark ring just inside the rim, biased to
        // the bottom-right, deepening the sense that the surface sits proud of the
        // desktop behind it.
        var groove = new Border
        {
            Width = w - 3,
            Height = h - 3,
            CornerRadius = new CornerRadius(Math.Max(0, radius - 1.5)),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(2.4),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 3 },
            CacheMode = new System.Windows.Media.BitmapCache(),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x05, 0x0A, 0x14), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x05, 0x0A, 0x14), 0.45),
                    new GradientStop(Color.FromArgb(0x4C, 0x04, 0x08, 0x12), 1.0),
                },
            },
        };
        Canvas.SetLeft(groove, left + 1.5);
        Canvas.SetTop(groove, top + 1.5);
        Panel.SetZIndex(groove, -5);
        PanelCanvas.Children.Add(groove);
    }

}
