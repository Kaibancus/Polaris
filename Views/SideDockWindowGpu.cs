using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Models;
using Polaris.Services;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Views;

/// <summary>GPU side dock (spike) — Stage A static render, Stage B hover hit-test +
/// floating name label, Stage C macOS-style magnify wave, Stage D running-app strip.
/// Draws the liquid-glass slab, the pinned icon column, a light-split divider and the
/// running-but-unpinned strip (Polaris tile first, green running dots, "+N" overflow)
/// in Direct2D under DirectComposition; a cursor poll drives a continuous magnify wave
/// across both halves and shows the hovered icon's name. Per-monitor DPI aware (layout
/// in DIPs, window + swap chain in physical px, D2D target DPI = 96 x scale). The window
/// stays click-through — launch/drag come in Stage E. Shown behind POLARIS_GPU_SIDEDOCK=1.</summary>
internal sealed class SideDockWindowGpu : IDisposable
{
    private const float GlassIconScale = 1.32f;
    private const float SideDockScaleK = 0.70f;
    private const float HoverScale = 1.5f;

    private enum SlotKind { Pinned, Run, Overflow }

    private readonly struct Slot
    {
        public readonly Vector2 Center;
        public readonly float G;
        public readonly string Name;
        public readonly bool Running;     // draws the breathing green dot
        public readonly SlotKind Kind;
        public readonly string IconKey;   // D2D bitmap cache key
        public readonly BitmapSource? Image;
        public readonly AppEntry? Entry;  // pinned: the app to launch
        public readonly IntPtr Window;    // running: the window to activate
        public Slot(Vector2 c, float g, string name, bool run, SlotKind kind, string iconKey,
            BitmapSource? img, AppEntry? entry, IntPtr window)
        { Center = c; G = g; Name = name; Running = run; Kind = kind; IconKey = iconKey; Image = img; Entry = entry; Window = window; }
    }

    private readonly AppConfig _config;
    private IntPtr _hwnd;
    private CompositionHost? _host;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private readonly Dictionary<string, ID2D1Bitmap?> _bmpCache = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();
    private DispatcherTimer? _timer;

    private readonly List<Slot> _slots = new();
    private DockSide _side;
    private int _winX, _winY, _winW, _winH;
    private double _dpi = 1.0;
    private int _hover = -1;
    private float _sx, _sy, _sw, _sh, _trayRadius, _opacity, _frost;
    private float _gIcon, _cellH;
    private float _seamMain, _bodyCross, _bodyCrossLen;
    private float _colCenterCross, _slabCrossLen, _pinnedAreaMain, _cellMain;
    private int _pinnedVisible;
    private float[] _waveCur = Array.Empty<float>();
    private const float WaveSupport = 2.3f;

    // ---- Interaction (Stage E) -------------------------------------------
    private static readonly Dictionary<IntPtr, SideDockWindowGpu> s_instances = new();
    private int _pressIdx = -1;        // slot under the mouse-down, or -1
    private bool _dragging;            // press has crossed the drag threshold
    private float _pressMain, _pressCross;   // mouse-down point (window-local DIP)
    private float _dragMain, _dragCross;     // current drag point (window-local DIP)

    public SideDockWindowGpu(AppConfig config) => _config = config;

    public void Show()
    {
        try { Build(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "failed: " + ex); }
    }

    private void Build()
    {
        _slots.Clear();
        var wa = MonitorLayout.ActiveWorkArea;
        _side = _config.Settings.DockPosition;
        bool vertical = _side is DockSide.Left or DockSide.Right;
        double uiScale = Math.Clamp(MonitorLayout.ActiveBounds.Height / 1080.0, 1.0, 2.0);
        double iconSize = _config.Settings.IconSize;
        double effIcon = iconSize * uiScale * (SideDockScaleK / GlassIconScale);
        double gIcon = effIcon * GlassIconScale;
        double cellH = effIcon * 1.46;

        double crossGap = 1 * uiScale;
        double padCross = gIcon * (HoverScale - 1.0) / 2.0 + effIcon * 0.12;
        double slabCrossLen = gIcon + padCross * 2.0;
        double slabCross = crossGap;
        double edgeBias = gIcon * (HoverScale - 1.0) * 0.30;
        double colCenterCross = slabCross + slabCrossLen / 2.0 - edgeBias;

        double startPad = effIcon * 0.7, endPad = effIcon * 0.7;
        double thickness = vertical ? gIcon * HoverScale + 240 * uiScale : gIcon * HoverScale + 130 * uiScale;

        var apps = _config.SideDockApps;
        int pinnedCount = apps.Count;

        // Horizontal docks are sized to their CONTENT (centred), not full-width —
        // matching the real dock's DesiredContentMain (incl. the running-area
        // reserve), so the GPU dock has the same footprint.
        double defCell = effIcon * 1.46;
        const int maxRunSlots = 1 + 10 + 1;   // Polaris + RunningMaxComplete + overflow
        double desiredMain = 12 * uiScale + startPad + pinnedCount * defCell
                           + effIcon * 0.55 + maxRunSlots * defCell + endPad + 12 * uiScale;
        double winMain = vertical ? wa.Height : Math.Min(desiredMain, wa.Width);
        _winW = (int)Math.Ceiling(vertical ? thickness : winMain);
        _winH = (int)Math.Ceiling(vertical ? wa.Height : thickness);
        double mainExtent = winMain;

        double startReserve = 12 * uiScale, endReserve = vertical ? 56 * uiScale : 12 * uiScale;
        double usableMain = mainExtent - startReserve - endReserve;

        // ---- Running-but-unpinned strip (Stage D) -------------------------
        var runItems = CollectRunning(apps, out int overflow);
        int runSlots = 1 + runItems.Count + (overflow > 0 ? 1 : 0);   // Polaris + apps + overflow
        double seam = effIcon * 0.55;

        // One uniform cell pitch above and below the divider; shrink only if the
        // combined column would overflow the usable band (mirrors the real dock).
        int totalCells = pinnedCount + runSlots;
        double fixedChrome = startPad + endPad + seam;
        double availForCells = usableMain - fixedChrome;
        if (totalCells > 0 && totalCells * cellH > availForCells)
            cellH = Math.Max(gIcon * 1.04, availForCells / totalCells);

        double runningBlockH = runSlots * cellH;          // RunStep == cellH
        int maxVisible = Math.Max(1, (int)Math.Floor((availForCells - runningBlockH) / cellH));
        int pinnedVisible = Math.Min(pinnedCount, maxVisible);
        double pinnedBlockH = pinnedVisible * cellH;
        double slabMainLen = startPad + pinnedBlockH + seam + runningBlockH + endPad;

        // Centre the VISIBLE ICON CLUSTER (pinned + running, incl. the seam gap),
        // not the slab box, on the usable band — same correction as the real dock.
        int visibleCells = pinnedVisible + runSlots;
        double centroidFromSlab = startPad
            + (visibleCells > 0 ? cellH * visibleCells / 2.0 : 0)
            + (visibleCells > 0 ? seam * runSlots / (double)visibleCells : 0);
        double slabMain = (startReserve + usableMain / 2.0) - centroidFromSlab;
        slabMain = Math.Min(slabMain, mainExtent - endReserve - slabMainLen);
        slabMain = Math.Max(slabMain, startReserve);
        double pinnedAreaMain = slabMain + startPad;
        double runAreaMain = pinnedAreaMain + pinnedBlockH + seam;

        double lastPinnedEnd = pinnedAreaMain + pinnedBlockH - (cellH - gIcon) / 2.0;
        double firstRunStart = runAreaMain + (cellH - gIcon) / 2.0;
        double seamMain = pinnedVisible > 0
            ? (lastPinnedEnd + firstRunStart) / 2.0
            : runAreaMain - seam / 2.0;

        double glassPad = gIcon * 0.30;
        double bodyCross = slabCross;
        double bodyCrossLen = (colCenterCross - bodyCross) + gIcon / 2.0 + glassPad;
        _trayRadius = (float)(iconSize * uiScale * 0.42);
        _opacity = (float)(1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0));
        _frost = (float)GlassChrome.FrostStrengthFor(_config.Settings.PanelTransparency);

        _winX = _side switch
        {
            DockSide.Right => (int)(wa.Right - _winW),
            DockSide.Left => (int)wa.Left,
            _ => (int)(wa.Left + (wa.Width - _winW) / 2.0),   // Top/Bottom centred
        };
        _winY = _side switch { DockSide.Bottom => (int)(wa.Bottom - _winH), _ => (int)wa.Top };

        (_sx, _sy, _sw, _sh) = _side switch
        {
            DockSide.Left => ((float)bodyCross, (float)slabMain, (float)bodyCrossLen, (float)slabMainLen),
            DockSide.Right => ((float)(_winW - bodyCross - bodyCrossLen), (float)slabMain, (float)bodyCrossLen, (float)slabMainLen),
            DockSide.Top => ((float)slabMain, (float)bodyCross, (float)slabMainLen, (float)bodyCrossLen),
            _ => ((float)slabMain, (float)(_winH - bodyCross - bodyCrossLen), (float)slabMainLen, (float)bodyCrossLen),
        };

        var running = RunningAppTracker.SnapshotRunning();
        var noTitles = new List<string>();
        var noAumids = new HashSet<string>();
        for (int i = 0; i < pinnedVisible && i < apps.Count; i++)
        {
            var entry = apps[i];
            double mainC = pinnedAreaMain + i * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, noTitles, noAumids);
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, entry.Name, run,
                SlotKind.Pinned, entry.EffectiveIconSource, img, entry, IntPtr.Zero));
        }
        for (int k = 0; k < runSlots; k++)
        {
            double mainC = runAreaMain + k * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            if (k == 0)
            {
                string exe = Environment.ProcessPath ?? "";
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, "Polaris", true,
                    SlotKind.Run, "polaris:" + exe, SafeIcon(exe), null, IntPtr.Zero));
            }
            else if (overflow > 0 && k == runSlots - 1)
            {
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, "+" + overflow, false,
                    SlotKind.Overflow, "", null, null, IntPtr.Zero));
            }
            else
            {
                var it = runItems[k - 1];
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, it.Name, true,
                    SlotKind.Run, it.IconKey, it.Image, null, it.Window));
            }
        }
        _seamMain = (float)seamMain;
        _bodyCross = (float)bodyCross;
        _bodyCrossLen = (float)bodyCrossLen;
        _pinnedVisible = pinnedVisible;
        _colCenterCross = (float)colCenterCross;
        _slabCrossLen = (float)slabCrossLen;
        _pinnedAreaMain = (float)pinnedAreaMain;
        _cellMain = (float)cellH;
        _gIcon = (float)gIcon;
        _cellH = (float)cellH;
        _waveCur = new float[_slots.Count];
        Array.Fill(_waveCur, 1f);

        _hwnd = CreateWindow(_winW, _winH);
        s_instances[_hwnd] = this;
        _dpi = DpiScale();
        // Layout is computed in DIPs (MonitorLayout returns DIPs); the Win32 window
        // + DComp swap chain live in PHYSICAL pixels. Size the window to physical px
        // and tell D2D the target DPI so all DIP-space drawing scales up 1:1.
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        DragAcceptFiles(_hwnd, true);   // accept desktop shortcuts / files dropped to pin
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.Normal,
            FontStyle.Normal, FontStretch.Normal, 13f, "zh-cn");
        _labelFormat.TextAlignment = TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;

        Render();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private float WaveScaleAt(float cursorMain, float iconMain)
    {
        float d = Math.Abs(cursorMain - iconMain) / _cellH;
        if (d >= WaveSupport)
            return 1f;
        float f = 0.5f * (1f + (float)Math.Cos(Math.PI * d / WaveSupport));
        return 1f + (HoverScale - 1f) * f;
    }

    private Vector2 PopOffset(float pop) => _side switch
    {
        DockSide.Left => new Vector2(pop, 0),
        DockSide.Right => new Vector2(-pop, 0),
        DockSide.Top => new Vector2(0, pop),
        _ => new Vector2(0, -pop),
    };

    private void Tick()
    {
        if (_host == null)
            return;
        bool vertical = _side is DockSide.Left or DockSide.Right;
        bool active = false;
        float curMain = 0;
        if (!_dragging && GetCursorPos(out POINT p))
        {
            float lx = (float)(p.X / _dpi - _winX), ly = (float)(p.Y / _dpi - _winY);
            float m = _gIcon * 0.6f;
            if (lx >= _sx - m && lx <= _sx + _sw + m && ly >= _sy - m && ly <= _sy + _sh + m)
            {
                active = true;
                curMain = vertical ? ly : lx;
            }
        }

        float k = 1f - (float)Math.Exp(-0.016 / 0.045);   // tau 45ms
        float maxDelta = 0f;
        int focal = -1; float best = float.MaxValue;
        for (int i = 0; i < _slots.Count; i++)
        {
            float iconMain = vertical ? _slots[i].Center.Y : _slots[i].Center.X;
            float target = active ? WaveScaleAt(curMain, iconMain) : 1f;
            float cur = _waveCur[i] + (target - _waveCur[i]) * k;
            _waveCur[i] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            if (active)
            {
                float d = Math.Abs(curMain - iconMain);
                if (d < best) { best = d; focal = i; }
            }
        }
        _hover = active && focal >= 0 && best <= _cellH ? focal : -1;

        // Render every frame while the wave is live or a launch bounce is playing;
        // once settled at rest, render one final frame and idle (the timer keeps
        // polling for re-entry).
        bool bouncing = _bounceIdx >= 0 && (Environment.TickCount64 - _bounceStart) <= BounceDurMs;
        if (!bouncing) _bounceIdx = -1;
        if (active || bouncing || maxDelta > 0.001f)
            Render();
    }

    private const long BounceDurMs = 480;
    private int _bounceIdx = -1;
    private long _bounceStart;

    /// <summary>Extra outward "pop" (in the dock's pop direction) for the launch
    /// bounce: the clicked icon leaps off the dock surface and falls back over
    /// <see cref="BounceDurMs"/> ms — a single damped hop (sin half-cycle with a
    /// gentle settle), mirroring the WPF dock's macOS-style launch hop.</summary>
    private float BounceOffset(int i)
    {
        if (i != _bounceIdx)
            return 0f;
        long el = Environment.TickCount64 - _bounceStart;
        if (el < 0 || el > BounceDurMs)
            return 0f;
        float t = el / (float)BounceDurMs;
        // Up fast, fall back with a small overshoot for a springy settle.
        float hop = MathF.Sin(MathF.PI * t);
        float settle = -0.12f * MathF.Sin(MathF.PI * 2f * t) * (1f - t);
        return _gIcon * 0.6f * (hop + settle);
    }

    private static Color4 Col(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    private void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;
        ctx.BeginDraw();
        ctx.Clear(Col(0, 0, 0, 0));
        GlassSlab.DrawGlass(ctx, _sx, _sy, _sw, _sh, _trayRadius, _opacity, _frost);
        if (_pinnedVisible > 0)
            DrawSeam(ctx);

        // Draw smallest-first so the magnified (focal) icon sits on top. The icon
        // being dragged is skipped here and drawn last at the cursor.
        var order = new int[_slots.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _waveCur[a].CompareTo(_waveCur[b]));
        int dragIdx = _dragging ? _pressIdx : -1;
        foreach (int i in order)
        {
            if (i == dragIdx)
                continue;
            float scale = _waveCur[i];
            Vector2 pop = PopOffset((scale - 1f) * _gIcon * 1.18f + BounceOffset(i));
            DrawIcon(ctx, _slots[i], scale, pop);
        }

        if (dragIdx >= 0 && dragIdx < _slots.Count)
        {
            // The dragged icon follows the cursor, lifted 1.12x with no pop.
            var s = _slots[dragIdx];
            var moved = new Slot(new Vector2(_dragMain, _dragCross), s.G, s.Name, s.Running,
                s.Kind, s.IconKey, s.Image, s.Entry, s.Window);
            DrawIcon(ctx, moved, 1.12f, Vector2.Zero);
        }
        else if (_hover >= 0 && _hover < _slots.Count)
            DrawHoverLabel(ctx, _slots[_hover], _waveCur[_hover]);
        ctx.EndDraw();
        _host.Present();
    }

    private void DrawIcon(ID2D1DeviceContext ctx, in Slot s, float scale, Vector2 pop)
    {
        float g = s.G, half = g / 2f, cx = s.Center.X, cy = s.Center.Y;
        var wave = Matrix3x2.CreateScale(scale, scale, s.Center) * Matrix3x2.CreateTranslation(pop);

        ctx.Transform = wave;
        var plate = new Rect(cx - half, cy - half, g, g);

        if (s.Kind == SlotKind.Overflow)
        {
            // Taskbar-style "+N" overflow marker — no icon, just centred text.
            if (_labelFormat != null && !string.IsNullOrEmpty(s.Name))
                using (var ink = ctx.CreateSolidColorBrush(Col(0xE6, 0xFF, 0xFF, 0xFF)))
                    ctx.DrawText(s.Name, _labelFormat, plate, ink);
        }
        else
        {
            var bmp = GetBitmap(ctx, s.IconKey, s.Image);
            if (bmp != null)
            {
                float pad = g * 0.14f, dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
                var bs = bmp.Size;
                ctx.Transform = Matrix3x2.CreateScale(dstSz / Math.Max(1f, bs.Width), dstSz / Math.Max(1f, bs.Height))
                              * Matrix3x2.CreateTranslation(dstX, dstY) * wave;
                ctx.DrawBitmap(bmp, 1f, InterpolationMode.HighQualityCubic);
                ctx.Transform = wave;
            }
        }

        if (s.Running)
        {
            float dot = Math.Max(2.6f, g * 0.07f), glow = dot * 2.3f;
            (float dx, float dy) = _side switch
            {
                DockSide.Left => (cx - half + dot * 0.05f, cy),
                DockSide.Right => (cx + half - dot * 0.05f, cy),
                DockSide.Top => (cx, cy - half + dot * 0.05f),
                _ => (cx, cy + half - dot * 0.05f),
            };
            // Soft halo via a radial gradient (green core → transparent) instead of a
            // hard-edged disc, so it reads as the same subtle breathing glow as the
            // WPF dock's blurred ellipse rather than an oversized solid blob.
            var center = new Vector2(dx, dy);
            using (var stops = ctx.CreateGradientStopCollection(new[]
            {
                new Vortice.Direct2D1.GradientStop { Position = 0f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.45f) },
                new Vortice.Direct2D1.GradientStop { Position = 0.5f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.18f) },
                new Vortice.Direct2D1.GradientStop { Position = 1f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0f) },
            }))
            using (var gl = ctx.CreateRadialGradientBrush(
                new RadialGradientBrushProperties { Center = center, GradientOriginOffset = Vector2.Zero, RadiusX = glow / 2f, RadiusY = glow / 2f },
                stops))
                ctx.FillEllipse(new Ellipse(center, glow / 2f, glow / 2f), gl);
            using (var co = ctx.CreateSolidColorBrush(new Color4(0x4C / 255f, 0xE0 / 255f, 0x6B / 255f, 1f)))
                ctx.FillEllipse(new Ellipse(center, dot / 2f, dot / 2f), co);
        }
        ctx.Transform = Matrix3x2.Identity;
    }

    private void DrawHoverLabel(ID2D1DeviceContext ctx, in Slot s, float scale)
    {
        if (_labelFormat == null || string.IsNullOrEmpty(s.Name))
            return;
        // Clear the magnified + popped focal icon.
        float reach = s.G / 2f * scale + (scale - 1f) * _gIcon * 1.18f;
        float w = Math.Max(40f, s.Name.Length * 13f * 0.95f + 18f), h = 24f, gap = 8f;
        (float lx, float ly) = _side switch
        {
            DockSide.Left => (s.Center.X + reach + gap + w / 2f, s.Center.Y),
            DockSide.Right => (s.Center.X - reach - gap - w / 2f, s.Center.Y),
            DockSide.Top => (s.Center.X, s.Center.Y + reach + gap + h / 2f),
            _ => (s.Center.X, s.Center.Y - reach - gap - h / 2f),
        };
        var rect = new Rect(lx - w / 2f, ly - h / 2f, w, h);
        // The real hover label is just floating text on a barely-there dark tint
        // (ARGB 0x05,1A1A1A) — no visible plate.
        using (var bg = ctx.CreateSolidColorBrush(Col(0x05, 0x1A, 0x1A, 0x1A)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = 7f, RadiusY = 7f }, bg);
        using (var ink = ctx.CreateSolidColorBrush(Col(0xE6, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(s.Name, _labelFormat, rect, ink);
    }

    private ID2D1Bitmap? GetBitmap(ID2D1DeviceContext ctx, string key, BitmapSource? src)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (_bmpCache.TryGetValue(key, out var cached))
            return cached;
        ID2D1Bitmap? d2d = null;
        try
        {
            if (src != null)
            {
                if (src.Format != PixelFormats.Pbgra32)
                    src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                d2d = _host!.CreateBitmap(w, h, px, stride);
            }
        }
        catch { d2d = null; }
        _bmpCache[key] = d2d;
        return d2d;
    }

    /// <summary>Light-split divider between the pinned column and the running strip:
    /// a soft cool glow plus a bright glassy highlight, drawn across the body at
    /// <see cref="_seamMain"/> (mirrors the WPF dock's <c>DrawSeam</c>).</summary>
    private void DrawSeam(ID2D1DeviceContext ctx)
    {
        (float ax, float ay) = ToLocal(_side, _seamMain, _bodyCross + 10f, _winW, _winH);
        (float bx, float by) = ToLocal(_side, _seamMain, _bodyCross + _bodyCrossLen - 10f, _winW, _winH);
        var p0 = new Vector2(ax, ay);
        var p1 = new Vector2(bx, by);
        ctx.Transform = Matrix3x2.Identity;
        // Approximate the WPF BlurEffect glow with two stacked translucent strokes.
        using (var glowWide = ctx.CreateSolidColorBrush(Col(0x40, 0xBF, 0xE0, 0xFF)))
            ctx.DrawLine(p0, p1, glowWide, 7f);
        using (var glow = ctx.CreateSolidColorBrush(Col(0x90, 0xBF, 0xE0, 0xFF)))
            ctx.DrawLine(p0, p1, glow, 4f);
        using (var shine = ctx.CreateSolidColorBrush(Col(0xDD, 0xEA, 0xF4, 0xFF)))
            ctx.DrawLine(p0, p1, shine, 1f);
    }

    private readonly struct RunItem
    {
        public readonly string Name, IconKey;
        public readonly BitmapSource? Image;
        public readonly IntPtr Window;
        public RunItem(string name, string key, BitmapSource? img, IntPtr window) { Name = name; IconKey = key; Image = img; Window = window; }
    }

    /// <summary>Collects running-but-unpinned taskbar apps for the running strip.
    /// A lightweight version of the WPF dock's filter (excludes pinned apps by full
    /// path / file name) — enough for the spike's visual parity.</summary>
    private List<RunItem> CollectRunning(IReadOnlyList<AppEntry> pinned, out int overflow)
    {
        overflow = 0;
        var result = new List<RunItem>();
        try
        {
            var excludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddPathAndName(string p)
            {
                try { excludePaths.Add(System.IO.Path.GetFullPath(p)); } catch { excludePaths.Add(p); }
                try { var fn = System.IO.Path.GetFileName(p); if (!string.IsNullOrWhiteSpace(fn)) excludeNames.Add(fn); }
                catch { /* unreadable path */ }
            }
            foreach (var a in pinned)
            {
                if (string.IsNullOrWhiteSpace(a.Path))
                    continue;
                // Mirror the WPF dock: resolve each pinned app's launcher AUMID and
                // its real AppsFolder exe, so AppsFolder-launched pins (Edge, VS Code…)
                // — whose a.Path is a pseudo-launcher, not the running exe — are still
                // matched and excluded from the running strip (no duplicate tile).
                string? aumid = WindowPreviewService.TryGetLauncherAumid(a.Path, a.Arguments);
                if (aumid != null)
                {
                    excludeAumids.Add(aumid);
                    string? exe = WindowPreviewService.TryResolveAppsFolderExe(aumid);
                    if (!string.IsNullOrWhiteSpace(exe))
                        AddPathAndName(exe);
                }
                else
                {
                    AddPathAndName(a.Path);
                }
            }

            var filtered = new List<TaskbarApp>();
            foreach (var ta in WindowPreviewService.GetTaskbarApps())
            {
                string full;
                try { full = System.IO.Path.GetFullPath(ta.Path); } catch { full = ta.Path; }
                if (!string.IsNullOrEmpty(full) && excludePaths.Contains(full))
                    continue;
                if (ta.Aumid != null)
                {
                    bool excluded = excludeAumids.Contains(ta.Aumid);
                    if (!excluded)
                        foreach (var ex in excludeAumids)
                            if (WindowPreviewService.AumidFamilyMatches(ta.Aumid, ex)) { excluded = true; break; }
                    if (excluded)
                        continue;
                }
                try { var fn = System.IO.Path.GetFileName(ta.Path); if (!string.IsNullOrWhiteSpace(fn) && excludeNames.Contains(fn)) continue; }
                catch { /* unreadable path */ }
                filtered.Add(ta);
            }

            const int max = 10;   // RunningMaxComplete
            if (filtered.Count > max)
            {
                overflow = filtered.Count - max;
                filtered = filtered.GetRange(0, max);
            }
            foreach (var ta in filtered)
            {
                bool pathless = string.IsNullOrEmpty(ta.Path);
                string key = !string.IsNullOrEmpty(ta.Aumid) ? "aumid:" + ta.Aumid
                           : (pathless ? "win:" + ta.Window : ta.Path);
                result.Add(new RunItem(FriendlyRunName(ta, pathless), key, ResolveRunIcon(ta, pathless), ta.Window));
            }
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "running collect failed: " + ex.Message); }
        return result;
    }

    /// <summary>A human-friendly label for a running app: the executable's embedded
    /// product description (e.g. "Microsoft Edge"), then its file name, then the raw
    /// window title for path-protected / UWP windows. The window title alone is a
    /// poor label — a browser's is the full page name, a terminal's is the tab.</summary>
    private static string FriendlyRunName(TaskbarApp ta, bool pathless)
    {
        if (!pathless && !string.IsNullOrEmpty(ta.Path))
        {
            try
            {
                var desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(ta.Path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                    return desc.Trim();
            }
            catch { /* unreadable metadata — fall through */ }
            try { return System.IO.Path.GetFileNameWithoutExtension(ta.Path); }
            catch { /* unreadable path */ }
        }
        return ta.Title ?? "";
    }

    private static BitmapSource? ResolveRunIcon(TaskbarApp ta, bool pathless)
    {
        try
        {
            // A real Win32 exe (not a WindowsApps-packaged path) carries its own
            // icon and is the most reliable source. The AppsFolder lookup is only
            // preferable for packaged/UWP apps whose exe has no embedded icon — and
            // for some apps (e.g. Edge launched under a profile-specific AUMID) it
            // returns a generic blank document, so try the exe icon first here.
            bool packaged = !string.IsNullOrEmpty(ta.Path)
                && ta.Path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!packaged && !string.IsNullOrEmpty(ta.Path))
            {
                var pb = IconExtractor.GetIcon(ta.Path);
                if (pb != null)
                    return pb;
            }
            if (!string.IsNullOrEmpty(ta.Aumid))
            {
                var b = IconExtractor.GetIcon(ShellNamespace.NormalizeAppsFolderPath(ta.Aumid));
                if (b == null && ta.Window != IntPtr.Zero)
                    b = WindowPreviewService.GetWindowIconImage(ta.Window);
                return b;
            }
            return pathless
                ? WindowPreviewService.GetWindowIconImage(ta.Window)
                : (IconExtractor.GetIcon(ta.Path)
                   ?? (ta.Window != IntPtr.Zero ? WindowPreviewService.GetWindowIconImage(ta.Window) : null));
        }
        catch { return null; }
    }

    private static BitmapSource? SafeIcon(string path)
    {
        try { return string.IsNullOrEmpty(path) ? null : IconExtractor.GetIcon(path); }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer?.Stop();
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        _host?.Dispose();
        if (_hwnd != IntPtr.Zero) { s_instances.Remove(_hwnd); DestroyWindow(_hwnd); }
    }

    // ---- Interaction (Stage E): click-launch, drag-reorder, drag-out-unpin ----

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_NCHITTEST = 0x0084;
    private const uint WM_DROPFILES = 0x0233;
    private const int HTTRANSPARENT = -1, HTCLIENT = 1;

    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, System.Text.StringBuilder? buf, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out POINT pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);

    /// <summary>Routes the raw window messages this instance cares about. Returns
    /// true (with <paramref name="result"/>) when handled; false defers to DefWindowProc.</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_DROPFILES:
            {
                IntPtr hDrop = wParam;
                try
                {
                    uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                    DragQueryPoint(hDrop, out POINT pt);
                    var paths = new List<string>();
                    for (uint i = 0; i < count; i++)
                    {
                        uint len = DragQueryFileW(hDrop, i, null, 0);
                        var sb = new System.Text.StringBuilder((int)len + 1);
                        if (DragQueryFileW(hDrop, i, sb, len + 1) > 0)
                            paths.Add(sb.ToString());
                    }
                    HandleDropFiles(paths, pt);
                }
                catch (Exception ex) { Log.Warn("SideDockGpu", "drop-in failed: " + ex.Message); }
                finally { DragFinish(hDrop); }
                return true;
            }
            case WM_NCHITTEST:
            {
                // lParam = SCREEN coords. Inside the glass slab → grab the click;
                // elsewhere → HTTRANSPARENT so the empty reserve passes through.
                int sx = unchecked((short)((long)lParam & 0xFFFF));
                int sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                float lx = (float)(sx / _dpi - _winX), ly = (float)(sy / _dpi - _winY);
                float m = _gIcon * 0.5f;
                bool inside = lx >= _sx - m && lx <= _sx + _sw + m && ly >= _sy - m && ly <= _sy + _sh + m;
                result = inside ? HTCLIENT : HTTRANSPARENT;
                return true;
            }
            case WM_LBUTTONDOWN:
            {
                (float lx, float ly) = ClientDip(lParam);
                _pressMain = lx; _pressCross = ly;
                _dragMain = lx; _dragCross = ly;
                _pressIdx = HitSlot(lx, ly);
                _dragging = false;
                if (_pressIdx >= 0)
                    SetCapture(_hwnd);
                return true;
            }
            case WM_MOUSEMOVE:
            {
                if (_pressIdx < 0)
                    return false;
                (float lx, float ly) = ClientDip(lParam);
                _dragMain = lx; _dragCross = ly;
                if (!_dragging && (MathF.Abs(lx - _pressMain) + MathF.Abs(ly - _pressCross)) > _gIcon * 0.35f)
                    _dragging = true;
                if (_dragging)
                    Render();
                return true;
            }
            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                int idx = _pressIdx;
                bool wasDrag = _dragging;
                (float lx, float ly) = ClientDip(lParam);
                _pressIdx = -1;
                _dragging = false;
                if (idx >= 0 && idx < _slots.Count)
                {
                    if (!wasDrag) ClickSlot(idx);
                    else DropSlot(idx, lx, ly);
                }
                if (_hwnd != IntPtr.Zero)
                    Render();
                return true;
            }
        }
        return false;
    }

    private (float lx, float ly) ClientDip(IntPtr lParam)
    {
        int cx = unchecked((short)((long)lParam & 0xFFFF));
        int cy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        return ((float)(cx / _dpi), (float)(cy / _dpi));
    }

    /// <summary>Index of the slot whose icon is under the (window-local DIP) point,
    /// or -1.</summary>
    private int HitSlot(float lx, float ly)
    {
        int best = -1; float bestD = _gIcon * 0.75f;
        for (int i = 0; i < _slots.Count; i++)
        {
            var c = _slots[i].Center;
            float d = MathF.Abs(lx - c.X) + MathF.Abs(ly - c.Y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void ClickSlot(int idx)
    {
        var s = _slots[idx];
        try
        {
            if (s.Kind == SlotKind.Pinned && s.Entry != null)
            {
                _bounceIdx = idx; _bounceStart = Environment.TickCount64;   // launch hop
                AppLauncher.Launch(s.Entry, null);
            }
            else if (s.Kind == SlotKind.Run && s.Window != IntPtr.Zero)
                WindowPreviewService.Activate(s.Window);
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "launch failed: " + ex.Message); }
    }

    private void DropSlot(int idx, float lx, float ly)
    {
        var s = _slots[idx];
        if (s.Kind != SlotKind.Pinned || s.Entry == null)
            return;   // only pinned icons reorder / unpin

        // Dragged clear of the icon column (across the body) → unpin.
        float cross = _side switch
        {
            DockSide.Left => lx,
            DockSide.Right => _winW - lx,
            DockSide.Top => ly,
            _ => _winH - ly,
        };
        if (MathF.Abs(cross - _colCenterCross) > _slabCrossLen * 0.85f)
        {
            UnpinPinned(s.Entry);
            return;
        }

        // Otherwise reorder to the slot nearest the drop's main-axis position.
        float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
        int tgt = (int)Math.Round((main - _pinnedAreaMain - _cellMain / 2.0) / _cellMain);
        tgt = Math.Clamp(tgt, 0, _config.SideDockApps.Count - 1);
        int src = idx;   // pinned slots are laid out in SideDockApps order
        if (src >= 0 && tgt != src && src < _config.Apps.Count && tgt < _config.Apps.Count)
        {
            var e = _config.Apps[src];
            _config.Apps.RemoveAt(src);
            _config.Apps.Insert(tgt, e);
            PersistAndRebuild();
        }
        else
        {
            Render();
        }
    }

    /// <summary>Pins desktop shortcuts / executables dropped onto the dock from
    /// outside (Explorer / desktop), mirroring the WPF dock's OnDrop: each becomes
    /// a resident app inserted at the pointer's main-axis position.</summary>
    private void HandleDropFiles(List<string> paths, POINT clientPt)
    {
        var entries = new List<AppEntry>();
        foreach (var p in paths)
        {
            try { var e = ShortcutResolver.CreateEntry(p); if (e != null) entries.Add(e); }
            catch { /* skip an unresolvable drop */ }
        }
        if (entries.Count == 0)
            return;

        // DragQueryPoint gives client (physical) coords; convert to window-local DIP.
        float lx = (float)(clientPt.X / _dpi), ly = (float)(clientPt.Y / _dpi);
        float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
        int dropIdx = (int)Math.Round((main - _pinnedAreaMain) / _cellMain);
        dropIdx = Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config));

        bool changed = false;
        foreach (var entry in entries)
        {
            if (_config.Apps.FindIndex(a => DockSync.Matches(a, entry)) >= 0)
                continue;   // already present — don't duplicate
            DockSync.InsertResident(_config, entry, dropIdx);
            dropIdx++;
            changed = true;
        }
        if (changed)
            PersistAndRebuild();
    }

    private void UnpinPinned(AppEntry entry)
    {
        int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
        if (idx >= 0)
        {
            int resident = Math.Min(DockSync.ResidentCount(_config), _config.Apps.Count);
            bool wasResident = idx < resident;
            _config.Apps.RemoveAt(idx);
            if (wasResident)
                _config.Settings.Ring0Count = Math.Max(0, resident - 1);
        }
        PersistAndRebuild();
    }

    /// <summary>Mirrors the resident region into the side-dock list, saves the config
    /// and rebuilds the GPU dock. (The WPF dock picks the change up on next launch —
    /// the spike isn't wired into the host's live-sync callbacks.)</summary>
    private void PersistAndRebuild()
    {
        try
        {
            DockSync.MirrorResidentToLeft(_config);
            ConfigStore.Save(_config);
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "persist failed: " + ex.Message); }
        Rebuild();
    }

    private void Rebuild()
    {
        _timer?.Stop();
        if (_hwnd != IntPtr.Zero) s_instances.Remove(_hwnd);
        _host?.Dispose(); _host = null;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        _hover = -1; _pressIdx = -1; _dragging = false;
        try { Build(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "rebuild failed: " + ex); }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    /// <summary>Device pixels per DIP for the active monitor. Computed from the
    /// physical mode (EnumDisplaySettings, which is independent of the caller's DPI
    /// awareness) over the WPF DIP width — reliable even before the GPU window is
    /// realized, unlike GetDpiForWindow/GetDpiForMonitor which can race to 96.</summary>
    private static double DpiScale()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0)
            {
                double dipW = System.Windows.SystemParameters.PrimaryScreenWidth;
                if (dipW > 0)
                {
                    double s = dm.dmPelsWidth / dipW;
                    if (s >= 0.5 && s <= 4.0) return s;
                }
            }
        }
        catch { /* fall through to 1.0 */ }
        return 1.0;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2,
            dmPanningWidth, dmPanningHeight;
    }


    private static (float x, float y) ToLocal(DockSide side, double main, double cross, int winW, int winH) => side switch
    {
        DockSide.Left => ((float)cross, (float)main),
        DockSide.Right => ((float)(winW - cross), (float)main),
        DockSide.Top => ((float)main, (float)cross),
        _ => ((float)main, (float)(winH - cross)),
    };

    // ---- Raw Win32 NOREDIRECTIONBITMAP window --------------------------------

    private static readonly WndProc s_wndProc = WndProcImpl;
    private static ushort s_atom;

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (s_instances.TryGetValue(h, out var inst) && inst.HandleMessage(m, w, l, out IntPtr r))
            return r;
        return DefWindowProcW(h, m, w, l);
    }

    private static IntPtr CreateWindow(int w, int h)
    {
        if (s_atom == 0)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "PolarisSideDockGpu",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        // Interactive (Stage E): NO WS_EX_TRANSPARENT — the window receives mouse
        // input. WM_NCHITTEST returns HTTRANSPARENT outside the glass slab so clicks
        // on the empty reserved area still pass through. WS_EX_NOACTIVATE keeps it
        // from stealing focus on click.
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST |
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "PolarisSideDockGpu", string.Empty, WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
}
