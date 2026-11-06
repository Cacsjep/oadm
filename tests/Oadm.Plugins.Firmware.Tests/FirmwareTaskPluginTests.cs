using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Firmware.Tests;

public sealed class FirmwareTaskPluginTests
{
    private static readonly FirmwareTaskTimings Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(2),
        RestartTimeout = TimeSpan.FromSeconds(30), // reached only when a test fails: the fake comes back after three probes
        ProbeTimeout = TimeSpan.FromSeconds(30), // one probe request: generous, the fake answers at once
        CommitRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private static FirmwareTaskPlugin Plugin(FirmwareTaskTimings? timings = null) => new(timings ?? Fast, TimeProvider.System);

    /// <summary>The production timings on a clock that only moves when the task waits: a timeout counts probes, not real time.</summary>
    private static FirmwareTaskPlugin SteppedPlugin() => new(new FirmwareTaskTimings { ProbeTimeout = TimeSpan.FromSeconds(30) }, new SteppingClock());

    private static readonly string[] Prepare = ["Check compatibility: Done", "Read device info: Done", "Validate file: Done", "Download AXIS OS from axis.com: Skipped", "Read the AXIS OS status: Done"];

    /// <summary>Runs the task the way the server does, so its steps end like in the task engine.</summary>
    private static Task Run(RecordingContext ctx, string? payload, IDeviceInfo device, FirmwareTaskPlugin plugin, CancellationToken ct) =>
        StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, device, payload, ct));

    private static string[] Then(params string[] lines) => [.. Prepare, .. lines];

    private static (RecordingContext Ctx, FakeAxisDevice Device, string Payload, byte[] Image) Setup(
        string fileName = "P3265-V_12_11_77.bin",
        FactoryDefaultMode mode = FactoryDefaultMode.None,
        bool allowDowngrade = false)
    {
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var image = Fixture.Image();
        var file = files.Add(fileName, image);
        var payload = new FirmwarePayload { FileId = file.Id, FileName = fileName, FactoryDefaultMode = mode, AllowDowngrade = allowDowngrade }.ToJson();
        return (new RecordingContext(device, files), device, payload, image);
    }

    [Fact]
    public void Declares_dialog_task_in_toolbar_and_context_menu()
    {
        var plugin = new FirmwareTaskPlugin();
        Assert.Equal("oadm.firmware", plugin.Id);
        Assert.Equal("Upgrade AXIS OS", plugin.DisplayName);
        Assert.Equal(TaskGroups.Maintenance, ((ITaskPlugin)plugin).Group);
        Assert.True(plugin.ShowInToolbar);
        Assert.True(plugin.RequiresDialog);
        Assert.Equal(4, ((ITaskPlugin)plugin).MaxParallelDevices); // large uploads: at most 4 at once
    }

    [Fact]
    public void CanRun_requires_status_ok_and_fwmgr_1x()
    {
        var plugin = new FirmwareTaskPlugin();
        Assert.True(plugin.CanRun(new FakeDevice(DeviceStatus.Ok)));
        Assert.True(plugin.CanRun(new FakeDevice(DeviceStatus.Ok, apis: [.. Fixture.RecordedApis()])));
        Assert.False(plugin.CanRun(new FakeDevice(DeviceStatus.Unknown)));
        Assert.False(plugin.CanRun(new FakeDevice(DeviceStatus.Unreachable)));
        Assert.False(plugin.CanRun(new FakeDevice(DeviceStatus.CredentialsRequired)));
        Assert.False(plugin.CanRun(new FakeDevice(DeviceStatus.CertificateChanged)));
        Assert.False(plugin.CanRun(new FakeDevice(DeviceStatus.Ok, apis: new DeviceApi("basic-device-info", "1.3"))));
        Assert.False(plugin.CanRun(new FakeDevice(DeviceStatus.Ok, apis: new DeviceApi("fwmgr", "2.0"))));
    }

    [Fact]
    public void NotSupportedReason_names_the_status_or_the_missing_firmware_api()
    {
        var plugin = new FirmwareTaskPlugin();
        Assert.Equal("Not read yet: refresh the device", plugin.NotSupportedReason(new FakeDevice(DeviceStatus.Unknown)));
        Assert.Equal("The device has no password yet: use Set password", plugin.NotSupportedReason(new FakeDevice(DeviceStatus.PasswordNotSet)));
        Assert.Equal("Needs the firmware management API (this device has 9.80.3)", plugin.NotSupportedReason(new FakeDevice(DeviceStatus.Ok, version: "9.80.3", apis: new DeviceApi("basic-device-info", "1.3"))));
    }

    [Fact]
    public async Task Full_upgrade_uploads_waits_for_restart_verifies_and_commits()
    {
        var (ctx, device, payload, image) = Setup();

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.Equal(image, device.UploadedBytes);
        Assert.Equal("P3265-V_12_11_77.bin", device.UploadedFileName);
        using var json = JsonDocument.Parse(device.UpgradeJson!);
        Assert.Equal("upgrade", json.RootElement.GetProperty("method").GetString());
        Assert.Equal("1.0", json.RootElement.GetProperty("apiVersion").GetString());
        var p = json.RootElement.GetProperty("params");
        Assert.Equal("none", p.GetProperty("factoryDefaultMode").GetString());
        Assert.Equal("never", p.GetProperty("autoCommit").GetString());
        Assert.Equal("30", p.GetProperty("autoRollback").GetString());
        Assert.Equal("12.11.77", device.Version);
        Assert.Equal(1, device.Commits);
        Assert.True(device.Committed);
        Assert.Empty(ctx.Warnings);
        Assert.Empty(ctx.Reports); // progress is derived from the steps
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Done", "Verify version: Done", "Read commit state: Done", "Commit AXIS OS: Done", "Completed: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("fwmgr 1.10", StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal("AXIS P3265-V, AXIS OS 11.11.160", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("2.0 MB, device accepted AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Upload AXIS OS to the device"));
        Assert.Equal("Answers with AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
        Assert.Equal("AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Verify version"));
        Assert.Equal("Not committed yet", StepRun.Detail(ctx.Steps, "Read commit state"));
        Assert.Equal("AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Commit AXIS OS"));

        // The compatibility re-check happens before the first write.
        Assert.True(device.Methods.IndexOf("getApiList") < device.Methods.IndexOf("upgrade"));
        Assert.True(device.Methods.IndexOf("status") < device.Methods.IndexOf("upgrade"));
    }

    [Fact]
    public async Task Already_up_to_date_finishes_with_warning_and_uploads_nothing()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_11_11_160.bin");

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.Single(ctx.Warnings);
        Assert.Contains("Already up to date", ctx.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(
            ["Check compatibility: Done", "Read device info: Done", "Validate file: Warning", "Download AXIS OS from axis.com: Skipped", "Read the AXIS OS status: Skipped", "Upload AXIS OS to the device: Skipped",
             "Install AXIS OS: Skipped", "Wait for the device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Already up to date.", StepRun.Detail(ctx.Steps, "Upload AXIS OS to the device"));
    }

    [Fact]
    public async Task Downgrade_is_refused_without_allow_downgrade()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_10_12_236.bin");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("Downgrade 11.11.160 → 10.12.236 (older major version) is not allowed. Turn on \"Allow downgrade\".", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.Equal(
            ["Check compatibility: Done", "Read device info: Done", "Validate file: Failed", "Download AXIS OS from axis.com: Skipped", "Read the AXIS OS status: Skipped", "Upload AXIS OS to the device: Skipped",
             "Install AXIS OS: Skipped", "Wait for the device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Validate file"));
    }

    [Fact]
    public async Task Downgrade_within_major_line_is_refused_too()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_11_11_100.bin");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Fact]
    public async Task Allowed_downgrade_without_factory_default_is_refused()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_10_12_236.bin", FactoryDefaultMode.None, allowDowngrade: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("requires a factory default", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Fact]
    public async Task Allowed_downgrade_with_hard_factory_default_lets_the_device_commit_and_warns()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_10_12_236.bin", FactoryDefaultMode.Hard, allowDowngrade: true);
        device.NewVersion = "10.12.236";

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        using var json = JsonDocument.Parse(device.UpgradeJson!);
        var p = json.RootElement.GetProperty("params");
        Assert.Equal("hard", p.GetProperty("factoryDefaultMode").GetString());
        Assert.Equal("started", p.GetProperty("autoCommit").GetString());
        Assert.Equal("never", p.GetProperty("autoRollback").GetString());
        Assert.Equal(0, device.Commits);
        Assert.Contains(ctx.Warnings, w => w.Contains("Hard factory default", StringComparison.Ordinal));
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Done", "Verify version: Done", "Read commit state: Skipped", "Commit AXIS OS: Skipped", "Completed: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("The device commits a factory default upgrade by itself.", StepRun.Detail(ctx.Steps, "Commit AXIS OS"));
        Assert.Equal("10.12.236", device.Version);
    }

    [Fact]
    public async Task Wrong_product_is_refused_before_upload()
    {
        var (ctx, device, payload, _) = Setup("Q6135-LE_12_11_77.bin");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("for Q6135-LE", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Fact]
    public async Task Too_small_file_is_refused_before_upload()
    {
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var file = files.Add("P3265-V_12_11_77.bin", Fixture.Image(1000));
        var ctx = new RecordingContext(device, files);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(ctx, new FirmwarePayload { FileId = file.Id }.ToJson(), new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("smaller than 1 MB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Fact]
    public async Task Image_starting_with_gzip_bytes_is_uploaded_the_device_decides()
    {
        // Older AXIS OS images (e.g. M3206-LVE_10_12_338.bin) start with gzip/tar-like bytes; the content is never judged.
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var image = Fixture.Image();
        image[0] = 0x1F;
        image[1] = 0x8B;
        image[2] = 0x08;
        var file = files.Add("P3265-V_12_11_77.bin", image);
        var ctx = new RecordingContext(device, files);

        await Run(ctx, new FirmwarePayload { FileId = file.Id, FileName = "P3265-V_12_11_77.bin" }.ToJson(), new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.Equal(image, device.UploadedBytes);
        Assert.Equal("12.11.77", device.Version);
    }

    [Theory]
    [InlineData(DeviceStatus.Unknown)]
    [InlineData(DeviceStatus.Unreachable)]
    [InlineData(DeviceStatus.CertificateChanged)]
    public async Task Never_starts_on_a_device_that_is_not_ok(DeviceStatus status)
    {
        var (ctx, device, payload, _) = Setup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(status), Plugin(), CancellationToken.None));

        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Methods);
        Assert.Equal("Check compatibility: Failed", StepRun.Lines(ctx.Steps)[0]);
        Assert.All(StepRun.Lines(ctx.Steps).Skip(1), l => Assert.EndsWith(": Skipped", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fresh_api_list_without_fwmgr_fails_as_not_compatible_before_any_write()
    {
        var (ctx, device, payload, _) = Setup();
        device.Apis = [new DeviceApi("basic-device-info", "1.3"), new DeviceApi("fwmgr", "2.0")];

        var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Equal("fwmgr", ex.ApiId);
        Assert.Equal(["getApiList"], device.Methods);
        Assert.Equal("Check compatibility: Failed", StepRun.Lines(ctx.Steps)[0]);
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.All(StepRun.Lines(ctx.Steps).Skip(1), l => Assert.EndsWith(": Skipped", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pending_uncommitted_upgrade_blocks_a_new_one()
    {
        var (ctx, device, payload, _) = Setup();
        device.StatusJson = """{"apiVersion":"1.10","method":"status","data":{"activeFirmwareVersion":"11.11.160","inactiveFirmwareVersion":"11.11.100","isCommited":false,"timeToRollback":48}}""";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("not committed", ex.Message, StringComparison.Ordinal);
        // OADM has no commit action: the text only names what the user can do.
        Assert.DoesNotContain("commit it", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wait until the device has rolled back by itself, or restart it, then try again. Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.Equal("Read the AXIS OS status: Failed", StepRun.Lines(ctx.Steps)[4]);
        Assert.Equal("Upload AXIS OS to the device: Skipped", StepRun.Lines(ctx.Steps)[5]);
    }

    [Fact]
    public async Task Missing_uploaded_file_fails_without_touching_the_device()
    {
        var device = new FakeAxisDevice();
        var ctx = new RecordingContext(device, new FakeFiles());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Run(ctx, new FirmwarePayload { FileId = "gone" }.ToJson(), new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"fileId\":\"\"}")]
    [InlineData("{\"fileId\":\"x\",\"factoryDefaultMode\":\"bogus\"}")]
    [InlineData("not json")]
    public async Task Invalid_payload_fails_before_any_request(string? payload)
    {
        var device = new FakeAxisDevice();
        var ctx = new RecordingContext(device, new FakeFiles());

        await Assert.ThrowsAsync<ArgumentException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Empty(device.Methods);
    }

    [Fact]
    public async Task Device_error_402_names_the_installed_application_and_changes_nothing()
    {
        var (ctx, device, payload, _) = Setup();
        device.UpgradeErrorCode = 402;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.StartsWith("An installed application does not support this AXIS OS version. Update or remove it with Applications (ACAP), then try again.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("still runs 11.11.160", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Probes);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Failed", "Install AXIS OS: Skipped", "Wait for the device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped"),
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Device_error_421_reports_that_the_old_firmware_still_runs()
    {
        var (ctx, device, payload, _) = Setup();
        device.UpgradeErrorCode = 421;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("does not match this device", ex.Message, StringComparison.Ordinal);
        Assert.Contains("still runs 11.11.160", ex.Message, StringComparison.Ordinal);
        // 11.11.160 -> 12.11.77 leaves the LTS track 11.11: the error repeats to be on its latest first.
        Assert.EndsWith("Major upgrade. Make sure the device runs the latest AXIS OS 11.11 first.", ex.Message, StringComparison.Ordinal);
        Assert.Contains((TaskLogLevel.Info, "Major upgrade. Make sure the device runs the latest AXIS OS 11.11 first."), ctx.Logs);
        Assert.Equal(0, device.Probes);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Failed", "Install AXIS OS: Skipped", "Wait for the device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Upload AXIS OS to the device"));
    }

    [Fact]
    public async Task Lost_answer_after_complete_upload_is_verified_by_version()
    {
        var (ctx, device, payload, _) = Setup();
        device.DropConnectionAfterUpload = true;

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.Equal("12.11.77", device.Version);
        Assert.Equal(1, device.Commits);
        Assert.Contains(ctx.Logs, l => l.Level == TaskLogLevel.Warning && l.Message.Contains("answer was lost", StringComparison.Ordinal));
        Assert.Contains("answer was lost", StepRun.Detail(ctx.Steps, "Upload AXIS OS to the device"), StringComparison.Ordinal);
        Assert.Equal(["Commit AXIS OS: Done", "Completed: Done"], StepRun.Lines(ctx.Steps)[^2..]);
    }

    [Fact]
    public async Task A_lost_probe_while_the_device_installs_is_not_a_rollback()
    {
        // One probe gets lost, the old version answers twice more, then the device restarts and answers with the new one.
        var (ctx, device, payload, _) = Setup();
        device.LostProbes = 1;

        await Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None);

        Assert.Equal("12.11.77", device.Version);
        Assert.Equal(1, device.Commits);
        Assert.True(device.Committed);
        Assert.Equal(1 + 2 + device.DownProbes + 1, device.Probes);
        Assert.Empty(ctx.Warnings);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Done", "Verify version: Done", "Read commit state: Done", "Commit AXIS OS: Done", "Completed: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Answers with AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
    }

    [Fact]
    public async Task A_lost_probe_and_then_the_new_version_at_once_commits_too()
    {
        var (ctx, device, payload, _) = Setup();
        device.LostProbes = 1;
        device.DownProbes = 0;

        await Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None);

        Assert.Equal(1 + 2 + 1, device.Probes);
        Assert.Equal(1, device.Commits);
        Assert.Equal(["Commit AXIS OS: Done", "Completed: Done"], StepRun.Lines(ctx.Steps)[^2..]);
    }

    [Fact]
    public async Task Device_that_comes_back_with_the_old_version_fails_with_rollback_info()
    {
        // The old version answers until the wait is over; the firmware manager then holds the new image as the inactive one.
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.RollBack;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None));

        Assert.Contains("came back with its previous AXIS OS version 11.11.160", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rolled back", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Commits);
        Assert.Equal("status", device.Methods[^1]);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Done", "Verify version: Failed", "Read commit state: Skipped", "Commit AXIS OS: Skipped"),
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Old_version_until_the_timeout_without_a_rollback_says_the_device_did_not_restart()
    {
        // A lost probe, then the old version for good: the firmware manager has no new image, so nothing rolled back.
        var (ctx, device, payload, _) = Setup();
        device.LostProbes = 1;
        device.After = AfterUpgrade.NeverDown;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None));

        Assert.Equal("The device did not restart within 15 minutes after the upload and still runs 11.11.160.", ex.Message);
        Assert.DoesNotContain("rolled back", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Commits);
        Assert.Equal("status", device.Methods[^1]);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Failed", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
    }

    [Fact]
    public async Task Old_version_until_the_timeout_with_an_unreadable_status_claims_no_rollback()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.RollBack;
        device.StatusFailsAfterUpgrade = true;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None));

        Assert.Equal("The device still runs 11.11.160 15 minutes after the upload: the new version is not running.", ex.Message);
        Assert.Equal("Wait for the device to come back: Failed", StepRun.Lines(ctx.Steps)[7]);
    }

    /// <summary>An image whose name does not say its version and that holds the version the device already runs.</summary>
    private static (RecordingContext Ctx, FakeAxisDevice Device, string Payload) SameVersion(FactoryDefaultMode mode = FactoryDefaultMode.None)
    {
        var (ctx, device, payload, _) = Setup("axis-os.bin", mode, allowDowngrade: mode != FactoryDefaultMode.None);
        device.NewVersion = device.Version;
        return (ctx, device, payload);
    }

    [Theory]
    [InlineData(1)] // one lost probe
    [InlineData(2)] // two in a row look like a restart, but the device says nothing waits to be committed
    public async Task The_same_version_again_waits_for_the_restart_after_a_lost_probe(int lostProbes)
    {
        // Lost probes, then answers while the device still installs (nothing to commit yet), then the restart.
        var (ctx, device, payload) = SameVersion();
        device.LostProbes = lostProbes;

        await Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None);

        Assert.Equal(lostProbes + device.InstallingProbes + device.DownProbes + 1, device.Probes);
        Assert.Equal(1, device.Commits);
        Assert.True(device.Committed);
        Assert.Empty(ctx.Warnings);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Done", "Verify version: Done", "Read commit state: Done", "Commit AXIS OS: Done", "Completed: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Not committed yet", StepRun.Detail(ctx.Steps, "Read commit state"));
    }

    [Fact]
    public async Task The_same_version_again_without_a_lost_probe_commits_after_the_restart()
    {
        var (ctx, device, payload) = SameVersion();

        await Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None);

        Assert.Equal(device.DownProbes + 1, device.Probes);
        Assert.Equal(1, device.Commits);
        Assert.Equal(["Commit AXIS OS: Done", "Completed: Done"], StepRun.Lines(ctx.Steps)[^2..]);
    }

    [Theory]
    [InlineData(1, "The device did not restart within 15 minutes after the upload and still runs 11.11.160.")]
    [InlineData(2, "The device did not install the upload within 15 minutes: nothing waits to be committed. It still runs 11.11.160.")] // away for two probes: maybe a restart
    public async Task The_same_version_again_that_never_restarts_fails_and_is_never_done(int lostProbes, string expected)
    {
        // Lost probes, then the device answers for good and nothing ever waits to be committed.
        var (ctx, device, payload) = SameVersion();
        device.LostProbes = lostProbes;
        device.After = AfterUpgrade.NeverDown;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None));

        Assert.Equal(expected, ex.Message);
        Assert.Equal(0, device.Commits);
        Assert.False(device.Committed);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Failed", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
    }

    [Fact]
    public async Task The_same_version_again_with_a_factory_default_waits_for_the_restart_too()
    {
        // The device commits by itself and OADM's login is gone after the reset: only the restart says that it happened.
        var (ctx, device, payload) = SameVersion(FactoryDefaultMode.Soft);
        device.LostProbes = 1;

        await Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None);

        Assert.Equal(1 + device.InstallingProbes + device.DownProbes + 1, device.Probes);
        Assert.Equal(0, device.Commits);
        Assert.Equal("Wait for the device to come back: Done", StepRun.Lines(ctx.Steps)[7]);
        Assert.Contains(ctx.Warnings, w => w.StartsWith("Soft factory default applied", StringComparison.Ordinal));

        // It never goes away: Failed, and the text says it did not restart.
        var (never, stuck, again) = SameVersion(FactoryDefaultMode.Soft);
        stuck.LostProbes = 1;
        stuck.After = AfterUpgrade.NeverDown;
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Run(never, again, new FakeDevice(), SteppedPlugin(), CancellationToken.None));
        Assert.Equal("The device did not restart within 15 minutes after the upload and still runs 11.11.160.", ex.Message);
    }

    [Fact]
    public async Task The_same_version_again_with_an_unreadable_status_takes_the_restart_as_the_proof()
    {
        // The status request is refused once the image is in: only a device that was away for more than one probe came back.
        var (ctx, device, payload) = SameVersion();
        device.LostProbes = 1;
        device.StatusFailsAfterUpgrade = true;

        // The commit then fails on the same unreadable status; what counts here is that the wait saw the restart.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), SteppedPlugin(), CancellationToken.None));

        Assert.Equal(1 + device.InstallingProbes + device.DownProbes + 1, device.Probes);
        Assert.Equal("Wait for the device to come back: Done", StepRun.Lines(ctx.Steps)[7]);
        Assert.Equal("Verify version: Done", StepRun.Lines(ctx.Steps)[8]);
    }

    [Fact]
    public async Task Times_out_when_the_device_never_comes_back()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.NeverBack;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            Run(ctx, payload, new FakeDevice(), Plugin(Fast with { RestartTimeout = TimeSpan.FromMilliseconds(150) }), CancellationToken.None));

        Assert.Contains("did not come back", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rolls back to 11.11.160 by itself 30 minutes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Failed", "Verify version: Skipped", "Read commit state: Skipped", "Commit AXIS OS: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("The device restarts", StepRun.Detail(ctx.Steps, "Install AXIS OS"));
    }

    [Fact]
    public async Task Times_out_when_the_device_never_restarts()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.NeverDown;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            Run(ctx, payload, new FakeDevice(), Plugin(Fast with { RestartTimeout = TimeSpan.FromMilliseconds(150) }), CancellationToken.None));

        Assert.Contains("did not restart", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Install AXIS OS: Failed", StepRun.Lines(ctx.Steps)[6]);
        Assert.Equal("Wait for the device to come back: Skipped", StepRun.Lines(ctx.Steps)[7]);
    }

    [Fact]
    public void Production_timings_are_generous()
    {
        var t = new FirmwareTaskTimings();
        Assert.Equal(TimeSpan.FromMinutes(15), t.RestartTimeout);
        Assert.True(t.AutoRollbackMinutes > t.RestartTimeout.TotalMinutes);
    }

    [Fact]
    public async Task Commit_is_retried_and_failure_explains_the_automatic_rollback()
    {
        var (ctx, device, payload, _) = Setup();
        device.CommitFailures = 5;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Equal(3, device.Commits);
        Assert.Contains("could not be committed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rolls back to 11.11.160", ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            Then("Upload AXIS OS to the device: Done", "Install AXIS OS: Done", "Wait for the device to come back: Done", "Verify version: Done",
                "Read commit state: Done", "Commit AXIS OS: Failed", "Wait before retrying the commit: Done",
                "Read commit state (attempt 2): Done", "Commit AXIS OS (attempt 2): Failed", "Wait before retrying the commit: Done",
                "Read commit state (attempt 3): Done", "Commit AXIS OS (attempt 3): Failed"),
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Several_files_install_the_file_for_the_device_model()
    {
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var other = files.Add("Q6135-LE_12_11_77.bin", Fixture.Image());
        var own = files.Add("P3265-V_12_11_77.bin", Fixture.Image());
        var ctx = new RecordingContext(device, files);
        var payload = new FirmwarePayload
        {
            FileId = other.Id,
            FileName = other.Name,
            Files = [new FirmwareFile(other.Id, other.Name), new FirmwareFile(own.Id, own.Name)],
        };

        await Run(ctx, payload.ToJson(), new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.True(device.Committed);
        Assert.StartsWith("P3265-V_12_11_77.bin", StepRun.Detail(ctx.Steps, "Validate file"), StringComparison.Ordinal);
        Assert.Equal("Upgrade AXIS OS to 12.11.77", payload.TaskName());
    }

    [Fact]
    public async Task Several_files_without_one_for_the_model_skip_the_device()
    {
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var a = files.Add("Q6135-LE_12_11_77.bin", Fixture.Image());
        var b = files.Add("M3215-LVE_12_11_147.bin", Fixture.Image());
        var ctx = new RecordingContext(device, files);
        var payload = new FirmwarePayload { FileId = a.Id, FileName = a.Name, Files = [new FirmwareFile(a.Id, a.Name), new FirmwareFile(b.Id, b.Name)] };

        await Run(ctx, payload.ToJson(), new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.Equal(0, device.Commits);
        Assert.Equal("No file for this model. Nothing was installed.", StepRun.Detail(ctx.Steps, "Validate file"));
        Assert.Equal("Upgrade AXIS OS", payload.TaskName()); // two versions: no version in the name
    }

    [Fact]
    public async Task Commit_waits_while_the_device_has_not_finished_booting()
    {
        var (ctx, device, payload, _) = Setup();
        device.CommitsWhileBooting = 5; // more than the commit attempts: waiting for the boot is not an attempt

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.Equal(6, device.Commits);
        Assert.True(device.Committed);
        Assert.Equal(["Read commit state: Done", "Commit AXIS OS: Done", "Completed: Done"], StepRun.Lines(ctx.Steps)[^3..]);
    }

    [Fact]
    public async Task Commit_succeeds_on_retry()
    {
        var (ctx, device, payload, _) = Setup();
        device.CommitFailures = 1;

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.Equal(2, device.Commits);
        Assert.True(device.Committed);
        Assert.Equal(
            ["Read commit state: Done", "Commit AXIS OS: Failed", "Wait before retrying the commit: Done", "Read commit state (attempt 2): Done", "Commit AXIS OS (attempt 2): Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps)[^6..]);
    }

    [Fact]
    public async Task Cancel_after_the_upload_says_that_the_device_rolls_back()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.NeverBack;
        using var cts = new CancellationTokenSource();

        var run = Task.Run(() => Run(ctx, payload, new FakeDevice(), Plugin(Fast with { RestartTimeout = TimeSpan.FromMinutes(1) }), cts.Token));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => StepRun.Lines(ctx.Steps).Contains("Wait for the device to come back: Running"), "the wait for the device");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // The device accepted the image and is never committed: the step and the log say what it does now.
        const string RollsBack = "The upgrade was not committed: the device rolls back to 11.11.160 by itself 30 minutes after booting and restarts.";
        Assert.Equal("Wait for the device to come back: Failed", StepRun.Lines(ctx.Steps)[7]);
        Assert.Equal("Cancelled. " + RollsBack, StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
        Assert.Equal("Not run: the task was cancelled.", StepRun.Detail(ctx.Steps, "Verify version"));
        Assert.Contains((TaskLogLevel.Warning, RollsBack), ctx.Logs);
        Assert.Equal(0, device.Commits);
    }

    [Fact]
    public async Task Cancel_after_the_upload_of_a_factory_default_upgrade_says_that_the_device_still_installs()
    {
        var (ctx, device, payload, _) = Setup(mode: FactoryDefaultMode.Soft);
        device.After = AfterUpgrade.NeverBack;
        using var cts = new CancellationTokenSource();

        var run = Task.Run(() => Run(ctx, payload, new FakeDevice(), Plugin(Fast with { RestartTimeout = TimeSpan.FromMinutes(1) }), cts.Token));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => StepRun.Lines(ctx.Steps).Contains("Wait for the device to come back: Running"), "the wait for the device");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        const string Installs = "The device still installs the new AXIS OS version and resets to factory default.";
        Assert.Equal("Cancelled. " + Installs, StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
        Assert.Contains((TaskLogLevel.Warning, Installs), ctx.Logs);
    }

    [Fact]
    public async Task Cancel_before_the_upload_stays_a_plain_cancel()
    {
        var (ctx, device, payload, _) = Setup();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), cts.Token));

        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.Equal("Cancelled.", StepRun.Detail(ctx.Steps, "Read the AXIS OS status"));
        Assert.DoesNotContain(ctx.Logs, l => l.Message.Contains("rolls back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_query_returns_recorded_fwmgr_state()
    {
        var device = new FakeAxisDevice { Version = "12.11.77" };

        var json = await new FirmwareTaskPlugin().QueryAsync(new QueryContext(device), new FakeDevice(), "status", null, CancellationToken.None);

        var info = FirmwareStatusInfo.FromJson(json)!;
        Assert.True(info.Supported);
        Assert.Equal("1.10", info.FwmgrVersion);
        Assert.Equal("12.11.77", info.ActiveVersion);
        Assert.Equal("7454651131", info.ActivePart);
        Assert.Null(info.InactiveVersion);
        Assert.Null(info.IsCommitted);
        Assert.Equal("crash", info.ResetSource);
        Assert.Equal("P3265-V", info.Model);
        Assert.Null(info.Applications); // the device has no application list (404)
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.DoesNotContain("commit", device.Methods);
    }

    [Fact]
    public async Task Status_query_returns_the_installed_applications()
    {
        var device = new FakeAxisDevice { Version = "12.11.77", ApplicationsXml = Fixture.Read("applications-list-p3265v-12.11.xml") };

        var json = await new FirmwareTaskPlugin().QueryAsync(new QueryContext(device), new FakeDevice(), "status", null, CancellationToken.None);

        var apps = FirmwareStatusInfo.FromJson(json)!.Applications!;
        Assert.Equal(8, apps.Count);
        var lpr = Assert.Single(apps, a => a.Name == "fflprapp");
        Assert.Equal("Signed", lpr.SignatureStatus);
        Assert.Equal([new AppOsRange("12.10", "13")], lpr.CompatibleOsVersions);
        Assert.True(Assert.Single(apps, a => a.Name == "objectanalytics").Bundled);
    }

    [Fact]
    public async Task An_unsigned_application_stops_an_upgrade_to_axis_os_13_before_the_upload()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_13_0_88.bin");
        device.Version = "12.11.77";
        device.ApplicationsXml = """
            <reply result="ok">
             <application Name="faceverifier" NiceName="Face Verifier" Bundled="No" Status="Stopped" SignatureStatus="Unknown"><CompatibleOsVersions><VersionRange><Min>12.10</Min><Max>99</Max></VersionRange></CompatibleOsVersions></application>
             <application Name="objectanalytics" NiceName="AXIS Object Analytics" Bundled="Yes" Status="Running" SignatureStatus="Signed"><CompatibleOsVersions><VersionRange><Min>12</Min><Max>12</Max></VersionRange></CompatibleOsVersions></application>
            </reply>
            """;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Equal("12.11.77 → 13.0.88. Face Verifier: not signed. Update or remove it with Applications (ACAP) first. Nothing was changed.", ex.Message);
        Assert.Contains("list.cgi", device.Methods);
        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Fact]
    public async Task Applications_are_not_read_below_axis_os_13()
    {
        var (ctx, device, payload, _) = Setup();
        device.ApplicationsXml = Fixture.Read("applications-list-p3265v-12.11.xml");

        await Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None);

        Assert.DoesNotContain("list.cgi", device.Methods);
        Assert.Contains("upgrade", device.Methods);
    }

    [Fact]
    public async Task Status_query_reports_unsupported_devices_without_calling_fwmgr()
    {
        var device = new FakeAxisDevice { Apis = [new DeviceApi("basic-device-info", "1.3")] };

        var json = await new FirmwareTaskPlugin().QueryAsync(new QueryContext(device), new FakeDevice(), "status", null, CancellationToken.None);

        Assert.False(FirmwareStatusInfo.FromJson(json)!.Supported);
        Assert.DoesNotContain("status", device.Methods);
    }

    [Fact]
    public async Task Unknown_query_is_rejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new FirmwareTaskPlugin().QueryAsync(new QueryContext(new FakeAxisDevice()), new FakeDevice(), "upgrade", null, CancellationToken.None));
    }
}
