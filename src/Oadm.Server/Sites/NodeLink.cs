using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;

using Grpc.Net.Client;

using Oadm.Contracts.SiteNode;
using Oadm.Sites.Transport;

namespace Oadm.Server.Sites;

/// <summary>
/// One TLS connection between the server and a site node, whichever side opened it (CLAUDE.md "# Sites", Transport): the node
/// serves gRPC on it and the server is the HTTP/2 client. The channel's connect callback hands out this one connection
/// once; when it ends, every call on it fails and <see cref="Closed"/> completes.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The site transport: the server's HTTP/2 client on a link to a site node, never to a device.")]
public sealed class NodeLink : IAsyncDisposable
{
    private readonly LinkStream _stream;
    private readonly SocketsHttpHandler _handler;
    private readonly GrpcChannel _strangerChannel;
    private readonly GrpcChannel _channel;
    private readonly SiteNodeService.SiteNodeServiceClient _strangerClient;
    private readonly SiteNodeService.SiteNodeServiceClient _trustedClient;
    private int _handedOut;
    private bool _trusted;

    public NodeLink(SslStream tls, IPEndPoint? remote)
    {
        ArgumentNullException.ThrowIfNull(tls);
        NodeFingerprint = tls.RemoteCertificate is { } certificate ? SiteTls.Fingerprint(certificate) : [];
        NodeFingerprintHex = Convert.ToHexString(NodeFingerprint);
        Remote = remote;
        RemoteHost = remote is null ? string.Empty : (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString();
        _stream = new LinkStream(tls);
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            ConnectCallback = (_, _) => Interlocked.Exchange(ref _handedOut, 1) == 0
                ? ValueTask.FromResult<Stream>(_stream)
                : ValueTask.FromException<Stream>(new IOException("The link to the site node is closed.")),
        };
        // One HTTP/2 connection, two channels over it: a stranger's answers (Hello, PairStart, Bind) are read with a limit
        // of a few KB; once the link is known to be a bound site's (Trust), every call may receive 32 MB.
        _handler = handler;
        _strangerChannel = GrpcChannel.ForAddress("http://site-node", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = false,
            MaxReceiveMessageSize = StrangerReceiveBytes,
            MaxSendMessageSize = 32 * 1024 * 1024,
        });
        _channel = GrpcChannel.ForAddress("http://site-node", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = false,
            MaxReceiveMessageSize = 32 * 1024 * 1024,
            MaxSendMessageSize = 32 * 1024 * 1024,
        });
        _strangerClient = new SiteNodeService.SiteNodeServiceClient(_strangerChannel);
        _trustedClient = new SiteNodeService.SiteNodeServiceClient(_channel);
    }

    /// <summary>What the server reads from a node in one answer until the link is a bound site's.</summary>
    public const int StrangerReceiveBytes = 16 * 1024;

    /// <summary>The client for calls; answers are limited to <see cref="StrangerReceiveBytes"/> until <see cref="Trust"/>.</summary>
    public SiteNodeService.SiteNodeServiceClient Client => Volatile.Read(ref _trusted) ? _trustedClient : _strangerClient;

    /// <summary>True once <see cref="Trust"/> was called.</summary>
    public bool IsTrusted => Volatile.Read(ref _trusted);

    /// <summary>The link carries a bound site's key (checked by the caller): its answers may be as large as any call needs.</summary>
    public void Trust() => Volatile.Write(ref _trusted, true);

    /// <summary>SHA-256 of the node's certificate on this connection.</summary>
    public byte[] NodeFingerprint { get; }

    public string NodeFingerprintHex { get; }

    public IPEndPoint? Remote { get; }

    /// <summary>"10.0.0.7" (IPv4-mapped addresses as IPv4).</summary>
    public string RemoteHost { get; }

    /// <summary>The node's Hello on this link (set by whoever asked first).</summary>
    public HelloReply? Hello { get; set; }

    /// <summary>Completes when the connection ended (either side).</summary>
    public Task Closed => _stream.Closed;

    public bool IsClosed => _stream.Closed.IsCompleted;

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _strangerChannel.Dispose();
        _handler.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The TLS stream, with the moment it ends: a read of 0 bytes, an error, or the handler closing it.</summary>
    private sealed class LinkStream(Stream inner) : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Closed => _closed.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => Watch(inner.FlushAsync(cancellationToken));

        public override int Read(byte[] buffer, int offset, int count) => Ended(inner.Read(buffer, offset, count), count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return Ended(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);
            }
            catch
            {
                _closed.TrySetResult();
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _closed.TrySetResult();
                throw;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _closed.TrySetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            _closed.TrySetResult();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        // HTTP/2 waits for data with zero-byte reads: only an empty answer to a real buffer is the end.
        private int Ended(int read, int asked)
        {
            if (read == 0 && asked > 0)
            {
                _closed.TrySetResult();
            }

            return read;
        }

        private async Task Watch(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                _closed.TrySetResult();
                throw;
            }
        }
    }
}
