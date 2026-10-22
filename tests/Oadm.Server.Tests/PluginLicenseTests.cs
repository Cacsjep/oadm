using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Licenses of commercial plugins: the server ID, entering and removing a license, the plugin's live license, trials.</summary>
public sealed class PluginLicenseTests
{
    private const string PluginId = "acme.reports";
    private static readonly (string PrivateKeyPem, string PublicKey) Keys = LicenseFile.CreateKeys();

    private static async Task<(TestServerHost Host, LicensedPlugin Plugin)> StartAsync(int trialDays = 0)
    {
        var host = await TestServerHost.StartAsync();
        var plugin = new LicensedPlugin();
        var registry = host.Get<PluginRegistry>();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(PluginId, "1.0.0", null)
        {
            DisplayName = "ACME reports",
            License = new PluginLicenseManifest(Keys.PublicKey, ["reports", "export"], trialDays),
        }));
        Assert.True(registry.RegisterCorePlugin(new LicensedPlugin("acme.free"), new PluginOrigin("acme.free", "1.0.0", null)));
        await host.Get<PluginLicenses>().InitializeAsync(CancellationToken.None); // the plugin was added after the start
        await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return (host, plugin);
    }

    private static string License(string server, DateOnly? expires = null, string plugin = PluginId, params string[] features) =>
        LicenseFile.Sign(new LicenseData(plugin, server, "ACME GmbH", DateOnly.FromDateTime(DateTime.Today), expires, features,
            new Dictionary<string, long> { ["sites"] = 3 }), Keys.PrivateKeyPem);

    private static Proto.PluginPackageInfo Package(Proto.PluginPackageList list) => Assert.Single(list.Packages, p => p.Id == PluginId);

    [Fact]
    public async Task LicenseForThisServerAppliesAtOnceAndReachesThePlugin()
    {
        var (host, plugin) = await StartAsync();
        await using var _ = host;
        var list = await host.Plugins.ListPackagesAsync(new Proto.Empty());
        Assert.True(Guid.TryParseExact(list.ServerId, "D", out var id));
        Assert.Equal(id.ToString("D"), await host.Get<ServerSettingsStore>().GetAsync<string>(PluginLicenses.ServerIdKey, CancellationToken.None));
        Assert.Equal(Proto.PluginLicenseStatus.PluginLicenseMissing, Package(list).License.Status);
        Assert.Equal(["reports", "export"], Package(list).License.AvailableFeatures);
        Assert.Null(list.Packages.First(p => p.Id != PluginId).License); // free plugins carry none
        Assert.NotNull(plugin.Context?.License);
        // The server's OADM PRO license, through a view of this plugin run (its handlers end when the plugin stops).
        Assert.IsType<Oadm.Core.Plugins.PluginProLicense>(plugin.Context!.Pro);
        Assert.Equal(host.Get<Oadm.Core.Licensing.ProLicense>().Current, plugin.Context.Pro!.Current);
        Assert.False(plugin.Context!.License!.IsValid);
        var changed = 0;
        plugin.Context.License.Changed += (_, _) => changed++;

        list = await host.Plugins.SetPackageLicenseAsync(new Proto.SetPackageLicenseRequest { Id = PluginId, License = License(list.ServerId, new DateOnly(2099, 12, 31), PluginId, "reports") });

        var state = Package(list).License;
        Assert.Equal(Proto.PluginLicenseStatus.PluginLicenseValid, state.Status);
        Assert.Equal(("ACME GmbH", "2099-12-31", "Licensed to ACME GmbH until 2099-12-31."), (state.Customer, state.Expires, state.Reason));
        Assert.Equal(3, state.Limits["sites"]);
        Assert.Equal(1, changed);
        Assert.True(plugin.Context.License.IsValid);
        Assert.True(plugin.Context.License.Has("reports"));
        Assert.False(plugin.Context.License.Has("export"));
        Assert.Equal(3, plugin.Context.License.Limit("sites"));

        // Stored: a new start of the license service reads it again.
        var again = new PluginLicenses(host.Get<PluginRegistry>(), host.Get<ServerSettingsStore>());
        await again.InitializeAsync(CancellationToken.None);
        Assert.Equal(LicenseStatus.Valid, again.Info(PluginId).Status);
        host.Get<PluginRegistry>().Licenses = host.Get<PluginLicenses>();

        list = await host.Plugins.RemovePackageLicenseAsync(new Proto.PackageIdRequest { Id = PluginId });
        Assert.Equal(Proto.PluginLicenseStatus.PluginLicenseMissing, Package(list).License.Status);
        Assert.False(plugin.Context.License.IsValid);
        Assert.Equal(0, plugin.Context.License.Limit("sites"));

        var audit = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.PluginLicenseEntered && e.Target == "ACME reports");
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.PluginLicenseRemoved && e.Target == "ACME reports");
    }

    [Fact]
    public async Task LicensesThatDoNotFitAreRefusedWithTheReason()
    {
        var (host, _) = await StartAsync();
        await using var __ = host;
        var server = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).ServerId;

        async Task<RpcException> Refused(string id, string text) =>
            await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.SetPackageLicenseAsync(new Proto.SetPackageLicenseRequest { Id = id, License = text }));

        var other = await Refused(PluginId, License("11111111-2222-3333-4444-555555555555"));
        Assert.Equal((StatusCode.InvalidArgument, "The license is for another server (11111111-2222-3333-4444-555555555555)."), (other.StatusCode, other.Status.Detail));
        var expired = await Refused(PluginId, License(server, new DateOnly(2020, 1, 31)));
        Assert.Equal("The license expired on 2020-01-31.", expired.Status.Detail);
        var changed = await Refused(PluginId, Tamper(License(server)));
        Assert.StartsWith("The license is not valid: its signature does not match.", changed.Status.Detail, StringComparison.Ordinal);
        var otherPlugin = await Refused(PluginId, License(server, null, "acme.other"));
        Assert.Equal("The license is for another plugin (acme.other).", otherPlugin.Status.Detail);
        Assert.Equal("The license is not valid: it is not an OADM license key.", (await Refused(PluginId, "hello")).Status.Detail);

        Assert.Equal(StatusCode.NotFound, (await Refused("acme.unknown", License(server))).StatusCode);
        var free = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages.First(p => p.License is null).Id;
        Assert.Equal(StatusCode.FailedPrecondition, (await Refused(free, License(server))).StatusCode);
        Assert.Equal(Proto.PluginLicenseStatus.PluginLicenseMissing, Package(await host.Plugins.ListPackagesAsync(new Proto.Empty())).License.Status);

        // Operators cannot enter or remove licenses.
        var operatorClient = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("op", UserRole.Operator));
        var denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await operatorClient.SetPackageLicenseAsync(new Proto.SetPackageLicenseRequest { Id = PluginId, License = License(server) }));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    [Fact]
    public async Task TrialRunsFromTheFirstStartAndThenEnds()
    {
        var (host, _) = await StartAsync(trialDays: 30);
        await using var __ = host;
        var registry = host.Get<PluginRegistry>();
        var settings = host.Get<ServerSettingsStore>();
        var start = new DateOnly(2026, 10, 1);
        var time = new ManualTime(start);
        await settings.SetJsonAsync(PluginLicenses.TrialsKey, "{\"" + PluginId + "\":\"2026-10-01\"}", CancellationToken.None);

        using var licenses = new PluginLicenses(registry, settings, time);
        await licenses.InitializeAsync(CancellationToken.None);
        var live = licenses.LicenseOf(PluginId)!;
        Assert.Equal((LicenseStatus.Trial, "Trial until 2026-10-30."), (live.Current.Status, live.Current.Reason));
        Assert.True(live.IsValid);
        Assert.True(live.Has("export")); // a trial unlocks everything
        Assert.Null(live.Limit("sites"));

        var changed = 0;
        live.Changed += (_, _) => changed++;
        time.Day = new DateOnly(2026, 10, 30);
        licenses.Refresh();
        Assert.Equal(0, changed); // still the last day
        time.Day = new DateOnly(2026, 10, 31);
        licenses.Refresh();
        Assert.Equal(1, changed);
        Assert.Equal((LicenseStatus.TrialEnded, "The trial ended on 2026-10-30. Enter a license."), (live.Current.Status, live.Current.Reason));
        Assert.False(live.IsValid);

        // A license that expires falls back to "Expired" on the day after its last day.
        var text = LicenseFile.Sign(new LicenseData(PluginId, licenses.ServerId, "ACME GmbH", start, new DateOnly(2026, 11, 15), [], new Dictionary<string, long>()), Keys.PrivateKeyPem);
        await licenses.SetAsync(PluginId, text, CancellationToken.None);
        Assert.Equal(LicenseStatus.Valid, live.Current.Status);
        time.Day = new DateOnly(2026, 11, 16);
        licenses.Refresh();
        Assert.Equal((LicenseStatus.Expired, "The license expired on 2026-11-15."), (live.Current.Status, live.Current.Reason));
        registry.Licenses = host.Get<PluginLicenses>();
    }

    [Fact]
    public void LicenseKeyIsOneLineAndSurvivesBreaksButNoChange()
    {
        const string Server = "7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37";
        var text = License(Server);
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.StartsWith(LicenseFile.Prefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal); // one line
        Assert.Equal(LicenseStatus.Valid, LicenseFile.Verify(" " + text.Insert(30, "\r\n ") + "\n", Keys.PublicKey, PluginId, Server, today).Status); // pasted with breaks
        Assert.Equal("ACME GmbH", LicenseFile.Peek(text)!.Customer);
        Assert.Null(LicenseFile.Peek("hello"));
        Assert.Equal(LicenseStatus.Valid, LicenseFile.Verify(text, Keys.PublicKey, PluginId, Server.ToUpperInvariant(), today).Status);
        Assert.Equal(LicenseStatus.Invalid, LicenseFile.Verify(Tamper(text), Keys.PublicKey, PluginId, Server, today).Status);
        var otherVendor = LicenseFile.CreateKeys();
        Assert.Equal(LicenseStatus.Invalid, LicenseFile.Verify(text, otherVendor.PublicKey, PluginId, Server, today).Status);
        Assert.Equal(LicenseStatus.Invalid, LicenseFile.Verify(text, "not a key", PluginId, Server, today).Status);
    }

    /// <summary>The key with one character of its content changed (still readable, the signature no longer fits).</summary>
    private static string Tamper(string key)
    {
        var at = LicenseFile.Prefix.Length + 10;
        return key[..at] + (key[at] == 'A' ? 'B' : 'A') + key[(at + 1)..];
    }

    /// <summary>A clock that stands on one day (local time = UTC).</summary>
    private sealed class ManualTime(DateOnly day) : TimeProvider
    {
        public DateOnly Day { get; set; } = day;

        public override DateTimeOffset GetUtcNow() => new(Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>A core plugin that keeps its context, to read the license OADM hands it.</summary>
    private sealed class LicensedPlugin(string id = PluginId) : ICorePlugin
    {
        public ICorePluginContext? Context { get; private set; }

        public string Id => id;

        public string DisplayName => "ACME reports";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Context = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
