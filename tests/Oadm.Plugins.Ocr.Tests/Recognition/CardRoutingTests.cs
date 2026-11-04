using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Ocr.Recognition;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The models on the graphics card (docs/spec/ocr.md "GPU"): which model goes there, the calls' few sizes, a failing card going
/// back to the CPU, the load by how full a call is and sessions held for their calls. No graphics card: pure rules and fakes.
/// </summary>
public sealed class CardRoutingTests
{
    [Theory]
    [InlineData(10.0, 100.0, true)]
    [InlineData(100.0, 100.0, true)]
    [InlineData(150.0, 100.0, true)]
    [InlineData(151.0, 100.0, false)]
    [InlineData(null, 100.0, false)]
    public void A_model_goes_to_the_card_unless_it_fails_or_is_clearly_slower(double? cardMs, double cpuMs, bool onCard) =>
        Assert.Equal(onCard, CardRoute.Worth(cardMs, cpuMs));

    [Theory]
    [InlineData(64, 128)]
    [InlineData(128, 128)]
    [InlineData(129, 160)]
    [InlineData(600, 640)]
    [InlineData(3200, 3200)]
    public void A_line_goes_to_the_next_call_width(int line, int call) => Assert.Equal(call, CardModels.LineWidth(line));

    [Fact]
    public void No_line_is_padded_by_more_than_a_third()
    {
        // Moving labels lose readings when lines are padded much wider (docs/spec/ocr.md "GPU").
        foreach (var (narrow, wide) in CardModels.LineWidths.Zip(CardModels.LineWidths.Skip(1)).Take(CardModels.LineWidths.Length - 2))
        {
            Assert.True(wide <= narrow * 1.34, $"{narrow} to {wide}");
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 16)]
    [InlineData(16, 16)]
    public void Lines_share_a_call_of_few_sizes(int lines, int batch) => Assert.Equal(batch, CardModels.BatchFor(lines, CardModels.LineBatches));

    private sealed class FakeCard : ICardFailure
    {
        public bool Failed { get; private set; }

        public bool MarkFailed()
        {
            var first = !Failed;
            Failed = true;
            return first;
        }
    }

    [Fact]
    public async Task A_card_call_that_fails_runs_on_the_cpu_and_the_next_ones_never_ask_the_card()
    {
        var card = new FakeCard();
        var told = new List<string>();
        var cardCalls = 0;

        var first = await CardRunner.Guarded(
            () =>
            {
                cardCalls++;
                return Task.FromException<int>(new InvalidOperationException("device removed"));
            },
            () => Task.FromResult(7),
            card,
            told.Add,
            CancellationToken.None);
        var second = await CardRunner.Guarded(
            () =>
            {
                cardCalls++;
                return Task.FromResult(1);
            },
            () => Task.FromResult(8),
            card,
            told.Add,
            CancellationToken.None);

        Assert.Equal((7, 8), (first, second));
        Assert.Equal(1, cardCalls);
        Assert.Equal(["device removed"], told); // one log line
    }

    [Fact]
    public async Task A_cancelled_reading_leaves_the_card_in_use()
    {
        var card = new FakeCard();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CardRunner.Guarded(
            () => Task.FromException<int>(new OperationCanceledException(stop.Token)),
            () => Task.FromResult(7),
            card,
            _ => { },
            stop.Token));

        Assert.False(card.Failed);
    }

    [Fact]
    public void A_call_counts_by_how_full_it_is_and_the_load_is_the_busier_lane()
    {
        var time = new FakeTimeProvider();
        var cpu = new BusyMeter(time);
        var finder = new BusyMeter(time);
        var reader = new BusyMeter(time);
        var load = new CombinedBusy(cpu, finder, 4, reader);

        // The reader's lane: 1 s of a call with 4 of 16 places in use, a quarter of its time.
        var started = reader.Start();
        time.Advance(TimeSpan.FromSeconds(1));
        reader.Stop(started, 0.25);
        Assert.Equal(TimeSpan.FromSeconds(0.25), reader.Busy);

        // The finder's lane the same second, full: the busier lane counts, not their sum (4 workers: 4 s).
        started = finder.Start();
        time.Advance(TimeSpan.FromSeconds(1));
        finder.Stop(started);
        Assert.Equal(TimeSpan.FromSeconds(4), load.Busy);
    }

    private sealed class Session : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_session_held_for_a_call_is_never_pushed_out_or_freed_as_idle()
    {
        var time = new FakeTimeProvider();
        using var budget = new GpuSessionBudget(100, time);
        var owner = new object();
        var held = budget.Hold(owner, "a", 60, () => new Session());

        // Too large beside the held one only together; a free one goes first.
        var free = budget.Get(owner, "b", 30, () => new Session());
        budget.Get(owner, "c", 30, () => new Session());
        Assert.True(free.Disposed);
        Assert.False(held.Disposed);

        time.Advance(GpuSessionBudget.IdleTimeout * 2);
        Assert.False(held.Disposed);

        budget.Free(owner, "a");
        time.Advance(GpuSessionBudget.IdleTimeout * 2);
        Assert.True(held.Disposed);
    }

    [Fact]
    public void A_size_that_would_push_a_session_out_does_not_fit()
    {
        using var budget = new GpuSessionBudget(100, new FakeTimeProvider());
        var owner = new object();
        budget.Get(owner, "a", 80, () => new Session());

        Assert.True(budget.Fits(owner, "a", 80)); // has its session
        Assert.True(budget.Fits(owner, "b", 20));
        Assert.False(budget.Fits(owner, "b", 21));
    }
}
