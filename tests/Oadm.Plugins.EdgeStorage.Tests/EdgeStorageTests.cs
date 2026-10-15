using System.Diagnostics;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Plugins.EdgeStorage.Disks;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

namespace Oadm.Plugins.EdgeStorage.Tests;

public sealed class ParsingTests
{
    [Fact]
    public void The_disk_list_of_a_camera_with_a_card_has_its_size_retention_and_encryption()
    {
        var disks = EdgeDisks.Parse(Fixtures.Read("q1615-disks.xml"));
        var card = Assert.Single(disks, d => d.IsSdCard);
        Assert.Equal((57922892L, 57922656L, 7, "fifo", false, false), (card.TotalKb, card.FreeKb, card.CleanupMaxAgeDays, card.CleanupPolicy, card.EncryptionEnabled, card.Encrypted));
        Assert.True(card.IsConnected);
        Assert.Null(card.ProblemText);
    }

    [Fact]
    public void An_unmounted_card_is_present_but_not_in_use_and_an_empty_slot_is_not_present()
    {
        var unmounted = Assert.Single(EdgeDisks.Parse(Fixtures.Read("q1615-disks-unmounted.xml")), d => d.IsSdCard);
        Assert.True(unmounted.IsPresent && unmounted.IsUnmounted);
        Assert.Equal("The SD card is not in use", unmounted.ProblemText);

        var empty = Assert.Single(EdgeDisks.Parse(Fixtures.Read("m3215-no-card-disks.xml")), d => d.IsSdCard);
        Assert.False(empty.IsPresent);
        Assert.Equal(EdgeDisks.NoDisk, EdgeDisks.FirstProblem(EdgeDisks.Parse(Fixtures.Read("m3215-no-card-disks.xml"))));
    }

    [Fact]
    public void Health_gives_the_wear_and_no_value_when_the_card_does_not_report_it()
    {
        Assert.Equal(1, StorageParsing.ParseHealth(Fixtures.Read("q1615-health.xml")).Single(h => h.DiskId == "SD_DISK").WearPercent);
        var p3265 = StorageParsing.ParseHealth(Fixtures.Read("p3265-health.xml"));
        Assert.Null(p3265.Single(h => h.DiskId == "SD_DISK").WearPercent); // -1: not supported
        Assert.Null(p3265.Single(h => h.DiskId == "NetworkShare").WearPercent); // -3: not present
        Assert.Equal(90, StorageParsing.ParseWearAlert(Fixtures.Read("p3265-alert-levels.xml")));
    }

    [Fact]
    public void Jobs_and_property_answers_are_read()
    {
        Assert.Equal(("15", true, 0), Read(StorageParsing.ParseJob(Fixtures.Read("job-started.xml"))));
        Assert.Equal(("15", true, 0), Read(StorageParsing.ParseJob(Fixtures.Read("job-running.xml"))));
        Assert.True(StorageParsing.ParseJob(Fixtures.Read("job-done.xml")).Finished);
        var unknown = StorageParsing.ParseJob(Fixtures.Read("job-unknown.xml"));
        Assert.False(unknown.Ok);
        Assert.Equal("The camera could not finish it: no such job", StorageTexts.JobFailed(unknown.Description));

        StorageParsing.EnsurePropertiesSuccess(Fixtures.Read("properties-success.xml"));
        var refused = Assert.Throws<StorageException>(() => StorageParsing.EnsurePropertiesSuccess(Fixtures.Read("properties-invalid-max-age.xml")));
        Assert.Equal((170, "The camera does not accept this retention (0 to 7,000 days)"), (refused.Code, refused.Message));

        static (string?, bool, int) Read(DiskJob job) => (job.JobId, job.Ok, job.Progress);
    }

    [Fact]
    public void Sizes_and_retention_read_like_the_card_label()
    {
        Assert.Equal("59.3 GB", StorageFormat.Size(57922892));
        Assert.Equal(("No limit", "1 day", "7,000 days"), (StorageTaskPlugin.RetentionText(0), StorageTaskPlugin.RetentionText(1), StorageTaskPlugin.RetentionText(7000)));
    }
}

public sealed class TaskTests
{
    private static async Task<(RecordingContext Ctx, Exception? Error)> RunAsync(SdCamera camera, TaskPayload payload, IDeviceInfo? device = null)
    {
        var ctx = new RecordingContext(camera);
        var task = new StorageTaskPlugin(null, pollInterval: TimeSpan.FromMilliseconds(1));
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, device ?? new FakeDevice(), EdgeStorageJson.Serialize(payload), CancellationToken.None));
            return (ctx, null);
        }
        catch (Exception ex)
        {
            return (ctx, ex);
        }
    }

    [Fact]
    public async Task Format_unmounts_formats_and_mounts_the_card_again()
    {
        var camera = new SdCamera();
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Format));

        Assert.Null(error);
        Assert.Equal(["Check compatibility: Done", "Read the SD card: Done", "Unmount the SD card: Done", "Format the SD card: Done", "Mount the SD card: Done", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.True(camera.Mounted);
        Assert.Contains("POST axis-cgi/disks/format.cgi diskid=SD_DISK", camera.Requests);
        Assert.Equal("Format SD card", new StorageTaskPlugin(null).GetTaskName(EdgeStorageJson.Serialize(new TaskPayload(StorageActions.Format))));
    }

    [Theory]
    [InlineData(StorageActions.Check, "Check the SD card", "checkdisk.cgi")]
    [InlineData(StorageActions.Repair, "Repair the SD card", "repair.cgi")]
    public async Task Check_and_repair_run_on_the_unmounted_card(string action, string step, string cgi)
    {
        var camera = new SdCamera();
        var (ctx, error) = await RunAsync(camera, new TaskPayload(action));

        Assert.Null(error);
        Assert.Contains(step + ": Done", StepRun.Lines(ctx.Steps));
        Assert.Contains(camera.Requests, r => r.EndsWith(cgi + " diskid=SD_DISK", StringComparison.Ordinal));
        Assert.True(camera.Mounted);
    }

    [Fact]
    public async Task A_failed_job_fails_its_step_and_the_card_is_mounted_again()
    {
        var camera = new SdCamera { FailJob = "format" };
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Format));

        Assert.IsType<StorageException>(error);
        Assert.Equal(["Check compatibility: Done", "Read the SD card: Done", "Unmount the SD card: Done", "Format the SD card: Failed", "Mount the SD card: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal("The camera could not finish it: Disk error", StepRun.Detail(ctx.Steps, "Format the SD card"));
        Assert.True(camera.Mounted);
    }

    [Fact]
    public async Task Encryption_on_sends_the_passphrase_in_the_body_and_formats()
    {
        var camera = new SdCamera();
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.EncryptionOn, Passphrase: "secret phrase&1"));

        Assert.Null(error);
        Assert.Equal(["Check compatibility: Done", "Read the SD card: Done", "Unmount the SD card: Done", "Turn on encryption: Done", "Format the SD card: Done", "Mount the SD card: Done", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.True(camera.Encrypted && camera.Mounted);
        Assert.Contains("POST axis-cgi/disks/properties/enablediskencryption.cgi schemaversion=1&diskid=SD_DISK&passphrase=secret+phrase%261", camera.Requests);
        Assert.DoesNotContain(camera.Requests, r => r.StartsWith("GET", StringComparison.Ordinal) && r.Contains("passphrase", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", new TaskPayload(StorageActions.EncryptionOn, Passphrase: "secret").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lost_unmount_answer_still_mounts_the_card_again()
    {
        var camera = new SdCamera { UnmountAnswerLost = true };
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Format));

        Assert.IsType<StorageException>(error);
        Assert.Equal(["Check compatibility: Done", "Read the SD card: Done", "Unmount the SD card: Failed", "Mount the SD card: Done", "Format the SD card: Skipped"], StepRun.Lines(ctx.Steps));
        Assert.True(camera.Mounted);
        Assert.DoesNotContain(camera.Requests, r => r.Contains("format.cgi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_mount_after_a_failed_format_keeps_the_format_error()
    {
        var camera = new SdCamera { FailJob = "format", FailMount = true };
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Format));

        Assert.Equal("The camera could not finish it: Disk error", error?.Message);
        Assert.Contains("Mount the SD card: Failed", StepRun.Lines(ctx.Steps));
        Assert.Contains(ctx.Logs, l => l.Level == TaskLogLevel.Error && l.Message.StartsWith("Mount the SD card:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_few_lost_status_answers_do_not_stop_a_running_format()
    {
        var camera = new SdCamera { FailingPolls = 3 };
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Format));

        Assert.Null(error);
        Assert.Contains("Format the SD card: Done", StepRun.Lines(ctx.Steps));
        Assert.True(camera.Mounted);
    }

    [Fact]
    public async Task Encryption_on_an_encrypted_card_changes_nothing()
    {
        var camera = new SdCamera { EncryptionEnabled = true, Encrypted = true };
        var (_, error) = await RunAsync(camera, new TaskPayload(StorageActions.EncryptionOn, Passphrase: "secret"));

        Assert.Equal("The SD card is encrypted already. Nothing was changed.", error?.Message);
        Assert.DoesNotContain(camera.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Encryption_off_on_an_encrypted_card_formats_it_plain()
    {
        var camera = new SdCamera { EncryptionEnabled = true, Encrypted = true };
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.EncryptionOff));

        Assert.Null(error);
        Assert.Contains("Turn off encryption: Done", StepRun.Lines(ctx.Steps));
        Assert.False(camera.Encrypted || camera.EncryptionEnabled);
        Assert.True(camera.Mounted);
    }

    [Fact]
    public async Task Encryption_off_on_an_unencrypted_card_changes_nothing()
    {
        var camera = new SdCamera();
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.EncryptionOff));

        Assert.Equal("The SD card is not encrypted. Nothing was changed.", error?.Message);
        Assert.DoesNotContain(camera.Requests, r => r.Contains("mount.cgi", StringComparison.Ordinal));
        Assert.Contains("Unmount the SD card: Skipped", StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Retention_is_set_and_checked_and_an_unchanged_value_is_skipped()
    {
        var camera = new SdCamera();
        var (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Retention, RetentionDays: 0));
        Assert.Null(error);
        Assert.Equal(0, camera.MaxAge);
        Assert.Equal("No limit", StepRun.Detail(ctx.Steps, "Set the retention"));
        Assert.DoesNotContain(camera.Requests, r => r.Contains("mount.cgi", StringComparison.Ordinal)); // the card stays in use

        (ctx, error) = await RunAsync(camera, new TaskPayload(StorageActions.Retention, RetentionDays: 0));
        Assert.Null(error);
        Assert.Contains("Set the retention: Skipped", StepRun.Lines(ctx.Steps));
        Assert.Equal("Remove SD card retention limit", StorageActions.TaskName(StorageActions.Retention, 0));
        Assert.Equal("Set SD card retention to 30 days", StorageActions.TaskName(StorageActions.Retention, 30));
    }

    [Fact]
    public async Task Without_a_card_or_the_API_nothing_is_changed()
    {
        var empty = new SdCamera { HasCard = false };
        var (ctx, error) = await RunAsync(empty, new TaskPayload(StorageActions.Format));
        Assert.Equal("No SD card. Nothing was changed.", error?.Message);
        Assert.Equal("Read the SD card: Failed", StepRun.Lines(ctx.Steps)[1]);

        var old = new SdCamera { Apis = [] };
        (_, error) = await RunAsync(old, new TaskPayload(StorageActions.Format));
        Assert.IsType<DeviceNotCompatibleException>(error);
        Assert.DoesNotContain(old.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("retention", 7001, null, "Enter 0 to 7,000 days. Nothing was changed.")]
    [InlineData("retention", null, null, "Enter 0 to 7,000 days. Nothing was changed.")]
    [InlineData("encryptionOn", null, "", "Enter a passphrase. Nothing was changed.")]
    [InlineData("wipe", null, null, "Unknown action. Nothing was changed.")]
    public async Task A_bad_payload_is_refused_before_any_request(string action, int? days, string? passphrase, string message)
    {
        var camera = new SdCamera();
        var (_, error) = await RunAsync(camera, new TaskPayload(action, days, passphrase));
        Assert.Equal(message, error?.Message);
        Assert.Empty(camera.Requests);
    }
}

public sealed class PluginTests(ITestOutputHelper output)
{
    private static (EdgeStoragePlugin Plugin, FakeDevices Devices, FakeCameras Cameras, RecordingRunner Runner, RecordingEvents Events) Create()
    {
        var plugin = new EdgeStoragePlugin(new DeviceSweepOptions { PublishInterval = TimeSpan.FromHours(1) });
        var devices = new FakeDevices();
        var cameras = new FakeCameras();
        var runner = new RecordingRunner();
        var events = new RecordingEvents();
        plugin.StartAsync(new FakeContext(devices, cameras, runner, events), CancellationToken.None).GetAwaiter().GetResult();
        return (plugin, devices, cameras, runner, events);
    }

    [Fact]
    public async Task The_table_lists_every_camera_with_a_slot_and_says_what_is_wrong()
    {
        var (plugin, devices, cameras, _, _) = Create();
        await using var _ = plugin;
        var ok = new FakeDevice { Address = "10.0.0.48" };
        var worn = new FakeDevice { Address = "10.0.0.49" };
        var empty = new FakeDevice { Address = "10.0.0.50" };
        var unmounted = new FakeDevice { Address = "10.0.0.51" };
        var locked = new FakeDevice { Address = "10.0.0.52", Status = DeviceStatus.CredentialsRequired };
        var speaker = new FakeDevice { Address = "10.0.0.60", Apis = [] }; // no slot: not in the table
        devices.Items.AddRange([ok, worn, empty, unmounted, locked, speaker]);
        cameras.Cameras[worn.Id] = new SdCamera { Wear = 95 };
        cameras.Cameras[empty.Id] = new SdCamera { HasCard = false };
        cameras.Cameras[unmounted.Id] = new SdCamera { Mounted = false };

        await plugin.InvokeAsync(EdgeStorageMethods.Read, null, CancellationToken.None);
        await plugin.Sweep!.Current;
        var state = EdgeStorageJson.Deserialize<StorageState>(await plugin.InvokeAsync(EdgeStorageMethods.GetState, null, CancellationToken.None));

        Assert.Equal(["10.0.0.48", "10.0.0.49", "10.0.0.50", "10.0.0.51", "10.0.0.52"], state.Rows.Select(r => r.Address));
        Assert.Equal((1, 1, 1, 1, 1), (state.Ok, state.Warnings, state.Problems, state.NoCard, state.Failed));
        var row = state.Rows[0];
        Assert.Equal((CardStates.Ok, 57922892L, 1, 90, 7, false, true, 0), (row.State, row.TotalKb, row.WearPercent, row.WearAlertPercent, row.RetentionDays, row.Encrypted, row.Mounted, row.UsedPercent));
        Assert.StartsWith("Worn 95 % (the camera warns at 90 %)", state.Rows[1].Text, StringComparison.Ordinal);
        Assert.Equal(("The SD card is not in use", false), (state.Rows[3].Text, state.Rows[3].Mounted));
        Assert.Equal(DeviceMessages.CredentialsRequired, state.Rows[4].Text);
        Assert.False(cameras.Cameras.ContainsKey(locked.Id)); // not contacted
    }

    [Fact]
    public async Task Each_method_needs_its_right_and_starts_one_task_per_camera()
    {
        var (plugin, _, _, runner, _) = Create();
        await using var _ = plugin;
        Assert.Equal(("show", "show", "manage", "format"), (plugin.RequiredPermission("read"), plugin.RequiredPermission("getState"), plugin.RequiredPermission("change"), plugin.RequiredPermission("format")));
        var format = plugin.Permissions.Single(p => p.Key == "format");
        Assert.True(format.FullDeviceControl && !format.OperatorDefault);
        Assert.True(plugin.Permissions.Single(p => p.Key == "manage").OperatorDefault);
        Assert.False(Assert.Single(plugin.TaskPlugins).ShowInMenus);

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await plugin.InvokeAsync("change", EdgeStorageJson.Serialize(new RunRequest(StorageActions.Retention, ids, 30, Owner: "joe@pc")), CancellationToken.None);
        var run = Assert.Single(runner.Runs);
        Assert.Equal((EdgeStorageInfo.TaskPluginId, 2, "joe@pc"), (run.PluginId, run.DeviceIds.Count, run.Owner));

        // A format through "change" (or a retention through "format") is refused: the right belongs to the action.
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("change", EdgeStorageJson.Serialize(new RunRequest(StorageActions.Format, ids)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("format", EdgeStorageJson.Serialize(new RunRequest(StorageActions.Retention, ids, 30)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("format", EdgeStorageJson.Serialize(new RunRequest(StorageActions.EncryptionOn, ids, Passphrase: "")), CancellationToken.None));
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task A_failing_device_list_ends_the_read_and_the_plugin_still_stops()
    {
        var plugin = new EdgeStoragePlugin(new DeviceSweepOptions { PublishInterval = TimeSpan.FromHours(1) });
        var events = new RecordingEvents();
        await plugin.StartAsync(new FakeContext(new ThrowingDevices(), new FakeCameras(), new RecordingRunner(), events), CancellationToken.None);

        await plugin.InvokeAsync(EdgeStorageMethods.Read, null, CancellationToken.None);
        await plugin.Sweep!.Current;
        var state = EdgeStorageJson.Deserialize<StorageState>(await plugin.InvokeAsync(EdgeStorageMethods.GetState, null, CancellationToken.None));

        Assert.False(state.Reading);
        await plugin.DisposeAsync(); // does not throw
    }

    private sealed class ThrowingDevices : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => throw new InvalidOperationException("database gone");

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("database gone");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Thirty_thousand_cameras_are_read_in_one_sweep()
    {
        var (plugin, devices, cameras, _, events) = Create();
        await using var _ = plugin;
        for (var i = 0; i < Oadm.Sdk.Devices.DeviceLimits.MaxDevices; i++)
        {
            devices.Items.Add(new FakeDevice { Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}" });
        }

        var watch = Stopwatch.StartNew();
        await plugin.InvokeAsync(EdgeStorageMethods.Read, null, CancellationToken.None);
        await plugin.Sweep!.Current;
        plugin.Sweep.Publish();
        output.WriteLine($"30,000 cameras read in {watch.ElapsedMilliseconds} ms");
        Assert.Equal(Oadm.Sdk.Devices.DeviceLimits.MaxDevices, plugin.Sweep.Count(r => r.State == CardStates.Ok));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(30, events.Published.Count(e => e.Topic == EdgeStorageMethods.RowsTopic)); // 1,000 rows per event
    }
}
