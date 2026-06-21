using System;
using System.Collections.Concurrent;
using System.Threading;
using Polaris.Services;

namespace Polaris.Services.Gpu;

/// <summary>
/// Dedicated render thread for a GPU dock (independent-render-thread path, behind
/// <c>POLARIS_GPU_RENDERTHREAD=1</c>). The thread owns the dock's
/// <see cref="CompositionHost"/> and all Direct3D/Direct2D/DirectComposition work,
/// and paces itself to the display refresh rate via the swap chain's frame-latency
/// waitable object (see <see cref="CompositionHost.WaitForVBlank"/>).
///
/// <para><b>Why:</b> the default path drives rendering from
/// <c>CompositionTarget.Rendering</c> on the UI thread, which the OS caps near
/// ~38–53fps on this hardware and which competes with input on the message pump
/// (severe lag on 4K@144). Moving the render+animation loop to its own thread paced
/// by the compositor vblank reaches the true refresh rate (60/120/144Hz…) and frees
/// the UI thread for input.</para>
///
/// <para><b>Threading contract:</b> the device (D3D11/D2D single-threaded factory +
/// DirectComposition, which have thread affinity) is created and used ONLY on this
/// thread. The owning dock must therefore create/resize/dispose its CompositionHost
/// and run its Tick/Render via <see cref="Post"/> (commands) and the frame callback —
/// never touch the host from the UI thread. Win32 HWND ops, WPF popups and config
/// stay on the UI thread.</para>
/// </summary>
internal sealed class RenderLoop
{
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Action _frame;        // render-thread: wait-for-vblank + tick + render + present
    private readonly string _name;
    private volatile bool _running = true;
    private volatile bool _active;

    /// <param name="name">Thread name (for debugging / logs).</param>
    /// <param name="frame">Invoked once per iteration ON THE RENDER THREAD while the
    /// loop is active; the dock should pace (WaitForVBlank), advance animation and
    /// render+present here. Must not throw fatally — exceptions are logged and the loop
    /// continues.</param>
    public RenderLoop(string name, Action frame)
    {
        _name = name;
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
            // Render cadence matters more than background work; nudge above normal so a
            // busy UI thread can't starve frame pacing.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>True when the caller is already executing on the render thread, so it
    /// can run host/device work inline instead of re-posting (and avoid deadlocking on
    /// itself).</summary>
    public bool OnRenderThread => Thread.CurrentThread == _thread;

    /// <summary>Queues <paramref name="action"/> to run on the render thread before the
    /// next frame (host create/resize/dispose, show/hide, input application). Runs
    /// inline if already on the render thread. Commands run in FIFO order.</summary>
    public void Post(Action action)
    {
        if (action == null) return;
        if (OnRenderThread) { RunCommand(action); return; }
        _commands.Enqueue(action);
        _wake.Set();
    }

    /// <summary>Runs <paramref name="action"/> on the render thread and blocks until it
    /// completes (or <paramref name="timeoutMs"/> elapses). Used for ordering-critical
    /// device work the UI thread must not race — chiefly disposing the CompositionHost
    /// before the HWND is destroyed on rebuild/teardown. Runs inline when already on the
    /// render thread (so it can't deadlock on itself); returns immediately if the loop
    /// has stopped.</summary>
    public void Invoke(Action action, int timeoutMs = 2000)
    {
        if (action == null) return;
        if (OnRenderThread || !_running) { RunCommand(action); return; }
        using var done = new ManualResetEventSlim(false);
        _commands.Enqueue(() => { try { action(); } finally { done.Set(); } });
        _wake.Set();
        try { done.Wait(timeoutMs); }
        catch { /* best-effort */ }
    }

    /// <summary>Marks the loop active (rendering every frame) or idle (blocked until the
    /// next command / re-activation). The dock sets this true while shown or animating
    /// and false once fully hidden + settled, so a hidden dock costs nothing.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active)
            _wake.Set();
    }

    public bool IsActive => _active;

    private void Run()
    {
        while (_running)
        {
            while (_commands.TryDequeue(out var cmd))
                RunCommand(cmd);

            if (!_running)
                break;

            if (!_active)
            {
                // Idle / hidden: block until a command arrives or we're re-activated.
                // The timeout is a defensive backstop against a missed signal.
                _wake.WaitOne(250);
                continue;
            }

            try { _frame(); }
            catch (Exception ex) { Log.Error("RenderLoop", _name + " frame", ex); }
        }
    }

    private void RunCommand(Action cmd)
    {
        try { cmd(); }
        catch (Exception ex) { Log.Error("RenderLoop", _name + " command", ex); }
    }

    /// <summary>Stops the loop and joins the thread. Safe to call from the UI thread;
    /// the caller is responsible for having the loop dispose the host first (post a
    /// dispose command before Stop, or dispose inside a final command).</summary>
    public void Stop()
    {
        _running = false;
        _active = false;
        _wake.Set();
        try
        {
            if (!OnRenderThread)
                _thread.Join(2000);
        }
        catch { /* best-effort */ }
        _wake.Dispose();
    }
}
