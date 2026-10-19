using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;

namespace Oadm.Core.Sites;

/// <summary>
/// Follows the sites (<see cref="ISiteStates.Changed"/>): when a site goes offline or online, or is renamed, every device
/// of it is published once as Updated in one batch with its computed status (<see cref="SiteDeviceStatus"/>); nothing is
/// written. A site that was offline for longer than <see cref="RefreshAfter"/> gets a full refresh per device when it is
/// back (through the polling's refresh queue, at most its <c>RefreshParallelism</c> at once).
/// </summary>
public sealed partial class SiteStatusPublisher : IAsyncDisposable
{
    /// <summary>Offline for longer than this: a full refresh per device when the site is back.</summary>
    public static readonly TimeSpan DefaultRefreshAfter = TimeSpan.FromMinutes(5);

    private readonly ISiteStates _sites;
    private readonly DeviceRepository _devices;
    private readonly IDeviceChangeFeed _feed;
    private readonly Action<IReadOnlyList<Guid>>? _queueRefresh;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<IReadOnlyCollection<Guid>> _changes = Channel.CreateUnbounded<IReadOnlyCollection<Guid>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<Guid, DateTimeOffset> _offlineSince = [];
    private Task _loop = Task.CompletedTask;
    private int _started;

    public SiteStatusPublisher(
        ISiteStates sites,
        DeviceRepository devices,
        IDeviceChangeFeed feed,
        Action<IReadOnlyList<Guid>>? queueRefresh = null,
        TimeProvider? time = null,
        ILogger<SiteStatusPublisher>? logger = null)
    {
        _sites = sites ?? throw new ArgumentNullException(nameof(sites));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _queueRefresh = queueRefresh;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Offline for longer than this: a full refresh per device when the site is back (default 5 minutes).</summary>
    public TimeSpan RefreshAfter { get; init; } = DefaultRefreshAfter;

    /// <summary>Batches published so far (tests).</summary>
    public int Batches => Volatile.Read(ref _batches);

    private int _batches;

    /// <summary>Raised after a batch was published (tests): the sites and the number of devices.</summary>
    public event EventHandler<int>? Published;

    /// <summary>Starts following the sites; the sites that are offline now count from now.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _sites.Changed += OnChanged;
        _loop = Task.Run(LoopAsync);
    }

    private void OnChanged(object? sender, SiteStatesChangedEventArgs e) => _changes.Writer.TryWrite([.. e.SiteIds]);

    private async Task LoopAsync()
    {
        var reader = _changes.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Coalesce a burst: one read and one batch for every site that changed meanwhile.
            var siteIds = new HashSet<Guid>();
            while (reader.TryRead(out var ids))
            {
                siteIds.UnionWith(ids);
            }

            try
            {
                await HandleAsync(siteIds).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The loop never ends on a failed read; the next change tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
    }

    private async Task HandleAsync(HashSet<Guid> siteIds)
    {
        var now = _time.GetUtcNow();
        var refresh = new List<Guid>();
        foreach (var siteId in siteIds)
        {
            var online = _sites.IsOnline(siteId);
            if (!online)
            {
                _offlineSince.TryAdd(siteId, now);
            }
            else if (_offlineSince.Remove(siteId, out var since) && now - since > RefreshAfter)
            {
                refresh.Add(siteId);
            }
        }

        var devices = await _devices.ListBySitesAsync(siteIds, CancellationToken.None).ConfigureAwait(false);
        if (devices.Count > 0)
        {
            _feed.PublishMany([.. devices.Select(d => new DeviceChange(DeviceChangeKind.Updated, d.Id, d))]);
            Interlocked.Increment(ref _batches);
            LogPublished(devices.Count, siteIds.Count);
        }

        if (refresh.Count > 0 && _queueRefresh is not null)
        {
            var ids = devices.Where(d => d.SiteId is { } s && refresh.Contains(s)).Select(d => d.Id).ToList();
            if (ids.Count > 0)
            {
                LogRefresh(ids.Count);
                _queueRefresh(ids);
            }
        }

        Published?.Invoke(this, devices.Count);
    }

    public async ValueTask DisposeAsync()
    {
        _sites.Changed -= OnChanged;
        _changes.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Site status: {Devices} devices of {Sites} sites published")]
    private partial void LogPublished(int devices, int sites);

    [LoggerMessage(Level = LogLevel.Information, Message = "A site is back after a longer time offline: a full refresh of its {Devices} devices is queued")]
    private partial void LogRefresh(int devices);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The devices of a site that went offline or online could not be published")]
    private partial void LogFailed(Exception ex);
}
