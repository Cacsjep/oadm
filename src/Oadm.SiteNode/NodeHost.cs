using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Oadm.SiteNode.AdminChannel;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Discovery;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Relays;
using Oadm.SiteNode.Service;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Feed;
using Oadm.Sites.Transport;

using Serilog;
using Serilog.Events;

namespace Oadm.SiteNode;

/// <summary>
/// The node's host: Kestrel serves <c>SiteNodeService</c> only on links (connections the node opened, or accepted and
/// put through TLS itself; no socket endpoint of its own), plus the outbound connector, the inbound listener, the mDNS
/// announcement, the binding clock and the local admin channel. Windows service "OADM Site Node" (OadmSiteNode) or
/// systemd (Type=notify); both are no-ops on a console.
/// </summary>
public static class NodeHost
{
    public const string WindowsServiceName = "OadmSiteNode";

    /// <summary>HTTP/2 streams per link (docs/sites.md, "Two connections per node").</summary>
    public const int MaxStreamsPerLink = 4096;

    public static WebApplication Build(NodeOptions options, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.DataDirectory);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        if (options.UseSerilog)
        {
            var logs = Path.Combine(options.DataDirectory, "logs");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                .WriteTo.File(Path.Combine(logs, "site-node-.log"), formatProvider: CultureInfo.InvariantCulture, rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 20 * 1024 * 1024,
                    rollOnFileSizeLimit: true, retainedFileCountLimit: 30, retainedFileTimeLimit: TimeSpan.FromDays(14))
                .CreateLogger();
            builder.Services.AddSerilog();
        }

        builder.Services.AddWindowsService(o => o.ServiceName = WindowsServiceName);
        builder.Services.AddSystemd();

        var listener = new AcceptedStreamListener();
        builder.Services.AddSingleton<IConnectionListenerFactory>(listener);
        builder.Services.AddSingleton(listener);
        builder.Services.AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = 16 * 1024 * 1024;
            o.EnableDetailedErrors = false;
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.Http2.MaxStreamsPerConnection = MaxStreamsPerLink;
            k.Limits.Http2.InitialConnectionWindowSize = 4 * 1024 * 1024;
            k.Limits.Http2.InitialStreamWindowSize = 1024 * 1024;
            k.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(15);
            k.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(30);
            // Links stay open as long as both sides answer the HTTP/2 pings: an idle worker link or a node waiting to be
            // bound must not be dropped every 130 s (Kestrel's default). Unbound inbound links have their own idle limit.
            // Not Timeout.InfiniteTimeSpan: Kestrel adds the ticks to a timestamp, and -1 ms closed idle links after the
            // next heartbeat (about a second).
            k.Limits.KeepAliveTimeout = KeepAliveTimeout;
            k.Limits.MinRequestBodyDataRate = null;
            k.Limits.MinResponseDataRate = null;
            k.Listen(new AcceptedStreamEndPoint(), l =>
            {
                l.Protocols = HttpProtocols.Http2;
                // Every call finds its link (server certificate, address) through this feature.
                l.Use(next => context =>
                {
                    if (context.RemoteEndPoint is LinkEndPoint endpoint)
                        context.Features.Set(endpoint.Link);
                    return next(context);
                });
            });
        });

        AddNode(builder.Services, options);
        var app = builder.Build();
        app.Use(LinkLimits(app.Services.GetRequiredService<BindingState>(), options.Time));
        app.MapGrpcService<SiteNodeGrpcService>();
        return app;
    }

    /// <summary>Kestrel's keep-alive timeout on links: in effect none (ten years); the HTTP/2 pings decide.</summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromDays(3650);

    /// <summary>What a link that is not the bound server's may send in one call (Hello, PairStart and Bind are a few KB).</summary>
    public const int UnboundRequestBytes = 64 * 1024;

    /// <summary>
    /// Every call counts as activity of its link; a caller that is not the bound server may send at most
    /// <see cref="UnboundRequestBytes"/> per call (gRPC lifts Kestrel's body limit, so the body stream is limited here).
    /// </summary>
    internal static Func<HttpContext, RequestDelegate, Task> LinkLimits(BindingState binding, TimeProvider time) => (context, next) =>
    {
        if (context.Features.Get<SiteLink>() is { } link)
        {
            link.Touch(time.GetTimestamp());
            if (!binding.IsBoundTo(link.ServerFingerprintHex))
                context.Request.Body = new LimitedReadStream(context.Request.Body, UnboundRequestBytes);
        }

        return next(context);
    };

    /// <summary>The node's services (shared by the host and tests that build parts of it).</summary>
    public static void AddNode(IServiceCollection services, NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton(options.Time);
        services.AddSingleton(sp => new NodeStore(options.DataDirectory, sp.GetRequiredService<ILogger<NodeStore>>()));
        services.AddSingleton(_ => new NodeIdentity(options.DataDirectory, options.HostName));
        services.AddSingleton(sp => new BindingState(sp.GetRequiredService<NodeStore>(), sp.GetRequiredService<NodeIdentity>(), options.Time, options.HostName));
        services.AddSingleton<PortPolicy>();
        services.AddSingleton<INetworkCards>(_ => options.NetworkCards ?? new SystemNetworkCards(options.Time));
        services.AddSingleton<NetworkScope>();
        services.AddSingleton(options.Connector ?? SystemDeviceConnector.Instance);
        services.AddSingleton(options.Dns ?? SystemNodeDns.Instance);
        services.AddSingleton(options.Pinger ?? SystemPinger.Instance);
        services.AddSingleton(_ => new ConnectionLog(Path.Combine(options.DataDirectory, "logs"), options.Time));
        services.AddSingleton(sp => new Dialer(
            sp.GetRequiredService<PortPolicy>(), sp.GetRequiredService<NetworkScope>(), sp.GetRequiredService<IDeviceConnector>(),
            sp.GetRequiredService<INodeDns>(), sp.GetRequiredService<ConnectionLog>(), options.Time, options.Limits));
        services.AddSingleton(sp => new RelayHost(sp.GetRequiredService<NodeStore>(), sp.GetRequiredService<PortPolicy>(),
            sp.GetRequiredService<NetworkScope>(), sp.GetRequiredService<ConnectionLog>(), options.Time));
        services.AddSingleton<ConnectionState>();
        services.AddSingleton<LinkRegistry>();
        services.AddSingleton<NodeStatusFeed>();
        services.AddSingleton(sp => new AxisBrowser(sp.GetRequiredService<NetworkScope>(), options.Time));
        services.AddSingleton(sp => new RangeScanner(sp.GetRequiredService<PortPolicy>(), sp.GetRequiredService<NetworkScope>(),
            sp.GetRequiredService<IDeviceConnector>(), sp.GetRequiredService<ConnectionLog>(), options.Time));
        services.AddSingleton<AdminHandler>();
        services.AddSingleton<OutboundConnector>();
        services.AddSingleton<InboundListener>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboundConnector>());
        services.AddHostedService(sp => sp.GetRequiredService<InboundListener>());
        services.AddHostedService<NodeAnnouncer>();
        services.AddHostedService<BindingClock>();
        services.AddHostedService<AdminChannelHost>();
        services.AddHostedService<ResetWatcher>();
    }
}

/// <summary>After Reset every link closes (they carry the old identity); the outbound connector connects again with the new one.</summary>
internal sealed class ResetWatcher(BindingState binding, LinkRegistry links) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        binding.IdentityReset += links.CloseAll;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        binding.IdentityReset -= links.CloseAll;
        links.CloseAll();
        return Task.CompletedTask;
    }
}
