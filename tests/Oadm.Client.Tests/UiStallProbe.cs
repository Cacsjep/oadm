using System.Diagnostics;

using Avalonia.Headless;
using Avalonia.Threading;

namespace Oadm.Client.Tests;

/// <summary>
/// Measures how long the UI thread cannot run a frame: a background thread posts one job at Render priority, waits until it
/// ran and records how long it waited, then posts the next after 5 ms. Every 16 ms it also lets the headless renderer draw a
/// frame (<see cref="AvaloniaHeadlessPlatform.ForceRenderTimerTick"/>), so the splash animation and the layout really run
/// and their cost counts. The longest wait is the longest stall a frame of the splash would see.
/// </summary>
internal sealed class UiStallProbe : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly List<(TimeSpan At, TimeSpan Wait)> _samples = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public UiStallProbe()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "UI stall probe" };
        _thread.Start();
    }

    /// <summary>Time since the probe started (phase marks).</summary>
    public TimeSpan Now => _clock.Elapsed;

    /// <summary>The longest wait of a Render job between <paramref name="from"/> and <paramref name="to"/> (by when it was posted).</summary>
    public TimeSpan LongestStall(TimeSpan from, TimeSpan to)
    {
        lock (_gate)
        {
            return _samples.Where(s => s.At >= from && s.At < to).Select(s => s.Wait).DefaultIfEmpty().Max();
        }
    }

    /// <summary>Stalls longer than <paramref name="threshold"/> in that span, oldest first: "at 1.234 s: 85 ms".</summary>
    public IReadOnlyList<string> Stalls(TimeSpan from, TimeSpan to, TimeSpan threshold)
    {
        lock (_gate)
        {
            return _samples.Where(s => s.At >= from && s.At < to && s.Wait > threshold)
                .Select(s => $"at {s.At.TotalSeconds:F3} s: {s.Wait.TotalMilliseconds:F0} ms").ToList();
        }
    }

    private void Loop()
    {
        TimeSpan lastFrame = TimeSpan.Zero;
        using var ran = new SemaphoreSlim(0);
        while (!_cts.IsCancellationRequested)
        {
            TimeSpan posted = _clock.Elapsed;
            bool frame = posted - lastFrame >= TimeSpan.FromMilliseconds(16);
            if (frame)
            {
                lastFrame = posted;
            }

            Dispatcher.UIThread.Post(() =>
            {
                TimeSpan wait = _clock.Elapsed - posted;
                lock (_gate)
                {
                    _samples.Add((posted, wait));
                }

                if (frame)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                try
                {
                    ran.Release();
                }
                catch (ObjectDisposedException)
                {
                    // the probe ended while this job waited
                }
            }, DispatcherPriority.Render);
            try
            {
                ran.Wait(_cts.Token);
                Thread.Sleep(5);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}
