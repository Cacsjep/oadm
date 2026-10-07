using Oadm.Core.Collections;
using Oadm.Core.Devices;
using Oadm.Core.Tasks;

namespace Oadm.Core.Tests.Collections;

public sealed class KeyedCoalescingChannelTests
{
    private sealed record Change(int Key, string Kind, int Version);

    [Fact]
    public async Task UnderPressureAWaitingChangeIsReplacedInPlaceAndTheReaderGetsTheLatest()
    {
        var channel = new KeyedCoalescingChannel<int, Change>(c => c.Key, coalesceAbove: 0);
        channel.TryWrite(new Change(1, "u", 1));
        channel.TryWrite(new Change(2, "u", 1));
        channel.TryWrite(new Change(1, "u", 2));
        channel.TryWrite(new Change(3, "u", 1));
        channel.TryWrite(new Change(1, "u", 3));
        Assert.Equal(3, channel.Count);
        channel.Complete();

        var read = new List<Change>();
        await foreach (var change in channel.Reader.ReadAllAsync())
        {
            read.Add(change);
        }

        Assert.Equal([new Change(1, "u", 3), new Change(2, "u", 1), new Change(3, "u", 1)], read);
    }

    [Fact]
    public async Task BelowTheThresholdEveryChangeIsDeliveredInOrder()
    {
        var channel = new KeyedCoalescingChannel<int, Change>(c => c.Key, coalesceAbove: 10);
        Change[] written = [new(1, "a", 1), new(1, "u", 2), new(2, "a", 1), new(1, "r", 3)];
        foreach (var change in written)
        {
            channel.TryWrite(change);
        }

        channel.Complete();
        var read = new List<Change>();
        await foreach (var change in channel.Reader.ReadAllAsync())
        {
            read.Add(change);
        }

        Assert.Equal(written, read);
        await channel.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReaderWaitsForAWriteAndEndsAfterComplete()
    {
        var channel = new KeyedCoalescingChannel<int, Change>(c => c.Key);
        var reading = Task.Run(async () =>
        {
            var read = new List<Change>();
            await foreach (var change in channel.Reader.ReadAllAsync())
            {
                read.Add(change);
            }

            return read;
        });

        await Task.Delay(50);
        channel.TryWrite(new Change(7, "a", 1));
        await Task.Delay(50);
        channel.Complete();
        Assert.False(channel.TryWrite(new Change(8, "a", 1)));
        Assert.Equal([new Change(7, "a", 1)], await reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelledWaitThrows()
    {
        var channel = new KeyedCoalescingChannel<int, Change>(c => c.Key);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await channel.Reader.WaitToReadAsync(cts.Token));
    }

    [Fact]
    public void TaskFeedKeepsAnAddedChangeAddedWhenAnUpdateFollows()
    {
        var record = new TaskRecord(Guid.NewGuid(), "p", "n", TaskState.Queued, "o", DateTimeOffset.UtcNow, null, null, 0, null, []);
        var merged = TaskChangeFeed.Merge(new TaskChange(TaskChangeKind.Added, record), new TaskChange(TaskChangeKind.Updated, record with { State = TaskState.Running }));
        Assert.Equal(TaskChangeKind.Added, merged.Kind);
        Assert.Equal(TaskState.Running, merged.Task.State);

        var removed = TaskChangeFeed.Merge(new TaskChange(TaskChangeKind.Added, record), new TaskChange(TaskChangeKind.Removed, record));
        Assert.Equal(TaskChangeKind.Removed, removed.Kind);
    }

    [Fact]
    public void DeviceFeedKeepsAnAddedChangeAddedWhenAnUpdateFollows()
    {
        var device = new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000001", Address = "10.0.0.1" };
        var merged = DeviceChangeSubscription.Merge(
            new DeviceChange(DeviceChangeKind.Added, device.Id, device),
            new DeviceChange(DeviceChangeKind.Updated, device.Id, device));
        Assert.Equal(DeviceChangeKind.Added, merged.Kind);
    }

    /// <summary>The former DropOldest(4096) channel lost changes of a 5,000-device burst; the coalescing one never does.</summary>
    [Fact]
    public async Task ABurstOf5000EntitiesWith10ChangesEachKeepsEveryFinalState()
    {
        var feed = new TaskChangeFeed();
        using var subscription = feed.Subscribe();
        var records = Enumerable.Range(0, 5000)
            .Select(i => new TaskRecord(Guid.NewGuid(), "p", "n", TaskState.Queued, "o", DateTimeOffset.UtcNow, null, null, 0, null, []))
            .ToList();
        foreach (var record in records)
        {
            feed.Publish(new TaskChange(TaskChangeKind.Added, record));
        }

        for (var progress = 10; progress <= 100; progress += 10)
        {
            foreach (var record in records)
            {
                feed.Publish(new TaskChange(TaskChangeKind.Updated, record with { Progress = progress, State = progress == 100 ? TaskState.Done : TaskState.Running }));
            }
        }

        feed.Complete();
        var final = new Dictionary<Guid, TaskChange>();
        await foreach (var change in subscription.Reader.ReadAllAsync())
        {
            final[change.Task.Id] = change;
        }

        Assert.Equal(5000, final.Count);
        Assert.All(final.Values, c =>
        {
            Assert.Equal(TaskChangeKind.Added, c.Kind);
            Assert.Equal(TaskState.Done, c.Task.State);
        });
    }
}
