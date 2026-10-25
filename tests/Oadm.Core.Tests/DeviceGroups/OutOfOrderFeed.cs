using Oadm.Core.Devices;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// Raises two changes of one device to the handlers that subscribed after it in the other order: the older one is held in
/// this first handler (after the feed stamped it) until the newer one went through every handler. Make it before the
/// handlers under test subscribe.
/// </summary>
internal sealed class OutOfOrderFeed : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly IDeviceChangeFeed _feed;
    private readonly SemaphoreSlim _held = new(0);
    private readonly SemaphoreSlim _release = new(0);
    private DeviceChange? _hold;

    public OutOfOrderFeed(IDeviceChangeFeed feed)
    {
        _feed = feed;
        _feed.Changed += OnChanged;
    }

    /// <summary>Publishes <paramref name="older"/>, then <paramref name="newer"/>; the handlers get the newer one first.</summary>
    public async Task PublishAsync(DeviceChange older, DeviceChange newer)
    {
        Volatile.Write(ref _hold, older);
        var first = Task.Factory.StartNew(() => _feed.Publish(older), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(await _held.WaitAsync(Ceiling), "the older change was not raised");
        _feed.Publish(newer);
        _release.Release();
        await first;
    }

    private void OnChanged(object? sender, DeviceChange change)
    {
        var hold = Volatile.Read(ref _hold);
        if (hold is not null && change.DeviceId == hold.DeviceId && Interlocked.CompareExchange(ref _hold, null, hold) == hold)
        {
            _held.Release();
            _release.Wait(Ceiling);
        }
    }

    public void Dispose()
    {
        _feed.Changed -= OnChanged;
        _held.Dispose();
        _release.Dispose();
    }
}
