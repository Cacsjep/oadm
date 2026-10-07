using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Oadm.Core.Discovery.Mdns;

/// <summary>Browses a DNS-SD service type over mDNS.</summary>
public interface IMdnsBrowser
{
    /// <summary>
    /// Queries periodically until <paramref name="cancellationToken"/> is cancelled and yields every
    /// instance when it becomes resolved or its address/host changes.
    /// </summary>
    IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Options for <see cref="IMdnsBrowser.BrowseAsync"/>.</summary>
public sealed class MdnsBrowseOptions
{
    public const string AxisVideoService = "_axis-video._tcp.local";

    /// <summary>Service type to browse, default <see cref="AxisVideoService"/>.</summary>
    public string ServiceType { get; init; } = AxisVideoService;

    /// <summary>Interval between repeated PTR queries after the initial burst (0 s, 1 s, 3 s).</summary>
    public TimeSpan QueryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Also listen on UDP 5353 for multicast announcements and responses (best effort: skipped when
    /// the port cannot be shared on this OS). Default true.
    /// </summary>
    public bool ListenOnMdnsPort { get; init; } = true;

    /// <summary>Optional raw packet tap (data, remote endpoint), used by the probe tool to capture fixtures.</summary>
    public Action<ReadOnlyMemory<byte>, IPEndPoint>? PacketCapture { get; init; }
}

/// <summary>
/// Cross-platform mDNS/DNS-SD browser on raw UDP sockets.
/// <para>
/// One query socket per up, multicast-capable IPv4 interface address, bound to an ephemeral port
/// with IP_MULTICAST_IF set to that address. Queries go to 224.0.0.251:5353 from a non-5353 port,
/// which makes them "legacy unicast" queries (RFC 6762 section 6.7): responders answer by unicast
/// to our socket, so this works even when Bonjour, Avahi or the Windows DNS client already own
/// port 5353. Additionally, a shared 5353 listener joined on every interface picks up multicast
/// answers and announcements when the OS lets us share the port.
/// </para>
/// </summary>
public sealed class MdnsBrowser : IMdnsBrowser
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndPoint = new(MulticastGroup, 5353);

    private readonly ILogger<MdnsBrowser> _logger;
    private readonly Func<IReadOnlyList<IPAddress>> _interfaceAddresses;

    public MdnsBrowser(ILogger<MdnsBrowser>? logger = null)
        : this(logger, GetMulticastInterfaceAddresses)
    {
    }

    internal MdnsBrowser(ILogger<MdnsBrowser>? logger, Func<IReadOnlyList<IPAddress>> interfaceAddresses)
    {
        _logger = logger ?? NullLogger<MdnsBrowser>.Instance;
        _interfaceAddresses = interfaceAddresses;
    }

    /// <summary>IPv4 unicast addresses of all interfaces that are up and support multicast (no loopback).</summary>
    public static IReadOnlyList<IPAddress> GetMulticastInterfaceAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || !nic.SupportsMulticast
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                {
                    result.Add(ua.Address);
                }
            }
        }

        return result;
    }

    public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(
        MdnsBrowseOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new MdnsBrowseOptions();
        var serviceType = options.ServiceType.TrimEnd('.');
        var aggregator = new MdnsResponseAggregator(serviceType);
        var packets = Channel.CreateUnbounded<ReceivedPacket>(new UnboundedChannelOptions { SingleReader = true });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;
        var sockets = OpenQuerySockets();
        Socket? listener = options.ListenOnMdnsPort ? OpenListener(sockets.Select(s => s.Local)) : null;
        var receivers = new List<Task>();
        try
        {
            foreach (var s in sockets)
            {
                receivers.Add(ReceiveLoopAsync(s.Socket, s.Local, packets.Writer, ct));
            }

            if (listener is not null)
            {
                receivers.Add(ReceiveLoopAsync(listener, null, packets.Writer, ct));
            }

            var ptrQuery = DnsMessage.BuildQuery(0, [new DnsQuestion(serviceType, DnsRecordType.Ptr, false)]);
            receivers.Add(QueryLoopAsync(sockets, ptrQuery, options.QueryInterval, ct));

            await foreach (var packet in packets.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                options.PacketCapture?.Invoke(packet.Data, packet.Remote);
                if (!DnsMessage.TryParse(packet.Data.Span, out var message) || message is null)
                {
                    continue;
                }

                var result = aggregator.Process(message, packet.Remote.Address, packet.Local);
                if (result.FollowUpQuestions.Count > 0)
                {
                    var followUp = DnsMessage.BuildQuery(0, result.FollowUpQuestions);
                    await SendAsync(sockets.Where(s => packet.Local is null || s.Local.Equals(packet.Local)), followUp, ct).ConfigureAwait(false);
                }

                foreach (var instance in result.Resolved)
                {
                    yield return instance;
                }
            }

        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            foreach (var s in sockets)
            {
                s.Socket.Dispose();
            }

            listener?.Dispose();
            try
            {
                await Task.WhenAll(receivers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private List<QuerySocket> OpenQuerySockets()
    {
        var sockets = new List<QuerySocket>();
        foreach (var address in _interfaceAddresses())
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(new QuerySocket(socket, address));
                DiscoveryLog.QuerySocketBound(_logger, address);
            }
            catch (SocketException ex)
            {
                socket?.Dispose();
                DiscoveryLog.QuerySocketFailed(_logger, ex, address);
            }
        }

        if (sockets.Count == 0)
        {
            DiscoveryLog.NoInterfaces(_logger);
        }

        return sockets;
    }

    private Socket? OpenListener(IEnumerable<IPAddress> interfaces)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            var joined = 0;
            foreach (var address in interfaces)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, address));
                    joined++;
                }
                catch (SocketException ex)
                {
                    DiscoveryLog.JoinFailed(_logger, ex, address);
                }
            }

            if (joined == 0)
            {
                socket.Dispose();
                return null;
            }

            return socket;
        }
        catch (SocketException ex)
        {
            socket?.Dispose();
            DiscoveryLog.ListenerUnavailable(_logger, ex);
            return null;
        }
    }

    private async Task QueryLoopAsync(List<QuerySocket> sockets, byte[] query, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            TimeSpan[] burst = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
            foreach (var delay in burst)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                await SendAsync(sockets, query, ct).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SendAsync(sockets, query, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(IEnumerable<QuerySocket> sockets, byte[] query, CancellationToken ct)
    {
        foreach (var s in sockets)
        {
            try
            {
                await s.Socket.SendToAsync(query, SocketFlags.None, MulticastEndPoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                DiscoveryLog.SocketError(_logger, ex, s.Local);
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, IPAddress? local, ChannelWriter<ReceivedPacket> writer, CancellationToken ct)
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                var data = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                await writer.WriteAsync(new ReceivedPacket(data, (IPEndPoint)result.RemoteEndPoint, local), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Windows reports ICMP port unreachable as ConnectionReset on UDP sockets; ignore.
            }
            catch (SocketException ex)
            {
                DiscoveryLog.SocketError(_logger, ex, local);
                return;
            }
        }
    }

    private sealed record QuerySocket(Socket Socket, IPAddress Local);

    private sealed record ReceivedPacket(ReadOnlyMemory<byte> Data, IPEndPoint Remote, IPAddress? Local);
}
