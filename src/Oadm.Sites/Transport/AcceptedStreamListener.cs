using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace Oadm.Sites.Transport;

/// <summary>
/// The Kestrel endpoint of connections the host opened itself (src/Oadm.SiteNode/README.md, "Links"): in outbound mode the node opens
/// TCP + TLS to the server and then serves HTTP/2 on that connection. Listen on it with
/// <c>options.Listen(new AcceptedStreamEndPoint(), o =&gt; o.Protocols = HttpProtocols.Http2)</c>; TLS is already done.
/// </summary>
public sealed class AcceptedStreamEndPoint : EndPoint
{
    public override string ToString() => "site-link";
}

/// <summary>
/// Hands streams to Kestrel as connections. Register it as a singleton <see cref="IConnectionListenerFactory"/>; it binds
/// only <see cref="AcceptedStreamEndPoint"/>, so the normal socket endpoints are unchanged.
/// </summary>
public sealed class AcceptedStreamListener : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private readonly Channel<StreamConnection> _incoming = Channel.CreateUnbounded<StreamConnection>();

    public bool CanBind(EndPoint endpoint) => endpoint is AcceptedStreamEndPoint;

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IConnectionListener>(new Listener(endpoint, _incoming.Reader));

    /// <summary>
    /// Serves HTTP/2 on <paramref name="stream"/> (already through TLS). The task ends when the connection closed (either
    /// side); the stream is disposed then.
    /// </summary>
    public Task ServeAsync(Stream stream, EndPoint? remote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var connection = new StreamConnection(stream, remote);
        if (!_incoming.Writer.TryWrite(connection))
            throw new InvalidOperationException("The listener is closed.");
        ct.Register(connection.Abort);
        return connection.Closed;
    }

    private sealed class Listener(EndPoint endpoint, ChannelReader<StreamConnection> incoming) : IConnectionListener
    {
        // Kestrel stops its accept loop by unbinding: AcceptAsync must then return null.
        private readonly CancellationTokenSource _unbound = new();

        public EndPoint EndPoint => endpoint;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _unbound.Token);
            try
            {
                return await incoming.ReadAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _unbound.Cancel();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _unbound.Cancel();
            _unbound.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// One stream as a Kestrel connection, built like Kestrel's socket transport: Kestrel reads and writes its own pipes,
    /// two pumps copy between them and the stream. The end of the stream reaches Kestrel as the end of its input (never a
    /// disposed stream under its feet), and Kestrel ending its output closes the stream.
    /// </summary>
    private sealed class StreamConnection : ConnectionContext, IDuplexPipe
    {
        private static long s_nextId;

        private readonly Stream _stream;
        private readonly Pipe _input = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _output = new(new PipeOptions(useSynchronizationContext: false));
        private readonly CancellationTokenSource _closed = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pumps;
        private int _streamClosed;
        private int _disposed;

        public StreamConnection(Stream stream, EndPoint? remote)
        {
            _stream = stream;
            RemoteEndPoint = remote;
            LocalEndPoint = new AcceptedStreamEndPoint();
            ConnectionId = "site-" + Interlocked.Increment(ref s_nextId);
            ConnectionClosed = _closed.Token;
            _pumps = Task.WhenAll(Task.Run(ReceiveAsync), Task.Run(SendAsync));
        }

        public Task Closed => _done.Task;

        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;

        public override IDuplexPipe Transport { get => this; set => throw new NotSupportedException(); }
        public override string ConnectionId { get; set; }
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override void Abort(ConnectionAbortedException abortReason)
        {
            _ = abortReason;
            CloseStream();
            _output.Reader.CancelPendingRead();
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CloseStream();
                _output.Reader.CancelPendingRead();
                await _pumps.ConfigureAwait(false);
                await _input.Reader.CompleteAsync().ConfigureAwait(false);
                await _output.Writer.CompleteAsync().ConfigureAwait(false);
                _closed.Dispose();
                _done.TrySetResult();
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        // Stream -> Kestrel. Ends when the peer closes or the stream fails; Kestrel then sees the end of its input.
        private async Task ReceiveAsync()
        {
            Exception? error = null;
            try
            {
                while (true)
                {
                    var memory = _input.Writer.GetMemory(16 * 1024);
                    var read = await _stream.ReadAsync(memory).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    _input.Writer.Advance(read);
                    var flush = await _input.Writer.FlushAsync().ConfigureAwait(false);
                    if (flush.IsCompleted || flush.IsCanceled)
                        break;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                error = Volatile.Read(ref _streamClosed) == 1 ? null : new ConnectionResetException(e.Message, e);
            }
            await _input.Writer.CompleteAsync(error).ConfigureAwait(false);
            SignalClosed();
        }

        // Kestrel -> stream. Ends when Kestrel completes its output or the connection is aborted; then the stream closes.
        private async Task SendAsync()
        {
            try
            {
                while (true)
                {
                    var result = await _output.Reader.ReadAsync().ConfigureAwait(false);
                    if (result.IsCanceled)
                        break;
                    foreach (var segment in result.Buffer)
                        await _stream.WriteAsync(segment).ConfigureAwait(false);
                    if (!result.Buffer.IsEmpty)
                        await _stream.FlushAsync().ConfigureAwait(false);
                    _output.Reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                        break;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
            await _output.Reader.CompleteAsync().ConfigureAwait(false);
            CloseStream();
            SignalClosed();
        }

        private void CloseStream()
        {
            if (Interlocked.Exchange(ref _streamClosed, 1) == 0)
                _stream.Dispose();
        }

        private void SignalClosed()
        {
            try
            {
                _closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
