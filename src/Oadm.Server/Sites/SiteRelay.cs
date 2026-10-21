using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Sites;

using SdkSiteTexts = Oadm.Sdk.Sites.SiteTexts;

namespace Oadm.Server.Sites;

/// <summary>
/// A relay at a site (docs/spec/sites.md, "Relay"): keeps one node <c>Relay</c> call open while the site is online and opens it
/// again after the node reconnects. Its <see cref="State"/> follows the node's answers (running, waiting for approval in
/// the node app, port in use, no permission) and the site (offline, node too old). The node asks its user for a port it
/// has not allowed (Listen on, with the reason) when the call opens. <see cref="ReleaseAsync"/> tells the node that the plugin
/// no longer serves there (the node gives the port back), then closes.
/// </summary>
internal abstract class SiteRelay : IRelay
{
    /// <summary>Longest pause before the call is opened again after it ended while the site stayed online.</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    /// <summary>How long a release waits for the node to end the call before the relay closes anyway.</summary>
    public static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(5);

    private readonly SiteManager _manager;
    private readonly Guid _siteId;
    private readonly string _reason;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Lock _lock = new();
    private RelayCall? _call;
    private Task _loop = Task.CompletedTask;
    private RelayState _state = RelayState.SiteOffline;
    private string _siteName;
    private int _disposed;
    private int _releasing;

    protected SiteRelay(SiteManager manager, string siteName, Guid siteId, SiteProtocol protocol, int port, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _manager = manager;
        _siteId = siteId;
        _siteName = siteName;
        Protocol = protocol;
        Port = port;
        _reason = string.IsNullOrWhiteSpace(reason) ? "Plugin" : reason.Trim();
        _manager.Changed += OnSiteChanged;
        _manager.ListChanged += OnListChanged;
    }

    public Guid? SiteId => _siteId;

    public SiteProtocol Protocol { get; }

    public int Port { get; }

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

    public string StateText
    {
        get
        {
            lock (_lock)
            {
                return SdkSiteTexts.RelayStateText(_reason, _siteName, _state, Protocol, Port);
            }
        }
    }

    public event EventHandler? StateChanged;

    private string Feature => Protocol == SiteProtocol.Udp ? "relay.udp" : "relay.tcp";

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var retry = TimeSpan.FromSeconds(1);
        var ct = _life.Token;
        while (!ct.IsCancellationRequested)
        {
            var (session, name) = _manager.RelayTarget(_siteId);
            if (name is not null)
            {
                lock (_lock)
                {
                    _siteName = name;
                }
            }

            if (session is null)
            {
                SetState(RelayState.SiteOffline);
                retry = TimeSpan.FromSeconds(1);
                await WaitAsync(null, ct).ConfigureAwait(false);
                continue;
            }

            if (!session.Supports(Feature))
            {
                SetState(RelayState.NotSupported);
                await WaitAsync(null, ct).ConfigureAwait(false);
                continue;
            }

            var answered = false;
            try
            {
                using var call = await session.OpenRelayAsync(name ?? _siteName, ct).ConfigureAwait(false);
                await call.Requests.WriteAsync(new RelayMessage
                {
                    Open = new RelayOpen { Protocol = Protocol == SiteProtocol.Udp ? Contracts.SiteNode.Protocol.Udp : Contracts.SiteNode.Protocol.Tcp, Port = Port, Reason = _reason },
                }, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _call = call;
                }

                while (await call.Responses.MoveNext(call.Lifetime).ConfigureAwait(false))
                {
                    answered = true;
                    var message = call.Responses.Current;
                    switch (message.KindCase)
                    {
                        case RelayMessage.KindOneofCase.State:
                            SetState(StateOf(message.State.State));
                            break;
                        case RelayMessage.KindOneofCase.Packet:
                            OnPacket(message.Packet);
                            break;
                    }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is RpcException or IOException or InvalidOperationException or Core.Sites.SiteException or OperationCanceledException)
            {
                // The link ended or the node refused: tried again below.
            }
            catch (Exception e) when (ct.IsCancellationRequested && e is OperationCanceledException or RpcException or IOException or InvalidOperationException)
            {
                break; // disposed
            }
            finally
            {
                lock (_lock)
                {
                    _call = null;
                }

                OnCallEnded();
            }

            if (ct.IsCancellationRequested || Volatile.Read(ref _releasing) == 1)
            {
                break; // disposed, or released: never opened again
            }

            // Ended while the site still counts as online: a short pause, longer each time it fails at once.
            retry = answered ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            if (_manager.RelayTarget(_siteId).Session is null)
            {
                SetState(RelayState.SiteOffline);
            }

            await WaitAsync(retry, ct).ConfigureAwait(false);
        }
    }

    private static RelayState StateOf(RelayStateKind kind) => kind switch
    {
        RelayStateKind.RelayStateRunning => RelayState.Running,
        RelayStateKind.RelayStateWaitingForApproval => RelayState.WaitingForApproval,
        RelayStateKind.RelayStatePortInUse => RelayState.PortInUse,
        RelayStateKind.RelayStateNoPermission => RelayState.NoPermission,
        RelayStateKind.RelayStateNotSupported => RelayState.NotSupported,
        _ => RelayState.SiteOffline,
    };

    /// <summary>Waits for a change of the site (or of its session) or <paramref name="delay"/>.</summary>
    private async Task WaitAsync(TimeSpan? delay, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waits = new List<Task> { _signal.WaitAsync(stop.Token) };
        if (delay is { } d)
        {
            waits.Add(Task.Delay(d, _manager.Time, stop.Token));
        }

        try
        {
            await Task.WhenAny(waits).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var wait in waits)
            {
                try
                {
                    await wait.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    private void OnSiteChanged(object? sender, Core.Sites.SiteStatesChangedEventArgs e)
    {
        if (e.SiteIds.Contains(_siteId))
        {
            Signal();
        }
    }

    private void OnListChanged(object? sender, EventArgs e)
    {
        // Session changes (a new control link, a session that ended) raise the list; only wake when it matters.
        var running = Volatile.Read(ref _call) is not null;
        var usable = _manager.RelayTarget(_siteId).Session is not null;
        if (running != usable)
        {
            Signal();
        }
    }

    private void SetState(RelayState state)
    {
        lock (_lock)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        Core.Sites.PluginEvents.Raise(StateChanged, this); // plugin handlers: a throw never ends the relay loop
    }

    /// <summary>A datagram, connection or data from the node.</summary>
    protected abstract void OnPacket(RelayPacket packet);

    /// <summary>The call ended: every connection through it is gone.</summary>
    protected virtual void OnCallEnded()
    {
    }

    /// <summary>Sends <paramref name="packet"/> to the node; false when no call is open (the site is offline).</summary>
    protected async ValueTask<bool> SendAsync(RelayPacket packet, CancellationToken ct)
    {
        RelayCall? call;
        lock (_lock)
        {
            call = _call;
        }

        if (call is null)
        {
            return false;
        }

        await _writing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await call.Requests.WriteAsync(new RelayMessage { Packet = packet }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is RpcException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>"192.168.0.30:50123" as sent by the node; port 0 on the unspecified address when it cannot be read.</summary>
    protected static IPEndPoint EndPointOf(string text) =>
        IPEndPoint.TryParse(text, out var endpoint) ? endpoint : new IPEndPoint(IPAddress.Any, 0);

    protected bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// The plugin no longer serves at this site: the node gets a release (it gives the port back) and ends the call, then
    /// the relay closes. While the site is offline nothing reaches the node and its port stays allowed.
    /// </summary>
    public async ValueTask ReleaseAsync()
    {
        if (IsDisposed || Interlocked.Exchange(ref _releasing, 1) == 1)
        {
            return;
        }

        RelayCall? call;
        lock (_lock)
        {
            call = _call;
        }

        try
        {
            if (call is not null)
            {
                using var timeout = new CancellationTokenSource(ReleaseWait, _manager.Time);
                var writing = false;
                try
                {
                    await _writing.WaitAsync(timeout.Token).ConfigureAwait(false);
                    writing = true;
                    await call.Requests.WriteAsync(new RelayMessage { Release = new RelayRelease() }, timeout.Token).ConfigureAwait(false);
                    await call.Requests.CompleteAsync().ConfigureAwait(false);
                    // The node ends the call once the port is back; an older node does not, the wait ends it.
                    await Task.WhenAny(_loop, Task.Delay(ReleaseWait, _manager.Time, timeout.Token)).ConfigureAwait(false);
                }
                catch (Exception e) when (e is RpcException or InvalidOperationException or ObjectDisposedException or OperationCanceledException or IOException)
                {
                    // The link ended meanwhile, or a send held the call too long: the node keeps the port.
                }
                finally
                {
                    if (writing)
                    {
                        _writing.Release();
                    }
                }
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _manager.Changed -= OnSiteChanged;
        _manager.ListChanged -= OnListChanged;
        await _life.CancelAsync().ConfigureAwait(false);
        RelayCall? call;
        lock (_lock)
        {
            call = _call;
        }

        call?.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        OnCallEnded();
        OnDisposed();
        _life.Dispose();
    }

    /// <summary>Ends what a derived relay keeps.</summary>
    protected virtual void OnDisposed()
    {
    }
}

/// <summary>A UDP relay at a site: datagrams from the devices and replies to them.</summary>
internal sealed class SiteUdpRelay(SiteManager manager, string siteName, Guid siteId, int port, string reason)
    : SiteRelay(manager, siteName, siteId, SiteProtocol.Udp, port, reason), IUdpRelay
{
    private readonly Channel<Datagram> _received = Channel.CreateBounded<Datagram>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
    });

    public ValueTask<Datagram> ReceiveAsync(CancellationToken ct) => _received.Reader.ReadAsync(ct);

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remote);
        // A datagram while the site is offline is lost, like on the wire.
        await SendAsync(new RelayPacket { RemoteAddress = remote.ToString(), Data = ByteString.CopyFrom(data.Span) }, ct).ConfigureAwait(false);
    }

    protected override void OnPacket(RelayPacket packet) =>
        _received.Writer.TryWrite(new Datagram(packet.Data.Memory, EndPointOf(packet.RemoteAddress)));

    protected override void OnDisposed() => _received.Writer.TryComplete();
}

/// <summary>A TCP relay at a site: every connection a device opens to the node's port, as a stream.</summary>
internal sealed class SiteTcpRelay(SiteManager manager, string siteName, Guid siteId, int port, string reason)
    : SiteRelay(manager, siteName, siteId, SiteProtocol.Tcp, port, reason), ITcpRelay
{
    private readonly Channel<IIncomingConnection> _accepted = Channel.CreateBounded<IIncomingConnection>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleWriter = true,
    });

    private readonly ConcurrentDictionary<ulong, RelayConnection> _connections = new();

    public ValueTask<IIncomingConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    protected override void OnPacket(RelayPacket packet)
    {
        if (packet.Accepted)
        {
            var connection = new RelayConnection(this, packet.ConnectionId, EndPointOf(packet.RemoteAddress), SiteId!.Value);
            _connections[packet.ConnectionId] = connection;
            if (!_accepted.Writer.TryWrite(connection))
            {
                // Nobody accepts (the plugin is behind): the device's connection is closed.
                Close(packet.ConnectionId);
                _ = SendAsync(new RelayPacket { ConnectionId = packet.ConnectionId, End = true, Closed = true }, CancellationToken.None).AsTask();
            }

            return;
        }

        if (!_connections.TryGetValue(packet.ConnectionId, out var known))
        {
            return;
        }

        if (!packet.Data.IsEmpty)
        {
            known.Deliver(packet.Data.Memory);
        }

        if (packet.End)
        {
            known.EndInput(null);
        }

        if (packet.Closed)
        {
            Close(packet.ConnectionId);
        }
    }

    protected override void OnCallEnded()
    {
        foreach (var id in _connections.Keys.ToList())
        {
            Close(id);
        }
    }

    protected override void OnDisposed()
    {
        _accepted.Writer.TryComplete();
        while (_accepted.Reader.TryRead(out var connection))
        {
            _ = connection.DisposeAsync().AsTask();
        }
    }

    private void Close(ulong id)
    {
        if (_connections.TryRemove(id, out var connection))
        {
            connection.EndInput(new IOException("The connection through the site ended."));
        }
    }

    /// <summary>Data of a connection to the node.</summary>
    internal ValueTask<bool> SendDataAsync(ulong id, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        SendAsync(new RelayPacket { ConnectionId = id, Data = ByteString.CopyFrom(data.Span) }, ct);

    /// <summary>The plugin closed the connection: the node closes the device's socket.</summary>
    internal async ValueTask CloseFromServerAsync(ulong id)
    {
        _connections.TryRemove(id, out _);
        await SendAsync(new RelayPacket { ConnectionId = id, End = true, Closed = true }, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>One device connection through a TCP relay.</summary>
internal sealed class RelayConnection(SiteTcpRelay relay, ulong id, IPEndPoint remote, Guid siteId) : IIncomingConnection
{
    private readonly Channel<ReadOnlyMemory<byte>> _input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
    private int _closed;

    public Stream Stream => field ??= new RelayConnectionStream(this);

    public IPEndPoint Remote { get; } = remote;

    public Guid? SiteId { get; } = siteId;

    internal void Deliver(ReadOnlyMemory<byte> data) => _input.Writer.TryWrite(data.ToArray());

    /// <summary>The device ended its side (null) or the connection broke.</summary>
    internal void EndInput(Exception? error) => _input.Writer.TryComplete(error);

    internal async ValueTask<ReadOnlyMemory<byte>?> ReadChunkAsync(CancellationToken ct)
    {
        try
        {
            return await _input.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException e) when (e.InnerException is null)
        {
            return null;
        }
        catch (ChannelClosedException e)
        {
            throw new IOException(e.InnerException!.Message, e.InnerException);
        }
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        const int chunk = 64 * 1024;
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            if (Volatile.Read(ref _closed) == 1 || !await relay.SendDataAsync(id, data.Slice(offset, Math.Min(chunk, data.Length - offset)), ct).ConfigureAwait(false))
            {
                throw new IOException("The connection through the site ended.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _input.Writer.TryComplete();
        await relay.CloseFromServerAsync(id).ConfigureAwait(false);
    }
}

/// <summary>The stream of a <see cref="RelayConnection"/>; disposing it closes the connection.</summary>
internal sealed class RelayConnectionStream(RelayConnection connection) : Stream
{
    private ReadOnlyMemory<byte> _pending;
    private bool _ended;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (_ended)
            {
                return 0;
            }

            var next = await connection.ReadChunkAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                _ended = true;
                return 0;
            }

            _pending = next.Value;
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        connection.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = connection.DisposeAsync().AsTask();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
