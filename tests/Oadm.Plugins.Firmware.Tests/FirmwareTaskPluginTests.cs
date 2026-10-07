using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Firmware.Tests;

public sealed class FirmwareTaskPluginTests
{
    private static readonly FirmwareTaskTimings Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(2),
        RestartTimeout = TimeSpan.FromSeconds(5),
        ProbeTimeout = TimeSpan.FromSeconds(1),
        CommitRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private static FirmwareTaskPlugin Plugin(FirmwareTaskTimings? timings = null) => new(timings ?? Fast, TimeProvider.System);

    private static readonly string[] Prepare = ["Check compatibility: Done", "Read device info: Done", "Validate file: Done", "Read firmware status: Done"];

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
        Assert.Equal("Upgrade firmware", plugin.DisplayName);
        Assert.Equal(TaskGroups.Maintenance, ((ITaskPlugin)plugin).Group);
        Assert.True(plugin.ShowInToolbar);
        Assert.True(plugin.RequiresDialog);
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
            Then("Upload firmware: Done", "Install firmware: Done", "Wait for device to come back: Done", "Verify version: Done", "Read commit state: Done", "Commit firmware: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("fwmgr 1.10", StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal("AXIS P3265-V, AXIS OS 11.11.160", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("2 MB, device accepted AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Upload firmware"));
        Assert.Equal("Answers with AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Wait for device to come back"));
        Assert.Equal("AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Verify version"));
        Assert.Equal("Not committed yet", StepRun.Detail(ctx.Steps, "Read commit state"));
        Assert.Equal("AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Commit firmware"));

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
            ["Check compatibility: Done", "Read device info: Done", "Validate file: Warning", "Read firmware status: Skipped", "Upload firmware: Skipped",
             "Install firmware: Skipped", "Wait for device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit firmware: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Already up to date.", StepRun.Detail(ctx.Steps, "Upload firmware"));
    }

    [Fact]
    public async Task Downgrade_is_refused_without_allow_downgrade()
    {
        var (ctx, device, payload, _) = Setup("P3265-V_10_12_236.bin");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("Downgrade 11.11.160 → 10.12.236 (older major version) is not allowed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.Equal(
            ["Check compatibility: Done", "Read device info: Done", "Validate file: Failed", "Read firmware status: Skipped", "Upload firmware: Skipped",
             "Install firmware: Skipped", "Wait for device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit firmware: Skipped"],
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
            Then("Upload firmware: Done", "Install firmware: Done", "Wait for device to come back: Done", "Verify version: Done", "Read commit state: Skipped", "Commit firmware: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("The device commits a factory default upgrade by itself.", StepRun.Detail(ctx.Steps, "Commit firmware"));
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
    public async Task Invalid_file_is_refused_before_upload()
    {
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var zip = Fixture.Image();
        "PK\u0003\u0004"u8.CopyTo(zip);
        var file = files.Add("P3265-V_12_11_77.bin", zip);
        var ctx = new RecordingContext(device, files);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(ctx, new FirmwarePayload { FileId = file.Id }.ToJson(), new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("ZIP", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", device.Methods);
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
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.Equal("Read firmware status: Failed", StepRun.Lines(ctx.Steps)[3]);
        Assert.Equal("Upload firmware: Skipped", StepRun.Lines(ctx.Steps)[4]);
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
    public async Task Device_error_421_reports_that_the_old_firmware_still_runs()
    {
        var (ctx, device, payload, _) = Setup();
        device.UpgradeErrorCode = 421;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("does not match this device", ex.Message, StringComparison.Ordinal);
        Assert.Contains("still runs 11.11.160", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Probes);
        Assert.Equal(
            Then("Upload firmware: Failed", "Install firmware: Skipped", "Wait for device to come back: Skipped", "Verify version: Skipped", "Read commit state: Skipped", "Commit firmware: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Upload firmware"));
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
        Assert.Contains("answer was lost", StepRun.Detail(ctx.Steps, "Upload firmware"), StringComparison.Ordinal);
        Assert.Equal("Commit firmware: Done", StepRun.Lines(ctx.Steps)[^1]);
    }

    [Fact]
    public async Task Device_that_comes_back_with_the_old_version_fails_with_rollback_info()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.RollBack;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload, new FakeDevice(), Plugin(), CancellationToken.None));

        Assert.Contains("came back with its previous firmware 11.11.160", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.Commits);
        Assert.Equal(
            Then("Upload firmware: Done", "Install firmware: Done", "Wait for device to come back: Done", "Verify version: Failed", "Read commit state: Skipped", "Commit firmware: Skipped"),
            StepRun.Lines(ctx.Steps));
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
            Then("Upload firmware: Done", "Install firmware: Done", "Wait for device to come back: Failed", "Verify version: Skipped", "Read commit state: Skipped", "Commit firmware: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("The device restarts", StepRun.Detail(ctx.Steps, "Install firmware"));
    }

    [Fact]
    public async Task Times_out_when_the_device_never_restarts()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.NeverDown;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            Run(ctx, payload, new FakeDevice(), Plugin(Fast with { RestartTimeout = TimeSpan.FromMilliseconds(150) }), CancellationToken.None));

        Assert.Contains("did not restart", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Install firmware: Failed", StepRun.Lines(ctx.Steps)[5]);
        Assert.Equal("Wait for device to come back: Skipped", StepRun.Lines(ctx.Steps)[6]);
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
            Then("Upload firmware: Done", "Install firmware: Done", "Wait for device to come back: Done", "Verify version: Done",
                "Read commit state: Done", "Commit firmware: Failed", "Wait before retrying the commit: Done",
                "Read commit state (attempt 2): Done", "Commit firmware (attempt 2): Failed", "Wait before retrying the commit: Done",
                "Read commit state (attempt 3): Done", "Commit firmware (attempt 3): Failed"),
            StepRun.Lines(ctx.Steps));
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
            ["Read commit state: Done", "Commit firmware: Failed", "Wait before retrying the commit: Done", "Read commit state (attempt 2): Done", "Commit firmware (attempt 2): Done"],
            StepRun.Lines(ctx.Steps)[^5..]);
    }

    [Fact]
    public async Task Cancellation_while_waiting_surfaces_as_cancellation()
    {
        var (ctx, device, payload, _) = Setup();
        device.After = AfterUpgrade.NeverBack;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(ctx, payload, new FakeDevice(), Plugin(Fast with { RestartTimeout = TimeSpan.FromMinutes(1) }), cts.Token));

        Assert.Equal("Wait for device to come back: Failed", StepRun.Lines(ctx.Steps)[6]);
        Assert.Equal("Cancelled.", StepRun.Detail(ctx.Steps, "Wait for device to come back"));
        Assert.Equal("Not run: the task was cancelled.", StepRun.Detail(ctx.Steps, "Verify version"));
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
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.DoesNotContain("commit", device.Methods);
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
