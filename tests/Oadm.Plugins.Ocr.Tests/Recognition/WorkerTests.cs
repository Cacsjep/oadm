using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Sdk.Testing;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The bounded load (docs/spec/ocr.md rule 4 and "GPU"): at most max(1, cores / 2) model calls on the CPU at once, the
/// accurate sets fewer still, and on a graphics card the lines of all waiting readings read together. Gates hold the work;
/// nothing depends on timing.
/// </summary>
public sealed class WorkerTests
{
    private sealed class FakeSet : IDisposable
    {
        public bool Accurate { get; init; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(8, 4)]
    [InlineData(24, 12)]
    public void Workers_are_half_the_cores_and_at_least_one(int cores, int workers)
    {
        Assert.Equal(workers, OcrLimits.Workers(cores));
        Assert.Equal(2, OcrLimits.ThreadsPerWorker);
    }

    [Fact]
    public async Task Every_call_adds_the_time_it_held_its_slot_to_the_load()
    {
        // The recognition load (docs/spec/ocr.md "Runtime stats"): two calls held for 3 s of the fake clock are 6 s busy.
        var time = new FakeTimeProvider();
        var busy = new BusyMeter(time);
        using var workers = new CpuWorkers<FakeSet>(2, 1, accurate => new FakeSet { Accurate = accurate }, busy);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var calls = Enumerable.Range(0, 2).Select(_ => workers.RunAsync(false, async set =>
        {
            Interlocked.Increment(ref running);
            await release.Task;
            return set;
        }, CancellationToken.None)).ToList();

        await Eventually.TrueAsync(() => Volatile.Read(ref running) == 2, "both calls hold a slot");
        time.Advance(TimeSpan.FromSeconds(3));
        release.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(TimeSpan.FromSeconds(6), busy.Busy);
    }

    [Fact]
    public async Task The_gate_holds_calls_to_the_slots_and_reuses_the_sets()
    {
        var created = 0;
        using var workers = new CpuWorkers<FakeSet>(3, 1, accurate =>
        {
            Interlocked.Increment(ref created);
            return new FakeSet { Accurate = accurate };
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        var calls = Enumerable.Range(0, 10).Select(_ => workers.RunAsync(false, async set =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return set;
        }, CancellationToken.None)).ToList();

        await Eventually.TrueAsync(() => Volatile.Read(ref running) == 3, "three calls running");
        Assert.Equal(3, workers.Busy);
        release.SetResult();
        var sets = await Task.WhenAll(calls);

        Assert.Equal(3, Volatile.Read(ref most));
        Assert.Equal(3, created);
        Assert.Equal(3, sets.Distinct().Count());
        Assert.Equal(0, workers.Busy);
    }

    [Fact]
    public async Task Accurate_calls_have_their_own_smaller_limit_within_the_slots()
    {
        using var workers = new CpuWorkers<FakeSet>(4, 1, accurate => new FakeSet { Accurate = accurate });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accurateRunning = 0;
        var mobileRunning = 0;
        var calls = new List<Task<bool>>();
        for (var i = 0; i < 3; i++)
        {
            calls.Add(workers.RunAsync(true, async set =>
            {
                Interlocked.Increment(ref accurateRunning);
                await release.Task;
                return set.Accurate;
            }, CancellationToken.None));
        }

        calls.Add(workers.RunAsync(false, async set =>
        {
            Interlocked.Increment(ref mobileRunning);
            await release.Task;
            return set.Accurate;
        }, CancellationToken.None));

        // One accurate call runs; the other two wait for the accurate limit (holding their slots), the mobile one gets the last slot.
        await Eventually.TrueAsync(() => Volatile.Read(ref accurateRunning) == 1 && Volatile.Read(ref mobileRunning) == 1, "one accurate and one mobile call running");
        Assert.Equal(4, workers.Busy);
        release.SetResult();
        var kinds = await Task.WhenAll(calls);

        Assert.Equal([true, true, true, false], kinds);
        Assert.Equal(3, Volatile.Read(ref accurateRunning));
    }

    [Fact]
    public async Task Disposing_the_workers_disposes_the_idle_sets()
    {
        FakeSet? made = null;
        var workers = new CpuWorkers<FakeSet>(1, 1, _ => made = new FakeSet());
        await workers.RunAsync(false, set => Task.FromResult(set), CancellationToken.None);

        workers.Dispose();

        Assert.True(made!.Disposed);
    }

    [Fact]
    public async Task A_set_in_use_is_disposed_when_its_call_ends_and_later_calls_are_refused()
    {
        var workers = new CpuWorkers<FakeSet>(2, 1, _ => new FakeSet());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSet? inUse = null;
        var call = workers.RunAsync(false, async set =>
        {
            inUse = set;
            await release.Task;
            return set.Disposed;
        }, CancellationToken.None);
        await Eventually.TrueAsync(() => Volatile.Read(ref inUse) is not null, "the call running");

        workers.Dispose();

        Assert.False(inUse!.Disposed);
        release.SetResult();
        Assert.False(await call);
        Assert.True(inUse.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => workers.RunAsync(false, set => Task.FromResult(set), CancellationToken.None));
    }

    [Fact]
    public async Task Sets_unused_for_a_while_are_disposed()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var made = new List<FakeSet>();
        using var workers = new CpuWorkers<FakeSet>(3, 1, _ =>
        {
            var set = new FakeSet();
            lock (made)
            {
                made.Add(set);
            }

            return set;
        }, time: time);

        // Three calls at once make three sets.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 3).Select(_ => workers.RunAsync(false, async set => { await release.Task; return set; }, CancellationToken.None)).ToList();
        await Eventually.TrueAsync(() => workers.Busy == 3, "three calls running");
        release.SetResult();
        await Task.WhenAll(calls);
        Assert.Equal(3, workers.Idle);

        // One set keeps being used; the other two age out.
        for (var minute = 0; minute < 3; minute++)
        {
            await workers.RunAsync(false, set => Task.FromResult(set), CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(1, workers.Idle);
        Assert.Equal(2, made.Count(s => s.Disposed));
    }

    [Fact]
    public async Task Stopping_the_line_batcher_waits_for_the_running_round()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;
        var batcher = new LineBatcher(async (lines, _) =>
        {
            entered.SetResult();
            await finish.Task;
            Volatile.Write(ref ended, true);
            return [.. lines.Select(_ => new LineScores([1], 1, 1))];
        });
        using var line = Line(10);
        var reading = batcher.RecognizeAsync([line], CancellationToken.None);
        await entered.Task;

        var stop = Task.Run(() => batcher.Stop());
        Assert.False(stop.IsCompleted);
        finish.SetResult();

        Assert.True(await stop);
        Assert.True(Volatile.Read(ref ended));
        Assert.Single(await reading);
    }

    [Fact]
    public async Task Lines_of_readings_that_wait_together_are_read_in_one_round()
    {
        var firstRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rounds = new List<int>();
        var entered = 0;
        using var batcher = new LineBatcher(async (lines, ct) =>
        {
            lock (rounds)
            {
                rounds.Add(lines.Count);
            }

            if (Interlocked.Increment(ref entered) == 1)
            {
                await firstRound.Task;
            }

            return [.. lines.Select(l => new LineScores([l.Width], 1, 1))];
        });

        using var a = Line(10);
        using var b = Line(20);
        using var c = Line(30);
        using var d = Line(40);
        var first = batcher.RecognizeAsync([a], CancellationToken.None);
        await Eventually.TrueAsync(() => Volatile.Read(ref entered) == 1, "the first round running");

        // While the card reads the first round, two readings wait: their lines go in the next round together.
        var second = batcher.RecognizeAsync([b, c], CancellationToken.None);
        var third = batcher.RecognizeAsync([d], CancellationToken.None);
        firstRound.SetResult();

        Assert.Equal(10, (await first).Single().Probabilities[0]);
        Assert.Equal([20f, 30f], (await second).Select(s => s.Probabilities[0]));
        Assert.Equal(40, (await third).Single().Probabilities[0]);
        Assert.Equal([1, 3], rounds);
        Assert.Equal(2, batcher.Rounds);
    }

    [Fact]
    public async Task A_failing_round_fails_its_readings_and_the_next_round_still_runs()
    {
        var fail = true;
        using var batcher = new LineBatcher((lines, ct) => fail
            ? throw new InvalidOperationException("The card is gone.")
            : Task.FromResult(lines.Select(_ => new LineScores([1], 1, 1)).ToArray()));
        using var line = Line(10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => batcher.RecognizeAsync([line], CancellationToken.None));
        fail = false;

        Assert.Single(await batcher.RecognizeAsync([line], CancellationToken.None));
    }

    private static SKBitmap Line(int width) => new(new SKImageInfo(width, 48, SKColorType.Rgba8888, SKAlphaType.Premul));

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
