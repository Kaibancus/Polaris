using System;
using System.Windows.Media;

namespace Polaris.Services.Gpu;

/// <summary>Vsync-aligned render clock: drives the GPU docks via CompositionTarget.Rendering at the
/// DISPLAY refresh rate (drop-in for the DispatcherTimer the docks used — same Start/Stop/Tick). A
/// fixed 16 ms DispatcherTimer aliases against the ~15.6 ms default OS timer resolution and actually
/// fires only every ~31 ms (~32 fps), capping the docks well under 60 even though each frame draws in
/// ~4 ms. CompositionTarget.Rendering is paced by the compositor vblank, so the docks hit a true
/// per-refresh frame rate and automatically exceed 60 fps on high-refresh displays. The docks' own
/// render gate still skips GPU work when the scene is settled, so an idle-but-shown dock pays only the
/// cheap tick.</summary>
internal sealed class FrameClock
{
    public event Action? Tick;
    private bool _running;
    private TimeSpan _last = TimeSpan.MinValue;
    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _last) return;   // WPF can fire twice with the same timestamp
            _last = re.RenderingTime;
        }
        Tick?.Invoke();
    }
}