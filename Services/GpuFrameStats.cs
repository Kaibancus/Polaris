using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Polaris.Services;

/// <summary>Opt-in GPU-dock frame meter (POLARIS_GPUFPS=1). Each dock calls
/// <see cref="Frame"/> after it Presents; once a second a row per dock is written with the
/// realised FPS, the worst inter-frame gap (stutter) and process memory. Used to validate
/// the render-loop frame rate + memory across scenarios. No-op when disabled.</summary>
public static class GpuFrameStats
{
    private sealed class Acc { public int Frames; public long Last; public double Worst; public bool Seen; }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Acc> _acc = new();
    private static readonly Stopwatch _wall = new();
    private static string? _csv;
    private static System.Threading.Timer? _timer;
    private static bool _en;

    public static void StartIfRequested()
    {
        if (_en) return;
        if (!string.Equals(Environment.GetEnvironmentVariable("POLARIS_GPUFPS"), "1", StringComparison.Ordinal)) return;
        _en = true;
        _csv = Path.Combine(AppContext.BaseDirectory, "gpufps-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".csv");
        try { File.WriteAllText(_csv, "elapsed_s,dock,fps,worst_gap_ms,ws_mb,priv_mb,gc_mb,gen2\n"); } catch { }
        _wall.Restart();
        _timer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    public static void Frame(string dock)
    {
        if (!_en) return;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_acc.TryGetValue(dock, out var a)) { a = new Acc { Last = now, Seen = true }; _acc[dock] = a; }
            a.Frames++; a.Seen = true;
            double gap = now - a.Last;
            if (a.Last != 0 && gap > a.Worst && gap < 1000.0) a.Worst = gap;
            a.Last = now;
        }
    }

    private static void Flush()
    {
        if (_csv == null) return;
        try
        {
            using var p = Process.GetCurrentProcess();
            double ws = p.WorkingSet64 / 1048576.0, pv = p.PrivateMemorySize64 / 1048576.0, el = _wall.Elapsed.TotalSeconds;
            double gc = GC.GetTotalMemory(false) / 1048576.0;
            int g2 = GC.CollectionCount(2);
            string tail = ws.ToString("0", CultureInfo.InvariantCulture) + "," + pv.ToString("0", CultureInfo.InvariantCulture)
                          + "," + gc.ToString("0", CultureInfo.InvariantCulture) + "," + g2.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            lock (_gate)
            {
                if (_acc.Count == 0)
                    sb.Append(el.ToString("0.0", CultureInfo.InvariantCulture)).Append(",idle,0,0,").Append(tail).Append('\n');
                else
                    foreach (var kv in _acc)
                    {
                        sb.Append(el.ToString("0.0", CultureInfo.InvariantCulture)).Append(',').Append(kv.Key).Append(',')
                          .Append(kv.Value.Frames).Append(',').Append(kv.Value.Worst.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                          .Append(tail).Append('\n');
                        kv.Value.Frames = 0; kv.Value.Worst = 0;
                        if (!kv.Value.Seen) kv.Value.Last = 0;
                        kv.Value.Seen = false;
                    }
            }
            File.AppendAllText(_csv, sb.ToString());
        }
        catch { }
    }
}
