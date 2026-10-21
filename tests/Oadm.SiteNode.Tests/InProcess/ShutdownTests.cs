using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.AdminChannel;
using Oadm.SiteNode.Feed;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace Oadm.SiteNode.Tests.InProcess;

/// <summary>The node stops at once and cleanly after Reset, with and without a server (run like Program runs it).</summary>
public sealed class ShutdownTests
{
    // A stop takes well under a second; below the host's 30 s shutdown timeout, so a stop that waits for it fails.
    private static readonly TimeSpan StopCeiling = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AfterResetWithoutAServerTheNodeStopsCleanly()
    {
        using var folder = new TempFolder();
        var (app, _) = TestNode.Build(folder, c => c.ServerAddress = "127.0.0.1:1"); // nothing listens on port 1
        using var stop = new CancellationTokenSource();
        var run = NodeHost.RunAsync(app, stop.Token);
        var connection = app.Services.GetRequiredService<ConnectionState>();
        await Eventually.TrueAsync(() => connection.Text.StartsWith("Cannot reach", StringComparison.Ordinal), "the node tried the server");
        var handler = app.Services.GetRequiredService<AdminHandler>();
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.PairingCode), isAdmin: true).Ok);

        Assert.True((await handler.HandleAsync(AdminRequest.Of(AdminCommands.Reset), isAdmin: true, CancellationToken.None)).Ok);
        await stop.CancelAsync();

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(StopCeiling)));
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task AfterResetWithABoundServerTheNodeStopsCleanly()
    {
        await using var server = new TestServer();
        using var folder = new TempFolder();
        var (app, _) = TestNode.Build(folder, c => c.ServerAddress = server.Address);
        using var stop = new CancellationTokenSource();
        var run = NodeHost.RunAsync(app, stop.Token);
        var binding = app.Services.GetRequiredService<BindingState>();
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(binding.PairingCode);
        var handler = app.Services.GetRequiredService<AdminHandler>();

        Assert.True((await handler.HandleAsync(AdminRequest.Of(AdminCommands.Reset), isAdmin: true, CancellationToken.None)).Ok);
        await using var unbound = await server.NextWorkingLinkAsync(); // the new identity connects
        await stop.CancelAsync();

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(StopCeiling)));
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task ABoundNodeStopsCleanlyWhileItsLinkIsOpen()
    {
        await using var server = new TestServer();
        using var folder = new TempFolder();
        var (app, _) = TestNode.Build(folder, c => c.ServerAddress = server.Address);
        using var stop = new CancellationTokenSource();
        var run = NodeHost.RunAsync(app, stop.Token);
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(app.Services.GetRequiredService<BindingState>().PairingCode);
        // Like the server: a Watch stream stays open on the control link.
        using var watch = link.Client.Watch(new WatchRequest());
        Assert.True(await watch.ResponseStream.MoveNext(CancellationToken.None));

        await stop.CancelAsync();

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(StopCeiling)));
        Assert.Equal(0, await run);
    }
}
