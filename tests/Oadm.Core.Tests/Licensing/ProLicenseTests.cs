using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Licensing;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Licensing;

namespace Oadm.Core.Tests.Licensing;

/// <summary>The OADM PRO license: the key format, the checks and the stored, live license (docs/spec/licensing.md).</summary>
public sealed class ProLicenseTests : IAsyncLifetime
{
    private const string Server = "7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37";
    private static readonly (string PrivateKeyPem, string PublicKey) Key = LicenseFile.CreateKeys();
    private static readonly (string PrivateKeyPem, string PublicKey) OtherKey = LicenseFile.CreateKeys();
    private static readonly DateOnly Today = new(2026, 10, 22);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private TestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Sign(string server = Server, DateOnly? expires = null, bool trial = false, long? devices = 500, string? key = null) =>
        ProLicenseFile.Sign(key ?? Key.PrivateKeyPem, server, "ACME GmbH", "Up to 500 devices", devices, Today, expires ?? new DateOnly(2028, 10, 22), trial);

    private ProLicense NewLicense(params string[] keys) =>
        new(_db.Get<ServerSettingsStore>(), () => Server, _time, keys.Length == 0 ? [Key.PublicKey] : keys);

    [Fact]
    public void AValidKeySaysPlanDevicesLastDayAndTrial()
    {
        var info = ProLicenseFile.Check(Sign(), [Key.PublicKey], Server, Today);

        Assert.Equal(ProLicenseStatus.Active, info.Status);
        Assert.True(info.IsActive);
        Assert.Equal(("ACME GmbH", "Up to 500 devices", 500L, new DateOnly(2028, 10, 22), false), (info.Customer, info.Plan, info.MaxDevices, info.Expires, info.IsTrial));
        Assert.Equal("Up to 500 devices, until 2028-10-22", info.Reason);

        var trial = ProLicenseFile.Check(Sign(trial: true, devices: null, expires: Today.AddDays(29)), [Key.PublicKey], Server.ToUpperInvariant(), Today);
        Assert.Equal((ProLicenseStatus.Active, true, (long?)null), (trial.Status, trial.IsTrial, trial.MaxDevices));
        Assert.Equal("Trial, until 2026-11-20", trial.Reason);
    }

    [Fact]
    public void KeysThatDoNotFitAreRefused()
    {
        Assert.Equal(ProLicenseStatus.WrongServer, ProLicenseFile.Check(Sign(server: "11111111-2222-3333-4444-555555555555"), [Key.PublicKey], Server, Today).Status);
        Assert.Equal(ProLicenseTexts.WrongServer, ProLicenseFile.Check(Sign(server: "other"), [Key.PublicKey], Server, Today).Reason);

        var expired = ProLicenseFile.Check(Sign(expires: Today.AddDays(-1)), [Key.PublicKey], Server, Today);
        Assert.Equal((ProLicenseStatus.Expired, "Expired on 2026-10-21."), (expired.Status, expired.Reason));
        Assert.Equal(ProLicenseStatus.Active, ProLicenseFile.Check(Sign(expires: Today), [Key.PublicKey], Server, Today).Status); // the last day counts

        // Signed with a key OADM does not know, changed after signing, for a plugin instead of PRO, or no key at all.
        Assert.Equal(ProLicenseStatus.Invalid, ProLicenseFile.Check(Sign(key: OtherKey.PrivateKeyPem), [Key.PublicKey], Server, Today).Status);
        Assert.Equal(ProLicenseStatus.Invalid, ProLicenseFile.Check(Tamper(Sign()), [Key.PublicKey], Server, Today).Status);
        var plugin = LicenseFile.Sign(new LicenseData("acme.reports", Server, "ACME GmbH", Today, null, [], new Dictionary<string, long>()), Key.PrivateKeyPem);
        Assert.Equal(ProLicenseStatus.Invalid, ProLicenseFile.Check(plugin, [Key.PublicKey], Server, Today).Status);
        var invalid = ProLicenseFile.Check("hello", [Key.PublicKey], Server, Today);
        Assert.Equal((ProLicenseStatus.Invalid, ProLicenseTexts.NotValid), (invalid.Status, invalid.Reason));
    }

    [Fact]
    public void ANewSigningKeyKeepsTheOldLicensesValid()
    {
        Assert.Equal(ProLicenseStatus.Active, ProLicenseFile.Check(Sign(), [OtherKey.PublicKey, Key.PublicKey], Server, Today).Status);
        Assert.Equal(ProLicenseStatus.Active, ProLicenseFile.Check(Sign(key: OtherKey.PrivateKeyPem), [Key.PublicKey, OtherKey.PublicKey], Server, Today).Status);
    }

    [Fact]
    public void TheProductionKeysAreReadable()
    {
        Assert.NotEmpty(ProLicenseKeys.All);
        Assert.Equal(ProLicenseKeys.All.Count, ProLicenseKeys.All.Select(k => k.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var (_, publicKey) in ProLicenseKeys.All)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            Assert.Equal(256, key.KeySize);
        }
    }

    [Fact]
    public async Task ImportStoresTheKeyAndAStartReadsItAgain()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        Assert.Equal(ProLicenseStatus.None, license.Current.Status);
        Assert.Equal(ProLicenseTexts.NoLicense, license.Current.Reason);
        var changed = 0;
        license.Changed += (_, _) => changed++;

        var info = await license.ImportAsync(Sign(), CancellationToken.None);

        Assert.True(info.IsActive);
        Assert.Equal(info, license.Current);
        Assert.Equal(1, changed);
        using var again = NewLicense();
        await again.InitializeAsync(CancellationToken.None);
        Assert.Equal(info, again.Current);

        await license.RemoveAsync(CancellationToken.None);
        Assert.Equal(ProLicenseStatus.None, license.Current.Status);
        Assert.Equal(2, changed);
        Assert.Null(await _db.Get<ServerSettingsStore>().GetJsonAsync(ProLicense.SettingKey, CancellationToken.None));
    }

    [Fact]
    public async Task ImportRefusesWhatIsNotActiveAndKeepsTheOldLicense()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        await license.ImportAsync(Sign(), CancellationToken.None);

        var wrong = await Assert.ThrowsAsync<ArgumentException>(() => license.ImportAsync(Sign(server: "other"), CancellationToken.None));
        Assert.Equal(ProLicenseTexts.WrongServer, wrong.Message);
        var expired = await Assert.ThrowsAsync<ArgumentException>(() => license.ImportAsync(Sign(expires: Today.AddDays(-3)), CancellationToken.None));
        Assert.Equal("Expired on 2026-10-19.", expired.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => license.ImportAsync(Tamper(Sign()), CancellationToken.None));

        Assert.True(license.Current.IsActive);
    }

    [Fact]
    public async Task ALicenseRunsOutAfterItsLastDay()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        await license.ImportAsync(Sign(expires: Today.AddDays(1), trial: true), CancellationToken.None);
        var changed = 0;
        license.Changed += (_, _) => changed++;

        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(license.Refresh().IsActive); // still its last day
        Assert.Equal(0, changed);

        _time.Advance(TimeSpan.FromDays(1));
        var info = license.Refresh();

        Assert.Equal(ProLicenseStatus.Expired, info.Status);
        Assert.Equal("Expired on 2026-10-23.", info.Reason);
        Assert.Equal(1, changed);
    }

    /// <summary>Changes the customer inside the signed content: the signature no longer matches.</summary>
    [Fact]
    public async Task AFailingHandlerNeitherStopsTheOthersNorTheImport()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        var reached = 0;
        license.Changed += (_, _) => throw new InvalidOperationException("a broken plugin");
        license.Changed += (_, _) => reached++;

        var info = await license.ImportAsync(Sign(), CancellationToken.None);

        Assert.True(info.IsActive);
        Assert.Equal(1, reached);
        Assert.True(license.Refresh().IsActive);
    }

    [Fact]
    public async Task TheHandlersNeverSeeTheAdministratorWhoImportedOrRemoved()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        using var view = new Oadm.Core.Plugins.PluginProLicense(license);
        var seen = new List<Oadm.Core.Auth.AuthenticatedCaller?>();
        license.Changed += (_, _) => seen.Add(Oadm.Core.Auth.CallerContext.Current);
        view.Changed += (_, _) => seen.Add(Oadm.Core.Auth.CallerContext.Current);
        var admin = new Oadm.Core.Auth.AuthenticatedCaller(Guid.NewGuid(), "alice", Oadm.Core.Auth.BuiltInRoles.AdministratorId,
            Oadm.Core.Auth.PermissionSet.All, "local", "pc", "hash");

        using (Oadm.Core.Auth.CallerContext.Use(admin))
        {
            await license.ImportAsync(Sign(), CancellationToken.None);
            await license.RemoveAsync(CancellationToken.None);
            Assert.Same(admin, Oadm.Core.Auth.CallerContext.Current);
        }

        Assert.Equal(4, seen.Count);
        Assert.All(seen, Assert.Null);
    }

    [Fact]
    public async Task AStoppedPluginsHandlersAreDropped()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        var view = new Oadm.Core.Plugins.PluginProLicense(license);
        var calls = 0;
        view.Changed += (_, _) => calls++;
        await license.ImportAsync(Sign(), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(view.Current.IsActive);

        view.Dispose();
        await license.RemoveAsync(CancellationToken.None);
        Assert.Equal(1, calls);
    }

    private static string Tamper(string key)
    {
        var parts = key[LicenseFile.Prefix.Length..].Split('.');
        var content = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])).Replace("ACME GmbH", "ACME Corp", StringComparison.Ordinal);
        return LicenseFile.Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(content)) + "." + parts[1];
    }
}
