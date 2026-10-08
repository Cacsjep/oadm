using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Plugins.DhcpServer.Status;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DhcpServer;

/// <summary>
/// The running DHCP server: settings, leases (persisted), the listener on port 67, the check for other DHCP servers,
/// the status line and the live events for the page. Starts disabled until the user enables it.
/// </summary>
public sealed partial class DhcpServerService : IAsyncDisposable
{
    public const string ConfigKey = "config";
    public const string LeasesKey = "leases";

    private readonly DhcpServerOptions _options;
    private readonly IPluginSettings? _settings;
    private readonly IPluginEvents? _events;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LeaseStore _store = new();
    private readonly DhcpEngine _engine;
    private readonly KeyedRateLimiter<ulong> _limiter;
    private readonly OtherServerCheck _check;
    private readonly FirewallRuleKeeper _firewallRule;
    private readonly DhcpDeviceAutoAdd? _autoAdd;
    private readonly CancellationTokenSource _cts = new();
    private Task? _background;
    private volatile DhcpConfig _config = new();
    private volatile DhcpListener? _listener;
    private volatile ServiceStatus? _bindError;
    private volatile IReadOnlyList<string> _otherServers = [];
    private long _lastBindAttempt;
    private long _lastCheck;
    private long _lastSweep;
    private long _lastPersist;
    private int _checkRunning;
    private string? _publishedState;

    /// <param name="firewall">Host firewall (Windows service only): UDP rule for the server port while enabled.</param>
    /// <param name="autoAdd">
    /// The server's automatic add (<see cref="ICorePluginContext.AutoAdd"/>): leases of Axis devices are handed to it, so
    /// managed devices are followed to their new address and, with <see cref="DhcpConfig.AutoAddAxisDevices"/>, new ones added.
    /// </param>
    public DhcpServerService(DhcpServerOptions? options = null, IPluginSettings? settings = null, IPluginEvents? events = null, ILogger? logger = null, IFirewallRules? firewall = null, Sdk.Devices.IDeviceAutoAdd? autoAdd = null)
    {
        _options = options ?? new DhcpServerOptions();
        _settings = settings;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
        _limiter = new KeyedRateLimiter<ulong>(_options.RateLimits.PerMac, _options.Time);
        _firewallRule = new FirewallRuleKeeper(Sdk.Network.FirewallRule.ForService("DHCP", FirewallProtocol.Udp, _options.ServerPort), firewall, _logger);
        _check = new OtherServerCheck(_options.Sockets, _options.ServerPort, _options.ClientPort, _logger) { Wait = _options.OtherServerWait };
        _engine = new DhcpEngine(_store, _options.Probe, _options.Engine with { ClientPort = _options.ClientPort }, _options.Time, _logger)
        {
            IgnoreMac = _check.IsProbeMac,
        };
        if (autoAdd is not null)
        {
            var queue = new DhcpDeviceAutoAdd(autoAdd, () => _config is { Enabled: true, AutoAddAxisDevices: true }, _options.AutoAdd, _options.Time, _logger);
            _autoAdd = queue;
            _engine.Leased = (mac, address) => queue.OnLeased(mac, address);
        }
    }

    /// <summary>The automatic add of Axis devices (null when the host offers none).</summary>
    public DhcpDeviceAutoAdd? AutoAdd => _autoAdd;

    public DhcpConfig Config => _config;

    public LeaseStore Leases => _store;

    public DhcpEngine Engine => _engine;

    public KeyedRateLimiter<ulong> Limiter => _limiter;

    public bool IsRunning => _listener is not null;

    /// <summary>The firewall rule of the DHCP server port (open while enabled).</summary>
    public FirewallRuleKeeper Firewall => _firewallRule;

    /// <summary>Loads the stored settings and leases and applies them (server start). Never waits for the network.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var config = new DhcpConfig();
        if (_settings is not null)
        {
            config = await ReadAsync<DhcpConfig>(ConfigKey, ct).ConfigureAwait(false) ?? config;
            if (await ReadAsync<List<StoredLease>>(LeasesKey, ct).ConfigureAwait(false) is { } leases)
            {
                var loaded = _store.Load(leases);
                LogLoaded(loaded);
            }
        }

        await ApplyAsync(config, ct).ConfigureAwait(false);
        _lastSweep = _lastPersist = _lastCheck = _options.Time.GetTimestamp();
        if (config.Enabled)
        {
            StartCheck(); // restored after a restart: the user confirmed before, so only warn
        }

        _background = Task.Run(() => BackgroundAsync(_cts.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_background is { } background)
        {
            try
            {
                await background.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopListenerAsync().ConfigureAwait(false);
            await _firewallRule.SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Stopping: a failed final write is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPersistFailed(ex);
        }

        if (_autoAdd is not null)
        {
            await _autoAdd.DisposeAsync().ConfigureAwait(false);
        }

        _engine.Dispose();
        _cts.Dispose();
        _gate.Dispose();
    }

    /// <summary>Interfaces for the select: every up interface with an IPv4 address (no "all interfaces").</summary>
    public IReadOnlyList<InterfaceOption> ListInterfaces() =>
        [.. UsableInterfaces().Select(n => InterfaceOptions.From(n, $"{n.PrimaryIpv4}/{NetworkOf(n).PrefixLength}"))];

    public IReadOnlyList<DhcpNetworkInfo> ListNetworks() => [.. UsableInterfaces().Select(NetworkOf)];

    public DhcpState GetState(bool full)
    {
        IReadOnlyList<LeaseInfo>? leases = null;
        long version;
        if (full)
        {
            leases = _store.Snapshot(out version);
        }
        else
        {
            version = _store.Version;
        }

        return new DhcpState
        {
            Config = _config,
            Status = ComputeStatus(),
            Interfaces = full ? ListInterfaces() : [],
            Networks = full ? ListNetworks() : [],
            Leases = leases,
            LeaseVersion = version,
            OtherServers = _otherServers,
        };
    }

    /// <summary>
    /// Save from the page: validates interface and range (field errors), checks for other DHCP servers before enabling
    /// (the page asks the user and saves again with them confirmed), stores the settings and applies them.
    /// </summary>
    public async Task<DhcpSaveReply> SaveAsync(DhcpSaveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var interfaceId = string.IsNullOrWhiteSpace(request.InterfaceId) ? null : request.InterfaceId.Trim();
        var nic = interfaceId is null ? null : UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, interfaceId, StringComparison.Ordinal));
        if (request.Enabled && nic is null)
        {
            errors["Interface"] = interfaceId is null ? "Select the interface the devices are connected to." : "This interface is not available on the server.";
        }

        var start = request.RangeStart?.Trim();
        var end = request.RangeEnd?.Trim();
        var network = nic is null ? null : NetworkOf(nic);
        if (request.Enabled || !string.IsNullOrEmpty(start) || !string.IsNullOrEmpty(end))
        {
            var (startError, endError) = DhcpValidation.Range(start, end, network);
            if (startError is not null && (request.Enabled || !string.IsNullOrEmpty(start)))
            {
                errors["RangeStart"] = startError;
            }

            if (endError is not null && (request.Enabled || !string.IsNullOrEmpty(end)))
            {
                errors["RangeEnd"] = endError;
            }
        }

        if (errors.Count > 0)
        {
            return new DhcpSaveReply(false, errors, null, GetState(full: false));
        }

        var sameInterface = string.Equals(_config.InterfaceId, interfaceId, StringComparison.Ordinal);
        var accepted = sameInterface ? _config.AcceptedOtherServers : [];
        var alreadyServing = _listener is not null && sameInterface;
        if (request.Enabled && !alreadyServing && DhcpBindings.For(nic!) is { } binding)
        {
            var result = await _check.RunAsync(binding, ct).ConfigureAwait(false);
            _otherServers = result.Servers;
            _lastCheck = _options.Time.GetTimestamp();
            var confirmed = request.ConfirmedOtherServers ?? [];
            if (result.Servers.Any(s => !confirmed.Contains(s, StringComparer.Ordinal) && !accepted.Contains(s, StringComparer.Ordinal)))
            {
                return new DhcpSaveReply(false, null, result.Servers, GetState(full: false));
            }

            accepted = [.. accepted.Union(confirmed.Intersect(result.Servers, StringComparer.Ordinal), StringComparer.Ordinal)];
        }

        var config = new DhcpConfig
        {
            Enabled = request.Enabled,
            InterfaceId = interfaceId,
            InterfaceName = nic?.Name ?? (string.Equals(interfaceId, _config.InterfaceId, StringComparison.Ordinal) ? _config.InterfaceName : interfaceId),
            RangeStart = string.IsNullOrEmpty(start) ? null : start,
            RangeEnd = string.IsNullOrEmpty(end) ? null : end,
            AcceptedOtherServers = accepted,
            AutoAddAxisDevices = request.AutoAddAxisDevices,
        };
        if (!config.Enabled)
        {
            _otherServers = [];
        }

        if (_settings is not null)
        {
            await _settings.SetAsync(ConfigKey, DhcpJson.Serialize(config), ct).ConfigureAwait(false);
        }

        await ApplyAsync(config, ct).ConfigureAwait(false);
        return new DhcpSaveReply(true, null, null, GetState(full: false));
    }

    /// <summary>Adds or edits a static lease (field errors under MAC, IP address, name).</summary>
    public StaticLeaseReply SaveStatic(StaticLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var network = CurrentNetwork();
        var errors = DhcpValidation.StaticLease(request.Mac, request.Address, request.Name, network);
        ulong? original = null;
        if (request.OriginalMac is not null)
        {
            original = MacAddress.TryParse(request.OriginalMac, out var o) ? o : throw new ArgumentException("The edited lease is not valid.", nameof(request));
        }

        if (errors.Count == 0)
        {
            if (MacAddress.TryParse(request.Mac, out var mac) && Ip4.TryParse(request.Address, out var address))
            {
                var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
                errors = _store.SetStatic(mac, address, name, original, Now);
            }
        }

        return new StaticLeaseReply(errors.Count == 0, errors.Count == 0 ? null : errors);
    }

    public StaticLeaseReply MakeStatic(LeaseRequest request) =>
        _store.MakeStatic(ParseMac(request)) ? new StaticLeaseReply(true, null) : throw new KeyNotFoundException("The dynamic lease no longer exists.");

    public DhcpState DeleteStatic(LeaseRequest request) =>
        _store.DeleteStatic(ParseMac(request)) ? GetState(full: false) : throw new KeyNotFoundException("The static lease no longer exists.");

    public DhcpState Release(LeaseRequest request) =>
        _store.Forget(ParseMac(request)) ? GetState(full: false) : throw new KeyNotFoundException("The lease no longer exists.");

    /// <summary>Writes changed leases now (tests; normally the background loop does it).</summary>
    public async Task PersistAsync(CancellationToken ct)
    {
        if (_settings is not null && _store.TakeDirty() is { } leases)
        {
            await _settings.SetAsync(LeasesKey, DhcpJson.Serialize(leases), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the background work once (tests): sweep, persist, publish.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        _store.Sweep(Now);
        await PersistAsync(ct).ConfigureAwait(false);
        PublishLeases();
        PublishState(force: false);
    }

    internal async Task ApplyAsync(DhcpConfig config, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _config = config;
            await StopListenerAsync().ConfigureAwait(false);
            await _firewallRule.SyncAsync(config.Enabled, ct).ConfigureAwait(false);
            if (!config.Enabled)
            {
                _bindError = null;
                LogStopped();
            }
            else
            {
                await TryBindAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        PublishState(force: true);
    }

    private async Task TryBindAsync(CancellationToken ct)
    {
        _lastBindAttempt = _options.Time.GetTimestamp();
        var name = _config.InterfaceName ?? _config.InterfaceId ?? "?";
        var nic = UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, _config.InterfaceId, StringComparison.Ordinal));
        if (nic is null || DhcpBindings.For(nic) is not { } binding)
        {
            _bindError = DhcpStatusTexts.InterfaceNotAvailable(name);
            LogBindFailed(name, "interface not available");
            return;
        }

        var network = NetworkOf(nic);
        if (DhcpValidation.Range(_config.RangeStart, _config.RangeEnd, network) is not (null, null)
            || !Ip4.TryParse(_config.RangeStart, out var start) || !Ip4.TryParse(_config.RangeEnd, out var end))
        {
            _bindError = DhcpStatusTexts.RangeOutsideSubnet(network.Subnet);
            LogBindFailed(name, "range outside " + network.Subnet);
            return;
        }

        IDhcpSocket socket;
        try
        {
            socket = _options.Sockets.Open(binding, _options.ServerPort, reusePort: false);
        }
        catch (SocketException ex)
        {
            string? holder = null;
            if (_options.Os == HostOs.Windows && PortBindErrors.Classify(ex.SocketErrorCode, _options.Os) == BindErrorKind.InUse)
            {
                holder = await _options.WindowsServices.IsRunningAsync("DHCPServer", ct).ConfigureAwait(false) ? "Windows DHCP Server"
                    : await _options.WindowsServices.IsRunningAsync("SharedAccess", ct).ConfigureAwait(false) ? "Internet Connection Sharing"
                    : null;
            }

            _bindError = DhcpStatusTexts.ForBindError(ex.SocketErrorCode, _options.ServerPort, name, _options.Os, holder);
            LogBindFailed(name, ex.SocketErrorCode.ToString());
            return;
        }

        _engine.Scope = DhcpScope.Create(network, start, end);
        var listener = new DhcpListener(socket, _engine, _limiter, _options.RateLimits.MaxInFlight, _logger);
        listener.Start();
        _listener = listener;
        _bindError = null;
        LogStarted(name, network.Address, network.PrefixLength, _config.RangeStart!, _config.RangeEnd!);
    }

    private async Task StopListenerAsync()
    {
        _engine.Scope = null;
        if (_listener is { } listener)
        {
            _listener = null;
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ServiceStatus ComputeStatus()
    {
        var config = _config;
        if (!config.Enabled)
        {
            return DhcpStatusTexts.Stopped;
        }

        if (_bindError is { } error)
        {
            return error;
        }

        if (_listener is null || _engine.Scope is not { } scope)
        {
            return DhcpStatusTexts.Stopped;
        }

        var accepted = _config.AcceptedOtherServers;
        if (_otherServers.Where(s => !accepted.Contains(s, StringComparer.Ordinal)).ToList() is { Count: > 0 } others)
        {
            return DhcpStatusTexts.OtherServer(others);
        }

        if (_engine.ExhaustedWithin(_options.ExhaustedWarning) || !_store.HasFreeAddress(scope.Pool, Now))
        {
            return DhcpStatusTexts.PoolExhausted;
        }

        return DhcpStatusTexts.Running(config.InterfaceName ?? scope.Network.Name, scope.Network.Address, scope.Network.PrefixLength);
    }

    private async Task BackgroundAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PublishInterval, _options.Time, ct).ConfigureAwait(false);
                var time = _options.Time;
                if (_config.Enabled && time.GetElapsedTime(_lastBindAttempt) >= _options.RetryBindInterval)
                {
                    await RetryOrRecheckAsync(ct).ConfigureAwait(false);
                }

                if (time.GetElapsedTime(_lastSweep) >= _options.SweepInterval)
                {
                    _lastSweep = time.GetTimestamp();
                    _store.Sweep(Now);
                }

                if (time.GetElapsedTime(_lastPersist) >= _options.PersistInterval)
                {
                    _lastPersist = time.GetTimestamp();
                    await PersistAsync(ct).ConfigureAwait(false);
                }

                if (_listener is not null && time.GetElapsedTime(_lastCheck) >= _options.OtherServerInterval)
                {
                    StartCheck();
                }

                PublishLeases();
                PublishState(force: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The background loop must survive; serving does not depend on it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBackgroundFailed(ex);
            }
        }
    }

    /// <summary>Not running: try to bind again. Running: rebind when the interface's address or subnet changed.</summary>
    private async Task RetryOrRecheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_config.Enabled)
            {
                return;
            }

            if (_listener is null)
            {
                await TryBindAsync(ct).ConfigureAwait(false);
                return;
            }

            _lastBindAttempt = _options.Time.GetTimestamp();
            var nic = UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, _config.InterfaceId, StringComparison.Ordinal));
            var current = nic is null ? null : NetworkOf(nic);
            if (current is null || _engine.Scope?.Network is not { } serving || !SameNetwork(current, serving))
            {
                LogInterfaceChanged(_config.InterfaceName ?? "?");
                await StopListenerAsync().ConfigureAwait(false);
                await TryBindAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartCheck()
    {
        _lastCheck = _options.Time.GetTimestamp();
        var nic = UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, _config.InterfaceId, StringComparison.Ordinal));
        if (nic is null || DhcpBindings.For(nic) is not { } binding || Interlocked.Exchange(ref _checkRunning, 1) == 1)
        {
            return;
        }

        var ct = _cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await _check.RunAsync(binding, ct).ConfigureAwait(false);
                    if (result.Error is null && _config.Enabled)
                    {
                        _otherServers = result.Servers;
                    }
                }
                catch (OperationCanceledException)
                {
                    // stopping
                }
#pragma warning disable CA1031 // Best effort check.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogBackgroundFailed(ex);
                }
                finally
                {
                    Volatile.Write(ref _checkRunning, 0);
                }
            },
            CancellationToken.None);
    }

    private void PublishLeases()
    {
        if (_store.TakeChanges() is { } changes)
        {
            // Large changes (e.g. thousands at once) are split so each event stays far below the event size limit.
            const int chunk = 2_000;
            if (changes.Changed.Count + changes.Removed.Count <= chunk)
            {
                _events?.Publish(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(changes));
                return;
            }

            foreach (var part in changes.Changed.Chunk(chunk))
            {
                _events?.Publish(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(new LeasesEvent(changes.Version, part, [])));
            }

            foreach (var part in changes.Removed.Chunk(chunk))
            {
                _events?.Publish(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(new LeasesEvent(changes.Version, [], part)));
            }
        }
    }

    private void PublishState(bool force)
    {
        var json = DhcpJson.Serialize(GetState(full: false) with { LeaseVersion = 0 });
        if (!force && string.Equals(json, _publishedState, StringComparison.Ordinal))
        {
            return;
        }

        _publishedState = json;
        _events?.Publish(DhcpServerMethods.StateTopic, json);
    }

    private DhcpNetworkInfo? CurrentNetwork()
    {
        var nic = UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, _config.InterfaceId, StringComparison.Ordinal));
        return nic is null ? _engine.Scope?.Network : NetworkOf(nic);
    }

    private IEnumerable<ServerNetworkInterface> UsableInterfaces() =>
        _options.Interfaces.List().Where(n => n.IsUp && (!n.IsLoopback || _options.IncludeLoopback) && n.PrimaryIpv4 is not null);

    /// <summary>What clients get on an interface: mask from its prefix, its gateway when in the subnet, its DNS servers and suffix.</summary>
    public static DhcpNetworkInfo NetworkOf(ServerNetworkInterface nic)
    {
        ArgumentNullException.ThrowIfNull(nic);
        var address = nic.PrimaryIpv4!;
        var prefix = nic.PrefixLengthOf(address) is { } p and > 0 and <= 32 ? p : 24;
        var a = Ip4.From(address);
        string? router = nic.Gateways
            .Where(g => g.AddressFamily == AddressFamily.InterNetwork)
            .Select(Ip4.From)
            .Where(g => g != 0 && g != a && Oadm.Plugins.Network.Model.Ipv4.SameSubnet(g, a, prefix))
            .Select(Ip4.Format)
            .FirstOrDefault();
        IReadOnlyList<string> dns = [.. nic.DnsServers.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).Where(d => d != "0.0.0.0").Distinct()];
        return new DhcpNetworkInfo(nic.Id, nic.Name, address.ToString(), prefix, router, dns, nic.DnsSuffix);
    }

    private static bool SameNetwork(DhcpNetworkInfo a, DhcpNetworkInfo b) =>
        a.Address == b.Address && a.PrefixLength == b.PrefixLength && a.Router == b.Router && a.Domain == b.Domain && a.Dns.SequenceEqual(b.Dns);

    private DateTime Now => _options.Time.GetUtcNow().UtcDateTime;

    private static ulong ParseMac(LeaseRequest request) =>
        MacAddress.TryParse(request?.Mac, out var mac) ? mac : throw new ArgumentException("The MAC address is not valid.", nameof(request));

    private async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
        where T : class
    {
        var json = await _settings!.GetAsync(key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return DhcpJson.Deserialize<T>(json);
        }
        catch (ArgumentException ex)
        {
            LogBadSetting(key, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP server running on {Interface} ({Address}/{Prefix}), range {Start} - {End}")]
    private partial void LogStarted(string @interface, string address, int prefix, string start, string end);

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP server stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP server: {Count} stored leases loaded")]
    private partial void LogLoaded(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP server cannot listen on {Interface}: {Error}")]
    private partial void LogBindFailed(string @interface, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP server: the address of {Interface} changed, restarting")]
    private partial void LogInterfaceChanged(string @interface);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored DHCP server setting {Key} ignored: {Error}")]
    private partial void LogBadSetting(string key, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP server background work failed")]
    private partial void LogBackgroundFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP server: storing the leases failed")]
    private partial void LogPersistFailed(Exception ex);
}
