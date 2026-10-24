using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The network of one route for plugin tests (<see cref="IDeviceNetwork"/>): which addresses answer a ping, which TCP
/// ports are open, the names DNS knows, OADM's address towards the devices and the networks a site reaches. No packet
/// leaves the test.
/// </summary>
public sealed class FakeDeviceNetwork : IDeviceNetwork
{
    /// <summary>The server's own network (<paramref name="siteId"/> null) or a site's.</summary>
    /// <param name="siteId">The site of the route; null = the server's own network.</param>
    public FakeDeviceNetwork(Guid? siteId = null)
    {
        SiteId = siteId;
    }

    /// <inheritdoc />
    public Guid? SiteId { get; }

    /// <summary>Addresses that answer a ping.</summary>
    public HashSet<string> Answering { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Open TCP ports ("10.0.0.48", 80).</summary>
    public HashSet<(string Address, int Port)> OpenPorts { get; } = [];

    /// <summary>Host names and their addresses (case-insensitive).</summary>
    public Dictionary<string, IReadOnlyList<IPAddress>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OADM's address towards every device (the server's own, or the site node's); null = none.</summary>
    public IPAddress? LocalAddress { get; set; }

    /// <summary>The networks the route reaches; null = every address (the server's own network).</summary>
    public Func<IPAddress, bool>? ReachesAddress { get; set; }

    /// <summary>Thrown by every call when set (a site that is offline, for example).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every call, oldest first: "ping 10.0.0.60", "tcp 10.0.0.60:80", "resolve cam.local", "local 10.0.0.60".</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <inheritdoc />
    public Task<bool> PingAsync(string address, TimeSpan timeout, CancellationToken ct)
    {
        Record("ping " + address);
        return Task.FromResult(Answering.Contains(address));
    }

    /// <inheritdoc />
    public Task<bool> ProbeTcpAsync(string address, int port, TimeSpan timeout, CancellationToken ct)
    {
        Record($"tcp {address}:{port}");
        return Task.FromResult(OpenPorts.Contains((address, port)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        Record("resolve " + name);
        if (IPAddress.TryParse(name.Trim('[', ']'), out var ip))
        {
            return Task.FromResult<IReadOnlyList<IPAddress>>([ip]);
        }

        return Task.FromResult(Names.TryGetValue(name, out var found) ? found : (IReadOnlyList<IPAddress>)[]);
    }

    /// <inheritdoc />
    public Task<IPAddress?> LocalAddressTowardsAsync(string address, CancellationToken ct)
    {
        Record("local " + address);
        return Task.FromResult(LocalAddress);
    }

    /// <inheritdoc />
    public bool Reaches(IPAddress address) => ReachesAddress?.Invoke(address) ?? true;

    private void Record(string call)
    {
        Calls.Enqueue(call);
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}

/// <summary>A site for plugin tests: online or not, and the ports its node lets relays listen on.</summary>
public sealed class FakeSite : ISiteInfo
{
    /// <summary>A site named <paramref name="name"/>.</summary>
    /// <param name="name">"Building B".</param>
    /// <param name="id">The site's id; null = a new one.</param>
    public FakeSite(string name, Guid? id = null)
    {
        Name = name;
        Id = id ?? Guid.NewGuid();
        Network = new FakeDeviceNetwork(Id);
    }

    /// <inheritdoc />
    public Guid Id { get; }

    /// <inheritdoc />
    public string Name { get; set; }

    /// <inheritdoc />
    public bool IsOnline { get; set; } = true;

    /// <summary>Ports allowed on the node's Listen on list; a relay on another port waits for approval.</summary>
    public HashSet<(SiteProtocol Protocol, int Port)> AllowedPorts { get; } = [];

    /// <summary>The node lacks UDP or TCP relays (an old node): its relays are <see cref="RelayState.NotSupported"/>.</summary>
    public bool RelaysNotSupported { get; set; }

    /// <summary>The site's network as its devices see it.</summary>
    public FakeDeviceNetwork Network { get; }

    /// <summary>The state a relay on <paramref name="protocol"/> <paramref name="port"/> has at this site now.</summary>
    public RelayState RelayStateFor(SiteProtocol protocol, int port) =>
        !IsOnline ? RelayState.SiteOffline
        : RelaysNotSupported ? RelayState.NotSupported
        : AllowedPorts.Contains((protocol, port)) ? RelayState.Running
        : RelayState.WaitingForApproval;
}

/// <summary>
/// The sites and their relays for plugin tests (<see cref="ISites"/>): add sites, take them offline, allow ports and read
/// what the plugin's relays received and sent. Change a site, then call <see cref="Update"/>: every relay takes its new
/// state and <see cref="SitesChanged"/> fires, like the host does after a change.
/// </summary>
public sealed class FakeSites : ISites
{
    private readonly ConcurrentDictionary<Guid, FakeSite> _sites = new();

    /// <summary>The sites, by id.</summary>
    public IReadOnlyDictionary<Guid, FakeSite> Sites => _sites;

    /// <summary>Every relay the plugin opened, oldest first (disposed ones too: <see cref="InMemoryRelay.IsDisposed"/>).</summary>
    public ConcurrentQueue<InMemoryRelay> Relays { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<ISiteInfo> List => [.. _sites.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public event EventHandler? SitesChanged;

    /// <summary>Adds a site (raises <see cref="SitesChanged"/>).</summary>
    public FakeSite Add(string name, bool online = true)
    {
        var site = new FakeSite(name) { IsOnline = online };
        _sites[site.Id] = site;
        Update();
        return site;
    }

    /// <summary>Removes a site (raises <see cref="SitesChanged"/>; its relays go offline).</summary>
    public void Remove(Guid siteId)
    {
        _sites.TryRemove(siteId, out _);
        Update();
    }

    /// <summary>Applies changed sites: every open relay takes its site's state, then <see cref="SitesChanged"/> fires.</summary>
    public void Update()
    {
        foreach (var relay in Relays.Where(r => !r.IsDisposed))
        {
            relay.SetState(_sites.TryGetValue(relay.SiteId!.Value, out var site) ? site.RelayStateFor(relay.Protocol, relay.Port) : RelayState.SiteOffline);
        }

        SitesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<IUdpRelay> OpenUdpRelayAsync(Guid siteId, int port, string reason, CancellationToken ct)
    {
        var site = Find(siteId);
        var relay = new InMemoryUdpRelay(siteId, port, reason, site.Name, site.RelayStateFor(SiteProtocol.Udp, port));
        Relays.Enqueue(relay);
        return Task.FromResult<IUdpRelay>(relay);
    }

    /// <inheritdoc />
    public Task<ITcpRelay> OpenTcpRelayAsync(Guid siteId, int port, string reason, CancellationToken ct)
    {
        var site = Find(siteId);
        var relay = new InMemoryTcpRelay(siteId, port, reason, site.Name, site.RelayStateFor(SiteProtocol.Tcp, port));
        Relays.Enqueue(relay);
        return Task.FromResult<ITcpRelay>(relay);
    }

    private FakeSite Find(Guid siteId) =>
        _sites.TryGetValue(siteId, out var site) ? site : throw new ArgumentException("Unknown site " + siteId + ".", nameof(siteId));
}

/// <summary>
/// The network of a core plugin for tests (<see cref="IPluginNetwork"/>): <see cref="Local"/> for the server's own network,
/// each site's <see cref="FakeSite.Network"/>, and in-memory listeners (<see cref="Listeners"/>) instead of sockets.
/// </summary>
public sealed class FakePluginNetwork(FakeSites? sites = null) : IPluginNetwork
{
    /// <summary>The server's own network.</summary>
    public FakeDeviceNetwork Local { get; } = new();

    /// <summary>The listeners the plugin opened on the server's own network, oldest first.</summary>
    public ConcurrentQueue<InMemoryRelay> Listeners { get; } = new();

    /// <summary>The state a new listener has (<see cref="RelayState.PortInUse"/> to test a port another program holds).</summary>
    public RelayState ListenState { get; set; } = RelayState.Running;

    /// <inheritdoc />
    public IDeviceNetwork ForDevice(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return ForSite(device.SiteId);
    }

    /// <inheritdoc />
    public IDeviceNetwork ForSite(Guid? siteId) =>
        siteId is { } id && sites?.Sites.GetValueOrDefault(id) is { } site ? site.Network
        : siteId is null ? Local
        : new FakeDeviceNetwork(siteId) { Failure = new InvalidOperationException("Site offline") };

    /// <inheritdoc />
    public Task<IUdpRelay> ListenUdpAsync(int port, string reason, CancellationToken ct) => ListenUdpAsync(port, reason, [], ct);

    /// <summary>The addresses of the last UDP listener (empty = every address).</summary>
    public IReadOnlyList<System.Net.IPAddress> LastUdpAddresses { get; private set; } = [];

    /// <summary>
    /// Asked for every chosen address of a new listener: true leaves it out of <see cref="IRelay.BoundAddresses"/> as the host
    /// does with an address that is not ready yet (the others serve). Null = every address binds.
    /// </summary>
    public Func<System.Net.IPAddress, bool>? RefuseAddress { get; set; }

    /// <inheritdoc />
    public Task<IUdpRelay> ListenUdpAsync(int port, string reason, IReadOnlyList<System.Net.IPAddress> addresses, CancellationToken ct)
    {
        LastUdpAddresses = addresses;
        var relay = new InMemoryUdpRelay(null, port, reason, SiteTexts.ThisServer, ListenState) { BoundAddresses = Bound(addresses) };
        Listeners.Enqueue(relay);
        return Task.FromResult<IUdpRelay>(relay);
    }

    /// <inheritdoc />
    public Task<ITcpRelay> ListenTcpAsync(int port, string reason, CancellationToken ct) => ListenTcpAsync(port, reason, [], ct);

    /// <summary>The addresses of the last TCP listener (empty = every address).</summary>
    public IReadOnlyList<System.Net.IPAddress> LastTcpAddresses { get; private set; } = [];

    /// <inheritdoc />
    public Task<ITcpRelay> ListenTcpAsync(int port, string reason, IReadOnlyList<System.Net.IPAddress> addresses, CancellationToken ct)
    {
        LastTcpAddresses = addresses;
        var relay = new InMemoryTcpRelay(null, port, reason, SiteTexts.ThisServer, ListenState) { BoundAddresses = Bound(addresses) };
        Listeners.Enqueue(relay);
        return Task.FromResult<ITcpRelay>(relay);
    }

    // Like the host: the chosen addresses that bind, the wildcard addresses for every address, none while not listening.
    private List<System.Net.IPAddress> Bound(IReadOnlyList<System.Net.IPAddress> addresses) =>
        ListenState != RelayState.Running ? []
        : addresses.Count == 0 ? [System.Net.IPAddress.Any, System.Net.IPAddress.IPv6Any]
        : [.. addresses.Where(a => RefuseAddress?.Invoke(a) != true)];
}

/// <summary>A relay or listener in memory: the test plays the devices on the other side.</summary>
public abstract class InMemoryRelay : IRelay
{
    private readonly string _reason;
    private readonly string _where;
    private RelayState _state;
    private int _disposed;

    private protected InMemoryRelay(Guid? siteId, SiteProtocol protocol, int port, string reason, string where, RelayState state)
    {
        SiteId = siteId;
        Protocol = protocol;
        Port = port;
        _reason = reason;
        _where = where;
        _state = state;
    }

    /// <inheritdoc />
    public Guid? SiteId { get; }

    /// <inheritdoc />
    public SiteProtocol Protocol { get; }

    /// <inheritdoc />
    public int Port { get; }

    /// <summary>The reason the plugin gave ("NTP").</summary>
    public string Reason => _reason;

    /// <inheritdoc />
    public RelayState State => _state;

    /// <inheritdoc />
    public string StateText => SiteTexts.RelayStateText(_reason, _where, _state, Protocol, Port);

    /// <inheritdoc />
    /// <remarks>Listeners of <see cref="FakePluginNetwork"/> set it (see <see cref="FakePluginNetwork.RefuseAddress"/>); relays at sites keep null.</remarks>
    public IReadOnlyList<IPAddress>? BoundAddresses { get; set; }

    /// <summary>The plugin disposed it (or its plugin stopped).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>The plugin released it (<see cref="IRelay.ReleaseAsync"/>): it no longer serves there.</summary>
    public bool IsReleased { get; private set; }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>Changes the state as the host would (raises <see cref="StateChanged"/>).</summary>
    public void SetState(RelayState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync()
    {
        IsReleased = true;
        return DisposeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            OnDisposed();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Ends the waiting reads.</summary>
    private protected abstract void OnDisposed();
}

/// <summary>A UDP relay in memory: <see cref="Deliver"/> a device's datagram, <see cref="ReadSentAsync"/> what the plugin answered.</summary>
public sealed class InMemoryUdpRelay : InMemoryRelay, IUdpRelay
{
    private readonly Channel<Datagram> _in = Channel.CreateUnbounded<Datagram>();
    private readonly Channel<Datagram> _out = Channel.CreateUnbounded<Datagram>();

    internal InMemoryUdpRelay(Guid? siteId, int port, string reason, string where, RelayState state)
        : base(siteId, SiteProtocol.Udp, port, reason, where, state)
    {
    }

    /// <summary>A device sends <paramref name="data"/> from <paramref name="from"/>.</summary>
    public void Deliver(ReadOnlyMemory<byte> data, IPEndPoint from) => _in.Writer.TryWrite(new Datagram(data.ToArray(), from));

    /// <summary>The next datagram the plugin sent (its <see cref="Datagram.Remote"/> is the receiver).</summary>
    public ValueTask<Datagram> ReadSentAsync(CancellationToken ct) => _out.Reader.ReadAsync(ct);

    /// <inheritdoc />
    public ValueTask<Datagram> ReceiveAsync(CancellationToken ct) => _in.Reader.ReadAsync(ct);

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct)
    {
        if (State == RelayState.Running && !IsDisposed)
        {
            _out.Writer.TryWrite(new Datagram(data.ToArray(), remote));
        }

        return ValueTask.CompletedTask;
    }

    private protected override void OnDisposed()
    {
        _in.Writer.TryComplete();
        _out.Writer.TryComplete();
    }
}

/// <summary>A TCP relay in memory: <see cref="Connect"/> opens a device's connection; the test reads and writes its end.</summary>
public sealed class InMemoryTcpRelay : InMemoryRelay, ITcpRelay
{
    private readonly Channel<IIncomingConnection> _accepted = Channel.CreateUnbounded<IIncomingConnection>();

    internal InMemoryTcpRelay(Guid? siteId, int port, string reason, string where, RelayState state)
        : base(siteId, SiteProtocol.Tcp, port, reason, where, state)
    {
    }

    /// <summary>A device at <paramref name="from"/> connects; returns the device's end of the connection.</summary>
    public Stream Connect(IPEndPoint from)
    {
        var (device, plugin) = InMemoryDuplex.Create();
        _accepted.Writer.TryWrite(new Incoming(plugin, from, SiteId));
        return device;
    }

    /// <inheritdoc />
    public ValueTask<IIncomingConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    private protected override void OnDisposed() => _accepted.Writer.TryComplete();

    private sealed class Incoming(Stream stream, IPEndPoint remote, Guid? siteId) : IIncomingConnection
    {
        public Stream Stream { get; } = stream;

        public IPEndPoint Remote { get; } = remote;

        public Guid? SiteId { get; } = siteId;

        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}

/// <summary>Two connected streams in memory: what one end writes the other reads; disposing an end ends the other's input.</summary>
internal static class InMemoryDuplex
{
    public static (Stream A, Stream B) Create()
    {
        var aToB = Channel.CreateUnbounded<byte[]>();
        var bToA = Channel.CreateUnbounded<byte[]>();
        return (new End(bToA.Reader, aToB.Writer), new End(aToB.Reader, bToA.Writer));
    }

    private sealed class End(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) : Stream
    {
        private ReadOnlyMemory<byte> _pending;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_pending.IsEmpty)
            {
                if (!await input.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (input.TryRead(out var chunk))
                {
                    _pending = chunk;
                }
            }

            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!output.TryWrite(buffer.ToArray()))
            {
                throw new IOException("The connection is closed.");
            }

            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                output.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
