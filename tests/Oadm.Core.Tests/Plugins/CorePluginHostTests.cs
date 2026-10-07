using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Plugins;

#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class CorePluginHostTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly PluginRegistry _registry = new();
    private readonly FakeDeviceRepository _devices = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly InMemoryPluginSettingsProvider _settings = new();
    private TaskEngine _engine = null!;
    private CorePluginHost _host = null!;

    public Task InitializeAsync()
    {
        _engine = new TaskEngine(new InMemoryTaskStore(), _registry, _devices, _vapix);
        _host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _engine.DisposeAsync();
    }

    [Fact]
    public async Task StartsInvokesAndStopsCorePlugins()
    {
        var calls = new List<string>();
        var first = new TestCorePlugin("core.a", calls);
        var second = new TestCorePlugin("core.b", calls);
        Register(first);
        Register(second);

        await _host.StartAllAsync(CancellationToken.None);

        Assert.Equal(CorePluginState.Running, _host.GetStatus("core.a").State);
        Assert.NotNull(first.Context);
        Assert.Same(_devices, first.Context.Devices);
        Assert.Same(_engine, first.Context.Tasks);
        Assert.Equal("echo:hi", await _host.InvokeAsync("CORE.A", "echo", "hi", CancellationToken.None));

        await _host.StopAllAsync(CancellationToken.None);

        Assert.Equal(["start core.a", "start core.b", "stop core.b", "stop core.a"], calls);
        Assert.All(_host.Statuses, s => Assert.Equal(CorePluginState.Stopped, s.State));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.InvokeAsync("core.a", "echo", null, CancellationToken.None));
    }

    [Fact]
    public async Task FaultingPluginIsIsolated()
    {
        var calls = new List<string>();
        Register(new TestCorePlugin("core.bad", calls) { FailStart = true });
        Register(new TestCorePlugin("core.good", calls));

        await _host.StartAllAsync(CancellationToken.None);

        var bad = _host.GetStatus("core.bad");
        Assert.Equal(CorePluginState.Faulted, bad.State);
        Assert.Equal("start failed", bad.Error);
        Assert.Equal(CorePluginState.Running, _host.GetStatus("core.good").State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.InvokeAsync("core.bad", "echo", null, CancellationToken.None));
    }

    [Fact]
    public async Task InvokeErrors()
    {
        Register(new TestCorePlugin("core.a", []));
        await _host.StartAllAsync(CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _host.InvokeAsync("core.missing", "echo", null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => _host.InvokeAsync("core.a", "unknown", null, CancellationToken.None));
    }

    [Fact]
    public async Task SettingsAreNamespacedPerPlugin()
    {
        var a = new TestCorePlugin("core.a", []);
        var b = new TestCorePlugin("core.b", []);
        Register(a);
        Register(b);
        await _host.StartAllAsync(CancellationToken.None);

        await a.Context!.Settings.SetAsync("Server", "\"x\"", CancellationToken.None);

        Assert.Equal("\"x\"", await a.Context.Settings.GetAsync("Server", CancellationToken.None));
        Assert.Null(await b.Context!.Settings.GetAsync("Server", CancellationToken.None));
    }

    [Fact]
    public async Task CorePluginCanStartTasks()
    {
        var core = new TestCorePlugin("core.a", []);
        Register(core);
        _registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("t.ok", (_, _, _) => Task.CompletedTask),
            new PluginOrigin("test", "1.0.0", null));
        await _host.StartAllAsync(CancellationToken.None);

        var taskId = await core.Context!.Tasks.RunOneAsync("t.ok", [_devices.Add()], null, "core.a", CancellationToken.None);
        await _engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TaskState.Done, (await _engine.GetAsync(taskId, CancellationToken.None))!.State);
    }

    private void Register(ICorePlugin plugin) =>
        Assert.True(_registry.RegisterCorePlugin(plugin, new PluginOrigin("test", "1.0.0", null)));

    private sealed class TestCorePlugin(string id, List<string> calls) : ICorePlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public bool FailStart { get; init; }

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            calls.Add("start " + id);
            if (FailStart)
            {
                throw new InvalidOperationException("start failed");
            }

            Context = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            calls.Add("stop " + id);
            return Task.CompletedTask;
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            method == "echo" ? Task.FromResult<string?>("echo:" + payloadJson) : throw new NotSupportedException(method);
    }
}
