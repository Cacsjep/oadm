using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Est.DeviceId;
using Oadm.Plugins.Est.Protocol;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.Est.Serving;

/// <summary>What the EST service needs from the host (the core plugin context, or fakes in tests).</summary>
public sealed record EstHost(
    IPluginSettings Settings,
    IDeviceRepository Devices,
    IPluginNetwork? Network,
    Func<IDeviceCertificateAuthority?> Authority,
    Func<bool> ProActive)
{
    public IPluginEvents? Events { get; init; }

    public Sdk.Events.IEventPublisher? Publisher { get; init; }

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The Axis device ID CAs; the embedded ones by default.</summary>
    public AxisDeviceId? DeviceIds { get; init; }

    /// <summary>This server's addresses for its TLS certificate; the network cards by default.</summary>
    public Func<IReadOnlyList<string>> LocalAddresses { get; init; } = ServerCertificates.LocalAddresses;

    public string HostName { get; init; } = Environment.MachineName;
}

/// <summary>
/// The EST server (docs/spec/est-server.md): listens on the server's network (TCP 9454 by default), TLS 1.2+ with the
/// server's certificate from the OADM CA and the camera's Axis device ID as client certificate, a small HTTP/1.1 per
/// connection (at most 20 requests, 30 s idle) and <see cref="EstEndpoint"/> for the operations. At most 64 connections
/// at once. One log line per request (never keys or requests). Page methods: <see cref="EstMethods"/>.
/// </summary>
public sealed partial class EstService : IAsyncDisposable
{
    private const int MaxConnections = 64;
    private const int MaxRequestsPerConnection = 20;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeviceIndexAge = TimeSpan.FromSeconds(30);

    private readonly EstHost _host;
    private readonly EstStore _store;
    private readonly EstEndpoint _endpoint;
    private readonly ServerCertificates _certificates;
    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly SemaphoreSlim _index = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private ITcpRelay? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private Dictionary<string, IDeviceInfo> _bySerial = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _indexedAt = DateTimeOffset.MinValue;

    public EstService(EstHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = host.Logger;
        _store = new EstStore(host.Settings, host.Time);
        _certificates = new ServerCertificates(host.Authority, host.Time);
        _endpoint = new EstEndpoint(_store, host.DeviceIds ?? new AxisDeviceId(), FindBySerialAsync, host.Authority, host.ProActive, host.Time);
    }

    public EstStore Store => _store;

    /// <summary>The listener on this server's network (null while closed).</summary>
    public ITcpRelay? Listener => _listener;

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.LoadAsync(ct).ConfigureAwait(false);
        await OpenAsync(_store.Config.Port, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await CloseAsync(release: false).ConfigureAwait(false);
        await _store.DisposeAsync().ConfigureAwait(false);
        _certificates.Dispose();
        _cts.Dispose();
        _connections.Dispose();
        _apply.Dispose();
        _index.Dispose();
    }

    public EstState GetState()
    {
        var listener = _listener;
        var config = _store.Config;
        string text;
        var ok = false;
        if (!_host.ProActive())
        {
            text = EstRefusals.NotPro;
        }
        else if (_host.Authority() is not { IsReady: true })
        {
            text = EstRefusals.NoCa;
        }
        else if (listener is null)
        {
            text = "Stopped";
        }
        else if (listener.State == RelayState.Running)
        {
            text = string.Create(CultureInfo.InvariantCulture, $"Running on port {config.Port}");
            ok = true;
        }
        else
        {
            text = listener.StateText;
        }

        var listeners = listener is null ? [] : new List<ListenerState> { new(null, "This server", listener.State == RelayState.Running, listener.StateText) };
        return new EstState { Config = config, StatusText = text, StatusOk = ok, Listeners = listeners, CertificateCount = _store.CertificateCount };
    }

    public async Task<SaveReply> SaveAsync(SaveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request.Config) is { } error)
        {
            return new SaveReply(null, error.Field, error.Message);
        }

        var portChanged = request.Config.Port != _store.Config.Port;
        await _store.SaveConfigAsync(request.Config, ct).ConfigureAwait(false);
        if (portChanged)
        {
            await CloseAsync(release: true).ConfigureAwait(false);
            await OpenAsync(request.Config.Port, ct).ConfigureAwait(false);
        }

        PublishState();
        return new SaveReply(GetState());
    }

    public RevokeReply Revoke(RevokeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = _store.Revoke(request.SerialNumbers ?? []);
        LogRevoked(count);
        PublishState();
        return new RevokeReply(count);
    }

    /// <summary>The field and its error of the first invalid entry; null when the settings are fine.</summary>
    internal static (string Field, string Message)? Validate(EstConfig config)
    {
        if (config.Port is < 1025 or > 65535)
        {
            return ("port", "Use 1025 to 65535.");
        }

        if (config.EnrollmentsKept is < 100 or > 5000)
        {
            return ("enrollmentsKept", "Keep from 100 to 5,000 enrollments.");
        }

        if (config.Profiles.Count == 0)
        {
            return ("profiles", "Add a profile.");
        }

        for (var i = 0; i < config.Profiles.Count; i++)
        {
            var profile = config.Profiles[i];
            if (!ProfileName().IsMatch(profile.Name ?? string.Empty))
            {
                return ($"profiles[{i}].name", "Use 1 to 32 letters, digits, - or _.");
            }

            if (config.Profiles.Take(i).Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return ($"profiles[{i}].name", "Another profile has this name.");
            }

            if (config.Profiles.Take(i).Any(p => p.Use == profile.Use))
            {
                return ($"profiles[{i}].use", "Another profile has this use.");
            }

            if (profile.ValidityDays is < 1 or > 825)
            {
                return ($"profiles[{i}].validityDays", "Use 1 to 825 days.");
            }
        }

        return null;
    }

    private async Task OpenAsync(int port, CancellationToken ct)
    {
        if (_host.Network is not { } network)
        {
            return;
        }

        await _apply.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var listener = await network.ListenTcpAsync(port, "EST server", ct).ConfigureAwait(false);
            listener.StateChanged += (_, _) => PublishState();
            _listener = listener;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
            LogListening(port, listener.StateText);
        }
        finally
        {
            _apply.Release();
        }
    }

    private async Task CloseAsync(bool release)
    {
        await _apply.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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

                try
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // closed
                }
            }
        }
        finally
        {
            _apply.Release();
        }
    }

    private async Task AcceptLoopAsync(IStreamListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IIncomingConnection connection;
            try
            {
                connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
            {
                return;
            }

            if (!_connections.Wait(0, CancellationToken.None))
            {
                await connection.DisposeAsync().ConfigureAwait(false); // too many at once: the camera tries again later
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(connection, ct).ConfigureAwait(false);
                }
                finally
                {
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>One camera connection: TLS, then requests until it closes, is idle or reached the limit.</summary>
    internal async Task ServeAsync(IIncomingConnection connection, CancellationToken ct)
    {
        await using var _ = connection.ConfigureAwait(false);
        var site = connection.SiteId ?? Guid.Empty;
        var addresses = connection.SiteId is null ? _host.LocalAddresses() : [];
        var certificate = await _certificates.GetAsync(site, addresses, connection.SiteId is null ? _host.HostName : null, ct).ConfigureAwait(false);
        if (certificate is null)
        {
            LogNoCertificate(connection.Remote);
            return; // no CA: nothing to offer TLS with
        }

        X509Certificate2? client = null;
        var presented = new List<X509Certificate2>();
        var tls = new SslStream(connection.Stream, leaveInnerStreamOpen: false);
        await using var tlsDispose = tls.ConfigureAwait(false);
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(certificate, AuthorityChain(), offline: true),
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
#pragma warning disable CA5359 // The client certificate is checked per request (EstEndpoint): a refusal gets an answer with its reason.
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
            {
                // Checked per request by EstEndpoint, so a refusal is an answer with its reason, not a broken handshake.
                if (certificate is not null)
                {
                    client = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    if (chain is not null)
                    {
                        foreach (var element in chain.ChainElements)
                        {
                            presented.Add(X509CertificateLoader.LoadCertificate(element.Certificate.RawData));
                        }

                        foreach (var extra in chain.ChainPolicy.ExtraStore)
                        {
                            presented.Add(X509CertificateLoader.LoadCertificate(extra.RawData));
                        }
                    }
                }

                return true;
            },
#pragma warning restore CA5359
        };

        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(IdleTimeout);
            await tls.AuthenticateAsServerAsync(options, handshake.Token).ConfigureAwait(false);
            var identity = new ClientIdentity(client, presented);
            var origin = new RequestOrigin(connection.SiteId, connection.Remote.Address);
            var reader = EstHttp.Reader(tls);
            for (var count = 1; count <= MaxRequestsPerConnection; count++)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);
                HttpRequest? request;
                try
                {
                    request = await reader.ReadAsync(idle.Token).ConfigureAwait(false);
                }
                catch (HttpFormatException ex)
                {
                    await EstHttp.WriteAsync(tls, HttpResponse.Text(400, ex.Message), close: true, ct).ConfigureAwait(false);
                    return;
                }

                if (request is null)
                {
                    return;
                }

                var result = await _endpoint.HandleAsync(request, identity, origin, ct).ConfigureAwait(false);
                var close = request.CloseAfter || count == MaxRequestsPerConnection;
                await EstHttp.WriteAsync(tls, result.Response, close, ct).ConfigureAwait(false);
                Record(result, connection);
                if (close)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested)
            {
                LogConnectionEnded(connection.Remote, ex.Message);
            }
        }
        finally
        {
            client?.Dispose();
            foreach (var certificateInChain in presented)
            {
                certificateInChain.Dispose();
            }
        }
    }

    private X509Certificate2Collection AuthorityChain()
    {
        var chain = new X509Certificate2Collection();
        foreach (var der in _host.Authority()?.Chain ?? [])
        {
            chain.Add(X509CertificateLoader.LoadCertificate(der));
        }

        return chain;
    }

    private void Record(EstResult result, IIncomingConnection connection)
    {
        var where = connection.SiteId is { } site ? "site " + site : SiteTexts.ThisServer;
        LogRequest(result.Operation, result.Profile, connection.Remote, where, result.Response.Status, result.Enrollment?.Reason ?? string.Empty);
        if (result.Enrollment is not { } enrollment)
        {
            return;
        }

        _store.AddEnrollment(enrollment);
        _host.Events?.Publish(EstMethods.EnrollmentsTopic, EstJson.Serialize(new EnrollmentList([enrollment])));
        if (enrollment.DeviceId is { } deviceId && _host.Publisher is { } publisher)
        {
            var data = new Dictionary<string, string>
            {
                ["profile"] = enrollment.Profile,
                ["kind"] = enrollment.Kind == EnrollmentKind.Renewal ? "Renewal" : "New",
                ["reason"] = enrollment.Reason,
            };
            publisher.Fire(enrollment.Ok ? EstPluginInfo.EnrolledEvent : EstPluginInfo.RefusedEvent, deviceId, data);
        }
    }

    private void PublishState() => _host.Events?.Publish(EstMethods.StateTopic, EstJson.Serialize(GetState()));

    /// <summary>The managed device with this serial; the index is read again when older than 30 s or on a miss (at most every 30 s).</summary>
    private async Task<IDeviceInfo?> FindBySerialAsync(string serial, CancellationToken ct)
    {
        var now = _host.Time.GetUtcNow();
        if (_bySerial.TryGetValue(serial, out var known) && now - _indexedAt < DeviceIndexAge)
        {
            return known;
        }

        await _index.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (now - _indexedAt >= DeviceIndexAge)
            {
                var devices = await _host.Devices.ListAsync(ct).ConfigureAwait(false);
                var index = new Dictionary<string, IDeviceInfo>(devices.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var device in devices)
                {
                    index.TryAdd(device.Serial, device);
                }

                _bySerial = index;
                _indexedAt = now;
            }

            return _bySerial.GetValueOrDefault(serial);
        }
        finally
        {
            _index.Release();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ProfileName();

    [LoggerMessage(Level = LogLevel.Information, Message = "EST server on port {Port}: {State}")]
    private partial void LogListening(int port, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "EST {Operation} {Profile} from {Remote} ({Where}): {Status} {Reason}")]
    private partial void LogRequest(string operation, string profile, System.Net.IPEndPoint remote, string where, int status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST connection from {Remote} closed: the PKI has no certificate authority")]
    private partial void LogNoCertificate(System.Net.IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Debug, Message = "EST connection from {Remote} ended: {Message}")]
    private partial void LogConnectionEnded(System.Net.IPEndPoint remote, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "EST: {Count} certificates revoked")]
    private partial void LogRevoked(int count);
}
