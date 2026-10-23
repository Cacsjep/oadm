using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Oadm.Sdk.Sites;

namespace Oadm.Core.Sites;

/// <summary>
/// The server's own network as <see cref="IDeviceNetwork"/> (docs/spec/sites.md, Plugins and SDK): ping, a TCP probe, DNS and
/// the server's address towards a device. The only place plugins' network checks open sockets on the server; a site route
/// is <see cref="SiteDeviceNetwork"/>.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The host's network checks and listeners on the server's own network (IDeviceNetwork, IPluginNetwork): plugins never open sockets themselves.")]
public sealed class LocalDeviceNetwork : IDeviceNetwork
{
    /// <summary>The server's own network.</summary>
    public static LocalDeviceNetwork Instance { get; } = new();

    public Guid? SiteId => null;

    public async Task<bool> PingAsync(string address, TimeSpan timeout, CancellationToken ct)
    {
        var ip = await AddressOfAsync(address, ct).ConfigureAwait(false);
        if (ip is null)
        {
            return false;
        }

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, timeout, buffer: null, options: null, ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false; // no route, ping not available: no answer
        }
    }

    public async Task<bool> ProbeTcpAsync(string address, int port, TimeSpan timeout, CancellationToken ct)
    {
        var ip = await AddressOfAsync(address, ct).ConfigureAwait(false);
        if (ip is null)
        {
            return false;
        }

        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(ip, port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true; // a host sent a reset: something is at this address
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var text = name.Trim().Trim('[', ']');
        if (IPAddress.TryParse(text, out var ip))
        {
            return [ip];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(text, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    public async Task<IPAddress?> LocalAddressTowardsAsync(string address, CancellationToken ct)
    {
        var ip = await AddressOfAsync(address, ct).ConfigureAwait(false);
        if (ip is null)
        {
            return null;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        try
        {
            // A connected UDP socket asks the routing table; no packet is sent.
            using var probe = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(ip, 123));
            var local = ((IPEndPoint)probe.LocalEndPoint!).Address;
            return local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any) ? null : local;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>An IP address as is, a name resolved on the server (IPv4 first); null when it has none.</summary>
    private async Task<IPAddress?> AddressOfAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var found = await ResolveAsync(address, ct).ConfigureAwait(false);
        return found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? (found.Count > 0 ? found[0] : null);
    }
}

/// <summary>
/// Listeners on the server's own network for plugins (<see cref="IPluginNetwork.ListenUdpAsync"/>,
/// <see cref="IPluginNetwork.ListenTcpAsync"/>): bound on every address (IPv4 and IPv6), or on the addresses of one
/// interface (an address that is not available is left out while the others serve), by the host, with the firewall rule
/// of the Windows service open while it listens. A port another program holds is <see cref="RelayState.PortInUse"/>
/// and tried again every <see cref="LocalListener.RetryInterval"/>.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The host's listeners on the server's own network for plugins (IPluginNetwork).")]
public abstract class LocalListener : IRelay
{
    /// <summary>How often a port that could not be bound is tried again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly Sdk.Network.FirewallRuleKeeper _firewall;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _lock = new();
    private readonly string _reason;
    private readonly IReadOnlyList<IPAddress> _addresses;
    private List<Socket> _sockets = [];
    private ITimer? _retry;
    private RelayState _state = RelayState.PortInUse;
    private int _disposed;

    private protected LocalListener(SiteProtocol protocol, int port, string reason, Sdk.Network.IFirewallRules? firewall, TimeProvider? time, bool loopbackOnly,
        IReadOnlyList<IPAddress>? addresses = null)
    {
        _addresses = addresses ?? [];
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Protocol = protocol;
        Port = port;
        _reason = string.IsNullOrWhiteSpace(reason) ? "Plugin" : reason.Trim();
        _time = time ?? TimeProvider.System;
        LoopbackOnly = loopbackOnly;
        _firewall = new Sdk.Network.FirewallRuleKeeper(
            Sdk.Network.FirewallRule.ForService(_reason, protocol == SiteProtocol.Udp ? Sdk.Network.FirewallProtocol.Udp : Sdk.Network.FirewallProtocol.Tcp, port),
            firewall);
    }

    public Guid? SiteId => null;

    public SiteProtocol Protocol { get; }

    /// <summary>The port; after binding port 0 (tests) the port the system chose.</summary>
    public int Port { get; private set; }

    /// <summary>Tests: bind on loopback only, never on a real network card.</summary>
    public bool LoopbackOnly { get; }

    public RelayState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public string StateText => SiteTexts.RelayStateText(_reason, SiteTexts.ThisServer, State, Protocol, Port);

    public event EventHandler? StateChanged;

    /// <summary>The bound sockets (one per address family).</summary>
    private protected IReadOnlyList<Socket> Sockets
    {
        get
        {
            lock (_lock)
            {
                return _sockets;
            }
        }
    }

    private protected CancellationToken Lifetime => _life.Token;

    /// <summary>Binds now; on failure the state says why and binding is tried again later.</summary>
    internal async Task StartAsync(CancellationToken ct)
    {
        await _firewall.SyncAsync(true, ct).ConfigureAwait(false);
        Bind();
    }

    private void Bind()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        var sockets = new List<Socket>();
        RelayState state;
        try
        {
            foreach (var address in Addresses())
            {
                var socket = Protocol == SiteProtocol.Udp
                    ? new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                    : new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(socket);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = false;
                }

                if (OperatingSystem.IsWindows())
                {
                    socket.ExclusiveAddressUse = true;
                }

                try
                {
                    socket.Bind(new IPEndPoint(address, Port));
                }
                catch (SocketException e) when (_addresses.Count > 1 && e.SocketErrorCode == SocketError.AddressNotAvailable)
                {
                    // One address of a chosen interface is not ready (an IPv6 address still checked) or gone: the others serve.
                    sockets.Remove(socket);
                    socket.Dispose();
                    continue;
                }

                Port = ((IPEndPoint)socket.LocalEndPoint!).Port; // port 0: the IPv6 socket takes the same port
                if (Protocol == SiteProtocol.Tcp)
                {
                    socket.Listen(128);
                }
            }

            state = sockets.Count > 0 ? RelayState.Running : RelayState.PortInUse; // none of the addresses: tried again later
        }
        catch (SocketException e)
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }

            sockets = [];
            state = e.SocketErrorCode == SocketError.AccessDenied ? RelayState.NoPermission : RelayState.PortInUse;
        }

        bool changed;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }

                return;
            }

            _sockets = sockets;
            changed = _state != state || state == RelayState.Running;
            _state = state;
            if (state != RelayState.Running)
            {
                _retry ??= Auth.CallerContext.Detached(() => _time.CreateTimer(_ => Retry(), null, RetryInterval, RetryInterval));
            }
            else
            {
                _retry?.Dispose();
                _retry = null;
            }
        }

        if (state == RelayState.Running)
        {
            foreach (var socket in sockets)
            {
                StartReceiving(socket);
            }
        }

        if (changed)
        {
            PluginEvents.Raise(StateChanged, this);
        }
    }

    private void Retry()
    {
        if (State != RelayState.Running)
        {
            Bind();
        }
    }

    private IEnumerable<IPAddress> Addresses()
    {
        if (LoopbackOnly)
        {
            yield return IPAddress.Loopback;
            yield break;
        }

        if (_addresses.Count > 0)
        {
            // One interface: its own addresses only.
            foreach (var address in _addresses)
            {
                yield return address;
            }

            yield break;
        }

        yield return IPAddress.Any;
        if (Socket.OSSupportsIPv6)
        {
            yield return IPAddress.IPv6Any;
        }
    }

    /// <summary>Starts the receive or accept loop of one bound socket.</summary>
    private protected abstract void StartReceiving(Socket socket);

    /// <summary>The socket ended by itself (a card went away): bound again after the retry interval.</summary>
    private protected void Failed()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1 || _state != RelayState.Running)
            {
                return;
            }

            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }

            _sockets = [];
            _state = RelayState.PortInUse;
            _retry ??= Auth.CallerContext.Detached(() => _time.CreateTimer(_ => Retry(), null, RetryInterval, RetryInterval));
        }

        PluginEvents.Raise(StateChanged, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        List<Socket> sockets;
        lock (_lock)
        {
            sockets = _sockets;
            _sockets = [];
            _retry?.Dispose();
            _retry = null;
        }

        await _life.CancelAsync().ConfigureAwait(false);
        foreach (var socket in sockets)
        {
            socket.Dispose();
        }

        await OnDisposedAsync().ConfigureAwait(false);
        await _firewall.SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
        _life.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Ends what a derived listener keeps (accepted connections).</summary>
    private protected virtual ValueTask OnDisposedAsync() => ValueTask.CompletedTask;

    /// <summary>A UDP listener on the server's own network.</summary>
    internal static async Task<IUdpRelay> OpenUdpAsync(int port, string reason, Sdk.Network.IFirewallRules? firewall, TimeProvider? time, bool loopbackOnly, CancellationToken ct)
    {
        var listener = new LocalUdpListener(port, reason, firewall, time, loopbackOnly);
        await listener.StartAsync(ct).ConfigureAwait(false);
        return listener;
    }

    /// <summary>A TCP listener on the server's own network.</summary>
    internal static async Task<ITcpRelay> OpenTcpAsync(int port, string reason, Sdk.Network.IFirewallRules? firewall, TimeProvider? time, bool loopbackOnly,
        IReadOnlyList<IPAddress>? addresses, CancellationToken ct)
    {
        var listener = new LocalTcpListener(port, reason, firewall, time, loopbackOnly, addresses);
        await listener.StartAsync(ct).ConfigureAwait(false);
        return listener;
    }

    private sealed class LocalUdpListener(int port, string reason, Sdk.Network.IFirewallRules? firewall, TimeProvider? time, bool loopbackOnly)
        : LocalListener(SiteProtocol.Udp, port, reason, firewall, time, loopbackOnly), IUdpRelay
    {
        private readonly System.Threading.Channels.Channel<(Datagram Datagram, Socket Socket)> _received =
            System.Threading.Channels.Channel.CreateBounded<(Datagram, Socket)>(new System.Threading.Channels.BoundedChannelOptions(1024)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

        // The socket a sender's datagram came in on answers it (IPv4 or IPv6): the most recent senders.
        private readonly RecentSenders<Socket> _senders = new(RecentSenders<Socket>.DefaultCapacity);

        public async ValueTask<Datagram> ReceiveAsync(CancellationToken ct)
        {
            var (datagram, socket) = await _received.Reader.ReadAsync(ct).ConfigureAwait(false);
            _senders.Set(datagram.Remote, socket);
            return datagram;
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(remote);
            var sockets = Sockets;
            // A socket of an earlier start (port in use, then listening again) is closed: the current one of the family answers.
            var socket = _senders.TryGet(remote, out var known) && sockets.Contains(known) ? known
                : sockets.FirstOrDefault(s => s.AddressFamily == remote.AddressFamily);
            if (socket is null)
            {
                return; // not listening (port in use, disposed): a datagram is lost like on the wire
            }

            try
            {
                await socket.SendToAsync(data, SocketFlags.None, remote, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }

        private protected override void StartReceiving(Socket socket) => _ = ReceiveLoopAsync(socket, Lifetime);

        private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
        {
            var buffer = new byte[65536];
            EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                    var from = (IPEndPoint)result.RemoteEndPoint;
                    _received.Writer.TryWrite((new Datagram(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), from), socket));
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    // Windows reports an ICMP port unreachable of an earlier reply here; ignore.
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    Failed();
                    return;
                }
            }
        }
    }

    private sealed class LocalTcpListener(int port, string reason, Sdk.Network.IFirewallRules? firewall, TimeProvider? time, bool loopbackOnly,
        IReadOnlyList<IPAddress>? addresses)
        : LocalListener(SiteProtocol.Tcp, port, reason, firewall, time, loopbackOnly, addresses), ITcpRelay
    {
        private readonly System.Threading.Channels.Channel<Socket> _accepted =
            System.Threading.Channels.Channel.CreateBounded<Socket>(new System.Threading.Channels.BoundedChannelOptions(128)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        public async ValueTask<IIncomingConnection> AcceptAsync(CancellationToken ct)
        {
            var socket = await _accepted.Reader.ReadAsync(ct).ConfigureAwait(false);
            return new LocalIncomingConnection(socket);
        }

        private protected override void StartReceiving(Socket socket) => _ = AcceptLoopAsync(socket, Lifetime);

        private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Socket connection;
                try
                {
                    connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    Failed();
                    return;
                }

                try
                {
                    await _accepted.Writer.WriteAsync(connection, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    connection.Dispose();
                    return;
                }
            }
        }

        private protected override ValueTask OnDisposedAsync()
        {
            _accepted.Writer.TryComplete();
            while (_accepted.Reader.TryRead(out var socket))
            {
                socket.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class LocalIncomingConnection(Socket socket) : IIncomingConnection
    {
        public Stream Stream { get; } = new NetworkStream(socket, ownsSocket: true);

        public IPEndPoint Remote { get; } = (IPEndPoint)socket.RemoteEndPoint!;

        public Guid? SiteId => null;

        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}

/// <summary>
/// The most recent senders of a listener and what answers each (a UDP listener's receiving socket): at most
/// <c>capacity</c> entries, the least recently seen one dropped first, O(1) per datagram. A sender that was dropped is
/// answered by the listener's default socket of its address family.
/// </summary>
internal sealed class RecentSenders<T>(int capacity)
    where T : class
{
    /// <summary>Senders kept per listener (NTP clients of one site or server: far fewer).</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<IPEndPoint, LinkedListNode<(IPEndPoint Remote, T Value)>> _index = [];
    private readonly LinkedList<(IPEndPoint Remote, T Value)> _order = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }

    public void Set(IPEndPoint remote, T value)
    {
        ArgumentNullException.ThrowIfNull(remote);
        lock (_lock)
        {
            if (_index.TryGetValue(remote, out var node))
            {
                _order.Remove(node);
                node.Value = (remote, value);
                _order.AddFirst(node);
                return;
            }

            if (_index.Count >= capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _index.Remove(oldest.Value.Remote);
            }

            _index[remote] = _order.AddFirst((remote, value));
        }
    }

    public bool TryGet(IPEndPoint remote, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(remote, out var node))
            {
                value = node.Value.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
