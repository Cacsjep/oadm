using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Status;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.NtpServer;

/// <summary>
/// The running NTP server: settings, UDP responder, upstream monitor, request log, status line and the live events for
/// the page. Serving never waits for the upstream or for this class: the responder reads <see cref="CurrentSource"/>.
/// With "Serve at sites" every bound site gets a UDP relay of the NTP port (<see cref="ISites"/>), answered by a
/// <see cref="SiteServer"/> from the same time source.
/// </summary>
public sealed partial class NtpServerService : IAsyncDisposable
{
    public const string ConfigKey = "config";

    private readonly NtpServerOptions _options;
    private readonly IPluginSettings? _settings;
    private readonly IPluginEvents? _events;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RequestLog _log = new();
    private readonly NtpRateLimiter _limiter;
    private readonly FirewallRuleKeeper _firewallRule;
    private readonly CancellationTokenSource _cts = new();
    private Task? _background;
    private volatile NtpConfig _config = new();
    private volatile NtpUdpServer? _server;
    private volatile UpstreamMonitor? _upstream;
    private volatile ServiceStatus? _bindError;
    private long _lastBindAttempt;
    private long _publishedSeq;
    private string? _publishedState;
    private readonly IPluginNetwork? _network;
    private readonly ISites? _sites;
    private readonly IDeviceScopes? _scopes;
    private string? _groupPublishedState;
    private readonly Dictionary<Guid, SiteServer> _siteServers = [];
    private readonly Lock _siteLock = new();

    /// <param name="firewall">Host firewall (Windows service only): UDP rule for the port while enabled.</param>
    /// <param name="network">The host's network: the server's address towards a device on its route.</param>
    /// <param name="sites">The bound sites and their relays ("Serve at sites"); null on a server without sites.</param>
    /// <param name="scopes">The device groups: the pages of users with a group get the requests and sites of their devices.</param>
    public NtpServerService(NtpServerOptions? options = null, IPluginSettings? settings = null, IPluginEvents? events = null, ILogger? logger = null, IFirewallRules? firewall = null,
        IPluginNetwork? network = null, ISites? sites = null, IDeviceScopes? scopes = null)
    {
        _scopes = scopes;
        Viewers = new ViewerScopes(scopes);
        _network = network;
        _sites = sites;
        _options = options ?? new NtpServerOptions();
        _settings = settings;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
        _limiter = new NtpRateLimiter(_options.RateLimit, _options.Time);
        _firewallRule = new FirewallRuleKeeper(Sdk.Network.FirewallRule.ForService("NTP", FirewallProtocol.Udp, _options.Port), firewall, _logger);
    }

    /// <summary>The device groups whose users opened the page: their pages get the state of their own devices.</summary>
    internal ViewerScopes Viewers { get; }

    /// <summary>The state as a user with a device group sees it (the requests and sites of the group's devices).</summary>
    internal NtpState GetState(NtpGroupView view) => view.Filter(GetState(full: true));

    /// <summary>The firewall rule of the NTP port (open while enabled).</summary>
    public FirewallRuleKeeper Firewall => _firewallRule;

    public NtpConfig Config => _config;

    public RequestLog Log => _log;

    public NtpRateLimiter Limiter => _limiter;

    /// <summary>Bound endpoints while running, else empty.</summary>
    public IReadOnlyList<IPEndPoint> Endpoints => _server?.LocalEndpoints ?? [];

    public UpstreamMonitor? Upstream => _upstream;

    /// <summary>What a request is answered with right now: the last good upstream state, else the local clock.</summary>
    public TimeSourceState CurrentSource => _upstream?.ServingState ?? TimeSourceState.Local;

    /// <summary>Loads the stored settings and applies them (server start).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var config = new NtpConfig();
        if (_settings is not null)
        {
            var json = await _settings.GetAsync(ConfigKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    config = NtpJson.Deserialize<NtpConfig>(json);
                }
                catch (ArgumentException ex)
                {
                    LogBadConfig(ex.Message);
                }
            }
        }

        await ApplyAsync(config, null, ct).ConfigureAwait(false);
        if (_sites is not null)
        {
            _sites.SitesChanged += OnSitesChanged;
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
            await StopServerAsync().ConfigureAwait(false);
            await StopUpstreamAsync().ConfigureAwait(false);
            await _firewallRule.SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (_sites is not null)
        {
            _sites.SitesChanged -= OnSitesChanged;
        }

        await StopSitesAsync().ConfigureAwait(false);
        _cts.Dispose();
        _gate.Dispose();
    }

    /// <summary>Interfaces for the select: "All interfaces" first, then every up interface with an address.</summary>
    public IReadOnlyList<InterfaceOption> ListInterfaces()
    {
        var options = new List<InterfaceOption> { new(NtpServerPluginInfo.AllInterfaces, NtpServerPluginInfo.AllInterfacesLabel, NtpServerPluginInfo.AllInterfacesLabel, []) };
        foreach (var nic in UsableInterfaces())
        {
            options.Add(InterfaceOptions.From(nic));
        }

        return options;
    }

    public NtpState GetState(bool full)
    {
        var upstream = _upstream?.Snapshot;
        return new NtpState
        {
            Config = _config,
            Status = ComputeStatus(),
            Interfaces = full ? ListInterfaces() : [],
            Requests = full ? _log.Snapshot() : [],
            Upstream = upstream?.ToInfo(),
            Stratum = CurrentSource.Stratum,
            Port = Endpoints.Count > 0 ? Endpoints[0].Port : _options.Port,
            HasSites = _sites is { List.Count: > 0 },
            Sites = SiteStates(),
            SiteChoices = _sites is null ? [] : [.. _sites.List.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new SiteChoice(s.Id, s.Name))],
        };
    }

    /// <summary>The relay of every site served now, by site name.</summary>
    public IReadOnlyList<SiteServing> SiteStates()
    {
        lock (_siteLock)
        {
            return [.. _siteServers.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.Serving)];
        }
    }

    /// <summary>The site servers now (tests).</summary>
    public IReadOnlyList<SiteServer> SiteServers
    {
        get
        {
            lock (_siteLock)
            {
                return [.. _siteServers.Values];
            }
        }
    }

    /// <summary>
    /// Save from the page: validates the interface and the upstream (resolve + one query with the normal timeouts; the
    /// running server keeps serving meanwhile), stores the settings and applies them.
    /// </summary>
    public async Task<SaveReply> SaveAsync(SaveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var interfaceId = string.IsNullOrWhiteSpace(request.InterfaceId) ? NtpServerPluginInfo.AllInterfaces : request.InterfaceId.Trim();
        string? interfaceName = NtpServerPluginInfo.AllInterfacesLabel;
        if (!IsAll(interfaceId))
        {
            var nic = _options.Interfaces.List().FirstOrDefault(n => string.Equals(n.Id, interfaceId, StringComparison.Ordinal));
            if (nic is null && !string.Equals(interfaceId, _config.InterfaceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The selected interface is not available on the server.", nameof(request));
            }

            interfaceName = nic?.Name ?? _config.InterfaceName;
        }

        var upstreamText = string.IsNullOrWhiteSpace(request.Upstream) ? null : request.Upstream.Trim();
        UpstreamSample? seed = null;
        if (upstreamText is not null)
        {
            var host = UpstreamHost.TryParse(upstreamText, out var parseError);
            if (host is null)
            {
                return new SaveReply(false, parseError, null, GetState(full: false));
            }

            try
            {
                seed = await UpstreamMonitor.QueryOnceAsync(host, _options.Upstream, _options.Resolver, _options.Time, ct).ConfigureAwait(false);
            }
            catch (UpstreamException ex)
            {
                return new SaveReply(false, $"{host.Host}: {ex.Message}.", null, GetState(full: false));
            }

            upstreamText = host.ToString();
        }

        var config = new NtpConfig { Enabled = request.Enabled, InterfaceId = interfaceId, InterfaceName = interfaceName, Upstream = upstreamText, ServeAtSites = request.ServeAtSites,
            ServeSites = request.ServeAtSites ? [.. (request.ServeSites ?? []).Distinct()] : [] };
        if (_settings is not null)
        {
            await _settings.SetAsync(ConfigKey, NtpJson.Serialize(config), ct).ConfigureAwait(false);
        }

        await ApplyAsync(config, seed, ct).ConfigureAwait(false);
        return new SaveReply(true, null, seed?.Describe(), GetState(full: false));
    }

    /// <summary>
    /// The address a device should use as NTP server. On the server's own network: the selected interface's address of
    /// the device's address family, or with "All interfaces" the local address the server routes to the device with. At a
    /// site (with "Serve at sites"): the site node's address in the device's network. Null + reason when not possible.
    /// </summary>
    /// <param name="deviceInfo">The device (its site).</param>
    /// <param name="device">The device's IP address.</param>
    /// <param name="network">The device's route (the task's <see cref="ITaskExecutionContext.Network"/>).</param>
    /// <param name="ct">Cancels the lookup.</param>
    public async Task<(IPAddress? Address, string? Error)> ServerAddressForAsync(IDeviceInfo deviceInfo, IPAddress device, IDeviceNetwork? network, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentNullException.ThrowIfNull(device);
        if (_config.Enabled && deviceInfo.SiteId is { } siteId)
        {
            return await SiteAddressForAsync(siteId, device, network, ct).ConfigureAwait(false);
        }

        if (!_config.Enabled || _server is not { } running)
        {
            return (null, "The OADM NTP server is not running. Enable it on the NTP server page. Nothing was changed.");
        }

        var family = device.IsIPv4MappedToIPv6 ? AddressFamily.InterNetwork : device.AddressFamily;
        if (!IsAll(_config.InterfaceId))
        {
            var bound = running.LocalEndpoints.Select(e => e.Address).ToList();
            var match = bound.FirstOrDefault(a => a.AddressFamily == family);
            return match is null
                ? (null, $"The NTP server listens on {_config.InterfaceName} without an {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address for this device. Nothing was changed.")
                : (match, null);
        }

        if (network is null)
        {
            return (null, "This OADM server cannot find its address towards the device. Nothing was changed.");
        }

        var local = await network.LocalAddressTowardsAsync((device.IsIPv4MappedToIPv6 ? device.MapToIPv4() : device).ToString(), ct).ConfigureAwait(false);
        return local is null
            ? (null, "The server has no route to the device. Nothing was changed.")
            : (local, null);
    }

    private async Task<(IPAddress? Address, string? Error)> SiteAddressForAsync(Guid siteId, IPAddress device, IDeviceNetwork? network, CancellationToken ct)
    {
        if (!_config.ServeAtSites)
        {
            return (null, NtpStatusTexts.NotServingAtSites);
        }

        SiteServer? server;
        lock (_siteLock)
        {
            server = _siteServers.GetValueOrDefault(siteId);
        }

        if (server is null)
        {
            return (null, _config.Serves(siteId) ? NtpStatusTexts.NotServingAtSites : NtpStatusTexts.NotServingAtSite);
        }

        if (server.Relay.State != RelayState.Running)
        {
            return (null, server.Relay.StateText + ". Nothing was changed.");
        }

        if (network is null)
        {
            return (null, "This OADM server cannot find the site node's address. Nothing was changed.");
        }

        var local = await network.LocalAddressTowardsAsync((device.IsIPv4MappedToIPv6 ? device.MapToIPv4() : device).ToString(), ct).ConfigureAwait(false);
        return local is null
            ? (null, $"The site node of {server.Name} has no address in the device's network. Nothing was changed.")
            : (local, null);
    }

    /// <summary>The address in the task name: the selected interface's IPv4 address, null for "All interfaces".</summary>
    public IPAddress? NameAddress =>
        !_config.Enabled || IsAll(_config.InterfaceId) ? null
        : _server?.LocalEndpoints.Select(e => e.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
          ?? (_server is { LocalEndpoints.Count: > 0 } s ? s.LocalEndpoints[0].Address : null);

    internal async Task ApplyAsync(NtpConfig config, UpstreamSample? seed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var upstreamChanged = seed is not null || !string.Equals(config.Upstream, _config.Upstream, StringComparison.OrdinalIgnoreCase)
                || config.Enabled != _config.Enabled || _upstream is null;
            _config = config;
            await StopServerAsync().ConfigureAwait(false);
            if (upstreamChanged || !config.Enabled)
            {
                await StopUpstreamAsync().ConfigureAwait(false);
            }

            await _firewallRule.SyncAsync(config.Enabled, ct).ConfigureAwait(false);
            if (!config.Enabled)
            {
                _bindError = null;
                LogStopped();
            }
            else
            {
                if (_upstream is null && UpstreamHost.TryParse(config.Upstream, out _) is { } host)
                {
                    _upstream = new UpstreamMonitor(host, _options.Upstream, _options.Resolver, _options.Time, logger: _logger);
                    _upstream.Start(seed);
                }

                await TryBindAsync(ct).ConfigureAwait(false);
            }

            await SyncSitesAsync(ct).ConfigureAwait(false);
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
        var name = IsAll(_config.InterfaceId) ? NtpServerPluginInfo.AllInterfacesLabel : _config.InterfaceName ?? _config.InterfaceId;
        var endpoints = EndpointsFor(_config);
        if (endpoints is null)
        {
            _bindError = NtpStatusTexts.InterfaceNotAvailable(name);
            LogBindFailed(name, "interface not available");
            return;
        }

        var server = new NtpUdpServer(endpoints, () => CurrentSource, _limiter, _log, _options.Time, _logger);
        try
        {
            server.Start();
            _server = server;
            _bindError = null;
            LogStarted(server.LocalEndpoints);
        }
        catch (SocketException ex)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            var windowsTime = _options.Os == HostOs.Windows
                && ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                && await _options.WindowsTime.IsRunningAsync(ct).ConfigureAwait(false);
            _bindError = NtpStatusTexts.ForBindError(ex.SocketErrorCode, _options.Port, name, _options.Os, windowsTime);
            LogBindFailed(name, ex.SocketErrorCode.ToString());
        }
    }

    private List<IPEndPoint>? EndpointsFor(NtpConfig config)
    {
        if (IsAll(config.InterfaceId))
        {
            var all = new List<IPEndPoint> { new(IPAddress.Any, _options.Port) };
            if (NtpUdpServer.SupportsIPv6)
            {
                all.Add(new IPEndPoint(IPAddress.IPv6Any, _options.Port));
            }

            return all;
        }

        var nic = UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, config.InterfaceId, StringComparison.Ordinal));
        if (nic is null)
        {
            return null;
        }

        // Link-local IPv6 needs a scope per interface and is not what cameras are configured with: left out.
        var endpoints = nic.Addresses.Where(a => !a.IsIPv6LinkLocal).Select(a => new IPEndPoint(a, _options.Port)).ToList();
        return endpoints.Count == 0 ? null : endpoints;
    }

    private IEnumerable<ServerNetworkInterface> UsableInterfaces() =>
        _options.Interfaces.List().Where(n => n.IsUp && (!n.IsLoopback || _options.IncludeLoopback) && n.PrimaryAddress is not null);

    private ServiceStatus ComputeStatus()
    {
        if (!_config.Enabled)
        {
            return NtpStatusTexts.Stopped;
        }

        if (_bindError is { } error)
        {
            return error;
        }

        if (_server is not { } server)
        {
            return NtpStatusTexts.Stopped;
        }

        if (_limiter.GloballyLimitedWithin(_options.TooManyRequestsWarning))
        {
            return NtpStatusTexts.TooManyRequests(_options.RateLimit.GlobalPerSecond);
        }

        if (_upstream?.Snapshot is { } upstream)
        {
            if (upstream.Reachable == false)
            {
                return NtpStatusTexts.UpstreamNotReachable(upstream.Host, upstream.LastError);
            }

            if (upstream.Reachable == true && upstream.LastSample is { } sample && Math.Abs(sample.OffsetSeconds) > 1)
            {
                return NtpStatusTexts.ClockDiffers(sample.OffsetSeconds);
            }
        }

        return NtpStatusTexts.Running(server.LocalEndpoints, IsAll(_config.InterfaceId));
    }

    private async Task BackgroundAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PublishInterval, _options.Time, ct).ConfigureAwait(false);
                if (_config.Enabled && _server is null && _options.Time.GetElapsedTime(_lastBindAttempt) >= _options.RetryBindInterval)
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_config.Enabled && _server is null)
                        {
                            await TryBindAsync(ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }

                var entries = PublishRequests();
                PublishState(force: false);
                await PublishToGroupsAsync(entries, ct).ConfigureAwait(false);
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

    /// <summary>The new requests to the pages of users with all devices; returns them (empty when none).</summary>
    private IReadOnlyList<RequestEntry> PublishRequests()
    {
        var last = _log.LastSeq;
        if (last <= _publishedSeq)
        {
            return [];
        }

        var entries = _log.Snapshot(_publishedSeq);
        _publishedSeq = last;
        _events?.Publish(NtpServerMethods.RequestsTopic, NtpJson.Serialize(new RequestsEvent(entries)), PluginEventAudience.AllDevices);
        return entries;
    }

    /// <summary>
    /// The pages of users with a device group (<see cref="Viewers"/>): a changed state and the new requests, each with the
    /// rows of the group's own devices only (<see cref="PluginEventAudience.DeviceScope"/>). Background work: the group's
    /// devices come from the device scopes.
    /// </summary>
    private async Task PublishToGroupsAsync(IReadOnlyList<RequestEntry> entries, CancellationToken ct)
    {
        if (_events is not { } events || _scopes is not { } scopes)
        {
            return;
        }

        var keys = Viewers.Keys();
        var state = GetState(full: false);
        var json = NtpJson.Serialize(state);
        var stateChanged = !string.Equals(json, _groupPublishedState, StringComparison.Ordinal);
        _groupPublishedState = json;
        if (keys.Count == 0 || (!stateChanged && entries.Count == 0))
        {
            return;
        }

        foreach (var key in keys)
        {
            var view = NtpGroupView.From(await scopes.Devices(key).ListAsync(ct).ConfigureAwait(false));
            var audience = PluginEventAudience.DeviceScope(key);
            if (stateChanged)
            {
                events.Publish(NtpServerMethods.StateTopic, NtpJson.Serialize(view.Filter(state)), audience);
            }

            if (view.Filter(entries) is { Count: > 0 } mine)
            {
                events.Publish(NtpServerMethods.RequestsTopic, NtpJson.Serialize(new RequestsEvent(mine)), audience);
            }
        }
    }

    private void PublishState(bool force)
    {
        var json = NtpJson.Serialize(GetState(full: false));
        if (!force && string.Equals(json, _publishedState, StringComparison.Ordinal))
        {
            return;
        }

        _publishedState = json;
        _events?.Publish(NtpServerMethods.StateTopic, json, PluginEventAudience.AllDevices);
    }

    /// <summary>
    /// "Serve at sites": one relay per served site (all, or the chosen ones) while the server is enabled; a site no longer
    /// served (turned off, not chosen, removed) gets its relay released, so its node gives the port back; new sites get one.
    /// Callers hold the gate.
    /// </summary>
    private async Task SyncSitesAsync(CancellationToken ct)
    {
        var wanted = _sites is not null && _config.Enabled
            ? _sites.List.Where(s => _config.Serves(s.Id)).ToDictionary(s => s.Id)
            : [];
        List<SiteServer> remove;
        lock (_siteLock)
        {
            remove = [.. _siteServers.Values.Where(s => !wanted.ContainsKey(s.SiteId))];
            foreach (var server in remove)
            {
                _siteServers.Remove(server.SiteId);
            }

            foreach (var server in _siteServers.Values)
            {
                server.Name = wanted[server.SiteId].Name;
            }
        }

        // Together: each release may wait up to a few seconds for its node.
        await Task.WhenAll(remove.Select(server => server.ReleaseAsync().AsTask())).ConfigureAwait(false);

        foreach (var site in wanted.Values)
        {
            bool known;
            lock (_siteLock)
            {
                known = _siteServers.ContainsKey(site.Id);
            }

            if (known)
            {
                continue;
            }

            try
            {
                var relay = await _sites!.OpenUdpRelayAsync(site.Id, _options.SitePort, NtpStatusTexts.RelayReason, ct).ConfigureAwait(false);
                var server = new SiteServer(site.Id, site.Name, relay, () => CurrentSource, _options.RateLimit, _log, _options.Time, _logger);
                server.Start();
                lock (_siteLock)
                {
                    _siteServers[site.Id] = server;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException)
            {
                LogSiteFailed(site.Name, ex.Message); // removed meanwhile, or the plugin stops
            }
        }
    }

    private async Task StopSitesAsync()
    {
        List<SiteServer> servers;
        lock (_siteLock)
        {
            servers = [.. _siteServers.Values];
            _siteServers.Clear();
        }

        foreach (var server in servers)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnSitesChanged(object? sender, EventArgs e) => _ = Task.Run(async () =>
    {
        try
        {
            await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await SyncSitesAsync(_cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            PublishState(force: false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // stopping
        }
    });

    private async Task StopServerAsync()
    {
        if (_server is { } server)
        {
            _server = null;
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StopUpstreamAsync()
    {
        if (_upstream is { } upstream)
        {
            _upstream = null;
            await upstream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsAll(string? interfaceId) =>
        string.IsNullOrEmpty(interfaceId) || string.Equals(interfaceId, NtpServerPluginInfo.AllInterfaces, StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "NTP server running on {Endpoints}")]
    private partial void LogStarted(IReadOnlyList<IPEndPoint> endpoints);

    [LoggerMessage(Level = LogLevel.Information, Message = "NTP server stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "NTP server cannot listen on {Interface}: {Error}")]
    private partial void LogBindFailed(string @interface, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NTP server cannot serve at site {Site}: {Error}")]
    private partial void LogSiteFailed(string site, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored NTP server settings ignored: {Error}")]
    private partial void LogBadConfig(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NTP server background work failed")]
    private partial void LogBackgroundFailed(Exception ex);
}
