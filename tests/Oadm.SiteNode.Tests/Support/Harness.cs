using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

using Google.Protobuf;

using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.State;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Tests.Support;

/// <summary>The real node (Kestrel, links, connector, listener) in this process with a temp data folder and fake cards.</summary>
internal sealed class TestNode : IAsyncDisposable
{
    private readonly TempFolder _folder;
    private readonly WebApplication _app;

    private TestNode(TempFolder folder, WebApplication app, NodeOptions options)
    {
        _folder = folder;
        _app = app;
        Options = options;
    }

    public NodeOptions Options { get; }

    public string DataDirectory => _folder.Path;

    public static async Task<TestNode> StartAsync(Action<NodeConfig>? config = null, Action<NodeOptions>? options = null)
    {
        var folder = new TempFolder();
        var (app, nodeOptions) = Build(folder, config, options);
        await app.StartAsync();
        return new TestNode(folder, app, nodeOptions);
    }

    /// <summary>The node as <see cref="StartAsync"/> builds it, not started (tests that run it like Program does).</summary>
    public static (WebApplication App, NodeOptions Options) Build(TempFolder folder, Action<NodeConfig>? config = null, Action<NodeOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var nodeOptions = new NodeOptions
        {
            DataDirectory = folder.Path,
            UseSerilog = false,
            HostName = "site-pc",
            Version = "3.7.0-test",
            NetworkCards = FakeCards.LoopbackSite(),
            AllowLoopbackDevices = true,
            Dns = new FakeDns(),
            Pinger = new NoPinger(),
            Browser = new FakeBrowser(), // no multicast in unit tests
            MaxBackoff = TimeSpan.FromSeconds(1),
            HandshakeTimeout = TimeSpan.FromSeconds(30),
        };
        options?.Invoke(nodeOptions);
        new NodeStore(folder.Path).Update(FakeCards.ChooseCameraCard); // the office card is not reached
        if (config is not null)
            new NodeStore(folder.Path).Update(config);
        return (NodeHost.Build(nodeOptions), nodeOptions);
    }

    public T Get<T>()
        where T : notnull => _app.Services.GetRequiredService<T>();

    public string Code => Get<BindingState>().PairingCode;

    public async ValueTask DisposeAsync()
    {
        Get<Links.LinkRegistry>().CloseAll();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _app.StopAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await _app.DisposeAsync();
        _folder.Dispose();
    }
}

/// <summary>
/// The OADM server's side of links in a test: its site identity, a listener for nodes that connect out, and TLS to nodes
/// that wait. Each accepted or opened connection becomes a <see cref="ServerLink"/> with its own gRPC client.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Channel<ServerLink> _links = Channel.CreateUnbounded<ServerLink>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ServerLink> _all = [];
    private readonly Task _accepting;

    public TestServer(X509Certificate2? identity = null)
    {
        Identity = identity ?? SiteTls.CreateIdentity("OADM test server");
        _listener.Start();
        _accepting = Task.Run(AcceptAsync);
    }

    public X509Certificate2 Identity { get; }

    public byte[] Fingerprint => SiteTls.Fingerprint(Identity);

    public string FingerprintHex => Convert.ToHexString(Fingerprint);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Address => "127.0.0.1:" + Port;

    /// <summary>The next connection a node opened to this server.</summary>
    public async Task<ServerLink> NextLinkAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _links.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>
    /// The next link that answers Hello. With TLS 1.3 the server's handshake ends before the node checks the server's key,
    /// so links the node refused show up here first and are skipped.
    /// </summary>
    public async Task<ServerLink> NextWorkingLinkAsync()
    {
        while (true)
        {
            var link = await NextLinkAsync();
            try
            {
                await link.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow.AddSeconds(30));
                return link;
            }
            catch (Grpc.Core.RpcException)
            {
                await link.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }

        List<ServerLink> all;
        lock (_all)
            all = [.. _all];
        foreach (var link in all)
            await link.DisposeAsync();
        _stop.Dispose();
    }

    private ServerLink Track(ServerLink link)
    {
        lock (_all)
            _all.Add(link);
        return link;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
                try
                {
                    using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // generous on a slow machine
                    await tls.AuthenticateAsServerAsync(SiteTls.ServerOptions(Identity, fp => fp is not null), handshake.Token);
                    _links.Writer.TryWrite(Track(new ServerLink(tls, Fingerprint)));
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                {
                    await tls.DisposeAsync();
                }
            });
        }
    }
}

/// <summary>One connection as the server uses it: gRPC client over the stream, the node's fingerprint from TLS.</summary>
internal sealed class ServerLink : IAsyncDisposable
{
    private readonly SslStream _stream;
    private readonly GrpcChannel _channel;
    private readonly byte[] _serverFingerprint;
    private int _connects;

    public ServerLink(SslStream stream, byte[] serverFingerprint)
    {
        _stream = stream;
        _serverFingerprint = serverFingerprint;
        NodeFingerprint = SiteTls.Fingerprint(stream.RemoteCertificate!);
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            ConnectCallback = (_, _) => Interlocked.Increment(ref _connects) == 1
                ? ValueTask.FromResult<Stream>(_stream)
                : throw new IOException("The link is gone."),
        };
        _channel = GrpcChannel.ForAddress("http://site-node", new GrpcChannelOptions { HttpHandler = handler, MaxReceiveMessageSize = 32 * 1024 * 1024 });
        Client = new SiteNodeService.SiteNodeServiceClient(_channel);
    }

    public SiteNodeService.SiteNodeServiceClient Client { get; }

    public byte[] NodeFingerprint { get; }

    public string NodeFingerprintHex => Convert.ToHexString(NodeFingerprint);

    /// <summary>The whole binding exchange with a typed code; the node's proof is checked.</summary>
    public async Task BindAsync(string code, string siteName = "Building B", string serverName = "OADM-SRV")
    {
        var pairing = new ServerPairing(PairingCode.Normalize(code) ?? code, NodeFingerprint, _serverFingerprint);
        var start = await Client.PairStartAsync(new PairStartRequest { ServerRound1 = pairing.Round1 });
        var (round2, proof) = pairing.Continue(start.NodeRound1, start.NodeRound2);
        var reply = await Client.BindAsync(new BindRequest
        {
            SiteName = siteName,
            ServerName = serverName,
            ServerRound2 = round2,
            Proof = ByteString.CopyFrom(proof),
        });
        if (!pairing.Check(reply.Proof.ToByteArray()))
            throw new InvalidOperationException("The node's proof does not match.");
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _stream.DisposeAsync();
    }
}

internal static class Allow
{
    /// <summary>Allows a TCP port on Connect to (a loopback test port).</summary>
    public static void Connect(NodeConfig config, int port) => config.Allowed.Add(new PortEntry(PortUse.Connect, PortProtocol.Tcp, port));
}
