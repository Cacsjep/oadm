using System.IO.Compression;
using System.Text;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Acap.Tests;
using Oadm.Plugins.AppManagement.Client;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>The page's Install applications and Install licenses dialogs: the shared checks per row and the run they send.</summary>
public sealed class DialogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-app-dialogs-" + Guid.NewGuid().ToString("N"));

    public DialogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A virus scanner may still hold a file it was just written: the temp folder stays.
        }
    }

    private string Eap(string name, string manifest)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, EapBuilder.FromManifest(manifest));
        return path;
    }

    private static DeviceApps Device(string address, string architecture = "aarch64", string firmware = "12.11.77", InstalledApplication? app = null) =>
        Samples.Device(address, app is null ? [] : [app]) with { Architecture = architecture, Firmware = firmware };

    [Fact]
    public async Task Install_checks_every_device_with_the_applications_plugins_own_rules()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var fresh = Device("10.0.0.1");
        var other = Device("10.0.0.2", "armv7hf");
        var newer = Device("10.0.0.3", app: Samples.App("hello", "Hello World", "2.0.0", vendor: "Acme"));
        var failed = Samples.Failed("10.0.0.4", "Timeout after 15 s");
        var signedOnly = Device("10.0.0.5", firmware: "13.0.12");
        var notOk = Samples.Failed("10.0.0.6", AppManagementInfo.NotChecked("Unreachable"), notChecked: true);
        var vm = new InstallAppsViewModel(ctx, [fresh, other, newer, failed, signedOnly, notOk]);

        // Nothing chosen: every device listed, Install off and says why.
        Assert.Equal(6, vm.Rows.Count);
        Assert.All(vm.Rows.Take(3), r => Assert.Equal(string.Empty, r.Verdict));
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal(InstallAppsViewModel.ChooseFilesFirst, vm.InstallBlockedReason);

        await vm.LoadFilesAsync([Eap("hello_aarch64.eap", EapBuilder.Manifest())]);

        var rows = vm.Rows.ToDictionary(r => r.DeviceId);
        Assert.Equal("New install", rows[fresh.DeviceId].Verdict);
        Assert.True(rows[fresh.DeviceId].IsOk);
        Assert.Equal("Not compatible", rows[other.DeviceId].Verdict);
        Assert.Equal("The package is built for aarch64, the device is armv7hf.", rows[other.DeviceId].Details);
        Assert.Equal("Not compatible", rows[newer.DeviceId].Verdict);
        Assert.Contains("downgrade option", rows[newer.DeviceId].Details, StringComparison.Ordinal);
        Assert.Equal(("Timeout after 15 s", true), (rows[failed.DeviceId].Verdict, rows[failed.DeviceId].IsError));
        Assert.Equal((AppManagementInfo.NotChecked("Unreachable"), true), (rows[notOk.DeviceId].Verdict, rows[notOk.DeviceId].IsWarning));
        Assert.True(rows[signedOnly.DeviceId].IsOk);
        Assert.True(rows[signedOnly.DeviceId].IsWarning); // AXIS OS 13 installs signed applications only
        Assert.Contains("only signed applications", rows[signedOnly.DeviceId].Details, StringComparison.Ordinal);

        // The same verdict as the Applications dialog's preview for the same package and device.
        var package = Assert.Single(vm.Packages);
        var shared = AcapCompatibility.Preview([package], new AcapDeviceFacts { Architecture = "armv7hf", FirmwareVersion = "12.11.77" }, [], false);
        Assert.Equal((shared.Verdict, shared.Details), (rows[other.DeviceId].Verdict, rows[other.DeviceId].Details));

        Assert.Equal(2, vm.FitCount);
        Assert.Equal("2 of 6 devices can install.", vm.Summary);
        Assert.True(vm.ShowDowngrade); // the role may downgrade: always shown
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.Null(vm.InstallBlockedReason);
        Assert.Equal("Hello World 1.2.0  ·  aarch64  ·  " + Oadm.Sdk.Client.FileSizeText.Format(new FileInfo(Path.Combine(_dir, "hello_aarch64.eap")).Length), vm.FileDetails);
    }

    [Fact]
    public async Task Install_uploads_the_files_and_asks_the_server_part_to_install_on_the_devices_that_fit()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var arm64 = Device("10.0.0.1");
        var arm32 = Device("10.0.0.2", "armv7hf", app: Samples.App("hello", "Hello World", "1.0.0", vendor: "Acme"));
        var mips = Device("10.0.0.3", "mips");
        var vm = new InstallAppsViewModel(ctx, [arm64, arm32, mips]);
        var closed = (bool?)null;
        vm.CloseRequested += (_, started) => closed = started;

        // One file per architecture: each device gets the one of its architecture, a device without one is skipped.
        await vm.LoadFilesAsync([Eap("hello_aarch64.eap", EapBuilder.Manifest()), Eap("hello_armv7hf.eap", EapBuilder.Manifest(architecture: "armv7hf"))]);
        var rows = vm.Rows.ToDictionary(r => r.DeviceId);
        Assert.Equal("2 files", vm.FileName);
        Assert.Equal(("New install", "aarch64 build"), (rows[arm64.DeviceId].Verdict, rows[arm64.DeviceId].Details));
        Assert.Equal(("Upgrade from 1.0.0", "armv7hf build"), (rows[arm32.DeviceId].Verdict, rows[arm32.DeviceId].Details));
        Assert.Equal(("No package for mips.", null), (rows[mips.DeviceId].Verdict, rows[mips.DeviceId].Details));
        Assert.False(rows[mips.DeviceId].CanInstall);

        vm.StartAfterInstall = false;
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(2, ctx.Uploads.Count);
        var request = AppManagementJson.Deserialize<RunRequest>(ctx.LastPayloadOf(AppManagementMethods.Run));
        Assert.Equal(AcapAction.Install, request.Action);
        var item = Assert.Single(request.Items);
        Assert.Equal(("hello", "Hello World"), (item.Application, item.DisplayName));
        Assert.Equal([arm64.DeviceId, arm32.DeviceId], item.DeviceIds);
        var install = Assert.IsType<AppInstall>(request.Install);
        Assert.Equal([("file-1", "aarch64"), ("file-2", "armv7hf")], install.Files.Select(f => (f.FileId, f.Architecture!)));
        Assert.Equal("1.2.0", install.Version);
        Assert.False(install.StartAfterInstall);
        Assert.False(install.AllowDowngrade);
        Assert.Null(install.Kind); // a new install and an upgrade
        Assert.Equal(((Oadm.Sdk.Client.ICorePluginClientContext)ctx).OwnerName, request.Owner);
        Assert.True(closed);
        Assert.Empty(ctx.TaskRuns); // never the Applications plugin's own dialog
    }

    [Fact]
    public async Task Files_that_do_not_belong_together_are_refused_before_any_upload()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var vm = new InstallAppsViewModel(ctx, [Device("10.0.0.1")]);

        await vm.LoadFilesAsync([Eap("a.eap", EapBuilder.Manifest()), Eap("b.eap", EapBuilder.Manifest(appName: "other", friendlyName: "Other", architecture: "armv7hf"))]);

        Assert.Equal("b.eap is another application (Other). Choose the files of one application.", vm.FileError);
        Assert.Equal(vm.FileError, vm.InstallBlockedReason);
        Assert.Empty(vm.Packages);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Empty(ctx.Uploads);
    }

    [Fact]
    public async Task Allow_downgrade_checks_again_and_needs_the_right()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var newer = Device("10.0.0.1", app: Samples.App("hello", "Hello World", "2.0.0", vendor: "Acme"));
        var vm = new InstallAppsViewModel(ctx, [newer]);
        await vm.LoadFilesAsync([Eap("hello.eap", EapBuilder.Manifest())]);
        Assert.Equal(0, vm.FitCount);
        Assert.Equal(InstallAppsViewModel.NothingFits, vm.InstallBlockedReason);

        vm.AllowDowngrade = true;
        await Eventually.TrueAsync(() => vm.FitCount == 1, "the downgrade fits");
        var row = Assert.Single(vm.Rows);
        Assert.Equal("Downgrade from 2.0.0", row.Verdict);
        Assert.True(row.IsWarning);

        await vm.InstallCommand.ExecuteAsync(null);
        var request = AppManagementJson.Deserialize<RunRequest>(ctx.LastPayloadOf(AppManagementMethods.Run));
        Assert.True(request.Install!.AllowDowngrade);
        Assert.Equal(InstallKind.Downgrade, request.Install.Kind);

        // A role without Downgrade applications never sees the option.
        var limited = new FakePageContext().WithEmptyServer();
        limited.Denied.Add(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Downgrade));
        var denied = new InstallAppsViewModel(limited, [newer]);
        await denied.LoadFilesAsync([Eap("hello2.eap", EapBuilder.Manifest())]);
        Assert.True(denied.HasDowngrade);
        Assert.False(denied.ShowDowngrade);
    }

    [Fact]
    public async Task A_failed_upload_or_an_older_host_says_so_and_keeps_the_dialog_open()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var vm = new InstallAppsViewModel(ctx, [Device("10.0.0.1")]);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.LoadFilesAsync([Eap("hello.eap", EapBuilder.Manifest())]);

        ctx.UploadFailure = new NotSupportedException();
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(InstallAppsViewModel.NoUpload, vm.ErrorText);

        ctx.UploadFailure = new IOException("The server closed the connection.");
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal("The server closed the connection.", vm.ErrorText);
        Assert.False(closed);
        Assert.Equal(0, ctx.CallsOf(AppManagementMethods.Run));
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    /// <summary>A key in the format of a real one (as the Applications plugin's tests use), with a made-up signature.</summary>
    private static string Key(string mac, string appId = "415090", string expires = "2027-09-18") => $"""
        <LicenseKey>
            <FormatVersion>1</FormatVersion>
            <ApplicationID>{appId}</ApplicationID>
            <MinimumMajorVersion>-1</MinimumMajorVersion>
            <MinimumMinorVersion>-1</MinimumMinorVersion>
            <MaximumMajorVersion>-1</MaximumMajorVersion>
            <MaximumMinorVersion>-1</MaximumMinorVersion>
            <ExpirationDate>{expires}</ExpirationDate>
            <DeviceID>{mac}</DeviceID>
            <SignatureKeyID>1</SignatureKeyID>
            <Signature>bm90IGEgcmVhbCBzaWduYXR1cmU=</Signature>
        </LicenseKey>
        """;

    private static InstalledApplication Lpv(string license = "Missing") =>
        Samples.App("lpv", "License Plate Verifier", "2.10", license: license, vendor: "Acme") with { ApplicationId = "415090" };

    [Fact]
    public async Task Install_licenses_matches_each_key_by_device_and_application_and_sends_the_keys_per_application()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        using var page = new ApplicationsViewModel(ctx, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero))) { HideAxisApps = false };
        var found = Samples.Device("10.0.0.1", Lpv(), Samples.App("aoa", "Object Analytics", license: "None"));
        var expired = Samples.Device("10.0.0.2", Lpv());
        var none = Samples.Device("10.0.0.3", Lpv());
        var otherApp = Samples.Device("10.0.0.4", Lpv());
        page.ApplyRows([found, expired, none, otherApp]);
        page.SetDeviceSelection(page.DeviceRows.Where(r => r.AppKey is not null));
        var vm = page.LicensesDialog()!;
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(InstallLicensesViewModel.ChooseKeysFirst, vm.InstallBlockedReason);

        // One key file, and a ZIP with the others (the Applications plugin's reader).
        var single = Path.Combine(_dir, "found.key");
        File.WriteAllText(single, Key(found.Mac!));
        var zip = Path.Combine(_dir, "keys.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("expired.xml", Key(expired.Mac!, expires: "2026-01-31")), ("other.xml", Key(otherApp.Mac!, appId: "999")), ("junk.txt", "not a key") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }

        await vm.LoadFilesAsync([single, zip]);

        string Verdict(DeviceApps device, string app = "lpv") => vm.Rows.Single(r => r.DeviceId == device.DeviceId && r.Row.AppKey == app).Verdict;
        Assert.Equal("Key found", Verdict(found));
        Assert.Equal(LicenseTargetRow.NoLicenseNeeded, Verdict(found, "aoa"));
        Assert.Equal("Key expired", Verdict(expired));
        Assert.Equal("No key", Verdict(none));
        Assert.Equal(LicenseKeyMatcher.NoKeyText, vm.Rows.Single(r => r.DeviceId == none.DeviceId).Details);
        Assert.Equal("Other application", Verdict(otherApp));
        Assert.Equal("2 files, 3 keys", vm.FileDetails);
        Assert.Contains("junk.txt", vm.ProblemText, StringComparison.Ordinal);
        Assert.Equal("Keys for 1 of 5 applications.", vm.Summary);

        // The same verdict as the Applications dialog for the same keys and device.
        var keys = LicenseKeySet.Read([("k", Encoding.UTF8.GetBytes(Key(found.Mac!)))]);
        Assert.Equal("Key found", new LicenseKeyMatcher(keys).Match(found.Mac, "415090", Samples.Today).Text);

        await vm.InstallCommand.ExecuteAsync(null);
        var request = AppManagementJson.Deserialize<RunRequest>(ctx.LastPayloadOf(AppManagementMethods.Run));
        Assert.Equal(AcapAction.InstallLicense, request.Action);
        var item = Assert.Single(request.Items);
        Assert.Equal(("lpv", "License Plate Verifier", "415090"), (item.Application, item.DisplayName, item.LicenseApplicationId));
        Assert.Equal([found.DeviceId], item.DeviceIds);
        var key = Assert.Single(item.LicenseKeys!);
        Assert.Equal(found.Mac, key.DeviceId);
        Assert.Empty(ctx.TaskRuns);
    }

    [Fact]
    public void Install_licenses_without_a_fitting_key_stays_off()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var rows = AppRow.For(Samples.Device("10.0.0.1", Lpv()), Samples.Today).Apps;
        var vm = new InstallLicensesViewModel(ctx, rows, () => Samples.Today);

        vm.ApplyKeys(LicenseKeySet.Read([("k", Encoding.UTF8.GetBytes(Key("ACCC8E000001")))]), 1, "k.key");

        Assert.Equal("No key", Assert.Single(vm.Rows).Verdict);
        Assert.Equal(InstallLicensesViewModel.NoKeyFits, vm.InstallBlockedReason);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }
}
