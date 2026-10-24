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
        // Outside a group user's call the context's accesses are the host's own (wrapped by the call guard).
        Assert.Equal(await ((Sdk.Devices.IDeviceRepository)_devices).ListAsync(CancellationToken.None), await first.Context.Devices.ListAsync(CancellationToken.None));
        Assert.NotNull(first.Context.Tasks);
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
        await _engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskState.Done, (await _engine.GetAsync(taskId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task ContextCarriesTheHostFirewallOnlyWhenOffered()
    {
        var firewall = new Oadm.Tests.Shared.RecordingFirewall();
        await using var host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings, firewall: firewall);
        var core = new TestCorePlugin("core.fw", []);
        Register(core);

        await host.StartAllAsync(CancellationToken.None);
        Assert.Same(firewall, core.Context!.Firewall);
        await host.StopAllAsync(CancellationToken.None);

        await _host.StartAllAsync(CancellationToken.None);
        Assert.Null(core.Context!.Firewall); // console runs and tests: no firewall changes
    }

    [Fact]
    public async Task ContextCarriesPluginFolderSecretsAndTaskCancel()
    {
        using var protector = new Oadm.Core.Security.CredentialProtector(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var secrets = new Oadm.Core.Security.PluginSecretProtector(protector);
        await using var host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings, secrets: secrets);
        var core = new TestCorePlugin("core.dir", []);
        Assert.True(_registry.RegisterCorePlugin(core, new PluginOrigin("test", "1.0.0", "/plugins/core.dir")));
        _registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("t.wait", (_, _, ct) => Task.Delay(Timeout.Infinite, ct)),
            new PluginOrigin("test", "1.0.0", null));

        await host.StartAllAsync(CancellationToken.None);

        Assert.Equal("/plugins/core.dir", core.Context!.PluginDirectory);
        var sealedValue = core.Context.Secrets!.Protect("pw", "a");
        Assert.NotEqual("pw", sealedValue);
        Assert.Equal("pw", core.Context.Secrets.Unprotect(sealedValue, "a"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => core.Context.Secrets.Unprotect(sealedValue, "b"));

        var taskId = await core.Context.Tasks.RunOneAsync("t.wait", [_devices.Add()], null, "core.dir", CancellationToken.None);
        Assert.True(core.Context.Tasks.Cancel(taskId));
        await _engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TaskState.Cancelled, (await _engine.GetAsync(taskId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task ContextOffersAPrivateDataFolderPerPluginCreatedOnFirstUse()
    {
        var root = Path.Combine(Path.GetTempPath(), "oadm-plugin-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings, pluginDataRoot: root);
            var core = new TestCorePlugin("oadm.system-report", []);
            Register(core);
            await host.StartAllAsync(CancellationToken.None);

            Assert.False(Directory.Exists(root));
            Assert.Equal(Path.Combine(root, "oadm.system-report"), core.Context!.DataDirectory);
            Assert.True(Directory.Exists(core.Context.DataDirectory));
            Assert.Equal("a_b", CorePluginHost.SafeFolderName("a/b"));
            Assert.Equal("_", CorePluginHost.SafeFolderName(".."));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProPackageRunsOnlyWhileProIsActive()
    {
        var calls = new List<string>();
        var pro = new Sdk.Testing.FakeProLicense();
        _registry.Pro = pro;
        Assert.True(_registry.RegisterCorePlugin(new TestCorePlugin("core.pro", calls), new PluginOrigin("pro.pkg", "1.0.0", null) { Pro = true }));

        await _host.StartAllAsync(CancellationToken.None);
        Assert.False(_registry.IsEnabled("pro.pkg"));
        Assert.Equal(CorePluginState.Stopped, _host.GetStatus("core.pro").State);
        var refused = Assert.Throws<InvalidOperationException>(() => _registry.SetEnabled("pro.pkg", true));
        Assert.Equal(Sdk.Licensing.ProLicenseTexts.NeedsPro, refused.Message);

        pro.Current = Sdk.Testing.FakeProLicense.ActivePro().Current;
        await _host.SyncEnabledAsync(CancellationToken.None);
        Assert.True(_registry.IsEnabled("pro.pkg"));
        Assert.Equal(CorePluginState.Running, _host.GetStatus("core.pro").State);

        // The license ends: the package stops, and the stored choice brings it back with a new license.
        _registry.SetEnabled("pro.pkg", true);
        pro.Current = Sdk.Licensing.ProLicenseInfo.None;
        await _host.SyncEnabledAsync(CancellationToken.None);
        Assert.False(_registry.IsEnabled("pro.pkg"));
        Assert.Equal(CorePluginState.Stopped, _host.GetStatus("core.pro").State);
        Assert.False(_registry.Packages.Single(p => p.Id == "pro.pkg").Enabled);
        pro.Current = Sdk.Testing.FakeProLicense.ActivePro().Current;
        Assert.True(_registry.IsEnabled("pro.pkg"));
    }

    [Fact]
    public async Task ServicesLiveWhileTheOfferingPluginRuns()
    {
        var services = new PluginServiceRegistry();
        await _host.DisposeAsync();
        _host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings, services: services);
        var offering = new TestCorePlugin("core.ca", []) { Offer = "the CA" };
        Assert.True(_registry.RegisterCorePlugin(offering, new PluginOrigin("ca.pkg", "1.0.0", null)));
        var user = new TestCorePlugin("core.user", []);
        Assert.True(_registry.RegisterCorePlugin(user, new PluginOrigin("user.pkg", "1.0.0", null)));

        await _host.StartAllAsync(CancellationToken.None);
        Assert.Equal("the CA", user.Context!.Services!.Find<OfferedText>()?.Text);
        Assert.Throws<InvalidOperationException>(() => user.Context.Services.Offer(new OfferedText("another")));

        _registry.SetEnabled("ca.pkg", false);
        await _host.SyncEnabledAsync(CancellationToken.None);
        Assert.Null(user.Context.Services.Find<OfferedText>());

        // A late offer from the stopped plugin (a background task) brings nothing back.
        offering.Context!.Services!.Offer(new OfferedText("late"));
        Assert.Null(user.Context.Services.Find<OfferedText>());

        user.Context.Services.Offer(new OfferedText("mine"));
        Assert.Equal("mine", services.Get<OfferedText>()?.Text);
        user.Context.Services.Withdraw<OfferedText>();
        Assert.Null(services.Get<OfferedText>());
    }

    [Fact]
    public async Task ATagAPluginProtectsStaysProtectedWhileThePluginIsInstalled()
    {
        var protectedTags = new Oadm.Core.Devices.ProtectedTagRegistry();
        await _host.DisposeAsync();
        _host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings, protectedTags: protectedTags);
        var plugin = new TestCorePlugin("core.vlan", []);
        Assert.True(_registry.RegisterCorePlugin(plugin, new PluginOrigin("vlan.pkg", "1.0.0", null)));

        await _host.StartAllAsync(CancellationToken.None);
        plugin.Context!.ProtectedTags!.Protect(["Vlan 20"], name => "The tag " + name + " sets a VLAN.");
        Assert.Equal("The tag Vlan 20 sets a VLAN.", protectedTags.RefusalFor(["Vlan 20"]));

        // Turned off: still protected with the plugin's text; a late call of the stopped plugin changes nothing.
        _registry.SetEnabled("vlan.pkg", false);
        await _host.SyncEnabledAsync(CancellationToken.None);
        Assert.Equal("The tag Vlan 20 sets a VLAN.", protectedTags.RefusalFor(["Vlan 20"]));
        plugin.Context.ProtectedTags.Protect(["Vlan 30"], name => name);
        Assert.Equal(["Vlan 20"], protectedTags.Names);

        // Turned on again: the plugin's new set replaces the kept one.
        _registry.SetEnabled("vlan.pkg", true);
        await _host.SyncEnabledAsync(CancellationToken.None);
        plugin.Context.ProtectedTags.Protect(["Vlan 30"], name => name);
        Assert.Equal(["Vlan 30"], protectedTags.Names);
    }

    [Fact]
    public async Task APluginThatFaultsOnStartKeepsItsProtectedTags()
    {
        var protectedTags = new Oadm.Core.Devices.ProtectedTagRegistry();
        protectedTags.For("core.vlan").Protect(["Vlan 20"], name => name); // restored at the server start
        protectedTags.Detach("core.vlan");
        await _host.DisposeAsync();
        _host = new CorePluginHost(_registry, _devices, _vapix, _engine, _settings, protectedTags: protectedTags);
        var plugin = new TestCorePlugin("core.vlan", []) { FailStart = true };
        Assert.True(_registry.RegisterCorePlugin(plugin, new PluginOrigin("vlan.pkg", "1.0.0", null)));

        await _host.StartAllAsync(CancellationToken.None);

        Assert.Equal(CorePluginState.Faulted, _host.GetStatus("core.vlan").State);
        Assert.Equal("Vlan 20", protectedTags.RefusalFor(["Vlan 20"]));
    }

    [Fact]
    public async Task EveryPluginGetsItsOwnRecords()
    {
        var a = new TestCorePlugin("core.a", []);
        var b = new TestCorePlugin("core.b", []);
        Assert.True(_registry.RegisterCorePlugin(a, new PluginOrigin("a.pkg", "1.0.0", null)));
        Assert.True(_registry.RegisterCorePlugin(b, new PluginOrigin("b.pkg", "1.0.0", null)));

        await _host.StartAllAsync(CancellationToken.None);
        await a.Context!.Records!.SetManyAsync("items", [new("one", "1")], CancellationToken.None);

        Assert.Equal("1", (await a.Context.Records.ListAsync("items", CancellationToken.None))["one"]);
        Assert.Empty(await b.Context!.Records!.ListAsync("items", CancellationToken.None));
    }

    private sealed record OfferedText(string Text);

    private void Register(ICorePlugin plugin) =>
        Assert.True(_registry.RegisterCorePlugin(plugin, new PluginOrigin("test", "1.0.0", null)));

    private sealed class TestCorePlugin(string id, List<string> calls) : ICorePlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public bool FailStart { get; init; }

        /// <summary>Offered as <see cref="OfferedText"/> on start.</summary>
        public string? Offer { get; init; }

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            calls.Add("start " + id);
            if (FailStart)
            {
                throw new InvalidOperationException("start failed");
            }

            Context = ctx;
            if (Offer is not null)
            {
                ctx.Services?.Offer(new OfferedText(Offer));
            }

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
