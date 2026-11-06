using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Identity;
using Oadm.Sites.Transport;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// A site node that keeps no rules (a node someone took over): it holds a key, connects to the site port like the real
/// node and answers whatever the test tells it to. With the key of a bound site (<see cref="KeyOfAsync"/>) the server
/// takes it for that site's node.
/// </summary>
internal sealed class RogueNode : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly AcceptedStreamListener _listener;
    private readonly X509Certificate2 _identity;
    private readonly List<SslStream> _links = [];

    private RogueNode(WebApplication app, AcceptedStreamListener listener, X509Certificate2 identity, RogueAnswers answers)
    {
        _app = app;
        _listener = listener;
        _identity = identity;
        Answers = answers;
    }

    public RogueAnswers Answers { get; }

    public static async Task<RogueNode> StartAsync(X509Certificate2 identity)
    {
        var answers = new RogueAnswers();
        var listener = new AcceptedStreamListener();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IConnectionListenerFactory>(listener);
        builder.Services.AddSingleton(answers);
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(o => o.Listen(new AcceptedStreamEndPoint(), l => l.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        app.MapGrpcService<RogueAnswers>();
        await app.StartAsync();
        return new RogueNode(app, listener, identity, answers);
    }

    /// <summary>
    /// The key of the site <paramref name="node"/> is bound as, read from a copy of its data folder; the node is stopped,
    /// so the key is free for a <see cref="RogueNode"/>.
    /// </summary>
    public static async Task<X509Certificate2> KeyOfAsync(SiteTestNode node)
    {
        var copy = node.CopyFolder();
        await node.DisposeAsync();
        try
        {
            using var identity = new NodeIdentity(copy, "site-pc");
            return X509CertificateLoader.LoadPkcs12(identity.Certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }
        finally
        {
            SiteTestNode.TryDelete(copy);
        }
    }

    /// <summary>Opens a link to the site port at <paramref name="address"/>:<paramref name="port"/>, through TLS with any server.</summary>
    public async Task ConnectAsync(IPAddress address, int port)
    {
        var socket = new TcpClient(address.AddressFamily);
        await socket.ConnectAsync(address, port);
        var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await tls.AuthenticateAsClientAsync(SiteTls.ClientOptions(_identity, _ => true), timeout.Token);
        lock (_links)
        {
            _links.Add(tls);
        }

        _ = _listener.ServeAsync(tls, null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        SslStream[] links;
        lock (_links)
        {
            links = [.. _links];
        }

        // Kestrel waits for open connections when it stops: close the links first.
        foreach (var link in links)
        {
            await link.DisposeAsync();
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            // A slow stop only shortens the graceful part: the dispose below ends the rest.
        }

        await _app.DisposeAsync();
        _identity.Dispose();
    }
}

/// <summary>What a <see cref="RogueNode"/> answers; the defaults are a bound node's honest answers.</summary>
internal sealed class RogueAnswers : SiteNodeService.SiteNodeServiceBase
{
    private int _hellos;

    /// <summary>The Hello by its number on all links (1 = the first, which the server asks when the link opens).</summary>
    public Func<int, HelloReply> HelloOf { get; set; } = _ => Bound();

    /// <summary>The statuses Watch sends, then it stays open.</summary>
    public IReadOnlyList<NodeStatus> Statuses { get; set; } = [new NodeStatus { State = NodeState.Bound }];

    /// <summary>Hellos answered so far.</summary>
    public int Hellos => Volatile.Read(ref _hellos);

    /// <summary>A bound node's Hello on its control link.</summary>
    public static HelloReply Bound(string version = "3.7.0-test") => new()
    {
        NodeId = "0123456789abcdef",
        Version = version,
        ProtocolVersion = 1,
        State = NodeState.Bound,
        HostName = "site-pc",
        LinkRole = LinkRole.Control,
    };

    public override Task<HelloReply> Hello(HelloRequest request, ServerCallContext context) =>
        Task.FromResult(HelloOf(Interlocked.Increment(ref _hellos)));

    public override async Task Watch(WatchRequest request, IServerStreamWriter<NodeStatus> responseStream, ServerCallContext context)
    {
        foreach (var status in Statuses)
        {
            await responseStream.WriteAsync(status, context.CancellationToken);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The server ended the session.
        }
    }
}
