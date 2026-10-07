using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Discovery.Mdns;

namespace Oadm.Core.Discovery;

/// <summary>
/// Runs mDNS browsing and IP range scans as sessions. Each session keeps its own list of
/// discovered devices, deduplicated by serial, and streams changes to any number of watchers.
/// Free of gRPC types; the server maps sessions and events to the DiscoveryService contract.
/// <para>
/// mDNS hits are enriched with the same anonymous <see cref="IDeviceProbe"/> used by the range
/// scan, so model, firmware, scheme and status (e.g. PasswordNotSet) are filled in shortly after
/// the device first appears.
/// </para>
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private readonly IMdnsBrowser _browser;
    private readonly RangeScanner _scanner;
    private readonly IDeviceProbe _probe;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public DiscoveryService(
        IMdnsBrowser browser,
        RangeScanner scanner,
        IDeviceProbe probe,
        ILogger<DiscoveryService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(probe);
        _browser = browser;
        _scanner = scanner;
        _probe = probe;
        _logger = logger ?? NullLogger<DiscoveryService>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Timeout used when probing addresses seen via mDNS.</summary>
    public TimeSpan MdnsProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Currently running or finished but not yet stopped sessions.</summary>
    public IReadOnlyCollection<DiscoverySession> Sessions => _sessions.Values.Select(s => s.Handle).ToList();

    /// <summary>
    /// Starts continuous mDNS browsing. Runs until <see cref="StopAsync"/> is called for the session.
    /// </summary>
    public DiscoverySession StartZeroConf(MdnsBrowseOptions? options = null)
    {
        var session = CreateSession(DiscoverySessionKind.ZeroConf);
        session.Run = Task.Run(() => RunZeroConfAsync(session, options, session.Stop.Token), CancellationToken.None);
        return session.Handle;
    }

    /// <summary>
    /// Starts a range scan. The session's watchers receive progress events and a final event with
    /// <see cref="DiscoveryEvent.Finished"/> = true, after which their streams end.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid or oversized range.</exception>
    public DiscoverySession StartRangeScan(IPAddress from, IPAddress to, RangeScanOptions? options = null)
        => StartRangeScan(Ipv4Range.Create(from, to), options);

    /// <inheritdoc cref="StartRangeScan(IPAddress, IPAddress, RangeScanOptions?)"/>
    public DiscoverySession StartRangeScan(Ipv4Range range, RangeScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(range);
        var session = CreateSession(DiscoverySessionKind.RangeScan);
        session.Run = Task.Run(() => RunRangeScanAsync(session, range, options, session.Stop.Token), CancellationToken.None);
        return session.Handle;
    }

    /// <summary>Snapshot of the devices discovered so far in a session.</summary>
    /// <exception cref="KeyNotFoundException">Unknown session.</exception>
    public IReadOnlyList<DiscoveredDevice> GetDevices(string sessionId) => GetSession(sessionId).Snapshot().Devices;

    /// <summary>
    /// Streams a session: first the devices already known (replay), then live changes. Ends when
    /// the session finishes (range scan) or is stopped, or when the token is cancelled.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unknown session.</exception>
    public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken cancellationToken = default)
        => WatchSessionAsync(GetSession(sessionId), cancellationToken);

    private static async IAsyncEnumerable<DiscoveryEvent> WatchSessionAsync(Session session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<DiscoveryEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        session.Subscribe(channel.Writer);
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            session.Unsubscribe(channel.Writer);
        }
    }

    /// <summary>Stops a session, ends all its watch streams and forgets it. Unknown ids are ignored.</summary>
    public async Task StopAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        await session.Stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await session.Run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        session.Complete(finishedEvent: null);
        session.Stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    private Session CreateSession(DiscoverySessionKind kind)
    {
        var session = new Session(new DiscoverySession(Guid.NewGuid().ToString("N"), kind));
        _sessions[session.Handle.Id] = session;
        DiscoveryLog.SessionStarted(_logger, session.Handle.Id, kind);
        return session;
    }

    private Session GetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var s) ? s : throw new KeyNotFoundException($"Unknown discovery session '{sessionId}'.");

    private async Task RunZeroConfAsync(Session session, MdnsBrowseOptions? options, CancellationToken ct)
    {
        using var enrichLimit = new SemaphoreSlim(8);
        var enrichments = new List<Task>();
        var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var instance in _browser.BrowseAsync(options, ct).ConfigureAwait(false))
            {
                var address = instance.Addresses.Count > 0 ? instance.Addresses[0] : null;
                if (address is null)
                {
                    continue;
                }

                var hostName = string.IsNullOrEmpty(instance.HostName) ? null : instance.HostName;
                if (instance.Serial is { } serial)
                {
                    Publish(session, new DeviceObservation(serial, address, hostName, null, null, null, null, DiscoverySources.Mdns));
                }

                // Probe each address once per session (model, status, scheme; serial if TXT lacked it).
                if (probed.Add(address.ToString()))
                {
                    enrichments.RemoveAll(t => t.IsCompleted);
                    enrichments.Add(EnrichAsync(session, address, hostName, enrichLimit, ct));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
        }
        finally
        {
            await Task.WhenAll(enrichments).ConfigureAwait(false);
        }
    }

    private async Task EnrichAsync(Session session, IPAddress address, string? hostName, SemaphoreSlim limit, CancellationToken ct)
    {
        try
        {
            await limit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await _probe.ProbeAsync(address, MdnsProbeTimeout, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    Publish(session, DeviceObservation.FromProbe(result, DiscoverySources.Mdns) with { HostName = hostName });
                }
                else
                {
                    session.MarkUnreachable(address, _time.GetUtcNow(), out var changed);
                    if (changed is not null)
                    {
                        session.Broadcast(new DiscoveryEvent(changed, 0, false));
                    }
                }
            }
            finally
            {
                limit.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
        }
    }

    private async Task RunRangeScanAsync(Session session, Ipv4Range range, RangeScanOptions? options, CancellationToken ct)
    {
        var progress = new SyncProgress(p =>
        {
            session.Progress = p;
            if (p < 100)
            {
                session.Broadcast(new DiscoveryEvent(null, p, false));
            }
        });
        try
        {
            await foreach (var result in _scanner.ScanAsync(range, options, progress, ct).ConfigureAwait(false))
            {
                Publish(session, DeviceObservation.FromProbe(result, DiscoverySources.RangeScan));
            }

            var count = session.Snapshot().Devices.Count;
            DiscoveryLog.SessionFinished(_logger, session.Handle.Id, count);
            session.Complete(new DiscoveryEvent(null, 100, true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
            session.Complete(new DiscoveryEvent(null, session.Progress, true));
        }
    }

    private void Publish(Session session, DeviceObservation observation)
    {
        var changed = session.Merge(observation, _time.GetUtcNow());
        if (changed is not null)
        {
            session.Broadcast(new DiscoveryEvent(changed, session.Handle.Kind == DiscoverySessionKind.RangeScan ? session.Progress : 0, false));
        }
    }

    /// <summary>A partial view of a device from one source; null fields mean "not known by this source".</summary>
    internal sealed record DeviceObservation(
        string Serial,
        IPAddress Address,
        string? HostName,
        string? Model,
        string? FirmwareVersion,
        string? Scheme,
        DiscoveredDeviceStatus? Status,
        DiscoverySources Source)
    {
        public static DeviceObservation FromProbe(DeviceProbeResult r, DiscoverySources source)
            => new(r.Serial, r.Address, null, r.Model, r.FirmwareVersion, r.Scheme, r.Status, source);
    }

    /// <summary>
    /// Merges an observation into an existing device (or creates it). Returns the new device when
    /// anything visible changed, otherwise null. Later non-null values win; the address follows
    /// the latest observation; sources accumulate.
    /// </summary>
    internal static DiscoveredDevice? Merge(DiscoveredDevice? existing, DeviceObservation o, DateTimeOffset now)
    {
        if (existing is null)
        {
            return new DiscoveredDevice(o.Serial, o.Serial, o.Address, o.HostName, o.Model, o.FirmwareVersion, o.Status ?? DiscoveredDeviceStatus.Unknown, o.Scheme, o.Source, now);
        }

        var merged = existing with
        {
            Address = o.Address,
            HostName = o.HostName ?? existing.HostName,
            Model = o.Model ?? existing.Model,
            FirmwareVersion = o.FirmwareVersion ?? existing.FirmwareVersion,
            Scheme = o.Scheme ?? existing.Scheme,
            Status = o.Status ?? existing.Status,
            Sources = existing.Sources | o.Source,
            LastSeenUtc = existing.LastSeenUtc,
        };

        return merged == existing ? null : merged with { LastSeenUtc = now };
    }

    private sealed class Session(DiscoverySession handle)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ChannelWriter<DiscoveryEvent>> _subscribers = [];
        private DiscoveryEvent? _finished;
        private bool _completed;

        public DiscoverySession Handle { get; } = handle;

        public CancellationTokenSource Stop { get; } = new();

        public Task Run { get; set; } = Task.CompletedTask;

        public int Progress { get; set; }

        public (IReadOnlyList<DiscoveredDevice> Devices, bool Completed) Snapshot()
        {
            lock (_lock)
            {
                return (_devices.Values.ToList(), _completed);
            }
        }

        public DiscoveredDevice? Merge(DeviceObservation observation, DateTimeOffset now)
        {
            lock (_lock)
            {
                _devices.TryGetValue(observation.Serial, out var existing);
                var merged = DiscoveryService.Merge(existing, observation, now);
                if (merged is not null)
                {
                    _devices[observation.Serial] = merged;
                }

                return merged;
            }
        }

        public void MarkUnreachable(IPAddress address, DateTimeOffset now, out DiscoveredDevice? changed)
        {
            changed = null;
            lock (_lock)
            {
                var device = _devices.Values.FirstOrDefault(d => d.Address.Equals(address) && d.Status == DiscoveredDeviceStatus.Unknown);
                if (device is not null)
                {
                    changed = device with { Status = DiscoveredDeviceStatus.Unreachable, LastSeenUtc = now };
                    _devices[device.Serial] = changed;
                }
            }
        }

        public void Subscribe(ChannelWriter<DiscoveryEvent> writer)
        {
            lock (_lock)
            {
                foreach (var d in _devices.Values)
                {
                    writer.TryWrite(new DiscoveryEvent(d, Handle.Kind == DiscoverySessionKind.RangeScan ? Progress : 0, false));
                }

                if (_completed)
                {
                    if (_finished is not null)
                    {
                        writer.TryWrite(_finished);
                    }

                    writer.TryComplete();
                    return;
                }

                _subscribers.Add(writer);
            }
        }

        public void Unsubscribe(ChannelWriter<DiscoveryEvent> writer)
        {
            lock (_lock)
            {
                _subscribers.Remove(writer);
            }
        }

        public void Broadcast(DiscoveryEvent e)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                foreach (var s in _subscribers)
                {
                    s.TryWrite(e);
                }
            }
        }

        public void Complete(DiscoveryEvent? finishedEvent)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    foreach (var s in _subscribers)
                    {
                        s.TryComplete();
                    }

                    _subscribers.Clear();
                    return;
                }

                _completed = true;
                _finished = finishedEvent;
                foreach (var s in _subscribers)
                {
                    if (finishedEvent is not null)
                    {
                        s.TryWrite(finishedEvent);
                    }

                    s.TryComplete();
                }

                _subscribers.Clear();
            }
        }
    }

    /// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to the thread pool and may reorder).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
