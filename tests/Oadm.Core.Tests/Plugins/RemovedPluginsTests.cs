using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// docs/spec/plugin-removal.md "State": every item of a formerly shipped id goes once, also in custom roles; an id still
/// known, a merged one, a start with a load error and a development run keep everything.
/// </summary>
#pragma warning disable CA1001 // disposed in DisposeAsync (IAsyncLifetime)
public sealed class RemovedPluginsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string EnabledKey = "Plugins.Enabled";
    private static readonly Guid CustomRole = Guid.NewGuid();
    private TestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private ServerSettingsStore Settings => _db.Get<ServerSettingsStore>();

    private RemovedPlugins Cleaner() => new(
        _db.Get<IDbContextFactory<OadmDbContext>>(), Settings, _db.Get<RoleStore>(), _db.Get<AuditLog>(),
        _db.Paths.PluginDataDirectory, EnabledKey, NullLogger.Instance);

    private static readonly string[] PreviousSet = ["oadm.est", "oadm.gone", "oadm.gone.task", "oadm.kept", "oadm.off"];
    private static readonly string[] CustomKeys = ["devices.refresh", "oadm.gone:manage", "task:oadm.gone.task", "task:oadm.gone.task:remove", "oadm.kept:manage", "oadm.gone-other:x"];
    private static readonly string[] WithState = ["oadm.gone", "oadm.kept", "oadm.est"];
    private static readonly string[] OperatorKeys = ["oadm.gone:manage", "oadm.kept:manage"];

    private static readonly IReadOnlySet<string> Known = new HashSet<string>(["oadm.kept", "oadm.off"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The previous start shipped oadm.gone, oadm.kept, oadm.off and the merged oadm.est; each has state.</summary>
    private async Task SeedAsync()
    {
        await Settings.SetAsync(RemovedPlugins.SettingKey, PreviousSet, CancellationToken.None);
        await Settings.SetJsonAsync(EnabledKey, """{"oadm.gone":true,"oadm.off":false}""", CancellationToken.None);
        foreach (var id in WithState)
        {
            await Settings.SetJsonAsync(SettingKeys.PluginKey(id, "a"), "1", CancellationToken.None);
            await Settings.SetJsonAsync(SettingKeys.PluginKey(id, "b"), "2", CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(_db.Paths.PluginDataDirectory, id, "sub"));
            File.WriteAllText(Path.Combine(_db.Paths.PluginDataDirectory, id, "sub", "f.txt"), "x");
        }

        await using var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        db.PluginRecords.AddRange(
            new PluginRecord { PluginId = "oadm.gone", Collection = "c", Key = "1" },
            new PluginRecord { PluginId = "oadm.kept", Collection = "c", Key = "1" });
        var keys = JsonSerializer.Serialize(CustomKeys);
        db.Roles.Add(new RoleEntity
        {
            Id = CustomRole, Name = "Agents", NormalizedName = "AGENTS", Kind = RoleKind.Custom,
            PermissionsJson = keys, KnownJson = keys, StartPage = "plugin:oadm.gone",
        });
        var op = await db.Roles.SingleAsync(r => r.Id == BuiltInRoles.OperatorId);
        op.PermissionsJson = JsonSerializer.Serialize(OperatorKeys);
        op.KnownJson = op.PermissionsJson;
        op.StartPage = "plugin:oadm.kept";
        await db.SaveChangesAsync();
    }

    private async Task<string[]> KeysOfAsync(Guid role)
    {
        await using var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        var entity = await db.Roles.AsNoTracking().SingleAsync(r => r.Id == role);
        return [.. JsonSerializer.Deserialize<string[]>(entity.PermissionsJson)!.Concat(JsonSerializer.Deserialize<string[]>(entity.KnownJson)!).Distinct().Order(StringComparer.Ordinal)];
    }

    private async Task<int> AuditCountAsync()
    {
        await using var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        return await db.AuditEntries.CountAsync(e => e.Action == AuditActions.PluginDataDeleted);
    }

    [Fact]
    public async Task EveryItemOfAGoneIdIsDeletedOnceAndOtherIdsKeepTheirs()
    {
        await SeedAsync();

        var deleted = await Cleaner().CleanAsync(["oadm.kept", "oadm.off", "oadm.new"], Known, loadErrors: false, CancellationToken.None);

        Assert.Equal(["oadm.gone", "oadm.gone.task"], deleted);
        Assert.Equal("""{"oadm.off":false}""", await Settings.GetJsonAsync(EnabledKey, CancellationToken.None));
        Assert.Null(await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.gone", "a"), CancellationToken.None));
        Assert.Null(await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.gone", "b"), CancellationToken.None));
        Assert.Equal("1", await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.kept", "a"), CancellationToken.None));
        Assert.Equal("1", await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.est", "a"), CancellationToken.None)); // merged into PKI
        Assert.False(Directory.Exists(Path.Combine(_db.Paths.PluginDataDirectory, "oadm.gone")));
        Assert.True(Directory.Exists(Path.Combine(_db.Paths.PluginDataDirectory, "oadm.kept")));
        Assert.True(Directory.Exists(Path.Combine(_db.Paths.PluginDataDirectory, "oadm.est")));
        await using (var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(["oadm.kept"], await db.PluginRecords.Select(r => r.PluginId).ToListAsync());
            Assert.Equal(string.Empty, (await db.Roles.SingleAsync(r => r.Id == CustomRole)).StartPage);
            Assert.Equal("plugin:oadm.kept", (await db.Roles.SingleAsync(r => r.Id == BuiltInRoles.OperatorId)).StartPage);
        }

        Assert.Equal(["devices.refresh", "oadm.gone-other:x", "oadm.kept:manage"], await KeysOfAsync(CustomRole));
        Assert.Equal(["oadm.kept:manage"], await KeysOfAsync(BuiltInRoles.OperatorId));
        Assert.Equal("", await _db.Get<RoleStore>().StartPageOfAsync(CustomRole, CancellationToken.None));
        Assert.Equal(2, await AuditCountAsync());
        Assert.Equal(["oadm.kept", "oadm.new", "oadm.off"], await Settings.GetAsync<string[]>(RemovedPlugins.SettingKey, CancellationToken.None) ?? []);

        // A second start changes nothing.
        Assert.Empty(await Cleaner().CleanAsync(["oadm.kept", "oadm.off", "oadm.new"], Known, loadErrors: false, CancellationToken.None));
        Assert.Equal(2, await AuditCountAsync());
    }

    [Fact]
    public async Task ALoadErrorOrADevelopmentRunDeletesNothingAndKeepsTheStoredSet()
    {
        await SeedAsync();

        Assert.Empty(await Cleaner().CleanAsync(["oadm.kept"], Known, loadErrors: true, CancellationToken.None));
        Assert.Empty(await Cleaner().CleanAsync(null, Known, loadErrors: false, CancellationToken.None));

        Assert.Equal("1", await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.gone", "a"), CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_db.Paths.PluginDataDirectory, "oadm.gone")));
        Assert.Contains("oadm.gone:manage", await KeysOfAsync(CustomRole));
        Assert.Contains("oadm.gone", await Settings.GetAsync<string[]>(RemovedPlugins.SettingKey, CancellationToken.None) ?? []);
        Assert.Equal(0, await AuditCountAsync());
    }

    [Fact]
    public async Task AnIdThatIsStillKnownKeepsItsStateAndWithoutAStoredSetNothingIsDeleted()
    {
        await SeedAsync();
        var all = new HashSet<string>(Known, StringComparer.OrdinalIgnoreCase) { "oadm.gone", "oadm.gone.task" }; // turned off, PRO-locked, an add-on, a data-folder plugin

        Assert.Empty(await Cleaner().CleanAsync(["oadm.kept"], all, loadErrors: false, CancellationToken.None));
        Assert.Equal("1", await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.gone", "a"), CancellationToken.None));

        // A backup made before the set existed holds no record: nothing of it goes.
        await Settings.ResetAsync(RemovedPlugins.SettingKey, CancellationToken.None);
        Assert.Empty(await Cleaner().CleanAsync(["oadm.kept"], Known, loadErrors: false, CancellationToken.None));
        Assert.Equal("1", await Settings.GetJsonAsync(SettingKeys.PluginKey("oadm.gone", "a"), CancellationToken.None));
    }
}
