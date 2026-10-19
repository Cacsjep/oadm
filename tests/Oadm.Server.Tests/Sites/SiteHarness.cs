using System.Net;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.Server.Tests.Support;
using Oadm.SiteNode;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Sites;

/// <summary>The real server (in-process) with its site listener on a random loopback port, optionally on a fake clock.</summary>
internal sealed class SiteServer : IAsyncDisposable
{
    private SiteServer(TestServerHost host, FakeTimeProvider? time)
    {
        Host = host;
        Time = time;
        Client = new Proto.SiteService.SiteServiceClient(host.Invoker);
    }

    public TestServerHost Host { get; }

    public FakeTimeProvider? Time { get; }

    public Proto.SiteService.SiteServiceClient Client { get; }

    public SiteManager Manager => Host.Get<SiteManager>();

    /// <summary>What a node types as the server address.</summary>
    public string Address => "127.0.0.1:" + Manager.ListenPort;

    public static async Task<SiteServer> StartAsync(bool accept = true, FakeTimeProvider? time = null, string? dataDirectory = null, Action<IServiceCollection>? configure = null)
    {
        var host = await TestServerHost.StartAsync(dataDirectory: dataDirectory, configureServices: s =>
        {
            s.RemoveAll<SiteOptions>();
            s.AddSingleton(new SiteOptions { ListenUrl = "127.0.0.1:0", Time = time, WorkerWait = TimeSpan.FromSeconds(30) });
            configure?.Invoke(s);
        });
        if (accept)
        {
            await host.Get<SiteManager>().SetAcceptAsync(true, CancellationToken.None);
        }

        return new SiteServer(host, time);
    }

    public async Task<Proto.SiteList> ListAsync() => await Client.ListSitesAsync(new Proto.Empty());

    /// <summary>Waits until a node waits to be bound and returns its row.</summary>
    public async Task<Proto.Site> WaitingAsync(string? nodeId = null)
    {
        Proto.Site? found = null;
        try
        {
            await Eventually.TrueAsync(async () =>
            {
                found = (await ListAsync()).Sites.FirstOrDefault(s => s.State == Proto.SiteState.Waiting && (nodeId is null || s.NodeId == nodeId));
                return found is not null;
            }, "a node waiting to be bound");
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + " List: " + await ListAsync() + " Server log: " + ServerLog(), e);
        }

        return found!;
    }

    /// <summary>Binds the waiting node of <paramref name="node"/> and waits until the site is online.</summary>
    public async Task<Guid> BindAsync(SiteTestNode node, string name = "Building B")
    {
        var waiting = await WaitingAsync(node.NodeId);
        Proto.Site site;
        try
        {
            site = await Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting.NodeId, NodeFingerprint = waiting.NodeFingerprint, Name = name, PairingCode = node.Code });
        }
        catch (RpcException e)
        {
            throw new InvalidOperationException(e.Status.Detail + " Server log: " + ServerLog(), e);
        }

        var id = Guid.Parse(site.Id);
        await Eventually.TrueAsync(() => Manager.IsOnline(id), "the bound site online");
        return id;
    }

    /// <summary>The server's log lines about sites (a failed wait names what happened).</summary>
    public string ServerLog()
    {
        var logs = Path.Combine(Host.DataDirectory, "logs");
        if (!Directory.Exists(logs))
        {
            return "(none)";
        }

        var lines = Directory.EnumerateFiles(logs).SelectMany(f =>
        {
            using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n');
        }).Where(l => l.Contains("Site", StringComparison.OrdinalIgnoreCase) || l.Contains("[ERR]", StringComparison.Ordinal) || l.Contains("[WRN]", StringComparison.Ordinal));
        return string.Join(" | ", lines.TakeLast(40));
    }

    public async Task<IReadOnlyList<string>> AuditActionsAsync() =>
        [.. (await Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Select(e => e.Action)];

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>
/// The real OADM Site Node in this process: own data folder, fake network cards (the camera network 192.168.0.0/24 and
/// loopback reached, an office card not), devices mapped to loopback end points.
/// </summary>
internal sealed class SiteTestNode : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly bool _ownsFolder;

    private SiteTestNode(WebApplication app, string folder, bool ownsFolder, MapConnector connector)
    {
        _app = app;
        DataDirectory = folder;
        _ownsFolder = ownsFolder;
        Connector = connector;
    }

    public string DataDirectory { get; }

    public MapConnector Connector { get; }

    public string Code => Get<BindingState>().PairingCode;

    public string NodeId => Get<NodeStore>().Snapshot().Id;

    public T Get<T>()
        where T : notnull => _app.Services.GetRequiredService<T>();

    /// <summary>Starts a node; <paramref name="folder"/> reuses a data folder (a restart or a copy).</summary>
    public static async Task<SiteTestNode> StartAsync(Action<NodeConfig>? config = null, string? folder = null, string hostName = "site-pc", MapConnector? connector = null)
    {
        var owns = folder is null;
        folder ??= Path.Combine(Path.GetTempPath(), "oadm-site-node-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        connector ??= new MapConnector();
        if (config is not null)
        {
            new NodeStore(folder).Update(config);
        }

        var options = new NodeOptions
        {
            DataDirectory = folder,
            ListenPort = 0,
            Announce = false,
            UseSerilog = false,
            HostName = hostName,
            Version = "3.7.0-test",
            NetworkCards = new FakeCards(
                new NetworkCard("cam", "Cameras", [new CardAddress(IPAddress.Parse("192.168.0.5"), 24), new CardAddress(IPAddress.Loopback, 8)], HasDefaultRouter: false),
                new NetworkCard("office", "Office", [new CardAddress(IPAddress.Parse("10.0.0.7"), 24)], HasDefaultRouter: true)),
            Dns = new FakeDns(),
            Pinger = new NoPinger(),
            Connector = connector,
            MaxBackoff = TimeSpan.FromSeconds(1),
        };
        var app = NodeHost.Build(options);
        await app.StartAsync();
        return new SiteTestNode(app, folder, owns, connector);
    }

    /// <summary>A copy of this node's data folder (a cloned VM): same id and key.</summary>
    public string CopyFolder()
    {
        var copy = Path.Combine(Path.GetTempPath(), "oadm-site-node-copy-" + Guid.NewGuid().ToString("N")[..12]);
        foreach (var file in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(DataDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(file, target);
            }
            catch (IOException)
            {
                // A log file in use: not part of the identity.
            }
        }

        return copy;
    }

    /// <summary>Points a device address and port at the node to a loopback end point.</summary>
    public void Map(string address, int port, IPEndPoint target) => Connector.Map[new IPEndPoint(IPAddress.Parse(address), port)] = target;

    public async ValueTask DisposeAsync()
    {
        Get<SiteNode.Links.LinkRegistry>().CloseAll();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _app.StopAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await _app.DisposeAsync();
        if (_ownsFolder)
        {
            TryDelete(DataDirectory);
        }
    }

    public static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class SiteAsserts
{
    public static async Task<RpcException> RefusedAsync(StatusCode code, Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.True(code == error.StatusCode, $"Expected {code}, got {error.StatusCode}: {error.Status.Detail}");
        return error;
    }
}
