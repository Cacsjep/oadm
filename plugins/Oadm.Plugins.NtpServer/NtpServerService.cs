using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Status;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer;

/// <summary>
/// The running NTP server: settings, UDP responder, upstream monitor, request log, status line and the live events for
/// the page. Serving never waits for the upstream or for this class: the responder reads <see cref="CurrentSource"/>.
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
    private readonly CancellationTokenSource _cts = new();
    private Task? _background;
    private volatile NtpConfig _config = new();
    private volatile NtpUdpServer? _server;
    private volatile UpstreamMonitor? _upstream;
    private volatile ServiceStatus? _bindError;
    private long _lastBindAttempt;
    private long _publishedSeq;
    private string? _publishedState;

    public NtpServerService(NtpServerOptions? options = null, IPluginSettings? settings = null, IPluginEvents? events = null, ILogger? logger = null)
    {
        _options = options ?? new NtpServerOptions();
        _settings = settings;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
        _limiter = new NtpRateLimiter(_options.RateLimit, _options.Time);
    }

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
        }
        finally
        {
            _gate.Release();
        }

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
        };
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

        var config = new NtpConfig { Enabled = request.Enabled, InterfaceId = interfaceId, InterfaceName = interfaceName, Upstream = upstreamText };
        if (_settings is not null)
        {
            await _settings.SetAsync(ConfigKey, NtpJson.Serialize(config), ct).ConfigureAwait(false);
        }

        await ApplyAsync(config, seed, ct).ConfigureAwait(false);
        return new SaveReply(true, null, seed?.Describe(), GetState(full: false));
    }

    /// <summary>
    /// The address a device should use as NTP server: the selected interface's address of the device's address family, or
    /// with "All interfaces" the local address the server routes to the device with. Null + reason when not possible.
    /// </summary>
    public (IPAddress? Address, string? Error) ServerAddressFor(IPAddress device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!_config.Enabled || _server is null)
        {
            return (null, "The OADM NTP server is not running. Enable it on the NTP server page. Nothing was changed.");
        }

        var family = device.IsIPv4MappedToIPv6 ? AddressFamily.InterNetwork : device.AddressFamily;
        if (!IsAll(_config.InterfaceId))
        {
            var bound = _server.LocalEndpoints.Select(e => e.Address).ToList();
            var match = bound.FirstOrDefault(a => a.AddressFamily == family);
            return match is null
                ? (null, $"The NTP server listens on {_config.InterfaceName} without an {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address for this device. Nothing was changed.")
                : (match, null);
        }

        try
        {
            using var probe = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(device.IsIPv4MappedToIPv6 ? device.MapToIPv4() : device, 123)); // no packet is sent
            var local = ((IPEndPoint)probe.LocalEndPoint!).Address;
            return local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any)
                ? (null, "The server has no route to the device. Nothing was changed.")
                : (local, null);
        }
        catch (SocketException ex)
        {
            return (null, $"The server has no route to the device ({ex.SocketErrorCode}). Nothing was changed.");
        }
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
            if (Socket.OSSupportsIPv6)
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

                PublishRequests();
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

    private void PublishRequests()
    {
        var last = _log.LastSeq;
        if (last <= _publishedSeq)
        {
            return;
        }

        var entries = _log.Snapshot(_publishedSeq);
        _publishedSeq = last;
        _events?.Publish(NtpServerMethods.RequestsTopic, NtpJson.Serialize(new RequestsEvent(entries)));
    }

    private void PublishState(bool force)
    {
        var json = NtpJson.Serialize(GetState(full: false));
        if (!force && string.Equals(json, _publishedState, StringComparison.Ordinal))
        {
            return;
        }

        _publishedState = json;
        _events?.Publish(NtpServerMethods.StateTopic, json);
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored NTP server settings ignored: {Error}")]
    private partial void LogBadConfig(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NTP server background work failed")]
    private partial void LogBackgroundFailed(Exception ex);
}
