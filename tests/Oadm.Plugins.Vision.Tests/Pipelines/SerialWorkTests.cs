using Oadm.Plugins.Vision.Pipelines;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>The order of a pipeline's frames and counter resets (<see cref="SerialWork"/>).</summary>
public sealed class SerialWorkTests
{
    [Fact]
    public async Task Work_runs_in_order_and_never_on_the_thread_that_hands_it_over()
    {
        var serial = new SerialWork();
        using var hold = new ManualResetEventSlim();
        var order = new List<int>();

        // Nothing before it: still the caller gets its task back at once, while the work runs elsewhere.
        var first = serial.RunAsync(() =>
        {
            hold.Wait(TimeSpan.FromSeconds(30));
            order.Add(1);
            return Task.FromResult(1);
        });
        Assert.False(first.IsCompleted);
        var second = serial.RunAsync(() => order.Add(2));

        hold.Set();
        Assert.Equal(1, await first);
        await second;
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task A_failed_piece_never_stops_the_ones_after_it()
    {
        var serial = new SerialWork();
        var failed = serial.RunAsync(() => throw new InvalidOperationException("broken"));
        var next = serial.RunAsync(() => Task.FromResult(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal(2, await next);
    }
}
