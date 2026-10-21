using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;

namespace Oadm.SiteNode.Relays;

/// <summary>
/// <c>Relay</c> (CLAUDE.md "# Sites", "Relay"): a UDP or TCP listener on the reached cards for as long as the call lives; every
/// datagram or connection goes to the server and the answers go back to the sender. The port must be on Listen on: a port
/// that is not is requested (with the reason) and the relay waits for approval in the app. A port another program holds or
/// the node may not use is reported and tried again every 30 seconds and after every change in the app or of the cards
/// (a card that gets a new address binds again). Only senders inside the reached networks are relayed and answered; a UDP
/// sender is forgotten 2 minutes after its last datagram (<see cref="UdpSenders"/>); a TCP relay holds at most
/// <see cref="MaxTcpConnections"/> connections. A release from the server (the plugin no longer serves at this site) ends the
/// relay and gives the port back (<see cref="PortPolicy.Release"/>) unless another relay still uses it; a lost link or a
/// stop never does.
/// </summary>
public sealed class RelayHost(NodeStore store, PortPolicy ports, NetworkScope networks, ConnectionLog log, TimeProvider time)
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Open connections of one TCP relay; more are closed at once.</summary>
    public const int MaxTcpConnections = 256;

    private readonly ConcurrentDictionary<long, RelayView> _relays = new();
    private long _nextId;

    /// <summary>Raised when a relay starts, stops or changes its state.</summary>
    public event Action? Changed;

    public IReadOnlyList<RelayView> Relays => [.. _relays.OrderBy(r => r.Key).Select(r => r.Value)];

    public async Task RunAsync(IAsyncStreamReader<RelayMessage> requests, IServerStreamWriter<RelayMessage> responses, string server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(responses);
        if (!await requests.MoveNext(ct).ConfigureAwait(false) || requests.Current.KindCase != RelayMessage.KindOneofCase.Open)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must open the relay."));
        var open = requests.Current.Open;
        var protocol = open.Protocol switch
        {
            Protocol.Tcp => PortProtocol.Tcp,
            Protocol.Udp => PortProtocol.Udp,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Choose UDP or TCP.")),
        };
        if (open.Port is <= 0 or > 65535)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The port must be 1 to 65535."));

        var id = Interlocked.Increment(ref _nextId);
        var outgoing = Channel.CreateBounded<RelayMessage>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new RelaySession(protocol, open.Port, open.Reason ?? "", outgoing.Writer, log, server, networks, time);
        var writer = WriteLoopAsync(outgoing.Reader, responses, stop.Token);
        var reader = ReadLoopAsync(requests, session, stop, stop.Token);
        try
        {
            await ManageAsync(id, session, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            _relays.TryRemove(id, out _);
            NodeEvents.Raise(Changed);
            session.Stop();
            await stop.CancelAsync().ConfigureAwait(false);
            outgoing.Writer.TryComplete();
            await Task.WhenAll(Quiet(writer), Quiet(reader)).ConfigureAwait(false);
            if (session.Released && !_relays.Values.Any(r => r.Protocol == session.Protocol && r.Port == session.Port))
            {
                try
                {
                    ports.Release(new PortEntry(PortUse.Listen, session.Protocol, session.Port));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // node.json could not be written: the port stays allowed, the administrator can remove it in the app.
                }
            }
        }
    }

    // Starts and stops the listeners as the app allows the port and chooses networks; ends with the call.
    private async Task ManageAsync(long id, RelaySession session, CancellationToken ct)
    {
        var changed = new Wakeup(); // never disposed: a late handler on another thread must not throw into the raiser
        void OnChange() => changed.Set();
        store.Changed += OnChange;
        networks.CardsChanged += OnChange;
        try
        {
            string? running = null;
            while (!ct.IsCancellationRequested)
            {
                var allowed = ports.Allows(PortUse.Listen, session.Protocol, session.Port);
                var addresses = networks.ReachedAddresses().Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
                var key = allowed ? string.Join(',', addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal)) : null;
                RelayStateKind state;
                string message;
                if (!allowed)
                {
                    ports.Request([new PortEntry(PortUse.Listen, session.Protocol, session.Port, session.Reason)]);
                    session.Stop();
                    running = null;
                    state = RelayStateKind.RelayStateWaitingForApproval;
                    message = AdminTexts.WaitingForApproval(session.Protocol, session.Port);
                }
                else if (addresses.Count == 0)
                {
                    session.Stop();
                    running = null;
                    state = RelayStateKind.RelayStateWaitingForApproval;
                    message = "Turn on a network in the OADM Site Node app.";
                }
                else if (string.Equals(running, key, StringComparison.Ordinal))
                {
                    state = RelayStateKind.RelayStateRunning;
                    message = "";
                }
                else
                {
                    session.Stop();
                    (state, message) = session.Start(addresses);
                    running = state == RelayStateKind.RelayStateRunning ? key : null;
                }

                if (session.Report(state, message))
                {
                    _relays[id] = new RelayView(session.Protocol, session.Port, session.Reason, StateName(state), message);
                    NodeEvents.Raise(Changed);
                }

                var retry = state is RelayStateKind.RelayStatePortInUse or RelayStateKind.RelayStateNoPermission;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var signal = changed.WaitAsync(wait.Token);
                var tasks = new List<Task> { signal, session.Failed };
                if (retry)
                    tasks.Add(Task.Delay(RetryInterval, time, wait.Token));
                await Task.WhenAny(tasks).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                await Quiet(signal).ConfigureAwait(false);
                if (session.Failed.IsCompleted)
                {
                    session.Stop();
                    running = null;
                }

                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            store.Changed -= OnChange;
            networks.CardsChanged -= OnChange;
        }
    }

    private static async Task ReadLoopAsync(IAsyncStreamReader<RelayMessage> requests, RelaySession session, CancellationTokenSource stop, CancellationToken ct)
    {
        while (await requests.MoveNext(ct).ConfigureAwait(false))
        {
            switch (requests.Current.KindCase)
            {
                case RelayMessage.KindOneofCase.Packet:
                    await session.FromServerAsync(requests.Current.Packet, ct).ConfigureAwait(false);
                    break;
                case RelayMessage.KindOneofCase.Release:
                    session.Released = true;
                    await stop.CancelAsync().ConfigureAwait(false); // the relay ends now; the port goes back in RunAsync
                    return;
            }
        }
    }

    private static async Task WriteLoopAsync(ChannelReader<RelayMessage> outgoing, IServerStreamWriter<RelayMessage> responses, CancellationToken ct)
    {
        await foreach (var message in outgoing.ReadAllAsync(ct).ConfigureAwait(false))
            await responses.WriteAsync(message, ct).ConfigureAwait(false);
    }

    private static string StateName(RelayStateKind state) => state switch
    {
        RelayStateKind.RelayStateRunning => "Running",
        RelayStateKind.RelayStateWaitingForApproval => "Waiting for approval",
        RelayStateKind.RelayStatePortInUse => "Port in use",
        RelayStateKind.RelayStateNoPermission => "No permission",
        _ => state.ToString(),
    };

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or RpcException or ObjectDisposedException or InvalidOperationException or ChannelClosedException)
        {
        }
    }

    /// <summary>The sockets of one relay call.</summary>
    private sealed class RelaySession(PortProtocol protocol, int port, string reason, ChannelWriter<RelayMessage> outgoing, ConnectionLog log, string server,
        NetworkScope networks, TimeProvider time)
    {
        private readonly Lock _lock = new();
        private readonly ConcurrentDictionary<ulong, Socket> _connections = new();
        private readonly UdpSenders _senders = new(time);
        private List<Socket> _sockets = [];
        private CancellationTokenSource? _running;
        private volatile bool _released;

        /// <summary>The server released the relay: the port goes back when it ends.</summary>
        public bool Released
        {
            get => _released;
            set => _released = value;
        }
        private TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _nextConnection;
        private RelayStateKind _reported;
        private string _reportedMessage = "";

        public PortProtocol Protocol => protocol;

        public int Port => port;

        public string Reason => reason;

        /// <summary>Completes when a listener stopped by itself (the card went away).</summary>
        public Task Failed
        {
            get
            {
                lock (_lock)
                    return _failed.Task;
            }
        }

        /// <summary>True when the state changed (sent to the server).</summary>
        public bool Report(RelayStateKind state, string message)
        {
            lock (_lock)
            {
                if (state == _reported && string.Equals(message, _reportedMessage, StringComparison.Ordinal))
                    return false;
                _reported = state;
                _reportedMessage = message;
            }

            outgoing.TryWrite(new RelayMessage { State = new RelayInfo { Protocol = protocol == PortProtocol.Tcp ? Contracts.SiteNode.Protocol.Tcp : Contracts.SiteNode.Protocol.Udp, Port = port, Reason = reason, State = state, Message = message } });
            return true;
        }

        public (RelayStateKind, string) Start(IReadOnlyList<IPAddress> addresses)
        {
            var sockets = new List<Socket>();
            try
            {
                foreach (var address in addresses)
                {
                    var socket = protocol == PortProtocol.Udp
                        ? new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                        : new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    sockets.Add(socket);
                    if (OperatingSystem.IsWindows())
                        socket.ExclusiveAddressUse = true;
                    socket.Bind(new IPEndPoint(address, port));
                    if (protocol == PortProtocol.Tcp)
                        socket.Listen(128);
                }
            }
            catch (SocketException e)
            {
                foreach (var socket in sockets)
                    socket.Dispose();
                return e.SocketErrorCode == SocketError.AccessDenied
                    ? (RelayStateKind.RelayStateNoPermission, AdminTexts.NoPermission(protocol, port))
                    : (RelayStateKind.RelayStatePortInUse, AdminTexts.PortInUse(protocol, port));
            }

            var running = new CancellationTokenSource();
            lock (_lock)
            {
                _sockets = sockets;
                _running = running;
                if (_failed.Task.IsCompleted)
                    _failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            foreach (var socket in sockets)
                _ = protocol == PortProtocol.Udp ? ReceiveUdpAsync(socket, running.Token) : AcceptTcpAsync(socket, running.Token);
            return (RelayStateKind.RelayStateRunning, "");
        }

        public void Stop()
        {
            List<Socket> sockets;
            CancellationTokenSource? running;
            lock (_lock)
            {
                sockets = _sockets;
                running = _running;
                _sockets = [];
                _running = null;
            }

            running?.Cancel();
            running?.Dispose();
            foreach (var socket in sockets)
                socket.Dispose();
            foreach (var connection in _connections.Values)
                connection.Dispose();
            _connections.Clear();
            _senders.Clear();
        }

        public async Task FromServerAsync(RelayPacket packet, CancellationToken ct)
        {
            if (protocol == PortProtocol.Udp)
            {
                if (!IPEndPoint.TryParse(packet.RemoteAddress, out var to) || !networks.Reaches(to.Address) || _senders.Find(to) is not { } socket)
                    return; // only answers to a sender the relay heard from lately, inside the reached networks
                try
                {
                    await socket.SendToAsync(packet.Data.Memory, SocketFlags.None, to, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                }

                return;
            }

            if (!_connections.TryGetValue(packet.ConnectionId, out var connection))
                return;
            try
            {
                if (!packet.Data.IsEmpty)
                    await connection.SendAsync(packet.Data.Memory, SocketFlags.None, ct).ConfigureAwait(false);
                if (packet.End)
                    connection.Shutdown(SocketShutdown.Send);
                if (packet.Closed && _connections.TryRemove(packet.ConnectionId, out _))
                    connection.Dispose();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Close(packet.ConnectionId, connection);
            }
        }

        private async Task ReceiveUdpAsync(Socket socket, CancellationToken ct)
        {
            var buffer = new byte[65536];
            EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                    var from = (IPEndPoint)result.RemoteEndPoint;
                    // Senders outside the reached networks (or spoofed as such) never reach the server, so the relay
                    // cannot be used to send answers elsewhere; a full table of recent senders drops new ones.
                    if (!networks.Reaches(from.Address) || !_senders.Touch(from, socket))
                        continue;
                    await outgoing.WriteAsync(new RelayMessage { Packet = new RelayPacket { RemoteAddress = from.ToString(), Data = ByteString.CopyFrom(buffer, 0, result.ReceivedBytes) } }, ct).ConfigureAwait(false);
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    // Windows reports an ICMP port unreachable of an earlier reply here; ignore.
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or ChannelClosedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    Fail();
                    return;
                }
            }
        }

        private async Task AcceptTcpAsync(Socket listener, CancellationToken ct)
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
                    Fail();
                    return;
                }

                var remote = connection.RemoteEndPoint?.ToString() ?? "";
                if (connection.RemoteEndPoint is not IPEndPoint peer || !networks.Reaches(peer.Address))
                {
                    connection.Dispose();
                    log.Add(server, $"{AdminTexts.Name(protocol)} {port} from {remote}", "Relay", "Outside the networks");
                    continue;
                }

                if (_connections.Count >= MaxTcpConnections)
                {
                    connection.Dispose();
                    log.Add(server, $"{AdminTexts.Name(protocol)} {port} from {remote}", "Relay", "Too many connections");
                    continue;
                }

                var id = (ulong)Interlocked.Increment(ref _nextConnection);
                _connections[id] = connection;
                log.Add(server, $"{AdminTexts.Name(protocol)} {port} from {remote}", "Relay", "Accepted");
                try
                {
                    await outgoing.WriteAsync(new RelayMessage { Packet = new RelayPacket { ConnectionId = id, Accepted = true, RemoteAddress = remote } }, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
                {
                    Close(id, connection);
                    return;
                }

                _ = PumpTcpAsync(id, connection, remote, ct);
            }
        }

        private async Task PumpTcpAsync(ulong id, Socket connection, string remote, CancellationToken ct)
        {
            var buffer = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    var read = await connection.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        await outgoing.WriteAsync(new RelayMessage { Packet = new RelayPacket { ConnectionId = id, End = true, RemoteAddress = remote } }, ct).ConfigureAwait(false);
                        return; // the server closes or ends its side
                    }

                    await outgoing.WriteAsync(new RelayMessage { Packet = new RelayPacket { ConnectionId = id, RemoteAddress = remote, Data = ByteString.CopyFrom(buffer, 0, read) } }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (Close(id, connection))
                    outgoing.TryWrite(new RelayMessage { Packet = new RelayPacket { ConnectionId = id, Closed = true, RemoteAddress = remote } });
            }
            catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
            {
                Close(id, connection);
            }
        }

        private bool Close(ulong id, Socket connection)
        {
            var removed = _connections.TryRemove(id, out _);
            connection.Dispose();
            return removed;
        }

        private void Fail()
        {
            lock (_lock)
                _failed.TrySetResult();
        }
    }
}
