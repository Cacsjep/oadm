using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Plugins.Network;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>
/// An in-memory UDP network: sockets by address and port, broadcasts reach every socket of the port (except the sender).
/// Nothing ever touches a real interface: no DHCP traffic leaves the test process.
/// </summary>
internal sealed class FakeDhcpNetwork : IDhcpSocketFactory
{
    private readonly Lock _sync = new();
    private readonly List<FakeSocket> _sockets = [];

    /// <summary>Binding these ports fails with the given error (port in use, permission).</summary>
    public Dictionary<int, SocketError> FailPorts { get; } = [];

    /// <summary>Every datagram sent: from, to, message.</summary>
    public List<(IPEndPoint From, IPEndPoint To, DhcpMessage Message)> Sent { get; } = [];

    public IDhcpSocket Open(DhcpBinding binding, int port, bool reusePort)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (FailPorts.TryGetValue(port, out var error))
        {
            throw new SocketException((int)error);
        }

        lock (_sync)
        {
            if (!reusePort && _sockets.Any(s => s.Port == port && s.Address.Equals(binding.Address) && !s.Reuse))
            {
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }
        }

        return Add(binding.Address, port, reusePort);
    }

    /// <summary>A device (or another server) on the fake network.</summary>
    public FakeSocket Add(IPAddress address, int port, bool reuse = true)
    {
        var socket = new FakeSocket(this, address, port, reuse);
        lock (_sync)
        {
            _sockets.Add(socket);
        }

        return socket;
    }

    public int OpenSockets(int port)
    {
        lock (_sync)
        {
            return _sockets.Count(s => s.Port == port);
        }
    }

    internal void Remove(FakeSocket socket)
    {
        lock (_sync)
        {
            _sockets.Remove(socket);
        }
    }

    internal void Deliver(FakeSocket from, ReadOnlyMemory<byte> data, IPEndPoint to)
    {
        var copy = data.ToArray();
        List<FakeSocket> targets;
        lock (_sync)
        {
            if (DhcpMessage.TryRead(copy, out var parsed, out _) && parsed is not null)
            {
                Sent.Add((new IPEndPoint(from.Address, from.Port), to, parsed));
            }

            var broadcast = to.Address.Equals(IPAddress.Broadcast);
            targets = [.. _sockets.Where(s => s != from && s.Port == to.Port && (broadcast || s.Address.Equals(to.Address) || s.Address.Equals(IPAddress.Any)))];
        }

        foreach (var target in targets)
        {
            target.Enqueue(copy, new IPEndPoint(from.Address, from.Port));
        }
    }
}

internal sealed class FakeSocket(FakeDhcpNetwork network, IPAddress address, int port, bool reuse) : IDhcpSocket
{
    private readonly Channel<(byte[] Data, IPEndPoint From)> _queue = Channel.CreateUnbounded<(byte[], IPEndPoint)>();

    public IPAddress Address { get; } = address;

    public int Port { get; } = port;

    public bool Reuse { get; } = reuse;

    public void Enqueue(byte[] data, IPEndPoint from) => _queue.Writer.TryWrite((data, from));

    public async ValueTask<DhcpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            var (data, from) = await _queue.Reader.ReadAsync(ct);
            data.CopyTo(buffer);
            return new DhcpReceived(data.Length, from);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(FakeSocket));
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct)
    {
        network.Deliver(this, datagram, destination);
        return ValueTask.CompletedTask;
    }

    /// <summary>Test side: send a message.</summary>
    public ValueTask SendAsync(DhcpMessage message, IPEndPoint destination)
    {
        var bytes = new byte[DhcpMessage.MaxLength];
        var length = message.Write(bytes);
        return SendAsync(bytes.AsMemory(0, length), destination, CancellationToken.None);
    }

    /// <summary>Test side: the next message matching <paramref name="match"/> within <paramref name="timeout"/>, else null.</summary>
    public async Task<(DhcpMessage Message, IPEndPoint From)?> ReceiveAsync(Func<DhcpMessage, bool> match, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[DhcpMessage.MaxLength];
        try
        {
            while (true)
            {
                var received = await ReceiveAsync(buffer, cts.Token);
                if (DhcpMessage.TryRead(buffer.AsSpan(0, received.Length), out var message, out _) && message is not null && match(message))
                {
                    return (message, received.Remote);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        network.Remove(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A DHCP client on the fake network (port 68), unconfigured (0.0.0.0) or with an address.</summary>
internal sealed class TestClient : IAsyncDisposable
{
    private static int s_xid = 1000;
    private readonly FakeDhcpNetwork _network;
    private FakeSocket _socket;

    public TestClient(FakeDhcpNetwork network, ulong mac, int clientPort = 68, int serverPort = 67)
    {
        _network = network;
        Mac = mac;
        ClientPort = clientPort;
        ServerPort = serverPort;
        _socket = network.Add(IPAddress.Any, clientPort);
    }

    public ulong Mac { get; }

    public int ClientPort { get; }

    public int ServerPort { get; }

    public static TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>After an ACK: listen on the leased address (unicast replies).</summary>
    public async Task UseAddressAsync(uint address)
    {
        await _socket.DisposeAsync();
        _socket = _network.Add(Ip4.ToAddress(address), ClientPort);
    }

    public DhcpMessage New(DhcpMessageType type) => new()
    {
        Op = DhcpMessage.BootRequest,
        TransactionId = (uint)Interlocked.Increment(ref s_xid),
        MessageType = type,
        Mac = Mac,
    };

    public async Task<DhcpMessage?> SendAsync(DhcpMessage message, IPEndPoint? to = null, TimeSpan? wait = null)
    {
        await _socket.SendAsync(message, to ?? new IPEndPoint(IPAddress.Broadcast, ServerPort));
        var reply = await _socket.ReceiveAsync(m => m.Op == DhcpMessage.BootReply && m.TransactionId == message.TransactionId, wait ?? Wait);
        return reply?.Message;
    }

    public Task<DhcpMessage?> DiscoverAsync(uint? requested = null, string? hostName = null)
    {
        var m = New(DhcpMessageType.Discover);
        m.RequestedAddress = requested;
        m.HostName = hostName;
        return SendAsync(m);
    }

    /// <summary>DISCOVER, OFFER, REQUEST, ACK: the leased address, or null.</summary>
    public async Task<uint?> AcquireAsync(string? hostName = null)
    {
        var offer = await DiscoverAsync(hostName: hostName);
        if (offer is null)
        {
            return null;
        }

        var request = New(DhcpMessageType.Request);
        request.ServerId = offer.ServerId;
        request.RequestedAddress = offer.YourAddress;
        request.HostName = hostName;
        var ack = await SendAsync(request);
        return ack?.MessageType == DhcpMessageType.Ack ? ack.YourAddress : null;
    }

    public ValueTask DisposeAsync() => _socket.DisposeAsync();
}

/// <summary>Another DHCP server (e.g. the router) on the fake network that offers addresses.</summary>
internal sealed class FakeOtherServer : IAsyncDisposable
{
    private readonly FakeSocket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeOtherServer(FakeDhcpNetwork network, string address = "10.0.0.1", int serverPort = 67, int clientPort = 68)
    {
        Address = IPAddress.Parse(address);
        _socket = network.Add(Address, serverPort);
        _loop = Task.Run(async () =>
        {
            var buffer = new byte[DhcpMessage.MaxLength];
            while (!_cts.IsCancellationRequested)
            {
                DhcpReceived received;
                try
                {
                    received = await _socket.ReceiveAsync(buffer, _cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                if (DhcpMessage.TryRead(buffer.AsSpan(0, received.Length), out var m, out _) && m is { Op: DhcpMessage.BootRequest, MessageType: DhcpMessageType.Discover })
                {
                    Discovers++;
                    var offer = new DhcpMessage
                    {
                        Op = DhcpMessage.BootReply,
                        TransactionId = m.TransactionId,
                        Flags = m.Flags,
                        ClientHardware = m.ClientHardware,
                        YourAddress = Ip4.From(IPAddress.Parse("10.0.0.250")),
                        MessageType = DhcpMessageType.Offer,
                        ServerId = Ip4.From(Address),
                        LeaseTime = 3600,
                    };
                    await _socket.SendAsync(offer, new IPEndPoint(IPAddress.Broadcast, clientPort));
                }
            }
        });
    }

    public IPAddress Address { get; }

    public int Discovers { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _socket.DisposeAsync();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}

/// <summary>The in-use probe: addresses in <see cref="InUse"/> answer.</summary>
internal sealed class FakeProbe : IAddressProbe
{
    public HashSet<string> InUse { get; } = [];

    public List<string> Probed { get; } = [];

    public TimeSpan Delay { get; set; }

    public async Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct)
    {
        lock (Probed)
        {
            Probed.Add(address);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return InUse.Contains(address) ? new AddressProbeResult(true, false) : AddressProbeResult.Silent;
    }
}

internal sealed class FakeServices : IWindowsServiceProbe
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsRunningAsync(string serviceName, CancellationToken ct) => Task.FromResult(Running.Contains(serviceName));
}

/// <summary>Fixed interfaces: Ethernet 10.0.0.17/24 (router and DNS 10.0.0.138), Wi-Fi 192.168.1.5/24, a down adapter, loopback, IPv6 only.</summary>
internal sealed class FakeInterfaces : IServerNetworkInterfaces
{
    public static ServerNetworkInterface Ethernet(string address = "10.0.0.17", int prefix = 24) =>
        new("eth-id", "Ethernet", "Intel(R) Ethernet Connection I219-LM", [IPAddress.Parse(address), IPAddress.Parse("fe80::1")], IsUp: true, IsLoopback: false)
        {
            Ipv4Index = 7,
            PrefixLengths = new Dictionary<IPAddress, int> { [IPAddress.Parse(address)] = prefix },
            Gateways = [IPAddress.Parse("10.0.0.138")],
            DnsServers = [IPAddress.Parse("10.0.0.138"), IPAddress.Parse("fd00::1")],
            DnsSuffix = "example.local",
        };

    public List<ServerNetworkInterface> Items { get; } =
    [
        Ethernet(),
        new("wifi-id", "Wi-Fi", "Wi-Fi", [IPAddress.Parse("192.168.1.5")], IsUp: true, IsLoopback: false)
        {
            PrefixLengths = new Dictionary<IPAddress, int> { [IPAddress.Parse("192.168.1.5")] = 24 },
        },
        new("down-id", "Ethernet 2", "Ethernet 2", [IPAddress.Parse("172.16.0.1")], IsUp: false, IsLoopback: false),
        new("v6-id", "Tunnel", "Tunnel", [IPAddress.Parse("2001:db8::5")], IsUp: true, IsLoopback: false),
        new("lo-id", "Loopback", "Loopback", [IPAddress.Loopback], IsUp: true, IsLoopback: true),
    ];

    public IReadOnlyList<ServerNetworkInterface> List() => Items;
}

internal static class Options
{
    /// <summary>Fake network, fake probe, fast timings: never a real socket.</summary>
    public static DhcpServerOptions Test(FakeDhcpNetwork network, FakeProbe? probe = null, FakeInterfaces? interfaces = null, TimeProvider? time = null) => new()
    {
        Sockets = network,
        Probe = probe ?? new FakeProbe(),
        Interfaces = interfaces ?? new FakeInterfaces(),
        Time = time ?? TimeProvider.System,
        OtherServerWait = TimeSpan.FromMilliseconds(300),
        PublishInterval = TimeSpan.FromMilliseconds(50),
        RetryBindInterval = TimeSpan.FromMilliseconds(200),
        PersistInterval = TimeSpan.FromMilliseconds(100),
        WindowsServices = new FakeServices(),
    };

    public static DhcpSaveRequest Enable(string start = "10.0.0.100", string end = "10.0.0.199", IReadOnlyList<string>? confirmed = null) =>
        new(true, "eth-id", start, end, confirmed);
}

/// <summary>Minimal core plugin context: in-memory settings, the real event hub.</summary>
internal sealed class TestCoreContext(IPluginSettings settings, IPluginEvents? events, IFirewallRules? firewall = null, IDeviceAutoAdd? autoAdd = null) : ICorePluginContext
{
    public IDeviceAutoAdd? AutoAdd { get; } = autoAdd;

    public IFirewallRules? Firewall { get; } = firewall;

    public IDeviceRepository Devices => throw new NotSupportedException();

    public IVapixClientFactory Vapix => throw new NotSupportedException();

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings { get; } = settings;

    public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events { get; } = events;
}

/// <summary>The page context against the in-process plugin and the real event hub; confirmations answered by <see cref="Confirm"/>.</summary>
internal sealed class PluginPageContext(DhcpServerPlugin plugin, PluginEventHub hub) : ICorePluginClientContext
{
    public List<IDeviceInfo> DeviceList { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public event EventHandler? DevicesChanged;

    public Func<string, string, bool> Confirm { get; set; } = (_, _) => true;

    public List<(string Title, string Message)> Confirmations { get; } = [];

    public List<string> Messages { get; } = [];

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(DhcpServerPluginInfo.PluginId, ct))
        {
            yield return item;
        }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message));
        return Task.FromResult(Confirm(title, message));
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed record TestDevice(string Serial, string Address, string? Model) : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public DeviceStatus Status => DeviceStatus.Ok;

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }
}

internal static class Mac
{
    public static ulong Of(string text) => MacAddress.TryParse(text, out var mac) ? mac : throw new ArgumentException(text);
}

internal static class Ip
{
    public static uint Of(string text) => Ip4.TryParse(text, out var a) ? a : throw new ArgumentException(text);
}

internal static class Clock
{
    public static FakeTimeProvider New() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
}
