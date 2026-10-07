using Grpc.Core;

using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Server.Tests.Support;
using Oadm.Sdk.Plugins;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

public sealed class SettingsAndPluginServiceTests
{
    [Fact]
    public async Task StartupCreatesDatabaseAndMasterKeyInTheDataFolder()
    {
        await using var host = await TestServerHost.StartAsync();
        var paths = host.Get<OadmPaths>();

        Assert.Equal(Path.GetFullPath(host.DataDirectory), paths.DataDirectory);
        Assert.True(File.Exists(paths.DatabasePath));
        Assert.True(File.Exists(paths.MasterKeyPath));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public async Task SettingsRoundTripAndPartialUpdate()
    {
        await using var host = await TestServerHost.StartAsync();

        var defaults = await host.Settings.GetAsync(new Proto.Empty());
        Assert.Equal(60, defaults.PollingIntervalSeconds);
        Assert.Equal(32, defaults.ScanParallelism);
        Assert.Equal(1500, defaults.ScanTimeoutMs);
        Assert.Equal("http://0.0.0.0:5080", defaults.ListenUrl);
        Assert.Equal(30, defaults.ZeroConfSeconds);
        Assert.False(string.IsNullOrEmpty(defaults.ServerName));

        var saved = await host.Settings.SetAsync(new Proto.ServerSettings
        {
            PollingIntervalSeconds = 30,
            ScanParallelism = 64,
            ScanTimeoutMs = 2000,
            ServerName = "oadm-lab",
            ListenUrl = "http://0.0.0.0:6000",
            ZeroConfSeconds = 45,
        });
        Assert.Equal(30, saved.PollingIntervalSeconds);

        var partial = await host.Settings.SetAsync(new Proto.ServerSettings { ServerName = "renamed" });
        Assert.Equal("renamed", partial.ServerName);
        Assert.Equal(64, partial.ScanParallelism);

        var reloaded = await host.Settings.GetAsync(new Proto.Empty());
        Assert.Equal(30, reloaded.PollingIntervalSeconds);
        Assert.Equal(64, reloaded.ScanParallelism);
        Assert.Equal(2000, reloaded.ScanTimeoutMs);
        Assert.Equal("renamed", reloaded.ServerName);
        Assert.Equal("http://0.0.0.0:6000", reloaded.ListenUrl);
        Assert.Equal(45, reloaded.ZeroConfSeconds); // kept by the partial update
    }

    [Fact]
    public async Task InvalidSettingsAreRejected()
    {
        await using var host = await TestServerHost.StartAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Settings.SetAsync(new Proto.ServerSettings { PollingIntervalSeconds = 1 }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        ex = await Assert.ThrowsAsync<RpcException>(() => host.Settings.SetAsync(new Proto.ServerSettings { ListenUrl = "ftp://x" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        foreach (var seconds in new[] { 4, 301 })
        {
            ex = await Assert.ThrowsAsync<RpcException>(() => host.Settings.SetAsync(new Proto.ServerSettings { ZeroConfSeconds = seconds }).ResponseAsync);
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }

        Assert.Equal(60, (await host.Settings.GetAsync(new Proto.Empty())).PollingIntervalSeconds);
    }

    [Fact]
    public async Task CorePluginsAreListedAndInvoked()
    {
        await using var host = await TestServerHost.StartAsync();
        Assert.Empty((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins);

        var core = new EchoCorePlugin();
        host.Get<PluginRegistry>().RegisterCorePlugin(core, new PluginOrigin("tests", "1.0.0", null));
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);

        var listed = Assert.Single((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins);
        Assert.Equal("test.echo", listed.Id);

        var reply = await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = "test.echo", Method = "echo", PayloadJson = "{\"a\":1}" });
        Assert.Equal("{\"a\":1}", reply.PayloadJson);

        // Contributed task shows up with its owner.
        var task = Assert.Single((await host.Tasks.ListTaskPluginsAsync(new Proto.Empty())).Plugins, p => p.Id == "test.echo.task");
        Assert.Equal("test.echo", task.OwnerCorePluginId);

        // Core plugin settings go to the database under Plugin:<id>:<key>.
        Assert.Equal("\"v\"", await host.Get<Core.Settings.ServerSettingsStore>().GetJsonAsync("Plugin:test.echo:started", CancellationToken.None));

        var unknown = await Assert.ThrowsAsync<RpcException>(() => host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = "nope", Method = "x" }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);

        var failing = await Assert.ThrowsAsync<RpcException>(() => host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = "test.echo", Method = "fail" }).ResponseAsync);
        Assert.Equal(StatusCode.Internal, failing.StatusCode);
    }

    private sealed class EchoCorePlugin : ICorePlugin
    {
        public string Id => "test.echo";

        public string DisplayName => "Echo";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [new EchoTask()];

        public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct) =>
            await ctx.Settings.SetAsync("started", "\"v\"", ct);

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            method == "fail" ? throw new NotSupportedException("boom") : Task.FromResult(payloadJson);
    }

    private sealed class EchoTask : ITaskPlugin
    {
        public string Id => "test.echo.task";

        public string DisplayName => "Echo task";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(Sdk.Devices.IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, Sdk.Devices.IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}
