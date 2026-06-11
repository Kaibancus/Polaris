using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace Polaris.Services;

/// <summary>
/// Lightweight, opt-in frame-rate profiler. It measures the real per-frame
/// interval via <see cref="CompositionTarget.Rendering"/> and attributes each
/// frame to the currently-active animation "scene" (a stack, so the most
/// recently begun animation owns the frames). A background thread writes a CSV
/// sample stream and a live per-scene summary so an operator can exercise each
/// animation and read average / minimum FPS while the app runs.
///
/// Enabled only when the environment variable <c>POLARIS_FPS=1</c> is set, so
/// normal runs pay nothing. All <see cref="Push"/>/<see cref="Pop"/> calls are
/// cheap no-ops when disabled.
///
/// NOTE: attaching a Rendering handler keeps WPF's render loop ticking every
/// frame even when idle, so "Idle" frames are captured too — that is intended
/// for a profiling session.
/// </summary>
public static class FpsProfiler
{
    private sealed class Acc
    {
        public long Frames;
        public double Seconds;
        public double WorstFrameMs;
    }

    private static readonly object _gate = new();
    private static readonly List<string> _stack = new();
    private static readonly Dictionary<string, Acc> _acc = new();
    private static readonly ConcurrentQueue<string> _csvQueue = new();
    private static readonly Stopwatch _wall = new();

    private static TimeSpan _lastRender = TimeSpan.MinValue;
    private static double _sampleSeconds;
    private static long _sampleFrames;
    private static double _sampleWorstMs;

    private static string? _csvPath;
    private static string? _summaryPath;
    private static System.Threading.Timer? _flushTimer;
    private static bool _enabled;

    public static bool Enabled => _enabled;

    private static string CurrentScene
    {
        get { lock (_gate) return _stack.Count > 0 ? _stack[^1] : "Idle"; }
    }

    /// <summary>Starts profiling if <c>POLARIS_FPS=1</c>. Safe to call once.</summary>
    public static void StartIfRequested()
    {
        if (_enabled)
            return;
        if (!string.Equals(Environment.GetEnvironmentVariable("POLARIS_FPS"), "1", StringComparison.Ordinal))
            return;

        _enabled = true;
        string dir = AppContext.BaseDirectory;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _csvPath = Path.Combine(dir, $"fps-samples-{stamp}.csv");
        _summaryPath = Path.Combine(dir, $"fps-summary-{stamp}.txt");
        try { File.WriteAllText(_csvPath, "elapsed_s,scene,fps,worst_frame_ms\n"); } catch { /* best effort */ }

        _wall.Restart();
        CompositionTarget.Rendering += OnRendering;
        // Flush CSV + rewrite the summary off the UI thread every second so file
        // I/O never perturbs the very frame timings we are measuring.
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    public static void Stop()
    {
        if (!_enabled)
            return;
        _enabled = false;
        CompositionTarget.Rendering -= OnRendering;
        _flushTimer?.Dispose();
        _flushTimer = null;
        Flush();
    }

    /// <summary>Marks the start of an animation scene (becomes the active owner
    /// of subsequent frames until <see cref="Pop"/>).</summary>
    public static void Push(string scene)
    {
        if (!_enabled)
            return;
        lock (_gate)
            _stack.Add(scene);
    }

    /// <summary>Marks the end of a scene (removes the most recent matching entry
    /// so nested/overlapping scenes unwind correctly).</summary>
    public static void Pop(string scene)
    {
        if (!_enabled)
            return;
        lock (_gate)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i] == scene)
                {
                    _stack.RemoveAt(i);
                    break;
                }
            }
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re)
            return;
        TimeSpan t = re.RenderingTime;
        if (_lastRender == TimeSpan.MinValue)
        {
            _lastRender = t;
            return;
        }
        if (t == _lastRender)
            return;                       // WPF fires the same time twice sometimes
        double dt = (t - _lastRender).TotalSeconds;
        _lastRender = t;
        if (dt <= 0 || dt > 1.0)
            return;                       // ignore pauses / outliers

        string scene = CurrentScene;
        double ms = dt * 1000.0;
        lock (_gate)
        {
            if (!_acc.TryGetValue(scene, out var a))
            {
                a = new Acc();
                _acc[scene] = a;
            }
            a.Frames++;
            a.Seconds += dt;
            if (ms > a.WorstFrameMs)
                a.WorstFrameMs = ms;
        }

        _sampleSeconds += dt;
        _sampleFrames++;
        if (ms > _sampleWorstMs)
            _sampleWorstMs = ms;
        if (_sampleSeconds >= 0.5)
        {
            double fps = _sampleFrames / _sampleSeconds;
            _csvQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:0.000},{1},{2:0.0},{3:0.0}",
                _wall.Elapsed.TotalSeconds, scene, fps, _sampleWorstMs));
            _sampleSeconds = 0;
            _sampleFrames = 0;
            _sampleWorstMs = 0;
        }
    }

    /// <summary>Drains buffered CSV lines and rewrites the live summary. Runs on
    /// a background thread (Timer callback) to keep file I/O off the UI thread.</summary>
    private static void Flush()
    {
        try
        {
            if (_csvPath != null && !_csvQueue.IsEmpty)
            {
                var sb = new StringBuilder();
                while (_csvQueue.TryDequeue(out var line))
                    sb.Append(line).Append('\n');
                File.AppendAllText(_csvPath, sb.ToString());
            }
        }
        catch { /* best effort */ }

        try
        {
            if (_summaryPath == null)
                return;
            (string scene, Acc a)[] snapshot;
            lock (_gate)
                snapshot = _acc.Select(kv => (kv.Key, new Acc
                {
                    Frames = kv.Value.Frames,
                    Seconds = kv.Value.Seconds,
                    WorstFrameMs = kv.Value.WorstFrameMs,
                })).ToArray();

            var sb = new StringBuilder();
            sb.Append("Scene".PadRight(16))
              .Append("Frames".PadLeft(9))
              .Append("Secs".PadLeft(9))
              .Append("AvgFPS".PadLeft(9))
              .Append("MinFPS".PadLeft(9))
              .Append("WorstMs".PadLeft(10))
              .Append("   <60?\n");
            foreach (var (scene, a) in snapshot.OrderByDescending(x => x.a.Seconds))
            {
                double avg = a.Seconds > 0 ? a.Frames / a.Seconds : 0;
                double min = a.WorstFrameMs > 0 ? 1000.0 / a.WorstFrameMs : 0;
                string flag = avg < 60 ? "  *** LOW" : "";
                sb.Append(scene.PadRight(16))
                  .Append(a.Frames.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                  .Append(a.Seconds.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9))
                  .Append(avg.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(9))
                  .Append(min.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(9))
                  .Append(a.WorstFrameMs.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(10))
                  .Append(flag).Append('\n');
            }
            File.WriteAllText(_summaryPath, sb.ToString());
        }
        catch { /* best effort */ }
    }
}
