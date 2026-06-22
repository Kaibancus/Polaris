using System;
using System.Runtime;
using System.Threading;

namespace Polaris.Services.Gpu;

/// <summary>Process-wide GC latency coordinator for the GPU docks. While any dock is
/// actively rendering, the GC is switched to <see cref="GCLatencyMode.SustainedLowLatency"/>
/// so it DEFERS the blocking gen2 collections that otherwise froze a frame for ~200-330ms
/// mid-animation. Diagnosis (POLARIS_GPUFPS): the GC heap stays tiny (2-17MB) yet ~26 gen2
/// collections fired in 80s of interaction, and EVERY worst-gap spike (203-328ms) landed
/// exactly on a gen2 collection — the per-frame brush/gradient churn kept promoting enough
/// to trigger frequent blocking gen2 GCs.
///
/// Reference-counted: the previous latency mode is restored once every dock has gone idle,
/// so a hidden tray app still reclaims memory normally. Cheap ephemeral (gen0/gen1) GCs are
/// NOT suppressed, so memory stays bounded across a (bounded) active dock session.</summary>
internal static class RenderGcScope
{
    private static int _active;
    private static GCLatencyMode _saved = GCLatencyMode.Interactive;

    /// <summary>Marks one dock as actively rendering; the first entrant switches the GC to
    /// SustainedLowLatency.</summary>
    public static void Enter()
    {
        if (Interlocked.Increment(ref _active) == 1)
        {
            try
            {
                _saved = GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            catch { /* latency mode is best-effort */ }
        }
    }

    /// <summary>Marks one dock as idle; the last leaver restores the previous latency mode so
    /// the deferred gen2 collections can run while nothing is animating.</summary>
    public static void Leave()
    {
        if (Interlocked.Decrement(ref _active) <= 0)
        {
            // Clamp at zero so an unbalanced extra Leave can't drive the count negative.
            if (Volatile.Read(ref _active) < 0)
                Interlocked.Exchange(ref _active, 0);
            try { GCSettings.LatencyMode = _saved; }
            catch { }
        }
    }
}
