using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>License key files: reading, matching to the devices, the task steps and the dialog.</summary>
public sealed class LicenseKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-acap-keys-" + Guid.NewGuid().ToString("N"));

    public LicenseKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateOnly Today = new(2026, 10, 13);

    /// <summary>A key in the format of a real one (AXIS Missing Features), with a made-up signature.</summary>
    public static string Key(string mac = "B8A44F631339", string appId = "415090", string expires = "2027-09-18") => $"""
        <LicenseKey>
            <Info></Info>
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

    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);

    private static string KeyFor(int major) => Key().Replace("<MinimumMajorVersion>-1<", $"<MinimumMajorVersion>{major}<", StringComparison.Ordinal)
        .Replace("<MaximumMajorVersion>-1<", $"<MaximumMajorVersion>{major}<", StringComparison.Ordinal);

    [Fact]
    public void Keys_of_two_major_versions_both_stay_and_the_fitting_one_is_chosen()
    {
        var set = LicenseKeySet.Read([("v1.xml", System.Text.Encoding.UTF8.GetBytes(KeyFor(1))), ("v2.xml", System.Text.Encoding.UTF8.GetBytes(KeyFor(2)))]);
        Assert.Equal(2, set.Keys.Count);
        Assert.Equal(2, set.AllByDevice("415090")["B8A44F631339"].Count);

        var payload = new AcapPayload { LicenseKeys = [.. set.Keys.Select(k => new LicenseKeyEntry(k.DeviceId, k.Xml))] };
        Assert.Equal(2, payload.KeysFor("B8A44F631339").Count);
        Assert.True(set.Keys.Any(k => k.Fits("2.3.0")) && set.Keys.Any(k => !k.Fits("2.3.0")));
    }

    [Fact]
    public void Without_an_application_id_on_the_device_the_package_id_is_checked()
    {
        var key = LicenseKey.Parse(Key(appId: "111"));
        var app = new InstalledApplication { Name = "MissingFeatures", Version = "3.8.0", ApplicationId = "0" };
        Assert.Null(AcapTaskPlugin.KeyProblem(key, "B8A44F631339", app, Today)); // nothing to compare: the MAC check holds
        Assert.StartsWith("The key is for another application", AcapTaskPlugin.KeyProblem(key, "B8A44F631339", app, Today, expectedApplicationId: "415090"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_file_names_its_device_application_and_end_date()
    {
        var key = LicenseKey.Parse(Key(mac: "b8:a4:4f:63:13:39"));
        Assert.Equal("B8A44F631339", key.DeviceId);
        Assert.Equal("415090", key.ApplicationId);
        Assert.Equal(new DateOnly(2027, 9, 18), key.ExpirationDate);
        Assert.Equal("valid until 2027-09-18", key.ValidityText);
        Assert.False(key.IsExpired(Today));
        Assert.True(LicenseKey.Parse(Key(expires: "2026-09-18")).IsExpired(Today));
        Assert.Null(LicenseKey.Parse(Key(expires: "-1")).ExpirationDate);

        Assert.Throws<FormatException>(() => LicenseKey.Parse("not xml"));
        Assert.Throws<FormatException>(() => LicenseKey.Parse("<Other/>"));
        Assert.Throws<FormatException>(() => LicenseKey.Parse(Key(mac: "nope")));
        Assert.Throws<FormatException>(() => LicenseKey.Parse(Key(expires: "18.09.2027")));
        Assert.Throws<FormatException>(() => LicenseKey.Parse("<LicenseKey><!DOCTYPE x [<!ENTITY a 'b'>]></LicenseKey>"));
        Assert.Throws<FormatException>(() => LicenseKey.Parse(Key() + new string(' ', LicenseKey.MaxBytes)));
    }

    [Fact]
    public void Files_and_zips_are_read_with_the_latest_key_per_device_and_problems_named()
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "keys/B8A44F000001.key", Key(mac: "B8A44F000001"));
            Write(zip, "keys/readme.txt", "hello");
        }

        var set = LicenseKeySet.Read(
        [
            ("old.key", Encoding.UTF8.GetBytes(Key(expires: "2026-12-31"))),
            ("new.key", Encoding.UTF8.GetBytes(Key(expires: "2027-09-18"))),
            ("bulk.zip", zipStream.ToArray()),
            ("broken.zip", [(byte)'P', (byte)'K', 3, 4, 1, 2]),
        ]);

        Assert.Equal(["B8A44F000001", "B8A44F631339"], set.Keys.Select(k => k.DeviceId));
        Assert.Equal(new DateOnly(2027, 9, 18), set.For("B8:A4:4F:63:13:39", "415090")!.ExpirationDate);
        Assert.Null(set.For("B8A44F631339", "999"));
        Assert.Equal(["bulk.zip: keys/readme.txt", "broken.zip"], set.Problems.Select(p => p.FileName));
        Assert.Equal("The ZIP file cannot be read.", set.Problems[1].Reason);
    }

    [Fact]
    public void A_zip_entry_that_unpacks_larger_than_a_key_is_not_read()
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "big.key", new string('a', 2_000_000));
        }

        var set = LicenseKeySet.Read([("bulk.zip", zipStream.ToArray())]);
        Assert.Empty(set.Keys);
        Assert.Equal("This is not a license key file (too large).", Assert.Single(set.Problems).Reason);
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static Task Run(RecordingContext ctx, string payload) =>
        StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    private static string LicensePayload(params LicenseKeyEntry[] keys) =>
        new AcapPayload { Action = AcapAction.InstallLicense, Application = "ax_msf", AppDisplayName = "Missing Features", LicenseKeys = keys }.ToJson();

    [Fact]
    public async Task Install_license_sends_the_key_of_this_device_and_verifies_it()
    {
        var device = new FakeAcapDevice();
        device.Add("ax_msf", "1.0", applicationId: "415090");
        var ctx = new RecordingContext(device);
        var payload = LicensePayload(new LicenseKeyEntry("B8A44F000001", Key(mac: "B8A44F000001")), new LicenseKeyEntry("B8A44F631339", Key()));

        await Run(ctx, payload);

        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Check license key: Done", "Install license key: Done", "Verify license: Done", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal(Key(), device.LicenseKeys["ax_msf"]);
        Assert.Equal("ax_msf nice: license Valid until 2027-09-18", StepRun.Detail(ctx.Steps, "Verify license"));
        Assert.Equal("Install license of Missing Features", Plugin.GetTaskName(payload));
        Assert.Equal([AcapPermissions.License], Plugin.RequiredPermissions(payload));
        Assert.DoesNotContain(ctx.Logs, l => l.Message.Contains("Signature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_without_a_key_is_done_with_a_warning_and_nothing_is_sent()
    {
        var device = new FakeAcapDevice();
        device.Add("ax_msf", "1.0", applicationId: "415090");
        var ctx = new RecordingContext(device);

        await Run(ctx, LicensePayload(new LicenseKeyEntry("B8A44F000001", Key(mac: "B8A44F000001"))));

        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Check license key: Warning", "Install license key: Skipped", "Verify license: Skipped", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal("No license key for this device. Nothing was sent.", StepRun.Detail(ctx.Steps, "Check license key"));
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void A_key_for_other_versions_of_the_application_is_named_before_sending()
    {
        var xml = Key().Replace("<MinimumMajorVersion>-1</MinimumMajorVersion>", "<MinimumMajorVersion>2</MinimumMajorVersion>", StringComparison.Ordinal)
            .Replace("<MaximumMajorVersion>-1</MaximumMajorVersion>", "<MaximumMajorVersion>2</MaximumMajorVersion>", StringComparison.Ordinal);
        var key = LicenseKey.Parse(xml);
        Assert.True(key.Fits("2.9.1"));
        Assert.False(key.Fits("3.0.0"));
        Assert.Equal("The key is for versions 2.0 to 2.x of Missing Features; 3.0.0 is installed.",
            AcapTaskPlugin.KeyProblem(key, "B8A44F631339", new InstalledApplication { Name = "ax_msf", NiceName = "Missing Features", Version = "3.0.0", ApplicationId = "415090" }, Today));
    }

    [Theory]
    [InlineData("415090", "2026-09-18", "The key expired on 2026-09-18. Nothing was changed.")]
    [InlineData("333330", "2027-09-18", "The key is for another application (ID 333330; ax_msf nice has ID 415090). Nothing was changed.")]
    public async Task A_key_that_cannot_work_fails_before_anything_is_sent(string appId, string expires, string message)
    {
        var device = new FakeAcapDevice();
        device.Add("ax_msf", "1.0", applicationId: "415090");
        var ctx = new RecordingContext(device);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, LicensePayload(new LicenseKeyEntry("B8A44F631339", Key(appId: appId, expires: expires)))));

        Assert.Equal(message, ex.Message);
        Assert.Empty(device.Writes);
        Assert.Equal("Check license key: Failed", StepRun.Lines(ctx.Steps)[2]);
    }

    [Fact]
    public async Task A_missing_application_or_a_refused_key_fails_with_the_reason()
    {
        var device = new FakeAcapDevice();
        var ctx = new RecordingContext(device);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, LicensePayload(new LicenseKeyEntry("B8A44F631339", Key()))));
        Assert.Equal("Missing Features is not installed on this device. Nothing was changed.", missing.Message);

        device.Add("ax_msf", "1.0", applicationId: "415090");
        device.LicenseReply = "Error: 21";
        var refused = await Assert.ThrowsAsync<AcapDeviceException>(() => Run(ctx, LicensePayload(new LicenseKeyEntry("B8A44F631339", Key()))));
        Assert.Equal("The license key file is not valid. (error 21)", refused.Message);
        Assert.Equal("Install license key: Failed", StepRun.Lines(ctx.Steps)[3]);
    }

    [Fact]
    public async Task Install_with_a_key_licenses_the_application_before_it_starts()
    {
        var device = new FakeAcapDevice { UploadedApplicationId = "415090" };
        var files = new InMemoryUploadedFiles();
        var ctx = new RecordingContext(device, files);
        var file = files.Add("msf.eap", EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", version: "1.0.0")));
        var payload = new AcapPayload
        {
            Action = AcapAction.Install, FileId = file.Id, Sha256 = file.Sha256, StartAfterInstall = true, Version = "1.0.0", AppDisplayName = "Missing Features",
            LicenseKeys = [new LicenseKeyEntry("B8A44F631339", Key())],
        }.ToJson();

        await Run(ctx, payload);

        Assert.Equal(["upload", "license ax_msf", "start ax_msf"], device.Writes);
        Assert.Contains("Verify license: Done", StepRun.Lines(ctx.Steps));
        Assert.Equal("Install Missing Features 1.0.0 with license", Plugin.GetTaskName(payload));
        Assert.Equal([AcapPermissions.Install, AcapPermissions.License], Plugin.RequiredPermissions(payload));
    }

    [Fact]
    public async Task After_an_install_a_refused_key_is_a_warning_and_the_application_still_starts()
    {
        var device = new FakeAcapDevice { UploadedApplicationId = "415090", LicenseReply = "Error: 30" };
        var files = new InMemoryUploadedFiles();
        var ctx = new RecordingContext(device, files);
        var file = files.Add("msf.eap", EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", version: "1.0.0")));
        var payload = new AcapPayload
        {
            Action = AcapAction.Install, FileId = file.Id, Sha256 = file.Sha256, StartAfterInstall = true,
            LicenseKeys = [new LicenseKeyEntry("B8A44F631339", Key())],
        }.ToJson();

        await Run(ctx, payload);

        Assert.Contains("Install license key: Warning", StepRun.Lines(ctx.Steps));
        Assert.Contains("Verify license: Skipped", StepRun.Lines(ctx.Steps));
        Assert.Contains("start ax_msf", device.Writes);
        Assert.NotEmpty(ctx.Warnings);
    }

    [Fact]
    public async Task A_remove_whose_answer_is_lost_is_verified_by_the_list_and_ends_with_a_warning()
    {
        var device = new FakeAcapDevice { RemoveTimesOut = true };
        device.Add("ax_msf", "1.0");
        var ctx = new RecordingContext(device);

        await Run(ctx, new AcapPayload { Action = AcapAction.Remove, Application = "ax_msf" }.ToJson());

        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Remove application: Done", "Verify removal: Warning", "Completed: Done"], StepRun.Lines(ctx.Steps));
        var control = device.Requests.Single(r => r.RequestUri!.OriginalString.Contains("control.cgi", StringComparison.Ordinal));
        Assert.True(control.Options.TryGetValue(Oadm.Sdk.Vapix.VapixRequestOptions.Timeout, out var timeout));
        Assert.Equal(TimeSpan.FromMinutes(2), timeout);
    }

    [Fact]
    public void A_payload_with_unusable_keys_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(new AcapPayload { Action = AcapAction.InstallLicense, Application = "ax_msf" }.ToJson()));
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(LicensePayload(new LicenseKeyEntry("not a mac", Key()))));
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(LicensePayload(new LicenseKeyEntry("B8A44F631339", new string('x', LicenseKey.MaxBytes + 1)))));
    }

    private sealed class KeyPicker(IReadOnlyList<string> paths) : ILicenseKeyPicker
    {
        public Task<IReadOnlyList<string>> PickLicenseKeysAsync() => Task.FromResult(paths);
    }

    private string WriteKey(string mac, string expires = "2027-09-18", string appId = "415090")
    {
        var path = Path.Combine(_dir, mac + "-" + Guid.NewGuid().ToString("N")[..6] + ".key");
        File.WriteAllText(path, Key(mac: mac, appId: appId, expires: expires));
        return path;
    }

    private static InstalledApplication Msf() =>
        new() { Name = "ax_msf", NiceName = "Missing Features", Vendor = "Axis", Version = "1.0", Status = "Running", License = "None", ApplicationId = "415090" };

    [Fact]
    public async Task The_dialog_matches_every_key_to_its_device_and_completes_with_the_usable_ones()
    {
        var licensed = new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" };
        var expired = new FakeDevice { Address = "10.0.0.49", Serial = "B8A44F000002" };
        var other = new FakeDevice { Address = "10.0.0.50", Serial = "B8A44F000003" };
        var none = new FakeDevice { Address = "10.0.0.51", Serial = "B8A44F000004" };
        var ctx = new FakeDialogContext();
        ctx.Devices[licensed.Id] = FakeDialogContext.State(apps: [Msf()]);
        var picker = new KeyPicker([WriteKey("B8A44F631339"), WriteKey("B8A44F000002", expires: "2026-01-01"), WriteKey("B8A44F000003", appId: "1"), Path.Combine(_dir, "missing.key")]);
        var vm = new AcapDialogViewModel(ctx, [licensed, expired, other, none], new FakePicker(null)) { Today = () => Today };
        vm.UseKeyPicker(picker);
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;

        vm.SelectedApplication = vm.Applications.Single();
        await vm.LicenseSelectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsLicenseMode);
        Assert.False(vm.ShowInstallButton);
        Assert.Equal("License of Missing Features", vm.LicenseTitle);
        Assert.Equal(["Key found", "Key expired", "Other application", "No key"], vm.LicenseRows.Select(r => r.Verdict));
        Assert.Equal("Keys for 1 of 4 devices.", vm.LicenseSummary);
        Assert.Equal("4 files, 3 keys", vm.LicenseFilesText);
        Assert.Equal("missing.key: This is not a license key file.", vm.LicenseProblemText);
        Assert.True(vm.InstallLicenseCommand.CanExecute(null));

        vm.InstallLicenseCommand.Execute(null);

        var payload = JsonSerializer.Deserialize<AcapPayload>(closed!, AcapPlugin.Json)!;
        Assert.Equal(AcapAction.InstallLicense, payload.Action);
        Assert.Equal("ax_msf", payload.Application);
        Assert.Equal("B8A44F631339", Assert.Single(payload.LicenseKeys).DeviceId);
    }

    [Fact]
    public async Task An_application_that_needs_no_license_says_so_and_offers_no_key()
    {
        var device = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[device.Id] = FakeDialogContext.State(apps:
        [
            new InstalledApplication { Name = "objectanalytics", NiceName = "AXIS Object Analytics", Version = "1.26", License = "None" },
            new InstalledApplication { Name = "ax_msf", NiceName = "Missing Features", Version = "1.0", License = "Missing", ApplicationId = "415090" },
        ]);
        var vm = new AcapDialogViewModel(ctx, [device], new FakePicker(null));
        vm.UseKeyPicker(new KeyPicker([WriteKey("B8A44F631339")]));
        await vm.InitializeAsync();

        var free = vm.Applications.Single(a => a.PackageName == "objectanalytics");
        Assert.Equal("Not required", free.License);
        vm.SelectedApplication = free;
        Assert.False(vm.LicenseSelectionCommand.CanExecute(null));
        Assert.Equal("This application needs no license.", vm.LicenseSelectionBlockedReason);

        var paid = vm.Applications.Single(a => a.PackageName == "ax_msf");
        Assert.Equal("Missing", paid.License);
        vm.SelectedApplication = paid;
        Assert.True(vm.LicenseSelectionCommand.CanExecute(null));
        Assert.Null(vm.LicenseSelectionBlockedReason);
    }

    [Fact]
    public async Task Keys_chosen_with_a_package_go_into_the_install_payload()
    {
        var device = new FakeDevice { Serial = "B8A44F631339" };
        var ctx = new FakeDialogContext();
        ctx.Devices[device.Id] = FakeDialogContext.State();
        var eap = Path.Combine(_dir, "msf.eap");
        File.WriteAllBytes(eap, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", version: "1.0.0")));
        var vm = new AcapDialogViewModel(ctx, [device], new FakePicker(eap)) { Today = () => Today };
        vm.UseKeyPicker(new KeyPicker([WriteKey("B8A44F631339")]));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;

        await vm.PickPackageCommand.ExecuteAsync(null);
        await vm.PickLicenseKeysCommand.ExecuteAsync(null);
        Assert.Equal("Keys for 1 of 1 device.", vm.LicenseSummary);
        await vm.InstallCommand.ExecuteAsync(null);

        var payload = JsonSerializer.Deserialize<AcapPayload>(closed!, AcapPlugin.Json)!;
        Assert.Equal(AcapAction.Install, payload.Action);
        Assert.Single(payload.LicenseKeys);
    }

    [Fact]
    public async Task Without_an_application_id_a_device_with_keys_of_several_applications_gets_none()
    {
        var device = new FakeDevice { Serial = "B8A44F631339" };
        var ctx = new FakeDialogContext();
        ctx.Devices[device.Id] = FakeDialogContext.State();
        var eap = Path.Combine(_dir, "noid.eap");
        File.WriteAllBytes(eap, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "noid", version: "1.0.0")));
        var vm = new AcapDialogViewModel(ctx, [device], new FakePicker(eap)) { Today = () => Today };
        vm.UseKeyPicker(new KeyPicker([WriteKey("B8A44F631339", appId: "1"), WriteKey("B8A44F631339", appId: "2")]));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;

        await vm.PickPackageCommand.ExecuteAsync(null);
        await vm.PickLicenseKeysCommand.ExecuteAsync(null);
        Assert.Equal("Keys for 0 of 1 device.", vm.LicenseSummary);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Empty(JsonSerializer.Deserialize<AcapPayload>(closed!, AcapPlugin.Json)!.LicenseKeys);
    }

    [Fact]
    public void A_payload_is_parsed_once_per_run_and_a_null_key_list_is_refused_as_empty()
    {
        var json = LicensePayload(new LicenseKeyEntry("B8A44F631339", Key()));
        Assert.Same(AcapPayload.ParseShared(json), AcapPayload.ParseShared(json));
        Assert.Equal("B8A44F631339", AcapPayload.ParseShared(json).KeyFor("B8A44F631339")!.DeviceId);
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse("""{"action":"installLicense","application":"ax_msf","licenseKeys":null}"""));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Keys_for_5000_devices_are_read_and_matched_fast()
    {
        var devices = Enumerable.Range(0, 5000).Select(i => new FakeDevice { Address = $"10.1.{i / 250}.{i % 250}", Serial = $"B8A44F{i:X6}" }).ToList();
        var zip = Path.Combine(_dir, "keys.zip");
        using (var file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var d in devices)
            {
                Write(archive, d.Serial + ".key", Key(mac: d.Serial));
            }
        }

        var ctx = new FakeDialogContext();
        ctx.Devices[devices[0].Id] = FakeDialogContext.State(apps: [Msf()]);
        var vm = new AcapDialogViewModel(ctx, devices, new FakePicker(null)) { Today = () => Today };
        vm.UseKeyPicker(new KeyPicker([zip]));
        await vm.InitializeAsync();
        vm.SelectedApplication = vm.Applications.Single();

        var watch = Stopwatch.StartNew();
        await vm.LicenseSelectionCommand.ExecuteAsync(null);
        var elapsed = watch.Elapsed;

        Assert.Equal(5000, vm.LicensedCount);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"{elapsed.TotalMilliseconds} ms");
    }
}
