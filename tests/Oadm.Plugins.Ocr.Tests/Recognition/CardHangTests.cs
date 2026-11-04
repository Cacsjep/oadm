using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Recognition;
using Oadm.Sdk.Testing;

using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// A graphics card that hangs (docs/spec/ocr.md "GPU"): a reading waits at most 2 minutes and for its token, its queued jobs
/// leave, a job it left is never touched again, the card then counts as failed and the reading goes to the CPU. Fake sessions
/// held at a gate, a fake clock, no graphics card.
/// </summary>
public sealed class CardHangTests
{
    /// <summary>A model whose calls wait at the gate.</summary>
    private sealed class HeldSessions : ICardSessions
    {
        public SemaphoreSlim Gate { get; } = new(0);

        public ConcurrentQueue<long[]> Calls { get; } = new();

        public int Disposed { get; private set; }

        public T Run<T>(float[] input, long[] shape, OutputReader<T> read)
        {
            Calls.Enqueue(shape);
            Gate.Wait();
            var frames = 40;
            var classes = 4;
            return read(new float[shape[0] * frames * classes * 20], [shape[0], frames, classes]);
        }

        public void Dispose() => Disposed++;
    }

    private sealed class Rig : IDisposable
    {
        private readonly List<SKBitmap> _lines = [];

        public FakeTimeProvider Time { get; } = new();

        public HeldSessions Finder { get; } = new();

        public HeldSessions Search { get; } = new();

        public HeldSessions Reader { get; } = new();

        public HeldSessions Angle { get; } = new();

        public int TooLarge { get; private set; }

        public CardModels Card { get; }

        public Rig() => Card = new CardModels(Finder, Search, Reader, Angle, ExecutionTarget.Cuda, new BusyMeter(Time), new BusyMeter(Time), null, Time, () => TooLarge++) { JoinLimit = TimeSpan.Zero };

        public SKBitmap[] Lines(int count, int width = 120)
        {
            var made = Enumerable.Range(0, count).Select(_ => new SKBitmap(width, 48, SKColorType.Rgba8888, SKAlphaType.Premul)).ToArray();
            _lines.AddRange(made);
            return made;
        }

        public void Dispose()
        {
            Finder.Gate.Release(100);
            Search.Gate.Release(100);
            Reader.Gate.Release(100);
            Angle.Gate.Release(100);
            Card.Dispose();
            foreach (var line in _lines)
            {
                line.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_hung_reader_call_ends_at_the_limit_on_the_cpu_and_leaves_the_card_for_good()
    {
        using var rig = new Rig();
        var told = new List<string>();
        var lines = rig.Lines(2);
        LineScores[] onCpu = [new LineScores(new float[4], 1, 4), new LineScores(new float[4], 1, 4)];

        var reading = CardRunner.Guarded(() => rig.Card.ReadAsync(lines, CancellationToken.None), () => Task.FromResult(onCpu), rig.Card, told.Add, CancellationToken.None);
        await Eventually.TrueAsync(() => !rig.Reader.Calls.IsEmpty, "the call hangs");

        rig.Time.Advance(CardModels.CallLimit - TimeSpan.FromSeconds(1));
        Assert.False(reading.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(onCpu, await reading.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System));
        Assert.True(rig.Card.Failed);
        Assert.Equal([CardModels.TimedOut], told); // one log line

        // The next reading never asks the card.
        Assert.Same(onCpu, await CardRunner.Guarded(() => throw new InvalidOperationException("never"), () => Task.FromResult(onCpu), rig.Card, told.Add, CancellationToken.None));
    }

    [Fact]
    public async Task A_hung_call_ends_for_a_cancelled_reading_at_once_and_the_card_stays_in_use()
    {
        using var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var reading = rig.Card.ReadAsync(rig.Lines(1), stop.Token);
        await Eventually.TrueAsync(() => !rig.Reader.Calls.IsEmpty, "the call hangs");

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System));
        Assert.False(rig.Card.Failed);
    }

    [Fact]
    public async Task A_reading_that_leaves_takes_its_queued_jobs_out_and_the_lane_never_touches_them()
    {
        using var rig = new Rig();
        var first = rig.Card.ReadAsync(rig.Lines(1), CancellationToken.None);
        await Eventually.TrueAsync(() => !rig.Reader.Calls.IsEmpty, "the call hangs");
        using var stop = new CancellationTokenSource();

        // A second reading waits behind the hung call; its lines go with it when it leaves.
        using (var line = new SKBitmap(120, 48, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            var second = rig.Card.ReadAsync([line], stop.Token);
            Assert.Equal(1, rig.Card.Queued);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(0, rig.Card.Queued);
        }

        rig.Reader.Gate.Release(10);
        Assert.Single(await first.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System));
        Assert.Single(rig.Reader.Calls); // no call for the second reading
    }

    [Fact]
    public async Task A_hung_call_never_reads_a_line_after_its_reading_left()
    {
        using var rig = new Rig();
        using var stop = new CancellationTokenSource();

        // Disposed as the reading's end does: the output reader takes the width from the job, never from the bitmap.
        var line = new SKBitmap(120, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
        var reading = rig.Card.ReadAsync([line], stop.Token);
        await Eventually.TrueAsync(() => !rig.Reader.Calls.IsEmpty, "the call hangs");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        line.Dispose();

        rig.Reader.Gate.Release();
        await Eventually.TrueAsync(() => rig.Reader.Gate.CurrentCount == 0, "the call ended");

        // The lane lives on: the next reading is read.
        rig.Reader.Gate.Release();
        Assert.Single(await rig.Card.ReadAsync(rig.Lines(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System));
    }

    [Fact]
    public async Task A_failed_card_sends_what_waits_to_the_cpu_at_once()
    {
        using var rig = new Rig();
        var hung = rig.Card.ReadAsync(rig.Lines(1), CancellationToken.None);
        await Eventually.TrueAsync(() => !rig.Reader.Calls.IsEmpty, "the call hangs");
        var waiting = rig.Card.ReadAsync(rig.Lines(1), CancellationToken.None);

        Assert.True(rig.Card.MarkFailed());

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System));
        Assert.Equal(0, rig.Card.Queued);
        Assert.False(hung.IsCompleted); // ends by its own limit
    }

    [Fact]
    public async Task Search_runs_on_the_lanes_own_copy_of_its_input()
    {
        using var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var input = PooledFloats.Rent(3 * 32 * 32, clear: true);
        var search = rig.Card.DetectAsync(input, 1, 32, 32, stop.Token);
        await Eventually.TrueAsync(() => !rig.Search.Calls.IsEmpty, "the call hangs");

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        input.Dispose(); // back in the pool while the call hangs on

        rig.Search.Gate.Release();
        await Eventually.TrueAsync(() => rig.Search.Gate.CurrentCount == 0, "the call ended");
    }

    [Fact]
    public async Task Dispose_returns_with_a_hung_call_and_never_frees_a_session_under_it()
    {
        var rig = new Rig();
        var reading = rig.Card.ReadAsync(rig.Lines(1), CancellationToken.None);
        await Eventually.TrueAsync(() => !rig.Reader.Calls.IsEmpty, "the call hangs");

        rig.Card.Dispose();

        Assert.True(rig.Card.LeftHanging);
        Assert.Equal(0, rig.Reader.Disposed);
        rig.Time.Advance(CardModels.CallLimit);
        await Assert.ThrowsAsync<TimeoutException>(() => reading.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System));
        rig.Dispose();
    }

    [Fact]
    public async Task A_call_over_the_cap_ends_for_that_call_alone_with_one_log_line()
    {
        using var budget = new GpuSessionBudget(GpuSessionBudget.SessionBytes * 2, new FakeTimeProvider());
        var time = new FakeTimeProvider();
        var told = 0;
        using var finder = new OverCap(budget);
        using var card = new CardModels(finder, finder, finder, finder, ExecutionTarget.DirectMl, new BusyMeter(time), new BusyMeter(time), budget, time, () => told++);
        using var input = PooledFloats.Rent(3 * 32 * 32, clear: true);

        foreach (var expected in new[] { 7, 8 })
        {
            var read = await CardRunner.Guarded(
                async () =>
                {
                    using var map = await card.FindAsync(input, 32, 32, CancellationToken.None);
                    return 1;
                },
                () => Task.FromResult(expected),
                card,
                _ => throw new InvalidOperationException("no card failure"),
                CancellationToken.None);
            Assert.Equal(expected, read);
        }

        Assert.False(card.Failed);
        Assert.Equal(1, told);
    }

    /// <summary>A model each of whose sessions is over the cap, in a real budget.</summary>
    private sealed class OverCap(GpuSessionBudget budget) : ICardSessions
    {
        public T Run<T>(float[] input, long[] shape, OutputReader<T> read)
        {
            budget.Hold<IDisposable>(this, "huge", budget.Max + 1, () => throw new InvalidOperationException("never made"));
            throw new InvalidOperationException("never run");
        }

        public void Dispose()
        {
        }
    }
}
