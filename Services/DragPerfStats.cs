using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Polaris.Services;

/// <summary>
/// Opt-in drag-specific diagnostics for the GPU docks (`POLARIS_GPUDRAG=1`).
/// Unlike <see cref="GpuFrameStats"/>, which samples whole-dock fps once a second,
/// this profiler aggregates one row PER DRAG GESTURE plus a lightweight event log
/// (rebuild / relayout / side-dock show paths) so cumulative drag degradation can
/// be pinned to a specific hot path.
/// </summary>
public static class DragPerfStats
{
    private sealed class Acc
    {
        public string Dock = string.Empty;
        public int Slots;
        public bool RenderThread;
        public bool GpuGhost;
        public long StartMs;
        public long LastFrameMs;
        public int Frames;
        public double WorstGapMs;
        public int LongGaps;
        public double ArrangeSumMs;
        public double ArrangeMaxMs;
        public double GhostMs;
        public string GhostMode = string.Empty;
    }

    private static readonly bool _en =
        string.Equals(Environment.GetEnvironmentVariable("POLARIS_GPUDRAG"), "1", StringComparison.Ordinal);
    private static readonly object _gate = new();
    private static readonly Dictionary<int, Acc> _acc = new();
    private static int _nextId;
    private static string? _csv;
    private static string? _events;

    private static void EnsureFiles()
    {
        if (!_en || _csv != null)
            return;
        string baseDir = AppContext.BaseDirectory;
        string stem = "dragdiag-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        _csv = Path.Combine(baseDir, stem + ".csv");
        _events = Path.Combine(baseDir, stem + ".log");
        try
        {
            File.WriteAllText(_csv,
                "ts,dock,session,slots,render_thread,gpu_ghost,drag_ms,frames,avg_fps,worst_gap_ms,long_gaps,avg_arrange_ms,max_arrange_ms,ghost_ms,ghost_mode,ws_mb,priv_mb,reason\n");
            File.WriteAllText(_events, "# ts,dock,session,event,message\n");
        }
        catch { }
    }

    public static int Begin(string dock, int slots, bool renderThread, bool gpuGhost)
    {
        if (!_en)
            return 0;
        EnsureFiles();
        lock (_gate)
        {
            int id = ++_nextId;
            _acc[id] = new Acc
            {
                Dock = dock,
                Slots = slots,
                RenderThread = renderThread,
                GpuGhost = gpuGhost,
                StartMs = Environment.TickCount64,
            };
            WriteEventNoLock(dock, id, "begin",
                $"slots={slots} renderThread={renderThread} gpuGhost={gpuGhost}");
            return id;
        }
    }

    public static void GhostCreated(int session, string mode, double ms)
    {
        if (!_en || session == 0)
            return;
        lock (_gate)
        {
            if (!_acc.TryGetValue(session, out var a))
                return;
            a.GhostMode = mode;
            a.GhostMs = ms;
            WriteEventNoLock(a.Dock, session, "ghost", $"{mode} {ms.ToString("0.00", CultureInfo.InvariantCulture)}ms");
        }
    }

    public static void Frame(int session, double arrangeMs)
    {
        if (!_en || session == 0)
            return;
        lock (_gate)
        {
            if (!_acc.TryGetValue(session, out var a))
                return;
            long now = Environment.TickCount64;
            if (a.LastFrameMs != 0)
            {
                double gap = now - a.LastFrameMs;
                if (gap < 1000.0)
                {
                    if (gap > a.WorstGapMs) a.WorstGapMs = gap;
                    if (gap >= 25.0) a.LongGaps++;
                }
            }
            a.LastFrameMs = now;
            a.Frames++;
            a.ArrangeSumMs += arrangeMs;
            if (arrangeMs > a.ArrangeMaxMs) a.ArrangeMaxMs = arrangeMs;
        }
    }

    public static void Event(string dock, int session, string evt, string message)
    {
        if (!_en)
            return;
        lock (_gate)
            WriteEventNoLock(dock, session, evt, message);
    }

    public static void End(int session, string reason)
    {
        if (!_en || session == 0)
            return;
        EnsureFiles();
        lock (_gate)
        {
            if (!_acc.TryGetValue(session, out var a))
                return;
            _acc.Remove(session);
            long now = Environment.TickCount64;
            double dragMs = Math.Max(1.0, now - a.StartMs);
            double fps = a.Frames * 1000.0 / dragMs;
            double avgArrange = a.Frames > 0 ? a.ArrangeSumMs / a.Frames : 0.0;
            using var p = Process.GetCurrentProcess();
            string line = string.Join(",",
                DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                a.Dock,
                session.ToString(CultureInfo.InvariantCulture),
                a.Slots.ToString(CultureInfo.InvariantCulture),
                a.RenderThread ? "1" : "0",
                a.GpuGhost ? "1" : "0",
                dragMs.ToString("0.0", CultureInfo.InvariantCulture),
                a.Frames.ToString(CultureInfo.InvariantCulture),
                fps.ToString("0.0", CultureInfo.InvariantCulture),
                a.WorstGapMs.ToString("0.0", CultureInfo.InvariantCulture),
                a.LongGaps.ToString(CultureInfo.InvariantCulture),
                avgArrange.ToString("0.000", CultureInfo.InvariantCulture),
                a.ArrangeMaxMs.ToString("0.000", CultureInfo.InvariantCulture),
                a.GhostMs.ToString("0.000", CultureInfo.InvariantCulture),
                a.GhostMode,
                (p.WorkingSet64 / 1048576.0).ToString("0", CultureInfo.InvariantCulture),
                (p.PrivateMemorySize64 / 1048576.0).ToString("0", CultureInfo.InvariantCulture),
                reason);
            try { File.AppendAllText(_csv!, line + "\n", Encoding.UTF8); } catch { }
            WriteEventNoLock(a.Dock, session, "end",
                $"reason={reason} dragMs={dragMs.ToString("0.0", CultureInfo.InvariantCulture)} fps={fps.ToString("0.0", CultureInfo.InvariantCulture)} worst={a.WorstGapMs.ToString("0.0", CultureInfo.InvariantCulture)} arrangeMax={a.ArrangeMaxMs.ToString("0.000", CultureInfo.InvariantCulture)}");
        }
    }

    private static void WriteEventNoLock(string dock, int session, string evt, string message)
    {
        try
        {
            if (_events == null)
                return;
            string line = string.Join(",",
                DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                dock,
                session.ToString(CultureInfo.InvariantCulture),
                evt,
                Escape(message));
            File.AppendAllText(_events, line + "\n", Encoding.UTF8);
        }
        catch { }
    }

    private static string Escape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return s;
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\"\"");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
