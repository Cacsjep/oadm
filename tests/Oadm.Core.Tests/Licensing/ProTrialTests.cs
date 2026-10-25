using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Licensing;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Licensing;

namespace Oadm.Core.Tests.Licensing;

/// <summary>The 30-day OADM PRO trial OADM starts itself, once per server (docs/spec/licensing.md "Trial").</summary>
public sealed class ProTrialTests : IAsyncLifetime
{
    private const string Server = "7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37";
    private static readonly (string PrivateKeyPem, string PublicKey) Key = LicenseFile.CreateKeys();
    private static readonly DateOnly Today = new(2026, 10, 22);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-trial-" + Guid.NewGuid().ToString("N"));
    private TestDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Directory.CreateDirectory(_folder);
        _db = await TestDatabase.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;

    private ProLicense NewLicense(TestDatabase? db = null, string? dataDirectory = null) =>
        new((db ?? _db).Get<ServerSettingsStore>(), () => Server, _time, [Key.PublicKey], dataDirectory: dataDirectory ?? Folder("a"));

    private static string Sign(DateOnly? expires = null) =>
        ProLicenseFile.Sign(Key.PrivateKeyPem, Server, "ACME GmbH", "Up to 500 devices", 500, Today, expires ?? new DateOnly(2028, 10, 22), trial: false);

    [Fact]
    public async Task TheTrialRunsThirtyDaysIncludingTheFirstAndStartsOnlyOnce()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        Assert.Equal((ProLicenseStatus.None, true), (license.Current.Status, license.Current.TrialAvailable));
        var changed = 0;
        license.Changed += (_, _) => changed++;

        var info = await license.StartTrialAsync(CancellationToken.None);

        Assert.Equal((ProLicenseStatus.Active, true, false), (info.Status, info.IsTrial, info.TrialAvailable));
        Assert.Equal(Today.AddDays(29), info.Expires);
        Assert.Equal("Trial, until 2026-11-20", info.Reason);
        Assert.Equal(info, license.Current);
        Assert.Equal(1, changed); // the PRO parts and PRO plugins follow at once
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => license.StartTrialAsync(CancellationToken.None));
        Assert.Equal(ProLicenseTexts.TrialUsed, again.Message);

        _time.Advance(TimeSpan.FromDays(29));
        Assert.True(license.Refresh().IsActive); // the 30th day still counts
        Assert.Equal(1, changed);

        _time.Advance(TimeSpan.FromDays(1));
        var ended = license.Refresh();
        Assert.Equal((ProLicenseStatus.Expired, "Trial ended on 2026-11-20.", true, false), (ended.Status, ended.Reason, ended.IsTrial, ended.TrialAvailable));
        Assert.Equal(2, changed);
        Assert.Equal(ProLicenseTexts.TrialUsed, (await Assert.ThrowsAsync<InvalidOperationException>(() => license.StartTrialAsync(CancellationToken.None))).Message);

        // A restart reads it again.
        using var restarted = NewLicense();
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal(ended, restarted.Current);
    }

    [Fact]
    public async Task AnImportedLicenseWinsOverTheTrial()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        await license.StartTrialAsync(CancellationToken.None);

        var bought = await license.ImportAsync(Sign(), CancellationToken.None);
        Assert.Equal((true, false, "Up to 500 devices, until 2028-10-22"), (bought.IsActive, bought.IsTrial, license.Current.Reason));
        Assert.True(license.HasKey);

        // Without the key the trial that still runs is back.
        var removed = await license.RemoveAsync(CancellationToken.None);
        Assert.Equal((ProLicenseStatus.Active, true), (removed.Status, removed.IsTrial));
        Assert.False(license.HasKey);

        // An expired key never hides a running trial; after the trial the key says why it is not active.
        await license.ImportAsync(Sign(expires: Today.AddDays(2)), CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(3));
        Assert.Equal((ProLicenseStatus.Active, true), (license.Refresh().Status, license.Current.IsTrial));
        _time.Advance(TimeSpan.FromDays(30));
        Assert.Equal((ProLicenseStatus.Expired, "Expired on 2026-10-24."), (license.Refresh().Status, license.Current.Reason));
    }

    [Fact]
    public async Task WhileALicenseIsActiveTheTrialIsNotOfferedAndNotStarted()
    {
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);
        await license.ImportAsync(Sign(), CancellationToken.None);
        Assert.False(license.Current.TrialAvailable);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => license.StartTrialAsync(CancellationToken.None));

        Assert.Equal(ProLicenseTexts.AlreadyActive, refused.Message);
        Assert.Null(await _db.Get<ServerSettingsStore>().GetJsonAsync(ProLicense.TrialSettingKey, CancellationToken.None));
        await license.RemoveAsync(CancellationToken.None);
        Assert.True(license.Current.TrialAvailable); // still unused
    }

    [Fact]
    public async Task ABackupRestoredOnAnotherComputerCarriesNoRunningTrial()
    {
        var first = Folder("first");
        using (var license = NewLicense(dataDirectory: first))
        {
            await license.InitializeAsync(CancellationToken.None);
            await license.StartTrialAsync(CancellationToken.None);
        }

        // The same database (the backup) on another computer: its data folder has another install file.
        using var elsewhere = NewLicense(dataDirectory: Folder("second"));
        await elsewhere.InitializeAsync(CancellationToken.None);
        Assert.Equal((ProLicenseStatus.Expired, ProLicenseTexts.TrialElsewhere, false), (elsewhere.Current.Status, elsewhere.Current.Reason, elsewhere.Current.TrialAvailable));
        Assert.Equal(ProLicenseTexts.TrialUsed, (await Assert.ThrowsAsync<InvalidOperationException>(() => elsewhere.StartTrialAsync(CancellationToken.None))).Message);

        // A license key still works there.
        Assert.True((await elsewhere.ImportAsync(Sign(), CancellationToken.None)).IsActive);
    }

    [Fact]
    public async Task ABackupFromBeforeTheTrialRestoredOnTheSameComputerDoesNotStartItAgain()
    {
        var folder = Folder("same");
        using (var license = NewLicense(dataDirectory: folder))
        {
            await license.InitializeAsync(CancellationToken.None);
            await license.StartTrialAsync(CancellationToken.None);
        }

        // An older database without the trial, on the computer that ran it: the install file still knows.
        await using var older = await TestDatabase.CreateAsync();
        using var restored = NewLicense(older, folder);
        await restored.InitializeAsync(CancellationToken.None);

        Assert.Equal((ProLicenseStatus.Active, true, false), (restored.Current.Status, restored.Current.IsTrial, restored.Current.TrialAvailable));
        Assert.Equal(ProLicenseTexts.TrialUsed, (await Assert.ThrowsAsync<InvalidOperationException>(() => restored.StartTrialAsync(CancellationToken.None))).Message);
    }

    [Fact]
    public async Task AnUnreadableTrialRecordCountsAsUsed()
    {
        await _db.Get<ServerSettingsStore>().SetJsonAsync(ProLicense.TrialSettingKey, "\"not a record\"", CancellationToken.None);
        using var license = NewLicense();
        await license.InitializeAsync(CancellationToken.None);

        Assert.False(license.Current.IsActive);
        Assert.False(license.Current.TrialAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => license.StartTrialAsync(CancellationToken.None));
    }
}
