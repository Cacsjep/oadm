using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Oadm.Contracts.SiteNode;
using Oadm.Sites.Transport;

namespace Oadm.Sites.Tests;

/// <summary>
/// The spike of docs/sites.md (Transport, "Risk, spike first"): the node opens TCP + TLS to the server, then serves
/// HTTP/2 (Kestrel) on that connection while the server is the gRPC client on the socket it accepted.
/// </summary>
public sealed class ReversedConnectionTests
{
    [Fact]
    public async Task TheServerCallsTheNodeOverTheConnectionTheNodeOpened()
    {
        await using var link = await ReversedLink.OpenAsync();

        var hello = await link.Client.HelloAsync(new HelloRequest());

        Assert.Equal("node-1", hello.NodeId);
        Assert.Equal(1, link.Connects);
    }

    [Fact]
    public async Task DialCarriesBytesBothWaysWithHalfClose()
    {
        await using var link = await ReversedLink.OpenAsync();
        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var echoed = await EchoAsync(link.Client, payload);

        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task ManyDialsShareTheOneConnection()
    {
        await using var link = await ReversedLink.OpenAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => EchoAsync(link.Client, BitConverter.GetBytes(i))));

        for (var i = 0; i < results.Length; i++)
            Assert.Equal(BitConverter.GetBytes(i), results[i]);
        Assert.Equal(1, link.Connects);
    }

    [Fact]
    public async Task ACallFailsAtOnceWhenTheNodeClosesTheConnection()
    {
        await using var link = await ReversedLink.OpenAsync();
        await link.Client.HelloAsync(new HelloRequest());

        await link.CloseNodeSideAsync();

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await link.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow.AddSeconds(30)));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
    }

    [Fact]
    public async Task TlsRefusesAServerKeyTheNodeDidNotPin()
    {
        var error = await Record.ExceptionAsync(() => ReversedLink.OpenAsync(nodeAcceptsServer: false));

        Assert.NotNull(error);
    }

    private static async Task<byte[]> EchoAsync(SiteNodeService.SiteNodeServiceClient client, byte[] payload)
    {
        using var call = client.Dial();
        await call.RequestStream.WriteAsync(new DialMessage { Open = new DialOpen { Host = "192.168.0.20", Port = 80, Kind = DialKind.Short } });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(DialError.None, call.ResponseStream.Current.Result.Error);

        var reading = Task.Run(async () =>
        {
            using var received = new MemoryStream();
            while (await call.ResponseStream.MoveNext())
            {
                var message = call.ResponseStream.Current;
                if (message.KindCase == DialMessage.KindOneofCase.End)
                    break;
                message.Data.WriteTo(received);
            }
            return received.ToArray();
        });
        for (var offset = 0; offset < payload.Length; offset += 64 * 1024)
        {
            var chunk = payload.AsMemory(offset, Math.Min(64 * 1024, payload.Length - offset));
            await call.RequestStream.WriteAsync(new DialMessage { Data = ByteString.CopyFrom(chunk.Span) });
        }
        await call.RequestStream.WriteAsync(new DialMessage { End = true });
        var echoed = await reading;
        await call.RequestStream.CompleteAsync();
        return echoed;
    }

    /// <summary>Echoes every Dial (Open answered with success, data back, end back).</summary>
    private sealed class EchoNode : SiteNodeService.SiteNodeServiceBase
    {
        public override Task<HelloReply> Hello(HelloRequest request, ServerCallContext context) =>
            Task.FromResult(new HelloReply { NodeId = "node-1", ProtocolVersion = 1, State = NodeState.UnboundOpen });

        public override async Task Dial(IAsyncStreamReader<DialMessage> requestStream, IServerStreamWriter<DialMessage> responseStream, ServerCallContext context)
        {
            if (!await requestStream.MoveNext())
                return;
            await responseStream.WriteAsync(new DialMessage { Result = new DialResult { Error = DialError.None } });
            while (await requestStream.MoveNext())
            {
                await responseStream.WriteAsync(requestStream.Current);
                if (requestStream.Current.KindCase == DialMessage.KindOneofCase.End)
                    return;
            }
        }
    }

    /// <summary>Server and node in this process, linked over loopback the outbound way.</summary>
    private sealed class ReversedLink : IAsyncDisposable
    {
        private readonly WebApplication _node;
        private readonly TcpListener _serverPort;
        private readonly SslStream _nodeSide;
        private readonly SslStream _serverSide;
        private readonly GrpcChannel _channel;
        private int _connects;

        private ReversedLink(WebApplication node, TcpListener serverPort, SslStream nodeSide, SslStream serverSide)
        {
            _node = node;
            _serverPort = serverPort;
            _nodeSide = nodeSide;
            _serverSide = serverSide;
            var handler = new SocketsHttpHandler
            {
                // The server never dials: every connection is one the node opened.
                ConnectCallback = (_, _) =>
                {
                    Interlocked.Increment(ref _connects);
                    return ValueTask.FromResult<Stream>(serverSide);
                },
            };
            _channel = GrpcChannel.ForAddress("http://site-node", new GrpcChannelOptions { HttpHandler = handler });
            Client = new SiteNodeService.SiteNodeServiceClient(_channel);
        }

        public SiteNodeService.SiteNodeServiceClient Client { get; }

        public int Connects => Volatile.Read(ref _connects);

        public static async Task<ReversedLink> OpenAsync(bool nodeAcceptsServer = true)
        {
            var nodeIdentity = SiteTls.CreateIdentity("node");
            var serverIdentity = SiteTls.CreateIdentity("server");
            var nodeFingerprint = SiteTls.Fingerprint(nodeIdentity);
            var serverFingerprint = SiteTls.Fingerprint(serverIdentity);

            var listener = new AcceptedStreamListener();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IConnectionListenerFactory>(listener);
            builder.Services.AddGrpc();
            builder.WebHost.ConfigureKestrel(o => o.Listen(new AcceptedStreamEndPoint(), l => l.Protocols = HttpProtocols.Http2));
            var node = builder.Build();
            node.MapGrpcService<EchoNode>();
            await node.StartAsync();

            var serverPort = new TcpListener(IPAddress.Loopback, 0);
            serverPort.Start();
            var port = ((IPEndPoint)serverPort.LocalEndpoint).Port;

            async Task<SslStream> ServerAcceptAsync()
            {
                var socket = await serverPort.AcceptTcpClientAsync();
                var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                using var timeout = new CancellationTokenSource(SiteTls.HandshakeTimeout);
                await tls.AuthenticateAsServerAsync(SiteTls.ServerOptions(serverIdentity, fp => fp is not null && fp.AsSpan().SequenceEqual(nodeFingerprint)), timeout.Token);
                return tls;
            }

            async Task<SslStream> NodeConnectAsync()
            {
                var socket = new TcpClient();
                await socket.ConnectAsync(IPAddress.Loopback, port);
                var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                using var timeout = new CancellationTokenSource(SiteTls.HandshakeTimeout);
                await tls.AuthenticateAsClientAsync(SiteTls.ClientOptions(nodeIdentity, fp => nodeAcceptsServer && fp is not null && fp.AsSpan().SequenceEqual(serverFingerprint)), timeout.Token);
                return tls;
            }

            try
            {
                var serverSide = ServerAcceptAsync();
                var nodeSide = NodeConnectAsync();
                await Task.WhenAll(serverSide, nodeSide);
                _ = listener.ServeAsync(nodeSide.Result, null, CancellationToken.None);
                return new ReversedLink(node, serverPort, nodeSide.Result, serverSide.Result);
            }
            catch
            {
                serverPort.Stop();
                await node.DisposeAsync();
                throw;
            }
        }

        public async Task CloseNodeSideAsync() => await _nodeSide.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            // Kestrel waits for open connections when it stops: close the link first.
            _channel.Dispose();
            await _serverSide.DisposeAsync();
            await _nodeSide.DisposeAsync();
            _serverPort.Stop();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _node.StopAsync(stop.Token);
            await _node.DisposeAsync();
        }
    }
}
