using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;

namespace Polaris.Services.Gpu;

/// <summary>GPU-rendering spike benchmark (POLARIS_GPU_BENCH=gpu|wpf). Animates the
/// SAME moving radial-gradient blob in a large (~main-dock-sized) per-pixel-alpha
/// window at the display refresh, driven by the same <c>CompositionTarget.Rendering</c>
/// clock, but composited two different ways:
/// <list type="bullet">
/// <item><b>wpf</b> — a WPF <c>AllowsTransparency</c> layered window (Tier-0 software
/// composite + per-frame full-surface <c>UpdateLayeredWindow</c> upload).</item>
/// <item><b>gpu</b> — DirectComposition + Direct2D (DWM composites on the GPU).</item>
/// </list>
/// It samples this process's CPU over a fixed run and appends the result to
/// <c>gpu-bench.csv</c>, so the two modes can be compared apples-to-apples (the
/// compositing cost difference scales with window AREA, which the tiny drag ghost
/// could not show).</summary>
internal static class GpuBenchmark
{
    private const int RunSeconds = 6;

    public static void Run(string mode)
    {
        var wa = SystemParameters.WorkArea;
        int w = (int)Math.Min(1440, wa.Width);
        int h = (int)Math.Min(900, wa.Height);

        if (mode.Equals("gpu", StringComparison.OrdinalIgnoreCase))
            RunGpu(w, h);
        else
            RunWpf(w, h);
    }

    private static void Finish(string mode, double cpuPercent)
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        double wsMb = proc.WorkingSet64 / 1024.0 / 1024.0;
        string line = $"{mode},{cpuPercent:F1},{wsMb:F0}";
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gpu-bench.csv"), line + Environment.NewLine);
        Application.Current.Shutdown();
    }

    // ---- WPF AllowsTransparency path -----------------------------------------

    private static void RunWpf(int w, int h)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            Left = 40, Top = 40, Width = w, Height = h,
        };
        var canvas = new Canvas();
        double r = 140;
        var blob = new System.Windows.Shapes.Ellipse
        {
            Width = r * 2, Height = r * 2,
            Fill = new RadialGradientBrush(
                System.Windows.Media.Color.FromArgb(0xC0, 0x6E, 0xB6, 0xFF),
                System.Windows.Media.Color.FromArgb(0x00, 0x3A, 0x86, 0xE0)),
        };
        canvas.Children.Add(blob);
        win.Content = canvas;
        win.Show();

        var sw = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        EventHandler tick = null!;
        tick = (_, _) =>
        {
            double t = sw.Elapsed.TotalSeconds;
            double x = (w - r * 2) * (0.5 + 0.5 * Math.Sin(t * 2.0));
            double y = (h - r * 2) * (0.5 + 0.5 * Math.Cos(t * 1.7));
            Canvas.SetLeft(blob, x);
            Canvas.SetTop(blob, y);
            if (t >= RunSeconds)
            {
                CompositionTarget.Rendering -= tick;
                proc.Refresh();
                double ms = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
                Finish("wpf", ms / sw.Elapsed.TotalMilliseconds * 100.0);
            }
        };
        CompositionTarget.Rendering += tick;
    }

    // ---- GPU DirectComposition + Direct2D path -------------------------------

    private static void RunGpu(int w, int h)
    {
        IntPtr hwnd = CreateBenchWindow(w, h);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        var host = new CompositionHost(hwnd, w, h);

        ID2D1RadialGradientBrush brush;
        using (var stops = host.Context.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f, Color = new Color4(0x6E/255f, 0xB6/255f, 1f, 0xC0/255f) },
            new Vortice.Direct2D1.GradientStop { Position = 1f, Color = new Color4(0x3A/255f, 0x86/255f, 0xE0/255f, 0f) },
        }))
        {
            brush = host.Context.CreateRadialGradientBrush(
                new RadialGradientBrushProperties
                {
                    Center = new Vector2(0, 0),
                    GradientOriginOffset = new Vector2(0, 0),
                    RadiusX = 140f,
                    RadiusY = 140f,
                },
                stops);
        }

        var sw = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        float r = 140f;
        EventHandler tick = null!;
        tick = (_, _) =>
        {
            double t = sw.Elapsed.TotalSeconds;
            float x = (float)((w - r * 2) * (0.5 + 0.5 * Math.Sin(t * 2.0))) + r;
            float y = (float)((h - r * 2) * (0.5 + 0.5 * Math.Cos(t * 1.7))) + r;
            host.Render(ctx =>
            {
                ctx.Transform = Matrix3x2.CreateTranslation(x, y);
                ctx.FillEllipse(new Ellipse(new Vector2(0, 0), r, r), brush);
                ctx.Transform = Matrix3x2.Identity;
            });
            if (t >= RunSeconds)
            {
                CompositionTarget.Rendering -= tick;
                proc.Refresh();
                double ms = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
                brush.Dispose();
                host.Dispose();
                DestroyWindow(hwnd);
                Finish("gpu", ms / sw.Elapsed.TotalMilliseconds * 100.0);
            }
        };
        CompositionTarget.Rendering += tick;
    }

    // ---- Raw Win32 NOREDIRECTIONBITMAP window for the GPU path ----------------

    private static readonly WndProc s_wndProc = DefWindowProcW;
    private static ushort s_atom;

    private static IntPtr CreateBenchWindow(int w, int h)
    {
        if (s_atom == 0)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "PolarisGpuBench",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            "PolarisGpuBench", string.Empty, WS_POPUP,
            40, 40, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4;

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
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
}
