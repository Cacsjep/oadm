using System.Collections.Concurrent;
using System.Net;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;

namespace Oadm.Core.Sites;

/// <summary>
/// The network of one core plugin for one run (<see cref="Sdk.Plugins.ICorePluginContext.Network"/> and
/// <see cref="Sdk.Plugins.ICorePluginContext.Sites"/>): the route of any device or site, listeners on the server's own
/// network and relays into the sites. The host disposes the scope when the plugin stops, fails or is turned off, which
/// closes every listener and relay the plugin left open.
/// </summary>
public sealed class PluginNetworkScope : IPluginNetwork, IAsyncDisposable
{
    private readonly DeviceNetworks _networks;
    private readonly Sdk.Network.IFirewallRules? _firewall;
    private readonly TimeProvider? _time;
    private readonly bool _loopbackOnly;
    private readonly ConcurrentDictionary<IRelay, byte> _open = new();
    private int _disposed;

    /// <param name="networks">The routes.</param>
    /// <param name="relays">The relays into the sites; null = a server without sites (<see cref="Sites"/> is null).</param>
    /// <param name="firewall">The Windows service's firewall: listeners open their rule while they listen.</param>
    /// <param name="time">Retries of a port in use.</param>
    /// <param name="loopbackOnly">Tests: listeners bind on loopback only.</param>
    public PluginNetworkScope(DeviceNetworks networks, ISiteRelays? relays = null, Sdk.Network.IFirewallRules? firewall = null, TimeProvider? time = null, bool loopbackOnly = false)
    {
        ArgumentNullException.ThrowIfNull(networks);
        _networks = networks;
        _firewall = firewall;
        _time = time;
        _loopbackOnly = loopbackOnly;
        Sites = relays is null ? null : new PluginSites(this, relays);
    }

    /// <summary>The bound sites and relays into them; null without sites.</summary>
    public ISites? Sites { get; }

    /// <summary>Listeners and relays the plugin has open now (tests).</summary>
    public int OpenCount => _open.Count;

    public IDeviceNetwork ForDevice(IDeviceInfo device) => _networks.For(device);

    public IDeviceNetwork ForSite(Guid? siteId) => _networks.For(siteId);

    public async Task<IUdpRelay> ListenUdpAsync(int port, string reason, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Track(new TrackedUdp(this, await LocalListener.OpenUdpAsync(port, reason, _firewall, _time, _loopbackOnly, ct).ConfigureAwait(false)));
    }

    public async Task<ITcpRelay> ListenTcpAsync(int port, string reason, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Track(new TrackedTcp(this, await LocalListener.OpenTcpAsync(port, reason, _firewall, _time, _loopbackOnly, ct).ConfigureAwait(false)));
    }

    /// <summary>Closes every listener and relay still open (the plugin stopped).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        (Sites as PluginSites)?.Detach();
        foreach (var relay in _open.Keys.ToList())
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }

        _open.Clear();
    }

    private T Track<T>(T relay)
        where T : IRelay
    {
        _open[relay] = 0;
        if (Volatile.Read(ref _disposed) == 1)
        {
            _ = relay.DisposeAsync().AsTask(); // stopped meanwhile
            throw new ObjectDisposedException(nameof(PluginNetworkScope), "The plugin stopped.");
        }

        return relay;
    }

    private void Untrack(IRelay relay) => _open.TryRemove(relay, out _);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private sealed class PluginSites : ISites
    {
        private readonly PluginNetworkScope _scope;
        private readonly ISiteRelays _relays;

        public PluginSites(PluginNetworkScope scope, ISiteRelays relays)
        {
            _scope = scope;
            _relays = relays;
            _relays.SitesChanged += OnChanged;
        }

        public IReadOnlyList<ISiteInfo> List => _relays.BoundSites;

        public event EventHandler? SitesChanged;

        public Task<IUdpRelay> OpenUdpRelayAsync(Guid siteId, int port, string reason, CancellationToken ct)
        {
            _scope.ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IUdpRelay>(_scope.Track(new TrackedUdp(_scope, _relays.OpenUdp(siteId, port, reason))));
        }

        public Task<ITcpRelay> OpenTcpRelayAsync(Guid siteId, int port, string reason, CancellationToken ct)
        {
            _scope.ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ITcpRelay>(_scope.Track(new TrackedTcp(_scope, _relays.OpenTcp(siteId, port, reason))));
        }

        /// <summary>The plugin stopped: no more site events reach it.</summary>
        public void Detach() => _relays.SitesChanged -= OnChanged;

        private void OnChanged(object? sender, EventArgs e) => SitesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A relay the scope closes when the plugin stops; disposing it removes it from the scope.</summary>
    private abstract class Tracked<T>(PluginNetworkScope scope, T inner) : IRelay
        where T : IRelay
    {
        protected T Inner { get; } = inner;

        public Guid? SiteId => Inner.SiteId;

        public SiteProtocol Protocol => Inner.Protocol;

        public int Port => Inner.Port;

        public RelayState State => Inner.State;

        public string StateText => Inner.StateText;

        public event EventHandler? StateChanged
        {
            add => Inner.StateChanged += value;
            remove => Inner.StateChanged -= value;
        }

        public async ValueTask DisposeAsync()
        {
            scope.Untrack(this);
            await Inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class TrackedUdp(PluginNetworkScope scope, IUdpRelay inner) : Tracked<IUdpRelay>(scope, inner), IUdpRelay
    {
        public ValueTask<Datagram> ReceiveAsync(CancellationToken ct) => Inner.ReceiveAsync(ct);

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct) => Inner.SendAsync(data, remote, ct);
    }

    private sealed class TrackedTcp(PluginNetworkScope scope, ITcpRelay inner) : Tracked<ITcpRelay>(scope, inner), ITcpRelay
    {
        public ValueTask<IIncomingConnection> AcceptAsync(CancellationToken ct) => Inner.AcceptAsync(ct);
    }
}
