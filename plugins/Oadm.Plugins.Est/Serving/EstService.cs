using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Est.DeviceId;
using Oadm.Plugins.Est.Protocol;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
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
    /// <summary>The bound sites and their relays; null on hosts without sites.</summary>
    public ISites? Sites { get; init; }

    public IPluginEvents? Events { get; init; }

    /// <summary>The plugin's records (the certificates, one row each); null keeps them in memory only (tests).</summary>
    public IPluginRecords? Records { get; init; }

    public Sdk.Events.IEventPublisher? Publisher { get; init; }

    /// <summary>The device groups: pages of users limited to a group get the rows of their own devices; null on older hosts.</summary>
    public IDeviceScopes? Scopes { get; init; }

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The Axis device ID CAs; the embedded ones by default.</summary>
    public AxisDeviceId? DeviceIds { get; init; }

    /// <summary>This server's addresses for its TLS certificate; the network cards by default.</summary>
    public Func<IReadOnlyList<string>> LocalAddresses { get; init; } = ServerCertificates.LocalAddresses;

    public string HostName { get; init; } = Environment.MachineName;

    /// <summary>The server's network interfaces for "Listen on".</summary>
    public IServerNetworkInterfaces Interfaces { get; init; } = SystemNetworkInterfaces.Instance;
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
    private const int MaxConnectionsPerAddress = 4;
    private static readonly TimeSpan MaxConnectionTime = TimeSpan.FromMinutes(2);
    private const int MaxRequestsPerConnection = 20;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeviceIndexAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RenewalCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RenewalCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ListenRetryInterval = TimeSpan.FromSeconds(30);
    private const int MaxContexts = 64;
    private const int MaxNotRenewedLogLines = 20;

    /// <summary>
    /// Axis cameras renew halfway through the lifetime and try again daily: a certificate counts as not renewed this long
    /// after its halfway point (short ones: a tenth of their lifetime) without a newer one.
    /// </summary>
    public static readonly TimeSpan NotRenewedGrace = TimeSpan.FromDays(2);

    private readonly EstHost _host;
    private readonly EstStore _store;
    private readonly EstEndpoint _endpoint;
    private readonly AxisDeviceId _deviceIds;
    private readonly ServerCertificates _certificates;
    private readonly Dictionary<System.Net.IPAddress, int> _perAddress = [];
    private readonly HashSet<Task> _serving = [];
    private int _open;
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly SemaphoreSlim _index = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private readonly Lock _contextGate = new();
    private readonly Dictionary<X509Certificate2, SslStreamCertificateContext> _contexts = new(ReferenceEqualityComparer.Instance);
    private ITcpRelay? _listener;
    private readonly Dictionary<Guid, (string Name, ITcpRelay Relay)> _siteRelays = [];
    private readonly SemaphoreSlim _sitesGate = new(1, 1);
    private Task _acceptLoop = Task.CompletedTask;
    private Dictionary<string, IDeviceInfo> _bySerial = new(StringComparer.OrdinalIgnoreCase);
    private long _indexedAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private ITimer? _renewalTimer;
    private ITimer? _listenRetry;
    private volatile bool _stopping; // DisposeAsync began: nothing opens a listener or relay any more
    private IReadOnlyList<System.Net.IPAddress> _boundAddresses = [];
    private (DateTimeOffset At, IReadOnlyList<string> Addresses)? _certificateAddresses;
    private int _checkingRenewals;

    public EstService(EstHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = host.Logger;
        Viewers = new ViewerScopes(host.Scopes);
        _store = new EstStore(host.Settings, host.Records, host.Time) { Logger = host.Logger };
        _certificates = new ServerCertificates(host.Authority, host.Time);
        _deviceIds = host.DeviceIds ?? new AxisDeviceId();
        _endpoint = new EstEndpoint(_store, _deviceIds, FindBySerialAsync, host.Authority, host.ProActive, host.Time,
            async (id, ct) => await host.Devices.FindAsync(id, ct).ConfigureAwait(false) is not null, InAnyGroup);
    }

    /// <summary>
    /// True when a device group covers the device (also without the host's groups: fail closed). Its certificates then
    /// take no host name the camera reports (docs/spec/device-scope.md "Host names in certificates").
    /// </summary>
    public bool InAnyGroup(IDeviceInfo device) => _host.Scopes is null || _host.Scopes.InAnyGroup(device);

    public EstStore Store => _store;

    /// <summary>The device groups whose users read the page (state and list events per group).</summary>
    internal ViewerScopes Viewers { get; }

    /// <summary>The OADM CA now (null without PKI).</summary>
    public IDeviceCertificateAuthority? Authority => _host.Authority();

    /// <summary>OADM PRO is active.</summary>
    public bool ProActive => _host.ProActive();

    /// <summary>The Axis device ID CAs the server checks cameras against.</summary>
    public AxisDeviceId DeviceIds => _deviceIds;

    public TimeProvider Time => _host.Time;

    /// <summary>The listener on this server's network (null while closed).</summary>
    public ITcpRelay? Listener => _listener;

    /// <summary>Loads, listens, starts the timers and the sites' relays; a start that fails stops what it began.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await _store.LoadAsync(ct).ConfigureAwait(false);
            await OpenAsync(_store.Config.Port, ct).ConfigureAwait(false);
            _renewalTimer = _host.Time.CreateTimer(_ => Track(CheckRenewalsAsync(_cts.Token)), null, RenewalCheckDelay, RenewalCheckInterval);
            _listenRetry = _host.Time.CreateTimer(_ => Track(RetryListenAsync(_cts.Token)), null, ListenRetryInterval, ListenRetryInterval);
            if (_host.Sites is { } sites)
            {
                sites.SitesChanged += OnSitesChanged;
                await SyncSitesAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The relay of a site (null when none is open): the set-up task reports its state.</summary>
    public ITcpRelay? SiteRelay(Guid siteId)
    {
        lock (_siteRelays)
        {
            return _siteRelays.TryGetValue(siteId, out var entry) ? entry.Relay : null;
        }
    }

    /// <summary>
    /// A camera at <paramref name="siteId"/> gets <paramref name="address"/> (the node's address towards it) as EST server:
    /// the address goes into the site's server certificate and the site's relay opens (the node app may ask to allow the
    /// port). Returns the site's relay; null when the site is not bound or cannot carry one.
    /// </summary>
    public async Task<ITcpRelay?> ServeSiteAsync(Guid siteId, string address, CancellationToken ct)
    {
        _store.AddSiteAddress(siteId, address);
        await SyncSitesAsync(ct).ConfigureAwait(false);
        PublishState();
        return SiteRelay(siteId);
    }

    /// <summary>
    /// One relay per bound site with set-up cameras; a site that is gone loses its relay (released: its node gives the
    /// port back) and its addresses.
    /// </summary>
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
            // A site missing from the bound ones (removed, or its node was reset and waits to be replaced) loses its relay
            // below; its addresses stay until the hourly check finds no managed device there (a replaced node keeps the site).
            var wanted = _store.Sites().Where(bound.ContainsKey).ToHashSet();
            List<ITcpRelay> remove;
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
                    var relay = await sites.OpenTcpRelayAsync(id, _store.Config.Port, "EST server", ct).ConfigureAwait(false);
                    relay.StateChanged += (_, _) => PublishState();
                    lock (_siteRelays)
                    {
                        _siteRelays[id] = (bound[id].Name, relay);
                    }

                    Track(Task.Run(() => AcceptLoopAsync(relay, _cts.Token), CancellationToken.None));
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
    }

    private async Task CloseSitesAsync(bool release)
    {
        List<ITcpRelay> relays;
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

    private void OnSitesChanged(object? sender, EventArgs e) => _ = Task.Run(async () =>
    {
        try
        {
            await SyncSitesAsync(_cts.Token).ConfigureAwait(false);
            PublishState();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // stopping
        }
    });

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

        if (_renewalTimer is not null)
        {
            await _renewalTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_listenRetry is not null)
        {
            await _listenRetry.DisposeAsync().ConfigureAwait(false);
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await CloseAsync(release: false).ConfigureAwait(false);
        await _sitesGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await CloseSitesAsync(release: false).ConfigureAwait(false); // the port stays allowed at the nodes for the next start
        _sitesGate.Release();
        Task[] serving;
        lock (_serving)
        {
            serving = [.. _serving];
        }

        try
        {
            await Task.WhenAll(serving).ConfigureAwait(false); // every connection ends before the store writes the last time
        }
#pragma warning disable CA1031 // A connection or check that failed or was cancelled never stops the last write.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeRest();
        }
    }

    private void DisposeRest()
    {
        _certificates.Dispose();
        _cts.Dispose();
        _apply.Dispose();
        _index.Dispose();
        _sitesGate.Dispose();
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
            text = !InterfaceOptions.IsAll(config.InterfaceId) && ListenInterface() is null ? NotAvailable(config) : "Stopped";
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

        var listeners = listener is null ? new List<ListenerState>() : [new(null, "This server", listener.State == RelayState.Running, listener.StateText)];
        lock (_siteRelays)
        {
            listeners.AddRange(_siteRelays.Select(s => new ListenerState(s.Key, s.Value.Name, s.Value.Relay.State == RelayState.Running, s.Value.Relay.StateText))
                .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase));
        }

        return new EstState
        {
            Config = config, StatusText = text, StatusOk = ok, Listeners = listeners, CertificateCount = _store.CertificateCount, Interfaces = ListInterfaces(),
        };
    }

    /// <summary>
    /// The state for a user limited to a device group (<paramref name="view"/>; null: every device): the listeners of the
    /// server's own network and of the sites where the group has devices, the certificates of its devices.
    /// </summary>
    internal EstState GetState(ViewedSet? view)
    {
        var state = GetState();
        return view is null ? state : state with
        {
            Listeners = [.. state.Listeners.Where(l => view.CoversSite(l.SiteId))],
            CertificateCount = _store.CountCertificates(view.Contains),
        };
    }

    /// <summary>The enrollments of the devices of <paramref name="view"/> (null: all; refusals of cameras OADM does not manage only then).</summary>
    internal IReadOnlyList<Enrollment> Enrollments(ViewedSet? view) =>
        view is null ? _store.Enrollments() : [.. _store.Enrollments().Where(e => view.Contains(e.DeviceId))];

    /// <summary>The certificate a listed enrollment of the devices of <paramref name="view"/> issued; null when gone or outside the view.</summary>
    internal async Task<byte[]?> IssuedAsync(string serialNumber, ViewedSet? view, CancellationToken ct)
    {
        if (view is not null && !_store.Enrollments().Any(e => view.Contains(e.DeviceId)
                && string.Equals(e.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase)))
        {
            return null; // answered like a certificate OADM no longer keeps
        }

        return await _store.IssuedAsync(serialNumber, ct).ConfigureAwait(false);
    }

    /// <summary>"Listen on": All interfaces first, then every interface that is up and has an address.</summary>
    public IReadOnlyList<InterfaceOption> ListInterfaces() =>
        [InterfaceOptions.All, .. UsableInterfaces().Select(n => InterfaceOptions.From(n))];

    /// <summary>The chosen interface now; null with All interfaces or when it is gone.</summary>
    public ServerNetworkInterface? ListenInterface()
    {
        var id = _store.Config.InterfaceId;
        return InterfaceOptions.IsAll(id) ? null : UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
    }

    /// <summary>The addresses the chosen interface listens on (no IPv6 link-local); empty with All interfaces.</summary>
    public static IReadOnlyList<System.Net.IPAddress> ListenAddresses(ServerNetworkInterface? nic) =>
        nic is null ? [] : [.. nic.Addresses.Where(a => !a.IsIPv6LinkLocal)];

    internal static string NotAvailable(EstConfig config) => $"Interface {config.InterfaceName ?? config.InterfaceId} is not available";

    private IEnumerable<ServerNetworkInterface> UsableInterfaces() =>
        _host.Interfaces.List().Where(n => n.IsUp && !n.IsLoopback && ListenAddresses(n).Count > 0);

    public async Task<SaveReply> SaveAsync(SaveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request.Config) is { } error)
        {
            return new SaveReply(null, error.Field, error.Message);
        }

        var config = request.Config;
        if (InterfaceOptions.IsAll(config.InterfaceId))
        {
            config = config with { InterfaceId = InterfaceOptions.AllId, InterfaceName = InterfaceOptions.AllLabel };
        }
        else if (UsableInterfaces().FirstOrDefault(n => string.Equals(n.Id, config.InterfaceId, StringComparison.Ordinal)) is { } nic)
        {
            config = config with { InterfaceName = nic.Name };
        }
        else if (string.Equals(config.InterfaceId, _store.Config.InterfaceId, StringComparison.Ordinal))
        {
            config = config with { InterfaceName = _store.Config.InterfaceName }; // gone meanwhile: kept, it listens again when it is back
        }
        else
        {
            return new SaveReply(null, "interface", "This interface is not available on the server.");
        }

        var portChanged = config.Port != _store.Config.Port;
        var reopen = portChanged || !string.Equals(config.InterfaceId, _store.Config.InterfaceId, StringComparison.Ordinal);
        await _store.SaveConfigAsync(config, ct).ConfigureAwait(false);
        if (reopen)
        {
            await CloseAsync(release: true).ConfigureAwait(false);
            await OpenAsync(config.Port, ct).ConfigureAwait(false);
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

            await SyncSitesAsync(ct).ConfigureAwait(false); // the sites listen on the new port
        }

        PublishState();
        return new SaveReply(GetState());
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
                return ($"profiles[{i}].name", "Use 1 to 32 letters, digits or _.");
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
            if (_listener is not null || _stopping)
            {
                return;
            }

            ITcpRelay listener;
            if (InterfaceOptions.IsAll(_store.Config.InterfaceId))
            {
                listener = await network.ListenTcpAsync(port, "EST server", ct).ConfigureAwait(false);
            }
            else if (ListenInterface() is { } nic)
            {
                _boundAddresses = ListenAddresses(nic);
                listener = await network.ListenTcpAsync(port, "EST server", _boundAddresses, ct).ConfigureAwait(false);
            }
            else
            {
                LogInterfaceMissing(NotAvailable(_store.Config)); // tried again every 30 s
                return;
            }

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
#pragma warning disable CA1031 // A failed accept must not end the server; the reason is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogAcceptFailed(ex);
                await Task.Delay(TimeSpan.FromSeconds(1), _host.Time, ct).ConfigureAwait(false);
                continue;
            }

            var address = connection.Remote.Address;
            if (!TryEnter(address))
            {
                await connection.DisposeAsync().ConfigureAwait(false); // too many at once (in all, or from this address): the camera tries again later
                continue;
            }

            var serving = Task.Run(async () =>
            {
                try
                {
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    lifetime.CancelAfter(MaxConnectionTime);
                    await ServeAsync(connection, lifetime.Token).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One broken connection must not end the server; the reason is logged.
                catch (Exception ex) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    LogConnectionFailed(ex, connection.Remote);
                }
                finally
                {
                    Leave(address);
                }
            }, CancellationToken.None);
            Track(serving);
        }
    }

    /// <summary>A connection slot: at most 64 in all and 4 per address, so one host cannot lock the cameras out.</summary>
    private bool TryEnter(System.Net.IPAddress address)
    {
        lock (_perAddress)
        {
            if (_open >= MaxConnections || _perAddress.GetValueOrDefault(address) >= MaxConnectionsPerAddress)
            {
                return false;
            }

            _open++;
            _perAddress[address] = _perAddress.GetValueOrDefault(address) + 1;
            return true;
        }
    }

    private void Leave(System.Net.IPAddress address)
    {
        lock (_perAddress)
        {
            _open--;
            if (--_perAddress[address] <= 0)
            {
                _perAddress.Remove(address);
            }
        }
    }

    private void Track(Task serving)
    {
        lock (_serving)
        {
            _serving.RemoveWhere(t => t.IsCompleted);
            _serving.Add(serving);
        }
    }

    /// <summary>One camera connection: TLS, then requests until it closes, is idle or reached the limit.</summary>
    internal async Task ServeAsync(IIncomingConnection connection, CancellationToken ct)
    {
        await using var _ = connection.ConfigureAwait(false);
        var site = connection.SiteId ?? Guid.Empty;
        // At a site: the node's addresses the cameras there were given (no SNI: the site picks the certificate).
        var addresses = connection.SiteId is { } siteId ? _store.SiteAddresses(siteId) : CertificateAddresses();
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
            ServerCertificateContext = ContextFor(certificate),
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            // The client's chain is only collected here: never a download of an issuer URL the client names.
            CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck },
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
            if (client is null && tls.RemoteCertificate is { } remote)
            {
                client = X509CertificateLoader.LoadCertificate(remote.GetRawCertData()); // a resumed session skips the callback
            }

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
                // A refusal of who the camera is ends the connection: nobody keeps a slot without a valid identity.
                var close = request.CloseAfter || count == MaxRequestsPerConnection || result.Response.Status is 401 or 403
                    || identity.Certificate is null; // no identity: one answer (the CA), never a slot held
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

    /// <summary>
    /// The TLS context of a server certificate with the CA chain, made once per certificate. Chain building can fail on
    /// some systems ("unknown chain building error"): then the certificate alone (the cameras have the CA chain anyway).
    /// </summary>
    private SslStreamCertificateContext ContextFor(X509Certificate2 certificate)
    {
        lock (_contextGate)
        {
            if (_contexts.TryGetValue(certificate, out var cached))
            {
                return cached;
            }

            if (_contexts.Count >= MaxContexts)
            {
                _contexts.Clear(); // certificates were replaced (addresses, CA): start over
            }

            SslStreamCertificateContext context;
            try
            {
                context = SslStreamCertificateContext.Create(certificate, AuthorityChain(), offline: true);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                LogChainFailed(ex);
                context = SslStreamCertificateContext.Create(certificate, null, offline: true);
            }

            _contexts[certificate] = context;
            return context;
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
        string where = SiteTexts.ThisServer;
        if (connection.SiteId is { } site)
        {
            lock (_siteRelays)
            {
                where = _siteRelays.TryGetValue(site, out var entry) ? "site " + entry.Name : "site " + site;
            }
        }

        if (result.Response.Status == 401)
        {
            LogNoIdentity(result.Operation, connection.Remote, where); // anything on the network can do this: debug only, never in Enrollments
        }
        else
        {
            LogRequest(result.Operation, result.Profile, connection.Remote, where, result.Response.Status, result.Enrollment?.Reason ?? string.Empty);
        }

        if (result.Enrollment is not { } enrollment)
        {
            return;
        }

        _store.AddEnrollment(enrollment, result.IssuedDer);
        // Only the pages that see the device; a refused camera OADM does not manage only for users with all devices.
        _host.Events?.Publish(EstMethods.EnrollmentsTopic, EstJson.Serialize(new EnrollmentList([enrollment])),
            enrollment.DeviceId is { } shownFor ? PluginEventAudience.Device(shownFor) : PluginEventAudience.AllDevices);
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

    /// <summary>
    /// Flags the certificates the cameras did not renew in time (<see cref="NotRenewedGrace"/>): event "Certificate not
    /// renewed" once per certificate, a log line, the page reloads its list. The certificates of a device OADM no longer
    /// manages are forgotten. Runs every hour; one check at a time.
    /// </summary>
    public async Task CheckRenewalsAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _checkingRenewals, 1) == 1)
        {
            return;
        }

        try
        {
            var now = _host.Time.GetUtcNow().UtcDateTime;
            var due = _store.DueForRenewal(now, NotRenewedGrace);
            var unbound = _host.Sites is { } sites
                ? _store.Sites().Where(id => !sites.List.Any(s => s.Id == id)).ToList()
                : [];
            if (due.Count == 0 && unbound.Count == 0)
            {
                return;
            }

            // One read of the device list for the whole check.
            var devices = await _host.Devices.ListAsync(ct).ConfigureAwait(false);
            var managed = devices.Select(d => d.Id).ToHashSet();
            var usedSites = devices.Select(d => d.SiteId).OfType<Guid>().ToHashSet();
            foreach (var gone in unbound.Where(id => !usedSites.Contains(id)))
            {
                _store.RemoveSite(gone); // removed for good: no managed device there any more
            }

            var flagged = 0;
            foreach (var certificate in due)
            {
                if (!managed.Contains(certificate.DeviceId))
                {
                    _store.ForgetDevice(certificate.DeviceId);
                    continue;
                }

                _store.MarkNotRenewed(certificate.SerialNumber, now);
                if (++flagged <= MaxNotRenewedLogLines)
                {
                    LogNotRenewed(certificate.Device, certificate.Profile, certificate.NotAfterUtc);
                }

                _host.Publisher?.Fire(EstPluginInfo.NotRenewedEvent, certificate.DeviceId, new Dictionary<string, string>
                {
                    ["profile"] = certificate.Profile,
                    ["validUntil"] = certificate.NotAfterUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC",
                });
            }

            if (flagged > MaxNotRenewedLogLines)
            {
                LogMoreNotRenewed(flagged - MaxNotRenewedLogLines);
            }

            if (flagged > 0)
            {
                PublishToEveryPage(EstMethods.CertificatesTopic, "{}"); // a signal: each page reads its own list again
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
#pragma warning disable CA1031 // The check runs again in an hour; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCheckFailed(ex);
        }
        finally
        {
            Volatile.Write(ref _checkingRenewals, 0);
        }
    }

    /// <summary>Delete all of the Enrollments list: every page empties its list.</summary>
    public void ClearEnrollments()
    {
        _store.ClearEnrollments();
        LogEnrollmentsCleared();
        PublishToEveryPage(EstMethods.EnrollmentsClearedTopic, "{}");
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
    /// The addresses of this server's TLS certificate: the chosen interface's (without temporary IPv6 addresses, like
    /// <see cref="ServerCertificates.LocalAddresses"/>) or every network card's; read at most every 30 s, not per connection.
    /// </summary>
    private IReadOnlyList<string> CertificateAddresses()
    {
        var now = _host.Time.GetUtcNow();
        if (_certificateAddresses is { } cached && now - cached.At < ListenRetryInterval)
        {
            return cached.Addresses;
        }

        var all = _host.LocalAddresses();
        IReadOnlyList<string> addresses = all;
        if (ListenInterface() is { } nic)
        {
            var own = ListenAddresses(nic).Select(a => a.ToString()).ToList();
            var stable = own.Where(all.Contains).ToList();
            addresses = stable.Count > 0 ? stable : own;
        }

        _certificateAddresses = (now, addresses);
        return addresses;
    }

    /// <summary>
    /// "Listen on" one interface: listens once it is back after it was gone, and again on its new addresses after they
    /// changed (a new DHCP lease).
    /// </summary>
    private async Task RetryListenAsync(CancellationToken ct)
    {
        if (InterfaceOptions.IsAll(_store.Config.InterfaceId))
        {
            return;
        }

        if (ListenInterface() is not { } nic)
        {
            if (_listener is not null)
            {
                try
                {
                    await CloseAsync(release: false).ConfigureAwait(false); // gone while listening: the status says so
                    PublishState();
                }
                catch (ObjectDisposedException)
                {
                    // stopping
                }
            }

            return;
        }

        var moved = _listener is not null && !ListenAddresses(nic).ToHashSet().SetEquals(_boundAddresses);
        if (_listener is not null && !moved)
        {
            return;
        }

        try
        {
            if (moved)
            {
                await CloseAsync(release: false).ConfigureAwait(false);
            }

            await OpenAsync(_store.Config.Port, ct).ConfigureAwait(false);
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

    private void PublishState()
    {
        if (_host.Events is not { } events)
        {
            return;
        }

        events.Publish(EstMethods.StateTopic, EstJson.Serialize(GetState()), PluginEventAudience.AllDevices);
        if (Viewers.Keys() is { Count: > 0 } keys)
        {
            _ = PublishScopedStateAsync(events, keys);
        }
    }

    /// <summary>The state of each group that reads the page, with its own listeners and certificates (best effort).</summary>
    private async Task PublishScopedStateAsync(IPluginEvents events, IReadOnlyList<string> keys)
    {
        try
        {
            await Task.Yield();
            foreach (var key in keys)
            {
                var view = await ViewedSet.OfScopeAsync(Viewers.Scopes, key, CancellationToken.None).ConfigureAwait(false);
                events.Publish(EstMethods.StateTopic, EstJson.Serialize(GetState(view)), PluginEventAudience.DeviceScope(key));
            }
        }
#pragma warning disable CA1031 // Events are best effort; the pages read the state again when they reconnect.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogScopedStateFailed(ex);
        }
    }

    /// <summary>The managed device with this serial; the index is read again when older than 30 s or on a miss (at most every 30 s).</summary>
    private async Task<IDeviceInfo?> FindBySerialAsync(string serial, CancellationToken ct)
    {
        var now = _host.Time.GetUtcNow();
        var indexedAt = new DateTimeOffset(Volatile.Read(ref _indexedAtTicks), TimeSpan.Zero);
        if (_bySerial.TryGetValue(serial, out var known) && now - indexedAt < DeviceIndexAge)
        {
            return known;
        }

        await _index.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (now - new DateTimeOffset(Volatile.Read(ref _indexedAtTicks), TimeSpan.Zero) >= DeviceIndexAge)
            {
                var devices = await _host.Devices.ListAsync(ct).ConfigureAwait(false);
                var index = new Dictionary<string, IDeviceInfo>(devices.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var device in devices)
                {
                    index.TryAdd(device.Serial, device);
                }

                _bySerial = index;
                Volatile.Write(ref _indexedAtTicks, now.UtcTicks);
            }

            return _bySerial.GetValueOrDefault(serial);
        }
        finally
        {
            _index.Release();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex ProfileName();

    [LoggerMessage(Level = LogLevel.Information, Message = "EST server at site {Site}: {State}")]
    private partial void LogSiteListening(string site, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST server cannot serve at site {Site}: {Error}")]
    private partial void LogSiteFailed(string site, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "EST server on port {Port}: {State}")]
    private partial void LogListening(int port, string state);

    [LoggerMessage(Level = LogLevel.Debug, Message = "EST {Operation} from {Remote} ({Where}): 401, no valid identity")]
    private partial void LogNoIdentity(string operation, System.Net.IPEndPoint remote, string where);

    [LoggerMessage(Level = LogLevel.Information, Message = "EST {Operation} {Profile} from {Remote} ({Where}): {Status} {Reason}")]
    private partial void LogRequest(string operation, string profile, System.Net.IPEndPoint remote, string where, int status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST connection from {Remote} closed: the PKI has no certificate authority")]
    private partial void LogNoCertificate(System.Net.IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Debug, Message = "EST connection from {Remote} ended: {Message}")]
    private partial void LogConnectionEnded(System.Net.IPEndPoint remote, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST: the CA chain for TLS cannot be built; the server certificate is sent alone")]
    private partial void LogChainFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST server: accepting a connection failed")]
    private partial void LogAcceptFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST connection from {Remote} failed")]
    private partial void LogConnectionFailed(Exception ex, System.Net.IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "EST: every enrollment was deleted")]
    private partial void LogEnrollmentsCleared();

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST: the check for certificates not renewed failed; tried again in an hour")]
    private partial void LogCheckFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST server: listening on the chosen interface failed; tried again in 30 s")]
    private partial void LogListenFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST server not listening: {Reason}")]
    private partial void LogInterfaceMissing(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST: {Device} did not renew its {Profile} certificate (valid until {NotAfter:yyyy-MM-dd HH:mm} UTC)")]
    private partial void LogNotRenewed(string device, string profile, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Debug, Message = "EST: publishing the state to the device groups failed")]
    private partial void LogScopedStateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST: {Count} more certificates were not renewed in time (the EST server page lists them)")]
    private partial void LogMoreNotRenewed(int count);
}
