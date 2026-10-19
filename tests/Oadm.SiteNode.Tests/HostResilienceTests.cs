using System.IO.Pipes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Testing;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.AdminChannel;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>The node's background loops survive their errors; a part that does fail stops the node with a non-zero exit code.</summary>
public sealed class HostResilienceTests
{
    [Fact]
    public async Task TheBindingClockGoesOnWhenATickCannotBeSaved()
    {
        using var folder = new TempFolder();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
        var store = new NodeStore(folder.Path);
        using var identity = new NodeIdentity(folder.Path, "site-pc");
        var state = new BindingState(store, identity, time, "site-pc");
        var logger = new CountingLogger<BindingClock>();
        using var clock = new BindingClock(state, new NodeOptions { Time = time, DataDirectory = folder.Path }, logger);
        var before = store.Read(c => c.BindingSecondsLeft);
        await clock.StartAsync(CancellationToken.None);

        // node.json cannot be replaced while a folder has its name.
        var path = Path.Combine(folder.Path, NodeStore.FileName);
        File.Delete(path);
        Directory.CreateDirectory(path);
        // The clock's loop starts in the background: move the clock on until a tick ran.
        await Eventually.TrueAsync(() =>
        {
            time.Advance(BindingClock.Interval);
            return logger.Warnings > 0;
        }, "the failed tick is logged");
        Assert.False(clock.ExecuteTask!.IsCompleted);

        Directory.Delete(path);
        await Eventually.TrueAsync(() =>
        {
            time.Advance(BindingClock.Interval);
            return File.Exists(path) && store.Read(c => c.BindingSecondsLeft) < before;
        }, "a later tick is saved");
        await clock.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APartThatFailsStopsTheNodeAsAFailure()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<NodeFailure>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeFailure>());
        builder.Services.AddHostedService<FailingPart>();
        using var host = builder.Build();
        var failure = host.Services.GetRequiredService<NodeFailure>();

        await host.StartAsync();
        await Eventually.TrueAsync(() => failure.Failed, "the failure is noticed");
        await host.StopAsync();

        Assert.True(failure.Failed);
    }

    [Fact]
    public async Task AStopWithoutAFailureIsNoFailure()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<NodeFailure>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeFailure>());
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
        Assert.False(host.Services.GetRequiredService<NodeFailure>().Failed);
    }

    [Fact]
    public async Task ALocalChannelThatCannotBeOpenedLeavesTheNodeRunning()
    {
        using var folder = new TempFolder();
        // Windows: another program holds the pipe name; elsewhere the socket's folder is a file.
        var blocker = Path.Combine(folder.Path, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "");
        var pipeName = "oadm-site-node-test-" + Guid.NewGuid().ToString("N");
        using var held = OperatingSystem.IsWindows() ? new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1) : null;
        var endpoint = new AdminEndpoint(pipeName, Path.Combine(blocker, "admin.sock"));

        await using var node = await TestNode.StartAsync(options: o => o.AdminEndpoint = endpoint);
        var channel = node.Get<IEnumerable<IHostedService>>().OfType<AdminChannelHost>().Single();
        await Eventually.TrueAsync(() => channel.Problem is not null, "the channel says why it is closed");

        Assert.False(node.Get<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.False(channel.ExecuteTask!.IsCompleted);
        Assert.False(node.Get<NodeFailure>().Failed);
    }

    private sealed class FailingPart : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new IOException("The disk is full.");
        }
    }

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }
}
