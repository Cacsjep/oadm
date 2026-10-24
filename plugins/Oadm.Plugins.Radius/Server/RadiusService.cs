using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DeviceIds;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.Radius.Server;

/// <summary>What the 802.1X server needs from the host (the core plugin context, or fakes in tests).</summary>
public sealed record RadiusHost(
    IPluginSettings Settings,
    IDeviceRepository Devices,
    IPluginNetwork? Network,
    Func<IDeviceCertificateAuthority?> Authority,
    Func<bool> ProActive)
{
    /// <summary>The bound sites and their relays; null on hosts without sites.</summary>
    public ISites? Sites { get; init; }

    public IPluginEvents? Events { get; init; }

    /// <summary>The log's records; null keeps the log in memory only.</summary>
    public IPluginRecords? Records { get; init; }

    public IEventPublisher? Publisher { get; init; }

    /// <summary>The tags of the VLAN rules, protected by the host; null on older hosts (tag rules then match no device).</summary>
    public IProtectedTags? ProtectedTags { get; init; }

    /// <summary>The device groups (state events per group); null on older hosts.</summary>
    public IDeviceScopes? Scopes { get; init; }

    /// <summary>Encrypts the shared secrets and the server key; null: no switch can be saved, the key stays in memory.</summary>
    public ISecretProtector? Secrets { get; init; }

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The Axis device ID CAs; the embedded ones by default.</summary>
    public AxisDeviceId? DeviceIds { get; init; }

    public string HostName { get; init; } = Environment.MachineName;

    public IServerNetworkInterfaces Interfaces { get; init; } = SystemNetworkInterfaces.Instance;

    /// <summary>Datagrams processed at once (the bound on parallel TLS handshakes, docs/spec "Limits and load").</summary>
    public int Workers { get; init; } = 64;
}

/// <summary>
/// The 802.1X server (docs/spec/radius-server.md): listens on UDP 1812 ("Listen on") and at every site with switches
/// (through the site node's relay), finds the switch of each datagram in an immutable <see cref="SwitchTable"/> by site and
/// source address (unknown senders are dropped and counted), runs the protocol layer (<see cref="RadiusProcessor"/>) on at
/// most <see cref="RadiusHost.Workers"/> datagrams at a time (a bounded queue in front: the switch retransmits), checks the
/// client certificates (<see cref="DeviceCertificateValidator"/>) and keeps the Sessions tab, the log and the switch rows.
/// Page methods: <see cref="RadiusMethods"/>. OADM PRO is checked on every datagram.
/// </summary>
public sealed partial class RadiusService : IAsyncDisposable
{
    public const string Reason = "802.1X server";
    public const string AuthenticatedEvent = "oadm.radius.authenticated";
    public const string FailedEvent = "oadm.radius.failed";

    private const int QueueCapacity = 8192;
    private const int EventChunk = 500;
    private const string SecretPurposePrefix = "radius:switch:";
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaintainInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResolveInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ListenRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AddressCheckInterval = TimeSpan.FromSeconds(5); // a new address is listened on within 5 s
    private static readonly TimeSpan QuietPublish = TimeSpan.FromSeconds(5);

    private readonly RadiusHost _host;
    private readonly RadiusStore _store;
    private readonly RadiusLog _log;
    private readonly SessionBoard _board;
    private readonly DeviceIndex _index;
    private readonly ServerIdentityKeeper _identity;
    private readonly RadiusProcessor _processor;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _listenGate = new(1, 1);
    private readonly SemaphoreSlim _sitesGate = new(1, 1);
    private readonly SemaphoreSlim _maintainGate = new(1, 1);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Guid, SwitchRuntime> _runtime = new();
    private readonly Dictionary<Guid, (string Name, IUdpRelay Relay)> _siteRelays = [];
    private readonly HashSet<Task> _running = [];
    private readonly Lock _unknownGate = new();
    private readonly Lock _followGate = new();
    private Task[] _workers = [];
    private volatile SwitchTable _switches = SwitchTable.Empty;
    private List<StoredSwitch> _stored = [];
    private volatile CaTrust _trust = CaTrust.None;
    private volatile VlanPlan _vlans = VlanPlan.Off;
    private VlanSettings _vlanSettings = new();
    private RadiusSettings _config = new();
    private IUdpRelay? _listener;
    private IReadOnlyList<IPAddress> _boundAddresses = [];
    private IDeviceCertificateAuthority? _followed; // under _followGate
    private long _caVersion; // + 1 with every Changed of the followed CA (CaTrust.Version)
    private ITimer? _tickTimer;
    private ITimer? _maintainTimer;
    private ITimer? _indexTimer;
    private ITimer? _listenRetry;
    private ITimer? _addressCheck;
    private long _lastResolveTicks;
    private long _unknownCount;
    private (string Address, string? Site, DateTime At)? _lastUnknown;
    private long _busyDrops;
    private int _tickCount;
    private int _maintainAgain; // 1 = a maintenance round was asked for
    private int _switchesDirty; // 1 = a state changed: publish at the next tick
    private int _switchesStale; // 1 = only last-request times changed: publish at most every 5 s
    private int _stateStale;
    private long _lastSwitchesPublish;
    private long _lastStatePublish;
    private volatile bool _stopping;

    public RadiusService(RadiusHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = host.Logger;
        Viewers = new ViewerScopes(host.Scopes);
        _store = new RadiusStore(host.Settings);
        _log = new RadiusLog(host.Records, host.Time, host.Logger);
        _board = new SessionBoard(host.Time);
        _index = new DeviceIndex(host.Devices, host.Time);
        _identity = new ServerIdentityKeeper(_store, host.Authority, host.Secrets, host.Time, host.Logger);
        var validator = new DeviceCertificateValidator(TrustNow, host.DeviceIds ?? new AxisDeviceId(), _index, host.Authority, () => _switches, () => _vlans, host.Time);
        _processor = new RadiusProcessor(new RadiusProtocolOptions(), validator, host.Time);
        _processor.SessionChanged += _board.Enqueue;
        _index.Missed += (_, _) => Track(Task.Run(() => RefreshIndexAsync(onlyAfterMiss: true)));
    }

    /// <summary>The device groups whose users read the page (state events per group).</summary>
    internal ViewerScopes Viewers { get; }

    internal RadiusProcessor Processor => _processor;

    internal RadiusLog Log => _log;

    internal SessionBoard Board => _board;

    internal DeviceIndex Index => _index;

    /// <summary>The listener on this server's network (null while closed).</summary>
    public IUdpRelay? Listener => _listener;

    /// <summary>The switches now.</summary>
    public SwitchTable Switches => _switches;

    // ------------------------------------------------------------------ start and stop

    /// <summary>Loads, listens, starts the workers, timers and the sites' relays; a start that fails stops what it began.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            await RefreshIndexAsync(onlyAfterMiss: false).ConfigureAwait(false);
            await MaintainAsync(ct).ConfigureAwait(false);
            _workers = [.. Enumerable.Range(0, Math.Max(1, _host.Workers)).Select(_ => Task.Run(() => WorkAsync(_cts.Token), CancellationToken.None))];
            await OpenAsync(ct).ConfigureAwait(false);
            if (_host.Sites is { } sites)
            {
                sites.SitesChanged += OnSitesChanged;
            }

            await SyncSitesAsync(ct).ConfigureAwait(false);
            _tickTimer = _host.Time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
            _maintainTimer = _host.Time.CreateTimer(_ => Track(MaintainAsync(_cts.Token)), null, MaintainInterval, MaintainInterval);
            _indexTimer = _host.Time.CreateTimer(_ => Track(RefreshIndexAsync(onlyAfterMiss: false)), null, DeviceIndex.RefreshInterval, DeviceIndex.RefreshInterval);
            _listenRetry = _host.Time.CreateTimer(_ => Track(RetryListenAsync(_cts.Token)), null, ListenRetryInterval, ListenRetryInterval);
            _addressCheck = _host.Time.CreateTimer(_ => CheckAddresses(), null, AddressCheckInterval, AddressCheckInterval);
            LogStarted(_config.Port, _switches.All.Count);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var config = await _store.ConfigAsync(ct).ConfigureAwait(false) ?? new RadiusSettings();
        if (config.CipherSuites.Count == 0 || TlsSettingsOf(config).Validate(ServerIdentityKeeper.KeyKind) is not null)
        {
            config = config with { CipherSuites = [.. TlsCipherSuites.DefaultFor(VersionOf(config.MinimumTls) ?? TlsVersion.Tls12).Order()] };
        }

        if (RadiusRules.MinimumIndex(config.MinimumTls) is null)
        {
            config = config with { MinimumTls = RadiusLimits.DefaultMinimumTls };
        }

        config = config with
        {
            LogKept = Math.Clamp(config.LogKept, RadiusLimits.MinLogKept, RadiusLimits.MaxLogKept),
            LargestFragment = Math.Clamp(config.LargestFragment, RadiusLimits.MinFragment, RadiusLimits.MaxFragment),
            Port = config.Port is >= 1 and <= 65535 ? config.Port : RadiusLimits.DefaultPort,
        };
        _config = config;
        _vlanSettings = await _store.VlansAsync(ct).ConfigureAwait(false) ?? new VlanSettings();
        _vlans = PlanOf(_vlanSettings);
        _stored = [.. await _store.SwitchesAsync(ct).ConfigureAwait(false)];
        var entries = new List<SwitchEntry>();
        foreach (var stored in _stored)
        {
            if (Unprotect(stored) is not { } secret)
            {
                LogSecretUnreadable(stored.Name);
                continue;
            }

            entries.Add(new SwitchEntry(stored.Id, stored.Name, stored.Address, stored.SiteId, secret, await ResolveQuietlyAsync(stored.Address, stored.SiteId, ct).ConfigureAwait(false)));
        }

        _switches = new SwitchTable(entries);
        await _log.LoadAsync(config.LogKept, ct).ConfigureAwait(false);
        _processor.Options = OptionsOf(config, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        if (_host.Sites is { } sites)
        {
            sites.SitesChanged -= OnSitesChanged;
        }

        Unfollow();
        foreach (var timer in new[] { _tickTimer, _maintainTimer, _indexTimer, _listenRetry, _addressCheck })
        {
            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await CloseAsync(release: false).ConfigureAwait(false);
        await _sitesGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await CloseSitesAsync(release: false).ConfigureAwait(false); // the port stays allowed at the nodes for the next start
        _sitesGate.Release();
        _queue.Writer.TryComplete();
        Task[] running;
        lock (_running)
        {
            running = [.. _running, .. _workers];
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A loop that failed or was cancelled never stops the last write of the log.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            await _log.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
            _saveGate.Dispose();
            _listenGate.Dispose();
            _sitesGate.Dispose();
            _maintainGate.Dispose();
            _index.Dispose();
            _identity.Dispose();
        }
    }

    // ------------------------------------------------------------------ datagrams

    /// <summary>
    /// One datagram from <paramref name="replyTo"/> (this server's listener or a site's relay): an unknown sender is dropped
    /// and counted, the rest waits for a worker (a full queue drops it: the switch retransmits).
    /// </summary>
    internal void Accept(Datagram datagram, IDatagramEndpoint replyTo, Guid? siteId)
    {
        if (!_host.ProActive())
        {
            return; // without OADM PRO the server answers nothing (switches fall back to their own rule)
        }

        var entry = _switches.Find(siteId, datagram.Remote.Address);
        if (entry is null)
        {
            CountUnknown(datagram.Remote, siteId);
            return;
        }

        if (!_queue.Writer.TryWrite(new Work(datagram, replyTo, siteId)))
        {
            Interlocked.Increment(ref _busyDrops);
        }
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(work, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // One datagram that failed (a reply that could not be sent) never stops a worker.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogHandleFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>Processes one datagram with the switch table of now (a switch removed or changed meanwhile counts as it is now).</summary>
    internal async Task HandleAsync(Work work, CancellationToken ct)
    {
        var table = _switches;
        var entry = table.Find(work.SiteId, work.Datagram.Remote.Address);
        if (entry is null || !_host.ProActive())
        {
            return;
        }

        var result = _processor.Process(work.Datagram.Data.Span, entry.Client, work.Datagram.Remote);
        if (!ReferenceEquals(table, _switches) && _switches.FindByKey(entry.Client.Key) is null)
        {
            _processor.EndSessions(entry.Client.Key); // removed while this datagram was processed: nothing of it stays
            return;
        }

        var runtime = _runtime.GetOrAdd(entry.Id, _ => new SwitchRuntime());
        if (runtime.Record(result, _host.Time.GetUtcNow().UtcDateTime))
        {
            Volatile.Write(ref _switchesDirty, 1);
        }
        else
        {
            Volatile.Write(ref _switchesStale, 1);
        }

        if (result.Outcome == RadiusOutcome.Dropped)
        {
            LogDropped(entry.Name, work.Datagram.Remote, result.Drop, result.Detail ?? string.Empty);
            return;
        }

        if (result.Response is { } response)
        {
            await work.ReplyTo.SendAsync(response, work.Datagram.Remote, ct).ConfigureAwait(false);
        }
    }

    private void CountUnknown(IPEndPoint remote, Guid? siteId)
    {
        var count = Interlocked.Increment(ref _unknownCount);
        string? site = null;
        if (siteId is { } id)
        {
            lock (_siteRelays)
            {
                site = _siteRelays.TryGetValue(id, out var relay) ? relay.Name : id.ToString();
            }
        }

        lock (_unknownGate)
        {
            _lastUnknown = (RadiusRules.Normalize(remote.Address).ToString(), site, _host.Time.GetUtcNow().UtcDateTime);
        }

        Volatile.Write(ref _stateStale, 1);
        if (count <= 20 || count % 1000 == 0)
        {
            LogUnknownSwitch(remote, site ?? SiteTexts.ThisServer, count);
        }
    }

    private async Task ReceiveLoopAsync(IUdpRelay relay, Guid? siteId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram datagram;
            try
            {
                datagram = await relay.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ChannelClosedException)
            {
                return;
            }
#pragma warning disable CA1031 // A failed receive must not end the server; the reason is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReceiveFailed(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _host.Time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Accept(datagram, relay, siteId);
        }
    }

    // ------------------------------------------------------------------ the tick: sessions, log, events

    /// <summary>
    /// Every 500 ms: sessions past their time end (every second), the session changes go to the pages, ended sessions into
    /// the log and the event rules, and the switch rows and the state are published when they changed.
    /// </summary>
    internal void Tick()
    {
        try
        {
            if (Interlocked.Increment(ref _tickCount) % 2 == 0)
            {
                _processor.ExpireSessions();
            }

            var (changed, ended) = _board.Drain(_switches.FindByKey, _index);
            if (ended.Count > 0)
            {
                Record(ended);
            }

            if (changed.Count > 0)
            {
                PublishRows(RadiusTopics.Sessions, changed, r => r.DeviceId);
            }

            var now = _host.Time.GetTimestamp();
            if (Interlocked.Exchange(ref _switchesDirty, 0) == 1
                || (Volatile.Read(ref _switchesStale) == 1 && _host.Time.GetElapsedTime(_lastSwitchesPublish) >= QuietPublish))
            {
                Volatile.Write(ref _switchesStale, 0);
                _lastSwitchesPublish = now;
                PublishSwitches();
            }

            if (Volatile.Read(ref _stateStale) == 1 && _host.Time.GetElapsedTime(_lastStatePublish) >= QuietPublish)
            {
                Volatile.Write(ref _stateStale, 0);
                PublishState();
            }
        }
#pragma warning disable CA1031 // The tick runs again in 500 ms; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTickFailed(ex);
        }
    }

    /// <summary>Ended sessions: one log row each, published, and the event rules' events.</summary>
    private void Record(IReadOnlyList<EndedSession> ended)
    {
        var rows = new List<LogRow>(ended.Count);
        foreach (var session in ended)
        {
            var snapshot = session.Snapshot;
            var tag = snapshot.Tag as CheckedCertificate;
            var authenticated = snapshot.State == EapSessionState.Authenticated;
            var leaf = snapshot.ClientCertificates is { Count: > 0 } chain ? chain[0] : null;
            var row = _log.Add(new LogRow
            {
                TimeUtc = (snapshot.Ended ?? _host.Time.GetUtcNow()).UtcDateTime,
                SwitchName = session.SwitchName,
                Port = snapshot.Port,
                Mac = snapshot.Mac.Display,
                Identity = snapshot.EapIdentity,
                DeviceId = session.Row.DeviceId,
                DeviceName = session.Row.DeviceName,
                DeviceGuess = session.Row.DeviceGuess,
                IpAddress = session.Row.IpAddress,
                Result = authenticated ? LogResults.Authenticated : LogResults.Failed,
                Reason = authenticated ? null : snapshot.Reason ?? snapshot.Text,
                Warning = snapshot.Warning,
                Vlan = snapshot.VlanId,
                CertificateSerial = tag?.CertificateSerial ?? SerialOf(leaf),
            }, leaf);
            rows.Add(row);
            Fire(snapshot, session.SwitchName, tag?.DeviceId);
            if (!_logger.IsEnabled(LogLevel.Information))
            {
                continue;
            }

            if (authenticated)
            {
                var vlan = snapshot.VlanId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "none";
                LogAuthenticated(session.SwitchName, snapshot.Port, row.DeviceName, vlan, snapshot.Warning ?? string.Empty);
            }
            else
            {
                LogFailed(session.SwitchName, snapshot.Port, row.DeviceName.Length > 0 ? row.DeviceName : snapshot.Mac.Display, row.Reason ?? string.Empty);
            }
        }

        rows.Reverse(); // newest first
        PublishRows(RadiusTopics.Log, rows, r => r.DeviceId);
    }

    /// <summary>"Device authenticated" and "Authentication failed" (not for a session the switch abandoned).</summary>
    private void Fire(EapSessionSnapshot snapshot, string switchName, Guid? deviceId)
    {
        if (_host.Publisher is not { } publisher)
        {
            return;
        }

        switch (snapshot.State)
        {
            case EapSessionState.Authenticated:
                publisher.Fire(AuthenticatedEvent, deviceId, new Dictionary<string, string>
                {
                    ["switch"] = switchName,
                    ["port"] = snapshot.Port,
                    ["vlan"] = snapshot.VlanId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                });
                break;
            case EapSessionState.Failed or EapSessionState.TimedOut:
                publisher.Fire(FailedEvent, deviceId, new Dictionary<string, string>
                {
                    ["switch"] = switchName,
                    ["port"] = snapshot.Port,
                    ["reason"] = snapshot.Reason ?? snapshot.Text,
                });
                break;
        }
    }

    private static string? SerialOf(byte[]? der)
    {
        if (der is null)
        {
            return null;
        }

        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der);
            return CertificateSerials.Normalize(certificate.SerialNumber);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rows of devices as one event per chunk: each user's page gets the rows of the devices the user sees, rows without a
    /// device (<see cref="Guid.Empty"/>) only users with all devices.
    /// </summary>
    private void PublishRows<T>(string topic, IReadOnlyList<T> rows, Func<T, Guid?> deviceOf)
    {
        if (_host.Events is not { } events)
        {
            return;
        }

        for (var start = 0; start < rows.Count; start += EventChunk)
        {
            var chunk = rows.Skip(start).Take(EventChunk).ToList();
            events.Publish(topic, RadiusJson.Serialize(chunk), PluginEventAudience.DeviceRows([.. chunk.Select(r => deviceOf(r) ?? Guid.Empty)]));
        }
    }

    /// <summary>An event without device data for every page: users with all devices and each group that reads the page.</summary>
    private void PublishToEveryPage(string topic, string payloadJson)
    {
        if (_host.Events is not { } events)
        {
            return;
        }

        events.Publish(topic, payloadJson, PluginEventAudience.AllDevices);
        foreach (var key in Viewers.Keys())
        {
            events.Publish(topic, payloadJson, PluginEventAudience.DeviceScope(key));
        }
    }

    /// <summary>
    /// The switch rows go to users with all devices only: a user limited to a group reads the switches of the group's routes
    /// with <see cref="RadiusMethods.ListSwitches"/> (the event carries every switch).
    /// </summary>
    private void PublishSwitches() => _host.Events?.Publish(RadiusTopics.Switches, RadiusJson.Serialize(ListSwitches()), PluginEventAudience.AllDevices);

    private void PublishState()
    {
        _lastStatePublish = _host.Time.GetTimestamp();
        if (_host.Events is not { } events)
        {
            return;
        }

        events.Publish(RadiusTopics.State, RadiusJson.Serialize(GetState(hasAllDevices: true)), PluginEventAudience.AllDevices);
        var keys = Viewers.Keys();
        if (keys.Count > 0)
        {
            var scoped = RadiusJson.Serialize(GetState(hasAllDevices: false));
            foreach (var key in keys)
            {
                events.Publish(RadiusTopics.State, scoped, PluginEventAudience.DeviceScope(key));
            }
        }
    }

    // ------------------------------------------------------------------ state and settings

    public RadiusState GetState(bool hasAllDevices)
    {
        var config = _config;
        var listener = _listener;
        string text;
        var kind = ServiceStatus.Error;
        if (!_host.ProActive())
        {
            text = RadiusTexts.NotPro;
        }
        else if (_host.Authority() is not { IsReady: true })
        {
            text = RadiusTexts.NoCa;
        }
        else if (listener is null)
        {
            text = !InterfaceOptions.IsAll(config.ListenOn) && ListenInterface() is null ? RadiusTexts.NotAvailable(config.ListenOnName ?? config.ListenOn) : "Stopped";
            kind = ServiceStatus.Neutral;
        }
        else
        {
            switch (listener.State)
            {
                case RelayState.Running when _identity.Identity is null:
                    text = RadiusTexts.NoCa;
                    break;
                case RelayState.Running:
                    text = RadiusTexts.Running(config.Port);
                    kind = ServiceStatus.Ok;
                    break;
                case RelayState.PortInUse:
                    text = RadiusTexts.PortInUse(config.Port);
                    break;
                case RelayState.NoPermission:
                    text = PortBindErrors.PermissionText(config.Port);
                    break;
                default:
                    text = listener.StateText;
                    break;
            }
        }

        List<SiteRef> notAllowed;
        lock (_siteRelays)
        {
            notAllowed = [.. _siteRelays.Where(r => r.Value.Relay.State == RelayState.WaitingForApproval)
                .Select(r => new SiteRef(r.Key, r.Value.Name)).OrderBy(s => s.SiteName, StringComparer.CurrentCultureIgnoreCase)];
        }

        UnknownRequests unknown;
        lock (_unknownGate)
        {
            unknown = new UnknownRequests(Interlocked.Read(ref _unknownCount), _lastUnknown?.Address, _lastUnknown?.Site, _lastUnknown?.At);
        }

        var vlans = _vlanSettings;
        if (!hasAllDevices)
        {
            // A user limited to a group sees Sessions and Log only: the status, never the server-wide settings, the
            // server's interfaces and addresses, the sites or the senders that are no switch (docs/spec "Device groups").
            return new RadiusState
            {
                Running = kind == ServiceStatus.Ok,
                StatusText = text,
                StatusKind = kind,
                HasAllDevices = false,
            };
        }

        return new RadiusState
        {
            Running = kind == ServiceStatus.Ok,
            StatusText = text,
            StatusKind = kind,
            Settings = config with { VlansEnabled = vlans.Enabled, DefaultVlan = vlans.DefaultVlan },
            CipherSuitesListed = CipherSuites,
            Interfaces = ListInterfaces(),
            ServerCertificate = _identity.Info,
            SitesNotAllowed = notAllowed,
            HasAllDevices = true,
            UnknownRequests = unknown,
        };
    }

    /// <summary>Every cipher suite the server can offer, for Advanced.</summary>
    public static IReadOnlyList<CipherSuiteOption> CipherSuites { get; } =
        [.. TlsCipherSuites.All.Select(s => new CipherSuiteOption(s.Id, s.Name, s.Group == CipherSuiteGroup.LegacyCbc, s.Group == CipherSuiteGroup.Tls13)
        {
            Usable = s.Key is null || s.Key == ServerIdentityKeeper.KeyKind,
        })];

    /// <summary>Save of General and Advanced (the VLAN fields are ignored). Throws <see cref="ArgumentException"/> with the field's message.</summary>
    public async Task<RadiusState> SaveSettingsAsync(RadiusSettings request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = request with { CipherSuites = [.. (request.CipherSuites ?? []).Distinct()], ListenOn = string.IsNullOrWhiteSpace(request.ListenOn) ? InterfaceOptions.AllId : request.ListenOn.Trim() };
        if (RadiusRules.Validate(settings, CipherSuites) is { } error)
        {
            throw RadiusErrors.Field(error.Field, error.Message);
        }

        if (TlsSettingsOf(settings).Validate(ServerIdentityKeeper.KeyKind) is { } tlsError)
        {
            throw RadiusErrors.Field("cipherSuites", tlsError);
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old = _config;
            if (InterfaceOptions.IsAll(settings.ListenOn))
            {
                settings = settings with { ListenOn = InterfaceOptions.AllId, ListenOnName = InterfaceOptions.AllLabel };
            }
            else if (UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, settings.ListenOn, StringComparison.Ordinal)) is { } nic)
            {
                settings = settings with { ListenOnName = nic.Name };
            }
            else if (string.Equals(settings.ListenOn, old.ListenOn, StringComparison.Ordinal))
            {
                settings = settings with { ListenOnName = old.ListenOnName }; // gone meanwhile: kept, it listens again when it is back
            }
            else
            {
                throw RadiusErrors.Field("listenOn", RadiusTexts.InterfaceGone);
            }

            settings = settings with { VlansEnabled = false, DefaultVlan = null };
            await _store.SaveConfigAsync(settings, ct).ConfigureAwait(false);
            _config = settings;
            _processor.Options = OptionsOf(settings, _identity.Identity);
            _log.SetKept(settings.LogKept);
            var portChanged = settings.Port != old.Port;
            if (portChanged || !string.Equals(settings.ListenOn, old.ListenOn, StringComparison.Ordinal))
            {
                await CloseAsync(release: true).ConfigureAwait(false);
                await OpenAsync(ct).ConfigureAwait(false);
            }

            if (portChanged)
            {
                await _sitesGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await CloseSitesAsync(release: true).ConfigureAwait(false);
                }
                finally
                {
                    _sitesGate.Release();
                }

                await SyncSitesAsync(ct).ConfigureAwait(false);
            }

            LogSettingsSaved(settings.Port, settings.MinimumTls, settings.CipherSuites.Count);
        }
        finally
        {
            _saveGate.Release();
        }

        Track(MaintainAsync(_cts.Token)); // the server certificate follows the new listen addresses
        PublishState();
        return GetState(hasAllDevices: true);
    }

    /// <summary>
    /// The VLAN rules with the tag choices of the managed devices and the rules. A user limited to a group gets empty VLAN
    /// settings: the rules are server-wide (docs/spec/device-scope.md "Server-wide services") and their tags are protected.
    /// </summary>
    public VlanSettings GetVlans(bool hasAllDevices)
    {
        if (!hasAllDevices)
        {
            return new VlanSettings();
        }

        var vlans = _vlanSettings;
        var tags = VlanPlan.TagsOf(vlans).Concat(_index.Tags());
        return vlans with
        {
            TagChoices = [.. tags.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(t => new TagChoice(t, t))],
        };
    }

    /// <summary>
    /// The plan of <paramref name="vlans"/> with its tags protected by the host (only users with all devices add or remove
    /// them on devices, docs/spec/radius-server.md "VLANs"); without the host's protection the tag rules match nothing.
    /// </summary>
    private VlanPlan PlanOf(VlanSettings vlans)
    {
        if (_host.ProtectedTags is not { } protectedTags)
        {
            return new VlanPlan(vlans, tagsProtected: false);
        }

        protectedTags.Protect(VlanPlan.TagsOf(vlans), RadiusTexts.VlanTag, RadiusTexts.VlanTagChange);
        return new VlanPlan(vlans);
    }

    /// <summary>Save of the VLAN rules; applies to the next authentication of each device.</summary>
    public async Task<VlanSettings> SaveVlansAsync(VlanSettings request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rules = (request.Rules ?? []).Select(r => r with { Kind = r.Kind?.Trim() ?? string.Empty, RefId = r.RefId?.Trim() ?? string.Empty }).ToList();
        var vlans = request with { Rules = rules };
        if (RadiusRules.Validate(vlans) is { } error)
        {
            throw RadiusErrors.Field(error.Field, error.Message);
        }

        var named = new List<VlanRule>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            string name;
            if (rule.Kind == VlanRuleKinds.Site)
            {
                var id = Guid.Parse(rule.RefId);
                name = id == Guid.Empty ? DeviceGroupMessages.ThisServer
                    : _host.Sites?.List.FirstOrDefault(s => s.Id == id)?.Name
                      ?? (_vlanSettings.Rules.FirstOrDefault(r => r.Kind == VlanRuleKinds.Site && r.RefId == rule.RefId)?.RefName
                          ?? throw RadiusErrors.Field(string.Create(CultureInfo.InvariantCulture, $"rules[{i}].refId"), RadiusTexts.SiteGone));
                rule = rule with { RefId = id.ToString() };
            }
            else
            {
                name = rule.RefId;
            }

            named.Add(rule with { RefName = name });
        }

        vlans = vlans with { Rules = named, TagChoices = [] };
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The tags of the old and the new rules stay protected while the save runs (a failed save keeps the old plan).
            _host.ProtectedTags?.Protect([.. VlanPlan.TagsOf(_vlanSettings), .. VlanPlan.TagsOf(vlans)], RadiusTexts.VlanTag, RadiusTexts.VlanTagChange);
            try
            {
                await _store.SaveVlansAsync(vlans, ct).ConfigureAwait(false);
                _vlanSettings = vlans;
            }
            finally
            {
                _vlans = PlanOf(_vlanSettings);
            }
        }
        finally
        {
            _saveGate.Release();
        }

        LogVlansSaved(vlans.Enabled, named.Count);
        PublishState();
        return GetVlans(hasAllDevices: true);
    }

    // ------------------------------------------------------------------ switches

    /// <summary>The switches; <paramref name="covers"/> (a user limited to a group): only those on the routes the group names.</summary>
    public SwitchList ListSwitches(Func<Guid?, bool>? covers = null)
    {
        var stored = Volatile.Read(ref _stored);
        var rows = new List<SwitchRow>(stored.Count);
        foreach (var s in stored)
        {
            if (covers is null || covers(s.SiteId))
            {
                rows.Add(RowOf(s));
            }
        }

        return new SwitchList(rows);
    }

    /// <summary>Adds or edits a switch; applies to the running server at once (atomic table swap).</summary>
    public async Task<SwitchRow> SaveSwitchAsync(SaveSwitchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_host.Secrets is not { } secrets)
        {
            throw new InvalidOperationException(RadiusTexts.NoSecretStore);
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        StoredSwitch saved;
        SwitchEntry? previous;
        try
        {
            var isNew = request.Id is null;
            var existing = isNew ? null : _stored.FirstOrDefault(s => s.Id == request.Id) ?? throw new KeyNotFoundException(RadiusTexts.SwitchGone);
            if (isNew && _stored.Count >= RadiusLimits.MaxSwitches)
            {
                throw RadiusErrors.Field("name", RadiusRules.TooManySwitches);
            }

            var others = _stored.Select(s => new SwitchRow { Id = s.Id, Name = s.Name, Address = s.Address, SiteId = s.SiteId });
            if (RadiusRules.Validate(request, others, isNew) is { } error)
            {
                throw RadiusErrors.Field(error.Field, error.Message);
            }

            if (request.SiteId is { } site && existing?.SiteId != site && _host.Sites?.List.Any(s => s.Id == site) != true)
            {
                throw RadiusErrors.Field("siteId", RadiusTexts.SiteGone);
            }

            var address = request.Address!.Trim();
            var addresses = await ResolveAsync(address, request.SiteId, ct).ConfigureAwait(false);
            var id = existing?.Id ?? Guid.NewGuid();
            byte[] secretBytes;
            string protectedSecret;
            if (!string.IsNullOrEmpty(request.Secret))
            {
                secretBytes = System.Text.Encoding.UTF8.GetBytes(request.Secret);
                protectedSecret = secrets.Protect(request.Secret, SecretPurposePrefix + id.ToString("N"));
            }
            else
            {
                secretBytes = Unprotect(existing!) ?? throw RadiusErrors.Field("secret", RadiusRules.EnterSecret);
                protectedSecret = existing!.SecretProtected;
            }

            saved = new StoredSwitch { Id = id, Name = request.Name!.Trim(), Address = address, SiteId = request.SiteId, SecretProtected = protectedSecret };
            var list = _stored.Where(s => s.Id != id).ToList();
            var index = existing is null ? list.Count : _stored.IndexOf(existing);
            list.Insert(Math.Min(index, list.Count), saved);
            await _store.SaveSwitchesAsync(list, ct).ConfigureAwait(false);
            _stored = list;
            var table = _switches;
            previous = table.ById.GetValueOrDefault(id);
            var entry = new SwitchEntry(id, saved.Name, saved.Address, saved.SiteId, secretBytes, addresses);
            _switches = new SwitchTable([.. table.All.Where(s => s.Id != id), entry]);
            if (previous is not null && (previous.SiteId != entry.SiteId || !previous.Addresses.ToHashSet().SetEquals(entry.Addresses)))
            {
                _processor.EndSessions(previous.Client.Key); // another switch now: its open sessions end
                _runtime.TryRemove(id, out _);
            }

            var runtime = _runtime.GetOrAdd(id, _ => new SwitchRuntime());
            runtime.ServerAddress = await ServerAddressForAsync(entry, ct).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var siteName = SiteName(saved.SiteId);
                LogSwitchSaved(isNew ? "added" : "changed", saved.Name, saved.Address, siteName);
            }
        }
        finally
        {
            _saveGate.Release();
        }

        await SyncSitesAsync(ct).ConfigureAwait(false);
        PublishSwitches();
        return RowOf(saved);
    }

    /// <summary>Removes a switch: its open sessions end, its next request is dropped as from an unknown switch.</summary>
    public async Task DeleteSwitchAsync(Guid id, CancellationToken ct)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _stored.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException(RadiusTexts.SwitchGone);
            var list = _stored.Where(s => s.Id != id).ToList();
            await _store.SaveSwitchesAsync(list, ct).ConfigureAwait(false);
            _stored = list;
            _switches = new SwitchTable([.. _switches.All.Where(s => s.Id != id)]);
            _processor.EndSessions(SwitchEntry.KeyOf(id));
            _runtime.TryRemove(id, out _);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var siteName = SiteName(existing.SiteId);
                LogSwitchSaved("removed", existing.Name, existing.Address, siteName);
            }
        }
        finally
        {
            _saveGate.Release();
        }

        await SyncSitesAsync(ct).ConfigureAwait(false);
        PublishSwitches();
    }

    /// <summary>A random shared secret of 24 letters and digits (accepted by every switch); nothing is stored.</summary>
    public static string GenerateSecret() =>
        new(RandomNumberGenerator.GetItems<char>("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 24));

    private SwitchRow RowOf(StoredSwitch stored)
    {
        var runtime = _runtime.GetValueOrDefault(stored.Id);
        var state = SwitchStates.NoRequests;
        if (stored.SiteId is { } site && !SiteOnline(site))
        {
            state = SwitchStates.SiteOffline;
        }
        else if (runtime is { WrongSecret: true })
        {
            state = SwitchStates.WrongSecret;
        }
        else if (runtime is { Answered: true })
        {
            state = SwitchStates.Answered;
        }

        return new SwitchRow
        {
            Id = stored.Id,
            Name = stored.Name,
            Address = stored.Address,
            SiteId = stored.SiteId,
            SiteName = SiteName(stored.SiteId),
            State = state,
            LastRequestUtc = runtime?.LastRequestUtc,
            DroppedCount = runtime?.Dropped ?? 0,
            MacOnlyCount = runtime?.MacOnly ?? 0,
            RadiusServerAddress = runtime?.ServerAddress,
        };
    }

    private bool SiteOnline(Guid siteId)
    {
        if (_host.Sites?.List.FirstOrDefault(s => s.Id == siteId) is not { IsOnline: true })
        {
            return false;
        }

        lock (_siteRelays)
        {
            return !_siteRelays.TryGetValue(siteId, out var relay) || relay.Relay.State != RelayState.SiteOffline;
        }
    }

    private string SiteName(Guid? siteId) =>
        siteId is { } id ? _host.Sites?.List.FirstOrDefault(s => s.Id == id)?.Name ?? SiteTexts.RemovedSite(id.ToString()) : DeviceGroupMessages.ThisServer;

    private byte[]? Unprotect(StoredSwitch stored)
    {
        if (_host.Secrets is not { } secrets || string.IsNullOrEmpty(stored.SecretProtected))
        {
            return null;
        }

        try
        {
            var secret = secrets.Unprotect(stored.SecretProtected, SecretPurposePrefix + stored.Id.ToString("N"));
            return secret.Length == 0 ? null : System.Text.Encoding.UTF8.GetBytes(secret);
        }
#pragma warning disable CA1031 // A secret encrypted with another master key (a restore elsewhere) only takes the switch out until it is saved again.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>The addresses of a switch: an IP address as is, a host name resolved on its route (this server or the site).</summary>
    private async Task<IReadOnlyList<IPAddress>> ResolveAsync(string address, Guid? siteId, CancellationToken ct)
    {
        if (IPAddress.TryParse(address, out var ip))
        {
            return [RadiusRules.Normalize(ip)];
        }

        IReadOnlyList<IPAddress> found = [];
        if (_host.Network is { } network)
        {
            try
            {
                found = await network.ForSite(siteId).ResolveAsync(address, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw RadiusErrors.Field("address", ex.Message, ex); // e.g. "Site offline: Building B"
            }
        }

        return found.Count > 0 ? found : throw RadiusErrors.Field("address", RadiusTexts.HostNameUnknown);
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveQuietlyAsync(string address, Guid? siteId, CancellationToken ct)
    {
        try
        {
            return await ResolveAsync(address, siteId, ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return []; // tried again every 10 minutes
        }
    }

    /// <summary>The address the switch must use as RADIUS server: the chosen interface's, else OADM's towards the switch (at a site: the node's).</summary>
    private async Task<string?> ServerAddressForAsync(SwitchEntry entry, CancellationToken ct)
    {
        if (entry.SiteId is null && ListenInterface() is { } nic)
        {
            return (nic.PrimaryIpv4 ?? nic.PrimaryAddress)?.ToString();
        }

        if (_host.Network is not { } network)
        {
            return null;
        }

        try
        {
            var towards = entry.Addresses.Count > 0 ? entry.Addresses[0].ToString() : entry.Address;
            return (await network.ForSite(entry.SiteId).LocalAddressTowardsAsync(towards, ct).ConfigureAwait(false))?.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // e.g. the site is offline: shown again after the next round
        }
    }

    /// <summary>Every 10 minutes: host names resolved again and the RADIUS server address of each switch.</summary>
    private async Task ResolveSwitchesAsync(CancellationToken ct)
    {
        var table = _switches;
        var changed = new List<SwitchEntry>();
        foreach (var entry in table.All)
        {
            var current = entry;
            if (!IPAddress.TryParse(entry.Address, out _))
            {
                var addresses = await ResolveQuietlyAsync(entry.Address, entry.SiteId, ct).ConfigureAwait(false);
                if (addresses.Count > 0 && !addresses.Select(RadiusRules.Normalize).ToHashSet().SetEquals(entry.Addresses))
                {
                    current = entry.WithAddresses(addresses);
                    changed.Add(current);
                }
            }

            _runtime.GetOrAdd(entry.Id, _ => new SwitchRuntime()).ServerAddress = await ServerAddressForAsync(current, ct).ConfigureAwait(false);
        }

        if (changed.Count > 0)
        {
            await _saveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var byId = changed.ToDictionary(c => c.Id);
                _switches = new SwitchTable([.. _switches.All.Select(s => byId.TryGetValue(s.Id, out var c) && ReferenceEquals(table.ById.GetValueOrDefault(s.Id), s) ? c : s)]);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        Volatile.Write(ref _switchesDirty, 1);
    }

    // ------------------------------------------------------------------ sessions, log, revocations

    internal IReadOnlyList<SessionRow> ListSessions(ViewedSet? view) =>
        view is null ? _board.Rows() : [.. _board.Rows().Where(r => view.Contains(r.DeviceId))];

    internal LogPage ListLog(LogQuery query, ViewedSet? view)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _log.Page(query.Search, query.Offset, query.Limit, view is null ? null : view.Contains, TimeZoneOf(query.TimeZoneId));
    }

    /// <summary>The page's time zone for the log search; null when it is not given or not known on this server.</summary>
    private static TimeZoneInfo? TimeZoneOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The client certificate of a log row the caller sees; any other row answers like one no longer kept.</summary>
    internal async Task<LogCertificate> LogCertificateAsync(long id, ViewedSet? view, CancellationToken ct)
    {
        var row = _log.Find(id);
        if (row is null || (view is not null && !view.Contains(row.DeviceId)))
        {
            throw new KeyNotFoundException(RadiusTexts.LogEntryGone);
        }

        var der = await _log.CertificateAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(RadiusTexts.LogEntryGone);
        return new LogCertificate(Convert.ToBase64String(der));
    }

    /// <summary>The bound sites; for a group user (<paramref name="view"/>) the sites with devices of the group.</summary>
    internal SiteList ListSites(ViewedSet? view) =>
        new([.. (_host.Sites?.List ?? []).Where(s => view is null || view.CoversSite(s.Id))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new SiteRef(s.Id, s.Name))]);

    /// <summary>One page of PKI's list of issued certificates; <paramref name="deviceIds"/>: only these devices (a group user).</summary>
    internal async Task<CertificateListPage> ListCertificatesAsync(CertificateListQuery query, IReadOnlySet<Guid>? deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Math.Max(0, query.Offset);
        if (_host.Authority() is not { } ca)
        {
            return new CertificateListPage { Offset = offset };
        }

        var page = await ca.ListIssuedAsync(new IssuedCertificateQuery
        {
            State = query.State,
            Purpose = string.IsNullOrWhiteSpace(query.Purpose) ? null : query.Purpose,
            Source = string.IsNullOrWhiteSpace(query.Source) ? null : query.Source,
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            DeviceIds = deviceIds,
            Offset = offset,
            Limit = Math.Clamp(query.Limit, 1, IssuedCertificateQuery.MaxLimit),
        }, ct).ConfigureAwait(false);
        return new CertificateListPage { Rows = page.Rows, Total = page.Total, Offset = offset };
    }

    /// <summary>
    /// An issued certificate (DER; empty when OADM kept no copy). For a group user (<paramref name="deviceIds"/>) only one of
    /// the group's devices; any other answers like an unknown one.
    /// </summary>
    internal async Task<LogCertificate> IssuedCertificateAsync(string serialNumber, IReadOnlySet<Guid>? deviceIds, CancellationToken ct)
    {
        var serial = CertificateSerials.Normalize(serialNumber);
        if (serial.Length == 0 || _host.Authority() is not { } ca)
        {
            throw new KeyNotFoundException(RadiusTexts.CertificateGone);
        }

        if (deviceIds is not null)
        {
            var page = await ca.ListIssuedAsync(new IssuedCertificateQuery { Search = serial, DeviceIds = deviceIds, Limit = IssuedCertificateQuery.MaxLimit }, ct).ConfigureAwait(false);
            if (!page.Rows.Any(r => CertificateSerials.Normalize(r.SerialNumber) == serial))
            {
                throw new KeyNotFoundException(RadiusTexts.CertificateGone);
            }
        }

        var der = await ca.GetIssuedCertificateAsync(serial, ct).ConfigureAwait(false);
        return new LogCertificate(der is null ? string.Empty : Convert.ToBase64String(der));
    }

    public void DeleteLog(string userName)
    {
        _log.Clear();
        LogLogCleared(userName);
        PublishToEveryPage(RadiusTopics.LogCleared, "{}");
    }

    /// <summary>
    /// One page of PKI's revocation list (newest first), the rows of <paramref name="view"/>'s devices (null: all) that match
    /// the search (serial number in any notation, device, reason, user; case-insensitive). One pass.
    /// </summary>
    internal RevokedPage ListRevoked(RevokedQuery query, ViewedSet? view)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, RadiusLimits.MaxRevokedPage);
        var text = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var serialText = text is null ? null : CertificateSerials.Normalize(text);
        var revocations = _host.Authority()?.Revocations ?? [];
        var rows = new List<RevokedRow>(Math.Min(limit, revocations.Count));
        var total = 0;
        foreach (var r in revocations)
        {
            if (view is not null && !view.Contains(r.DeviceId))
            {
                continue;
            }

            var deviceName = r.DeviceId is { } id ? DeviceIndex.NameOf(_index.ById(id)) : string.Empty;
            var reason = CertificateRevocation.ReasonText(r.Reason);
            if (text is not null
                && !r.SerialNumber.Contains(serialText!, StringComparison.OrdinalIgnoreCase)
                && !deviceName.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !reason.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !r.UserName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (total >= offset && rows.Count < limit)
            {
                rows.Add(new RevokedRow
                {
                    SerialNumber = r.SerialNumber,
                    RevokedUtc = r.RevokedUtc,
                    Reason = reason,
                    UserName = r.UserName,
                    DeviceId = r.DeviceId,
                    DeviceName = deviceName,
                    NotAfterUtc = r.NotAfterUtc,
                });
            }

            total++;
        }

        return new RevokedPage(rows) { Total = total };
    }

    /// <summary>Revokes through PKI (the one revocation list).</summary>
    public async Task<RevokeReply> RevokeAsync(RevokeRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serials = (request.Serials ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(CertificateSerials.Normalize).Distinct(StringComparer.Ordinal).ToList();
        if (serials.Count is 0 or > 5000)
        {
            throw RadiusErrors.Field("serials", RadiusTexts.RevokeCount);
        }

        var reason = request.Reason switch
        {
            RevokeReasons.KeyCompromised => RevocationReason.KeyCompromised,
            RevokeReasons.DeviceReplaced => RevocationReason.DeviceReplaced,
            RevokeReasons.DeviceRemoved => RevocationReason.DeviceRemoved,
            RevokeReasons.Other => RevocationReason.Other,
            _ => throw RadiusErrors.Field("reason", RadiusTexts.ChooseReason),
        };
        var ca = _host.Authority() ?? throw new InvalidOperationException(RadiusTexts.NoCa);
        if (await FirstOtherUseAsync(ca, serials, ct).ConfigureAwait(false) is not null)
        {
            throw RadiusErrors.Field("serials", RadiusTexts.OnlyDot1x);
        }

        var result = await ca.RevokeAsync(serials, reason, userName, ct).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var reasonText = CertificateRevocation.ReasonText(reason);
            LogRevoked(result.Revoked, reasonText, userName);
        }
        return new RevokeReply(result.Revoked, result.AlreadyRevoked, result.NotFound);
    }

    /// <summary>
    /// The first serial that is no IEEE 802.1X certificate, else null, in one pass (docs/spec/radius-server.md "Revoke"): PKI
    /// lists it with another use (HTTPS, MQTT, RTSPS); or PKI does not list it and not every client certificate of this
    /// server's log with that serial was issued by an OADM CA (an Axis device ID certificate never counts: revoking its
    /// serial would lock the camera out for good), or the log has none.
    /// </summary>
    private async Task<string?> FirstOtherUseAsync(IDeviceCertificateAuthority ca, List<string> serials, CancellationToken ct)
    {
        var unlisted = new List<string>();
        foreach (var serial in serials)
        {
            switch (ca.PurposeOf(serial))
            {
                case null:
                    unlisted.Add(serial);
                    break;
                case DeviceCertificatePurposes.Dot1x:
                    break;
                default:
                    return serial;
            }
        }

        if (unlisted.Count == 0)
        {
            return null;
        }

        var logged = _log.CertificatesOf(unlisted.ToHashSet(StringComparer.Ordinal));
        var trust = TrustNow(); // the current and the previous OADM CAs
        var now = _host.Time.GetUtcNow();
        foreach (var serial in unlisted)
        {
            if (!logged.TryGetValue(serial, out var fingerprints))
            {
                return serial;
            }

            foreach (var fingerprint in fingerprints)
            {
                if (await _log.CertificateByFingerprintAsync(fingerprint, ct).ConfigureAwait(false) is not { } der || !IssuedByOadmCa(trust, der, now))
                {
                    return serial;
                }
            }
        }

        return null;
    }

    /// <summary>The logged certificate chains to an OADM CA that issued it directly (checked within its own validity).</summary>
    private static bool IssuedByOadmCa(CaTrust trust, byte[] der, DateTimeOffset now)
    {
        X509Certificate2 leaf;
        try
        {
            leaf = X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException)
        {
            return false;
        }

        using (leaf)
        {
            var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime());
            return DeviceCertificateValidator.ChainsTo(trust, leaf, [], notAfter < now ? notAfter : now);
        }
    }

    // ------------------------------------------------------------------ maintenance: CA, server certificate, devices

    /// <summary>
    /// Every minute and after the CA changed: the trusted CAs, the server certificate (made again for new names, a new CA or
    /// 30 days before it ends) and, every 10 minutes, the switches' host names.
    /// </summary>
    internal async Task MaintainAsync(CancellationToken ct)
    {
        // A request while a round runs (a CA change in the middle of it) is never lost: the running round goes again.
        Volatile.Write(ref _maintainAgain, 1);
        while (Volatile.Read(ref _maintainAgain) == 1)
        {
            try
            {
                if (!await _maintainGate.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    return; // the round that holds the gate sees the flag
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _maintainAgain, 0) == 1)
                {
                    await MaintainOnceAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _maintainGate.Release();
            }
        }
    }

    private async Task MaintainOnceAsync(CancellationToken ct)
    {
        try
        {
            Follow();
            var ca = _host.Authority();
            var trust = _trust;
            if (!ReferenceEquals(ca, trust.Authority) || CaTrust.KeyOf(ca) != trust.Key)
            {
                // Once a minute the hashes too: previous CAs that changed without a Changed event.
                _trust = CaTrust.Build(ca, Volatile.Read(ref _caVersion));
            }

            try
            {
                if (await _identity.EnsureAsync(CertificateNames(), ct).ConfigureAwait(false))
                {
                    _processor.Options = OptionsOf(_config, _identity.Identity);
                    PublishState();
                }
            }
            catch (Exception ex) when (ex is CertificateRequestRefusedException or CryptographicException or InvalidOperationException)
            {
                LogCertificateFailed(ex.Message);
            }

            if (_lastResolveTicks == 0 || _host.Time.GetElapsedTime(_lastResolveTicks) >= ResolveInterval)
            {
                _lastResolveTicks = _host.Time.GetTimestamp();
                await ResolveSwitchesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
#pragma warning disable CA1031 // Tried again in a minute; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMaintainFailed(ex);
        }
    }

    /// <summary>
    /// The names of the server certificate: the listen addresses (IPv4; with All interfaces those of every card that is up,
    /// without self-assigned 169.254 addresses, which come and go with a cable) and the host name. The keeper makes no new
    /// certificate while the names only shrink (a card went down), so the certificate does not change with every cable.
    /// </summary>
    private List<string> CertificateNames()
    {
        var nic = ListenInterface();
        var cards = nic is null ? UsableInterfaces() : [nic];
        var names = cards.SelectMany(n => n.Addresses).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !IsSelfAssigned(a))
            .Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(_host.HostName))
        {
            names.Add(_host.HostName.Trim());
        }

        return names;
    }

    /// <summary>
    /// The trusted OADM CAs for a handshake: rebuilt at once when PKI's CA is gone or changed since the last round, so a
    /// stopped PKI or a replaced CA never leaves the old one trusted until the next minute (fail closed).
    /// </summary>
    private CaTrust TrustNow()
    {
        var trust = _trust;
        var ca = _host.Authority();
        if (ReferenceEquals(ca, trust.Authority) && trust.Version == Volatile.Read(ref _caVersion))
        {
            return trust; // the same offer, no Changed since the build: two reads, no hashing per handshake
        }

        Follow(); // a new offer: its Changed events count from now on
        trust = CaTrust.Build(ca, Volatile.Read(ref _caVersion));
        _trust = trust;
        return trust;
    }

    /// <summary>An IPv4 link-local address (169.254.0.0/16, RFC 3927) a card gives itself without a DHCP server.</summary>
    private static bool IsSelfAssigned(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>Follows the current CA offer's events (from the maintenance round and from a handshake that met a new offer).</summary>
    private void Follow()
    {
        lock (_followGate)
        {
            var ca = _host.Authority();
            if (ReferenceEquals(ca, _followed) || _stopping)
            {
                return;
            }

            UnfollowLocked();
            _followed = ca;
            if (ca is not null)
            {
                ca.Changed += OnCaChanged;
                ca.RevocationChanged += OnRevocationChanged;
            }
        }
    }

    private void Unfollow()
    {
        lock (_followGate)
        {
            UnfollowLocked();
        }
    }

    private void UnfollowLocked()
    {
        if (_followed is { } old)
        {
            old.Changed -= OnCaChanged;
            old.RevocationChanged -= OnRevocationChanged;
            _followed = null;
        }
    }

    /// <summary>The CA changed: the next handshake builds the trusted set again (<see cref="CaTrust.Version"/>), the round follows.</summary>
    private void OnCaChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _caVersion);
        Track(Task.Run(() => MaintainAsync(_cts.Token)));
    }

    private void OnRevocationChanged(object? sender, EventArgs e) => PublishToEveryPage(RadiusTopics.Revoked, "{}");

    private async Task RefreshIndexAsync(bool onlyAfterMiss)
    {
        try
        {
            await _index.RefreshAsync(onlyAfterMiss, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // stopping
        }
#pragma warning disable CA1031 // Read again with the next round; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogIndexFailed(ex);
        }
    }

    private static RadiusProtocolOptions OptionsOf(RadiusSettings config, TlsServerIdentity? identity) => new()
    {
        Tls = TlsSettingsOf(config),
        LargestFragment = config.LargestFragment,
        ServerIdentity = identity,
    };

    private static TlsSettings TlsSettingsOf(RadiusSettings config) =>
        new(VersionOf(config.MinimumTls) ?? (TlsVersion)0, config.CipherSuites.ToHashSet());

    private static TlsVersion? VersionOf(string? text) => RadiusRules.MinimumIndex(text) switch
    {
        0 => TlsVersion.Tls10,
        1 => TlsVersion.Tls11,
        2 => TlsVersion.Tls12,
        3 => TlsVersion.Tls13,
        _ => null,
    };

    // ------------------------------------------------------------------ listening

    /// <summary>"Listen on": All interfaces first, then every interface that is up and has an address.</summary>
    public IReadOnlyList<InterfaceOption> ListInterfaces() =>
        [InterfaceOptions.All, .. UsableInterfaces().Select(n => InterfaceOptions.From(n))];

    /// <summary>The chosen interface now; null with All interfaces or when it is gone.</summary>
    private ServerNetworkInterface? ListenInterface()
    {
        var id = _config.ListenOn;
        return InterfaceOptions.IsAll(id) ? null : UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
    }

    private static IReadOnlyList<IPAddress> ListenAddresses(ServerNetworkInterface? nic) =>
        nic is null ? [] : [.. nic.Addresses.Where(a => !a.IsIPv6LinkLocal)];

    /// <summary>
    /// All interfaces: every address of every card that is up, plus the loopback addresses (a RADIUS test on the server
    /// itself, as with the wildcard socket before); none without a card (then the listener takes every address).
    /// </summary>
    private List<IPAddress> AllListenAddresses()
    {
        var cards = UsableInterfaces().SelectMany(ListenAddresses).ToList();
        if (cards.Count == 0)
        {
            return [];
        }

        var loopback = _host.Interfaces.List().Where(n => n.IsUp && n.IsLoopback).SelectMany(ListenAddresses).Where(IPAddress.IsLoopback);
        return [.. cards.Concat(loopback).Distinct()];
    }

    /// <summary>
    /// Every 5 s while listening: the addresses of the cards (one cheap list of the interfaces); when they changed, listens on
    /// the new set at once instead of at the next 30 s retry. The OS has no address change event the plugin host passes on.
    /// </summary>
    private void CheckAddresses()
    {
        if (_listener is null || _stopping)
        {
            return; // the 30 s retry opens a listener that is not there
        }

        try
        {
            var now = InterfaceOptions.IsAll(_config.ListenOn) ? AllListenAddresses() : ListenAddresses(ListenInterface());
            if (now.ToHashSet().SetEquals(_boundAddresses))
            {
                return;
            }
        }
#pragma warning disable CA1031 // Checked again in 5 s.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        Track(RetryListenAsync(_cts.Token));
    }

    private List<ServerNetworkInterface> UsableInterfaces() =>
        [.. _host.Interfaces.List().Where(n => n.IsUp && !n.IsLoopback && ListenAddresses(n).Count > 0)];

    private async Task OpenAsync(CancellationToken ct)
    {
        if (_host.Network is not { } network)
        {
            return;
        }

        await _listenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listener is not null || _stopping)
            {
                return;
            }

            IUdpRelay listener;
            if (InterfaceOptions.IsAll(_config.ListenOn))
            {
                // Every address of every card on its own socket: the answer leaves from the address the switch sent to, as
                // switches check (RFC 2865 3, RFC 5080 2.2.1). One wildcard socket would answer from the address the routing
                // table picks. No card with an address: every address.
                _boundAddresses = AllListenAddresses();
                listener = await network.ListenUdpAsync(_config.Port, Reason, _boundAddresses, ct).ConfigureAwait(false);
            }
            else if (ListenInterface() is { } nic)
            {
                _boundAddresses = ListenAddresses(nic);
                listener = await network.ListenUdpAsync(_config.Port, Reason, _boundAddresses, ct).ConfigureAwait(false);
            }
            else
            {
                LogInterfaceMissing(RadiusTexts.NotAvailable(_config.ListenOnName ?? _config.ListenOn)); // tried again every 30 s
                return;
            }

            listener.StateChanged += (_, _) => PublishState();
            _listener = listener;
            Track(Task.Run(() => ReceiveLoopAsync(listener, null, _cts.Token), CancellationToken.None));
            LogListening(_config.Port, listener.StateText);
        }
        finally
        {
            _listenGate.Release();
        }
    }

    private async Task CloseAsync(bool release)
    {
        await _listenGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_listener is { } listener)
            {
                _listener = null;
                if (release)
                {
                    await listener.ReleaseAsync().ConfigureAwait(false);
                }
                else
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _listenGate.Release();
        }
    }

    /// <summary>
    /// "Listen on" one interface: listens once it is back, and again on its new addresses after they changed. All interfaces:
    /// again on the new set of addresses after a card or an address came or went.
    /// </summary>
    private async Task RetryListenAsync(CancellationToken ct)
    {
        if (InterfaceOptions.IsAll(_config.ListenOn))
        {
            try
            {
                if (_listener is not null && AllListenAddresses().ToHashSet().SetEquals(_boundAddresses))
                {
                    return;
                }

                await CloseAsync(release: false).ConfigureAwait(false);
                await OpenAsync(ct).ConfigureAwait(false);
                PublishState();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // stopping
            }
#pragma warning disable CA1031 // Tried again in 30 s; the reason goes to the server log.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogListenFailed(ex);
            }

            return;
        }

        try
        {
            if (ListenInterface() is not { } nic)
            {
                if (_listener is not null)
                {
                    await CloseAsync(release: false).ConfigureAwait(false); // gone while listening: the status says so
                    PublishState();
                }

                return;
            }

            var moved = _listener is not null && !ListenAddresses(nic).ToHashSet().SetEquals(_boundAddresses);
            if (_listener is not null && !moved)
            {
                return;
            }

            if (moved)
            {
                await CloseAsync(release: false).ConfigureAwait(false);
            }

            await OpenAsync(ct).ConfigureAwait(false);
            PublishState();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // stopping
        }
#pragma warning disable CA1031 // Tried again in 30 s; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogListenFailed(ex);
        }
    }

    // ------------------------------------------------------------------ sites

    /// <summary>The relay of a site (null when none is open).</summary>
    public IUdpRelay? SiteRelay(Guid siteId)
    {
        lock (_siteRelays)
        {
            return _siteRelays.TryGetValue(siteId, out var entry) ? entry.Relay : null;
        }
    }

    /// <summary>One relay per bound site that has switches; a site without switches (or gone) loses its relay (released).</summary>
    private async Task SyncSitesAsync(CancellationToken ct)
    {
        if (_host.Sites is not { } sites)
        {
            return;
        }

        await _sitesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopping)
            {
                return; // stopping: a relay opened now would never be closed
            }

            var bound = sites.List.ToDictionary(s => s.Id);
            var wanted = _switches.Sites.Where(bound.ContainsKey).ToHashSet();
            List<IUdpRelay> remove;
            lock (_siteRelays)
            {
                remove = [.. _siteRelays.Where(r => !wanted.Contains(r.Key)).Select(r => r.Value.Relay)];
                foreach (var id in _siteRelays.Keys.Where(id => !wanted.Contains(id)).ToList())
                {
                    _siteRelays.Remove(id);
                }
            }

            foreach (var relay in remove)
            {
                await relay.ReleaseAsync().ConfigureAwait(false);
            }

            foreach (var id in wanted)
            {
                lock (_siteRelays)
                {
                    if (_siteRelays.ContainsKey(id))
                    {
                        continue;
                    }
                }

                try
                {
                    var relay = await sites.OpenUdpRelayAsync(id, _config.Port, Reason, ct).ConfigureAwait(false);
                    relay.StateChanged += (_, _) =>
                    {
                        Volatile.Write(ref _switchesDirty, 1);
                        PublishState();
                    };
                    lock (_siteRelays)
                    {
                        _siteRelays[id] = (bound[id].Name, relay);
                    }

                    Track(Task.Run(() => ReceiveLoopAsync(relay, id, _cts.Token), CancellationToken.None));
                    LogSiteListening(bound[id].Name, relay.StateText);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException)
                {
                    LogSiteFailed(bound[id].Name, ex.Message); // removed meanwhile, or the plugin stops
                }
            }
        }
        finally
        {
            _sitesGate.Release();
        }

        PublishState();
    }

    private async Task CloseSitesAsync(bool release)
    {
        List<IUdpRelay> relays;
        lock (_siteRelays)
        {
            relays = [.. _siteRelays.Values.Select(v => v.Relay)];
            _siteRelays.Clear();
        }

        foreach (var relay in relays)
        {
            if (release)
            {
                await relay.ReleaseAsync().ConfigureAwait(false);
            }
            else
            {
                await relay.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnSitesChanged(object? sender, EventArgs e) => Track(Task.Run(async () =>
    {
        try
        {
            await SyncSitesAsync(_cts.Token).ConfigureAwait(false);
            PublishSwitches();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // stopping
        }
    }));

    private void Track(Task task)
    {
        lock (_running)
        {
            _running.RemoveWhere(t => t.IsCompleted);
            _running.Add(task);
        }
    }

    // ------------------------------------------------------------------ log messages (never secrets or keys)

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server started on UDP {Port} with {Switches} switches")]
    private partial void LogStarted(int port, int switches);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server on port {Port}: {State}")]
    private partial void LogListening(int port, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server not listening: {Reason}")]
    private partial void LogInterfaceMissing(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: listening on the chosen interface failed; tried again in 30 s")]
    private partial void LogListenFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server at site {Site}: {State}")]
    private partial void LogSiteListening(string site, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server cannot serve at site {Site}: {Error}")]
    private partial void LogSiteFailed(string site, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "802.1X server: request from {Remote} ({Where}) dropped, no switch has this address ({Count} so far)")]
    private partial void LogUnknownSwitch(IPEndPoint remote, string where, long count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "802.1X server: request of switch {Switch} from {Remote} dropped: {Drop} {Detail}")]
    private partial void LogDropped(string @switch, IPEndPoint remote, RadiusDropReason drop, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X: {Device} authenticated on {Switch} port {Port}, VLAN {Vlan} {Warning}")]
    private partial void LogAuthenticated(string @switch, string port, string device, string vlan, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X: {Device} failed on {Switch} port {Port}: {Reason}")]
    private partial void LogFailed(string @switch, string port, string device, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: a request failed")]
    private partial void LogHandleFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: receiving a request failed")]
    private partial void LogReceiveFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: the session tick failed")]
    private partial void LogTickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: the shared secret of switch {Switch} cannot be read; save the switch again")]
    private partial void LogSecretUnreadable(string @switch);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server: switch {Switch} ({Address}, {Site}) {Change}")]
    private partial void LogSwitchSaved(string change, string @switch, string address, string site);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server settings saved: port {Port}, minimum TLS {MinimumTls}, {Suites} cipher suites")]
    private partial void LogSettingsSaved(int port, string minimumTls, int suites);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server: VLAN rules saved (assign VLANs {Enabled}, {Rules} rules)")]
    private partial void LogVlansSaved(bool enabled, int rules);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server: the log was deleted by {User}")]
    private partial void LogLogCleared(string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server: {Count} certificates revoked ({Reason}) by {User}")]
    private partial void LogRevoked(int count, string reason, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: no server certificate from the OADM CA: {Reason}")]
    private partial void LogCertificateFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: the maintenance round failed; tried again in a minute")]
    private partial void LogMaintainFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: reading the device list failed; tried again in 10 s")]
    private partial void LogIndexFailed(Exception ex);

    /// <summary>One datagram waiting for a worker.</summary>
    internal sealed record Work(Datagram Datagram, IDatagramEndpoint ReplyTo, Guid? SiteId);

    /// <summary>The runtime state of one switch (counters since the start).</summary>
    private sealed class SwitchRuntime
    {
        private long _dropped;
        private long _macOnly;

        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>Requests without EAP (MAC address or password checks), never counted as a wrong secret.</summary>
        public long MacOnly => Interlocked.Read(ref _macOnly);

        public DateTime? LastRequestUtc { get; private set; }

        public bool WrongSecret { get; private set; }

        public bool Answered { get; private set; }

        public string? ServerAddress { get; set; }

        /// <summary>Counts one result; true when the switch's state chip changes.</summary>
        public bool Record(RadiusResult result, DateTime now)
        {
            var before = (WrongSecret, Answered);
            LastRequestUtc = now;
            switch (result.Outcome)
            {
                case RadiusOutcome.Answered or RadiusOutcome.AnsweredFromCache:
                    Answered = true;
                    WrongSecret = false;
                    break;
                case RadiusOutcome.Dropped when result.Drop == RadiusDropReason.WrongSecret:
                    WrongSecret = true;
                    Interlocked.Increment(ref _dropped);
                    break;
                case RadiusOutcome.Dropped when result.Drop is RadiusDropReason.Malformed or RadiusDropReason.NotARequest or RadiusDropReason.AnswerTooLarge:
                    Interlocked.Increment(ref _dropped);
                    break;
                case RadiusOutcome.Dropped when result.Drop == RadiusDropReason.NotEap:
                    Interlocked.Increment(ref _macOnly);
                    break;
            }

            return before != (WrongSecret, Answered);
        }
    }
}

/// <summary>Input errors of the page methods: the field (the DTO property) as the parameter name, the message for the page.</summary>
public static class RadiusErrors
{
    public static ArgumentException Field(string field, string message, Exception? inner = null) => new(message, field, inner);
}

/// <summary>Status and refusal texts of the 802.1X server (one phrase per concept).</summary>
public static class RadiusTexts
{
    public const string NotPro = "OADM PRO is not active.";
    public const string NoCa = "The PKI has no certificate authority.";
    public const string InterfaceGone = "This interface is not available on the server.";
    public const string SiteGone = "This site no longer exists.";
    public const string SwitchGone = "This switch no longer exists.";
    public const string HostNameUnknown = "The server cannot find this host name.";
    public const string NoSecretStore = "This server cannot keep shared secrets.";
    public const string LogEntryGone = "This log entry is no longer kept.";
    public const string RevokeCount = "Choose 1 to 5,000 certificates.";
    public const string ChooseReason = "Choose a reason.";
    public const string NotRunning = "The 802.1X server is not running.";
    public const string CertificateGone = Oadm.Sdk.Certificates.IssuedCertificateMessages.NotListed;
    public const string OnlyDot1x = "Only IEEE 802.1X certificates can be revoked here.";

    /// <summary>The plugin id of PKI, whose permission "Manage the certificate authority" Revoke needs too.</summary>
    public const string PkiPluginId = "oadm.pki";

    /// <summary>Revoke without PKI's permission (the host's wording of a missing permission).</summary>
    public const string NeedsCaPermission = "Your role does not allow: PKI: Manage the certificate authority.";

    /// <summary>"The tag Floor 3 sets a VLAN. Only users with all devices can add or remove it. Nothing was changed."</summary>
    public static string VlanTag(string tag) => $"The tag {tag} sets a VLAN. Only users with all devices can add or remove it. Nothing was changed.";

    /// <summary>A rename or delete of a tag a VLAN rule names (every user).</summary>
    public static string VlanTagChange(string tag) => $"The tag {tag} sets a VLAN. Change the VLAN rule first. Nothing was changed.";

    public static string Running(int port) => string.Create(CultureInfo.InvariantCulture, $"Running on port {port}");

    public static string PortInUse(int port) => string.Create(CultureInfo.InvariantCulture, $"Port {port} is used by another program");

    public static string NotAvailable(string interfaceName) => $"Interface {interfaceName} is not available";
}
