using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// <see cref="ICorePluginContext.Audit"/> (docs/spec/plugins.md, SDK 0.17.0): what a plugin does by itself is one "Plugin
/// action" entry with the plugin's name, as "system" outside a call and as the caller inside one; a host without an audit
/// log gives none.
/// </summary>
#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class PluginAuditTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static CancellationToken Ct => CancellationToken.None;

    private readonly FakeDeviceRepository _devices = new();
    private readonly PluginRegistry _registry = new();
    private readonly ContextPlugin _plugin = new();
    private TestDatabase _db = null!;
    private AuditLog _log = null!;
    private TaskEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _log = new AuditLog(_db.Get<IDbContextFactory<OadmDbContext>>(), TimeProvider.System);
        _engine = new TaskEngine(new InMemoryTaskStore(), _registry, _devices, new FakeVapixClientFactory());
        Assert.True(_registry.RegisterCorePlugin(_plugin, new PluginOrigin(ContextPlugin.PluginId, "1.0.0", null) { MinSdkVersion = SdkInfo.Version }));
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<CorePluginHost> StartAsync(bool withAudit = true)
    {
        var host = new CorePluginHost(_registry, _devices, new FakeVapixClientFactory(), _engine, new InMemoryPluginSettingsProvider())
        {
            AuditLog = withAudit ? _log : null,
        };
        await host.StartAllAsync(Ct);
        return host;
    }

    [Fact]
    public async Task APluginsOwnActionIsAPluginActionOfTheSystemWithThePluginsName()
    {
        await using var host = await StartAsync();

        await _plugin.Context!.Audit!.WriteAsync("Deleted 3 plates whose time ended", Ct);

        var entry = Assert.Single((await _log.ListAsync(0, 0, Ct)).Entries);
        Assert.Equal(("system", string.Empty, AuditActions.PluginCall, ContextPlugin.Name, "Deleted 3 plates whose time ended"),
            (entry.UserName, entry.ClientAddress, entry.Action, entry.Target, entry.Detail));
    }

    [Fact]
    public async Task InAUsersCallTheEntryNamesTheUser()
    {
        await using var host = await StartAsync();

        using (CallerContext.Use(new AuthenticatedCaller(Guid.NewGuid(), "anna", BuiltInRoles.AdministratorId, PermissionSet.All, "10.0.0.5", "pc", "hash") { Devices = CallerDevices.All }))
        {
            await _plugin.Context!.Audit!.WriteAsync("Saved the settings", Ct);
        }

        var entry = Assert.Single((await _log.ListAsync(0, 0, Ct)).Entries);
        Assert.Equal(("anna", ContextPlugin.Name), (entry.UserName, entry.Target));
    }

    [Fact]
    public async Task AnEmptyDetailIsRefusedAndAHostWithoutAnAuditLogGivesNone()
    {
        await using (var host = await StartAsync())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _plugin.Context!.Audit!.WriteAsync(" ", Ct));
            Assert.Equal(0, (await _log.ListAsync(0, 0, Ct)).TotalCount);
        }

        await using var without = await StartAsync(withAudit: false);
        Assert.Null(_plugin.Context!.Audit);
    }

    private sealed class ContextPlugin : ICorePlugin
    {
        public const string PluginId = "test.audit";
        public const string Name = "Audit test";

        public string Id => PluginId;

        public string DisplayName => Name;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Context = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
