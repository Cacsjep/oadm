using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Acap.Tests;

public sealed class AcapTaskPluginTests
{
    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);

    private static (FakeAcapDevice Device, RecordingContext Ctx, InMemoryUploadedFiles Files) Setup()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        return (device, new RecordingContext(device, files), files);
    }

    private static readonly string[] Prepared =
        ["Check compatibility: Done", "Read package: Done", "Read device info: Done", "Read embedded development version: Done", "Read unsigned application setting: Done", "Read installed applications: Done", "Check compatibility of package: Done"];

    /// <summary>Runs the task the way the server does (fresh steps per run), so its steps end like in the task engine.</summary>
    private static Task Run(RecordingContext ctx, string? payload) =>
        StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    private static string InstallPayload(UploadedFile file, bool allowDowngrade = false, bool start = false, string? app = null) =>
        new AcapPayload { Action = AcapAction.Install, FileId = file.Id, Sha256 = file.Sha256, Application = app, AllowDowngrade = allowDowngrade, StartAfterInstall = start }.ToJson();

    [Fact]
    public void Declares_a_dialog_task_in_the_context_menu()
    {
        Assert.Equal("oadm.acap", Plugin.Id);
        Assert.Equal("Applications (ACAP)...", Plugin.DisplayName);
        Assert.True(Plugin.RequiresDialog);
        Assert.False(Plugin.ShowInToolbar);
    }

    [Fact]
    public void CanRun_needs_the_application_api_in_the_cached_list()
    {
        Assert.True(Plugin.CanRun(new FakeDevice()));
        Assert.True(Plugin.CanRun(new FakeDevice { Apis = [new("application", "1.3")] }));
        Assert.False(Plugin.CanRun(new FakeDevice { Apis = [] }));
        Assert.False(Plugin.CanRun(new FakeDevice { Apis = [new("application", "2.0")] }));
        Assert.False(Plugin.CanRun(new FakeDevice { Apis = [new("packagemanager", "1.4")] }));
        Assert.False(Plugin.CanRun(new FakeDevice { Status = DeviceStatus.Unreachable }));
        Assert.False(Plugin.CanRun(new FakeDevice { Status = DeviceStatus.CredentialsRequired }));
    }

    [Fact]
    public async Task Execute_rechecks_the_fresh_api_list_before_any_write()
    {
        var (device, ctx, files) = Setup();
        device.Apis.Clear();
        device.Apis.Add(new DeviceApi("application", "2.0"));
        device.Add("hello", "1.0.0");
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest()));

        foreach (var payload in new[] { InstallPayload(file), new AcapPayload { Action = AcapAction.Remove, Application = "hello" }.ToJson() })
        {
            var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => Run(ctx, payload));
            Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(2, device.ApiListCalls);
        Assert.Empty(device.Writes);
        Assert.Equal(["Check compatibility: Failed", "Read installed applications: Skipped", "Remove application: Skipped", "Verify removal: Skipped"], StepRun.Lines(ctx.Steps));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("""{"action":"remove"}""")]
    [InlineData("""{"action":"start","application":"a&b"}""")]
    [InlineData("""{"action":"install"}""")]
    [InlineData("""{"action":"explode","application":"x"}""")]
    public async Task Invalid_payloads_fail_without_touching_the_device(string? payload)
    {
        var (device, ctx, _) = Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => Run(ctx, payload));
        Assert.Empty(device.Requests);
        Assert.Equal(0, device.ApiListCalls);
    }

    [Fact]
    public async Task Installs_a_new_application_verifies_and_starts_it()
    {
        var (device, ctx, files) = Setup();
        var eap = EapBuilder.FromManifest(EapBuilder.Manifest(appName: "hello", version: "1.2.0"));
        var file = files.Add("hello_1_2_0_aarch64.eap", eap);

        await Run(ctx, InstallPayload(file, start: true, app: "hello"));

        Assert.Equal(["upload", "start hello"], device.Writes);
        Assert.Equal(eap.Length, device.UploadedBytes);
        Assert.Equal("hello_1_2_0_aarch64.eap", device.UploadedFileName);
        Assert.True(device.Apps["hello"].IsRunning);
        Assert.Empty(ctx.Progress); // progress is derived from the steps
        Assert.Equal([.. Prepared, "Upload package: Done", "Verify installation: Done", "Start application: Done", "Verify application state: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal("hello 1.2.0, aarch64", StepRun.Detail(ctx.Steps, "Read package"));
        Assert.Equal("AXIS OS 12.11.77, aarch64", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("Installed Hello World 1.2.0 (Stopped)", StepRun.Detail(ctx.Steps, "Verify installation"));
        Assert.Equal("Hello World is running", StepRun.Detail(ctx.Steps, "Verify application state"));
        Assert.Contains(ctx.Logs, l => l.Message.Contains("New install on AXIS OS 12.11.77 (aarch64)", StringComparison.Ordinal));
        Assert.Empty(ctx.Warnings);
    }

    [Fact]
    public async Task Upgrades_an_installed_application()
    {
        var (device, ctx, files) = Setup();
        device.Add("hello", "1.0.0", status: "Running");
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "1.2.0")));

        await Run(ctx, InstallPayload(file, start: true));

        Assert.Equal(["upload"], device.Writes);
        Assert.Equal("1.2.0", device.Apps["hello"].Version);
        Assert.Contains(ctx.Logs, l => l.Message.StartsWith("Upgrade from 1.0.0", StringComparison.Ordinal));
        Assert.StartsWith("Upgraded", StepRun.Detail(ctx.Steps, "Verify installation"), StringComparison.Ordinal);
        Assert.Equal([.. Prepared, "Upload package: Done", "Verify installation: Done", "Start application: Skipped", "Verify application state: Skipped"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Refuses_a_downgrade_without_the_option()
    {
        var (device, ctx, files) = Setup();
        device.Add("hello", "2.0.0");
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "1.2.0")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, InstallPayload(file)));

        Assert.Contains("downgrade option", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
        Assert.Equal("2.0.0", device.Apps["hello"].Version);
        Assert.Equal(
            ["Check compatibility: Done", "Read package: Done", "Read device info: Done", "Read embedded development version: Done", "Read unsigned application setting: Done", "Read installed applications: Done", "Check compatibility of package: Failed", "Upload package: Skipped", "Verify installation: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Check compatibility of package"));
    }

    [Fact]
    public async Task Downgrades_with_the_explicit_option()
    {
        var (device, ctx, files) = Setup();
        device.Add("hello", "2.0.0");
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "1.2.0")));

        await Run(ctx, InstallPayload(file, allowDowngrade: true));

        Assert.Equal("1.2.0", device.Apps["hello"].Version);
        Assert.StartsWith("Downgraded", StepRun.Detail(ctx.Steps, "Verify installation"), StringComparison.Ordinal);
        Assert.Equal([.. Prepared, "Upload package: Done", "Verify installation: Done"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Refuses_the_wrong_architecture()
    {
        var (device, ctx, files) = Setup();
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest(architecture: "armv7hf")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, InstallPayload(file)));

        Assert.Contains("built for armv7hf, the device is aarch64", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
        Assert.Contains(ctx.Logs, l => l.Level == TaskLogLevel.Error);
    }

    [Fact]
    public async Task Refuses_too_old_firmware()
    {
        var (device, ctx, files) = Setup();
        device.Firmware = "11.11.124";
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest(schema: "1.11.0", osMin: "12.11", osMax: "99")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, InstallPayload(file)));

        Assert.Contains("AXIS OS 11.11.124 is not supported", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task Refuses_a_damaged_package()
    {
        var (device, ctx, files) = Setup();
        var file = files.Add("broken.eap", "not a tarball"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, InstallPayload(file)));

        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
        Assert.Equal("Read package: Failed", StepRun.Lines(ctx.Steps)[1]);
        Assert.All(StepRun.Lines(ctx.Steps).Skip(2), l => Assert.EndsWith(": Skipped", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refuses_a_file_that_differs_from_the_dialog()
    {
        var (device, ctx, files) = Setup();
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest()));

        var wrongHash = new AcapPayload { Action = AcapAction.Install, FileId = file.Id, Sha256 = new string('0', 64) }.ToJson();
        var wrongName = new AcapPayload { Action = AcapAction.Install, FileId = file.Id, Application = "other" }.ToJson();
        var missing = new AcapPayload { Action = AcapAction.Install, FileId = "nope" }.ToJson();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, wrongHash));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, wrongName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, missing));
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task Device_upload_error_fails_with_its_message()
    {
        var (device, ctx, files) = Setup();
        device.UploadReply = "Error: 2";
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest()));

        var ex = await Assert.ThrowsAsync<AcapDeviceException>(() => Run(ctx, InstallPayload(file)));

        Assert.Contains("signature", ex.Message, StringComparison.Ordinal);
        Assert.Equal([.. Prepared, "Upload package: Failed", "Verify installation: Skipped"], StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Upload package"));
    }

    [Fact]
    public async Task Lost_upload_answer_is_verified_through_the_list()
    {
        var (device, ctx, files) = Setup();
        device.UploadTimesOut = true;
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "1.2.0")));

        await Run(ctx, InstallPayload(file));

        Assert.Contains(ctx.Warnings, w => w.Contains("not confirmed in time", StringComparison.Ordinal));
        Assert.Equal([.. Prepared, "Upload package: Done", "Verify installation: Warning"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Warns_when_the_application_does_not_start()
    {
        var (device, ctx, files) = Setup();
        device.StartHasNoEffect = true;
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest()));

        await Run(ctx, InstallPayload(file, start: true));

        Assert.Contains(ctx.Warnings, w => w.Contains("after the start command", StringComparison.Ordinal));
        Assert.Equal([.. Prepared, "Upload package: Done", "Verify installation: Done", "Start application: Done", "Verify application state: Warning"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Removes_an_application()
    {
        var (device, ctx, _) = Setup();
        device.Add("hello", "1.0.0", status: "Running");

        await Run(ctx, new AcapPayload { Action = AcapAction.Remove, Application = "hello" }.ToJson());

        Assert.Equal(["remove hello"], device.Writes);
        Assert.False(device.Apps.ContainsKey("hello"));
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Remove application: Done", "Verify removal: Done"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Remove_of_a_missing_application_is_done_with_warning()
    {
        var (device, ctx, _) = Setup();

        await Run(ctx, new AcapPayload { Action = AcapAction.Remove, Application = "hello" }.ToJson());

        Assert.Empty(device.Writes);
        Assert.Contains(ctx.Warnings, w => w.Contains("not installed", StringComparison.Ordinal));
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Warning", "Remove application: Skipped", "Verify removal: Skipped"], StepRun.Lines(ctx.Steps));
        Assert.Equal("Not installed.", StepRun.Detail(ctx.Steps, "Remove application"));
    }

    [Fact]
    public async Task Refuses_to_remove_a_bundled_application()
    {
        var (device, ctx, _) = Setup();
        device.Add("objectanalytics", "1.26.205", status: "Running", bundled: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(ctx, new AcapPayload { Action = AcapAction.Remove, Application = "objectanalytics" }.ToJson()));

        Assert.Contains("bundled", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Failed", "Remove application: Skipped", "Verify removal: Skipped"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Starts_and_stops_an_application()
    {
        var (device, ctx, _) = Setup();
        device.Add("hello", "1.0.0");

        await Run(ctx, new AcapPayload { Action = AcapAction.Start, Application = "hello" }.ToJson());
        Assert.True(device.Apps["hello"].IsRunning);

        await Run(ctx, new AcapPayload { Action = AcapAction.Stop, Application = "hello" }.ToJson());
        Assert.False(device.Apps["hello"].IsRunning);
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Stop application: Done", "Verify application state: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal("hello nice is Stopped", StepRun.Detail(ctx.Steps, "Verify application state"));

        Assert.Equal(["start hello", "stop hello"], device.Writes);
    }

    [Fact]
    public async Task Start_of_a_running_application_changes_nothing()
    {
        var (device, ctx, _) = Setup();
        device.Add("hello", "1.0.0", status: "Running");

        await Run(ctx, new AcapPayload { Action = AcapAction.Start, Application = "hello" }.ToJson());

        Assert.Empty(device.Writes);
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Start application: Skipped", "Verify application state: Skipped"], StepRun.Lines(ctx.Steps));
        Assert.Contains("already running", StepRun.Detail(ctx.Steps, "Start application"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_start_fails_the_device()
    {
        var (device, ctx, _) = Setup();
        device.StartHasNoEffect = true;
        device.Add("hello", "1.0.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(ctx, new AcapPayload { Action = AcapAction.Start, Application = "hello" }.ToJson()));
        Assert.Contains("did not start", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Done", "Start application: Done", "Verify application state: Failed"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Start_on_a_device_without_the_application_is_skipped_with_warning()
    {
        var (device, ctx, _) = Setup();

        await Run(ctx, new AcapPayload { Action = AcapAction.Stop, Application = "hello" }.ToJson());

        Assert.Empty(device.Writes);
        Assert.Single(ctx.Warnings);
        Assert.Equal(["Check compatibility: Done", "Read installed applications: Warning", "Stop application: Skipped", "Verify application state: Skipped"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Query_lists_applications_and_device_facts_read_only()
    {
        var (device, ctx, _) = Setup();
        device.Add("hello", "1.0.0", status: "Running");
        device.Add("objectanalytics", "1.26.205", bundled: true);

        var json = await Plugin.QueryAsync(ctx, new FakeDevice(), AcapPlugin.ListApplicationsMethod, null, CancellationToken.None);
        var result = ListApplicationsResult.FromJson(json!);

        Assert.Equal(2, result.Applications.Count);
        Assert.Equal("aarch64", result.Device.Architecture);
        Assert.Equal("12.11.77", result.Device.FirmwareVersion);
        Assert.True(result.Device.AllowUnsigned);
        Assert.True(result.Applications.Single(a => a.Name == "hello").IsRunning);
        Assert.Empty(device.Writes);
        Assert.Equal(1, device.ApiListCalls);
    }

    [Fact]
    public async Task Query_rejects_unknown_methods_and_incompatible_devices()
    {
        var (device, ctx, _) = Setup();
        await Assert.ThrowsAsync<NotSupportedException>(() => Plugin.QueryAsync(ctx, new FakeDevice(), "removeEverything", null, CancellationToken.None));

        device.Apis.Clear();
        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => Plugin.QueryAsync(ctx, new FakeDevice(), AcapPlugin.ListApplicationsMethod, null, CancellationToken.None));
        Assert.Empty(device.Writes);
    }
}
