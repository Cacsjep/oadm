using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Reading;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>The server part: reads on demand through the caller's view, the rows per session, the actions as oadm.acap tasks.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private readonly FakeCoreContext _ctx = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
    private AppManagementPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new AppManagementPlugin(new AppManagementOptions { Time = _time, EventInterval = TimeSpan.FromHours(1) });
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private static readonly InstalledApplication Vmd = Samples.App("vmd", "AXIS Video Motion Detection", "4.5.2", bundled: true);
    private static readonly InstalledApplication Lpv = Samples.App("lpv", "AXIS License Plate Verifier", "2.10", "Stopped", "Missing");

    private FakeDevice Add(string address, DeviceStatus status = DeviceStatus.Ok, bool api = true, FakeCamera? camera = null) =>
        _ctx.Add(Samples.ManagedDevice(address, status, api), camera ?? Samples.Camera(Vmd, Lpv));

    private async Task<T> CallAsync<T>(ICallContext call, string method, object? payload)
    {
        var json = await _ctx.InvokeAsync(_plugin, call, method, payload is null ? null : AppManagementJson.Serialize(payload));
        return AppManagementJson.Deserialize<T>(json);
    }

    /// <summary>Starts a read and waits for its end (the session's state), then returns every row.</summary>
    private async Task<IReadOnlyList<DeviceApps>> ReadAsync(ICallContext call, ReadRequest request)
    {
        await CallAsync<ReadState>(call, AppManagementMethods.Read, request);
        await Eventually.TrueAsync(async () => !(await CallAsync<ReadState>(call, AppManagementMethods.GetState, null)).Reading, "the read ended");
        return (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices;
    }

    [Fact]
    public async Task A_read_lists_the_applications_with_one_request_and_never_writes()
    {
        var device = Add("10.0.0.48");
        var rows = await ReadAsync(_ctx.CallAsAllDevices(), new ReadRequest { DeviceIds = [device.Id] });

        var row = Assert.Single(rows);
        Assert.Null(row.Error);
        Assert.Equal(["lpv", "vmd"], row.Applications.Select(a => a.Name).Order());
        Assert.True(row.Applications.Single(a => a.Name == "vmd").Bundled);
        var camera = _ctx.Cameras[device.Id];
        // list.cgi, then AllowUnsigned and the embedded development version for the install dialog's checks: reads only.
        Assert.Equal(3, camera.Requests.Count);
        Assert.Equal("POST " + ApplicationApiClient.ListPath, camera.Requests[0]);
        Assert.StartsWith("POST /axis-cgi/applications/config.cgi?action=get&name=AllowUnsigned", camera.Requests[1], StringComparison.Ordinal);
        Assert.StartsWith("GET /axis-cgi/param.cgi?action=list&", camera.Requests[2], StringComparison.Ordinal);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task A_read_needs_a_choice()
    {
        Add("10.0.0.48");
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<ReadState>(_ctx.CallAsAllDevices(), AppManagementMethods.Read, new ReadRequest()));
        Assert.Empty(_ctx.FakeJobs.All);
        Assert.All(_ctx.Cameras.Values, c => Assert.Empty(c.Requests));
    }

    [Fact]
    public async Task Devices_that_are_not_ok_are_not_read()
    {
        var device = Add("10.0.0.48", DeviceStatus.CredentialsRequired);
        var row = Assert.Single(await ReadAsync(_ctx.CallAsAllDevices(), new ReadRequest { All = true }));

        Assert.True(row.NotChecked);
        Assert.Equal("Not checked: Credentials required", row.Error);
        Assert.Empty(_ctx.Cameras[device.Id].Requests);
    }

    [Fact]
    public async Task A_device_without_the_application_api_gets_its_row_without_a_request()
    {
        var device = Add("10.0.0.48", api: false);
        var row = Assert.Single(await ReadAsync(_ctx.CallAsAllDevices(), new ReadRequest { All = true }));

        Assert.Equal(AppManagementInfo.NoApplicationApi, row.Error);
        Assert.False(row.NotChecked);
        Assert.Empty(_ctx.Cameras[device.Id].Requests);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_device_with_the_error()
    {
        var camera = new FakeCamera { Status = System.Net.HttpStatusCode.InternalServerError };
        Add("10.0.0.48", camera: camera);
        var row = Assert.Single(await ReadAsync(_ctx.CallAsAllDevices(), new ReadRequest { All = true }));

        Assert.Equal(DeviceMessages.ServerError(500), row.Error);
        Assert.Empty(row.Applications);
    }

    [Fact]
    public async Task A_group_user_reads_only_the_devices_of_the_group()
    {
        var mine = Add("10.0.0.48");
        var other = Add("10.0.0.49");
        var call = _ctx.CallAsGroup([mine.Id]);

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => CallAsync<ReadState>(call, AppManagementMethods.Read, new ReadRequest { DeviceIds = [mine.Id, other.Id] }));
        Assert.Empty(_ctx.Cameras[other.Id].Requests);

        var rows = await ReadAsync(call, new ReadRequest { All = true });
        Assert.Equal([mine.Id], rows.Select(r => r.DeviceId));
        Assert.Empty(_ctx.Cameras[other.Id].Requests);
        Assert.Empty(_ctx.RefusedAccesses);
    }

    [Fact]
    public async Task The_rows_belong_to_the_session_and_go_only_to_its_pages()
    {
        var device = Add("10.0.0.48");
        var first = _ctx.CallAsAllDevices();
        await ReadAsync(first, new ReadRequest { DeviceIds = [device.Id] });

        var second = _ctx.CallAsAllDevices();
        Assert.Empty((await CallAsync<RowsPage>(second, AppManagementMethods.GetRows, new RowsRequest())).Devices);
        Assert.Contains(_ctx.PublishedEvents, e => e.Topic == AppManagementMethods.RowsTopic);
        Assert.All(_ctx.PublishedEvents, e => Assert.Equal(first.SessionId, e.Audience.SessionId));
    }

    [Fact]
    public async Task A_new_choice_drops_the_rows_of_devices_no_longer_chosen()
    {
        var a = Add("10.0.0.48");
        var b = Add("10.0.0.49");
        var call = _ctx.CallAsAllDevices();
        Assert.Equal(2, (await ReadAsync(call, new ReadRequest { All = true })).Count);

        var rows = await ReadAsync(call, new ReadRequest { DeviceIds = [b.Id] });
        Assert.Equal([b.Id], rows.Select(r => r.DeviceId));
        Assert.Contains(_ctx.PublishedEvents, e => e.Topic == AppManagementMethods.RowsTopic && e.PayloadJson!.Contains(a.Id.ToString(), StringComparison.Ordinal) && e.PayloadJson.Contains("removed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AcapAction.Start)]
    [InlineData(AcapAction.Stop)]
    [InlineData(AcapAction.Remove)]
    public async Task An_action_starts_one_acap_task_run_per_application_with_the_dialogs_payload_and_never_writes(AcapAction action)
    {
        var a = Add("10.0.0.48");
        var b = Add("10.0.0.49");
        var call = _ctx.CallAsAllDevices();
        var request = new RunRequest
        {
            Action = action,
            Owner = "admin@pc",
            Items =
            [
                new RunItem { Application = "lpv", DisplayName = "AXIS License Plate Verifier", DeviceIds = [a.Id, b.Id] },
                new RunItem { Application = "vmd", DisplayName = "AXIS Video Motion Detection", DeviceIds = [b.Id] },
            ],
        };

        var reply = await CallAsync<RunReply>(call, AppManagementMethods.Run, request);

        Assert.Equal(3, reply.TaskIds.Count);
        Assert.Equal(2, _ctx.TaskRuns.Count);
        Assert.All(_ctx.TaskRuns, r => Assert.Equal(AcapPlugin.Id, r.PluginId));
        var lpv = AcapPayload.Parse(_ctx.TaskRuns[0].PayloadJson);
        Assert.Equal(action, lpv.Action);
        Assert.Equal("lpv", lpv.Application);
        Assert.Equal("AXIS License Plate Verifier", lpv.AppDisplayName);
        Assert.Equal([a.Id, b.Id], _ctx.TaskRuns[0].DeviceIds);
        Assert.Equal("vmd", AcapPayload.Parse(_ctx.TaskRuns[1].PayloadJson).Application);
        Assert.Equal([b.Id], _ctx.TaskRuns[1].DeviceIds);
        Assert.All(_ctx.Cameras.Values, c => Assert.Empty(c.Requests));
    }

    [Fact]
    public async Task An_action_the_role_lacks_starts_nothing()
    {
        var a = Add("10.0.0.48");
        var limited = new DeniedCall(_ctx.CallAsAllDevices(), PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Remove));
        var request = new RunRequest { Action = AcapAction.Remove, Items = [new RunItem { Application = "lpv", DeviceIds = [a.Id] }] };

        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => CallAsync<RunReply>(limited, AppManagementMethods.Run, request));
        Assert.Equal("Your role does not allow: Remove applications.", refused.Message);
        Assert.Empty(_ctx.TaskRuns);
    }

    [Fact]
    public async Task Installs_and_invalid_names_are_refused_before_any_task()
    {
        var a = Add("10.0.0.48");
        var call = _ctx.CallAsAllDevices();
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<RunReply>(call, AppManagementMethods.Run,
            new RunRequest { Action = AcapAction.Install, Items = [new RunItem { Application = "lpv", DeviceIds = [a.Id] }] }));
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<RunReply>(call, AppManagementMethods.Run,
            new RunRequest { Action = AcapAction.Stop, Items = [new RunItem { Application = "../x", DeviceIds = [a.Id] }] }));
        Assert.Empty(_ctx.TaskRuns);
    }

    [Fact]
    public async Task Install_starts_one_install_task_run_with_the_uploaded_packages_and_follows_the_whole_device()
    {
        var a = Add("10.0.0.48");
        var b = Add("10.0.0.49");
        var call = _ctx.CallAsAllDevices();
        await ReadAsync(call, new ReadRequest { DeviceIds = [a.Id, b.Id] });
        var request = new RunRequest
        {
            Action = AcapAction.Install,
            Items = [new RunItem { Application = "hello", DisplayName = "Hello World", DeviceIds = [a.Id, b.Id] }],
            Install = new AppInstall
            {
                Files = [new AcapPackageFile("file-1", "AA", "aarch64"), new AcapPackageFile("file-2", "BB", "armv7hf")],
                Version = "1.2.0",
                StartAfterInstall = true,
                Kind = InstallKind.Install,
            },
        };

        await CallAsync<RunReply>(call, AppManagementMethods.Run, request);

        var run = Assert.Single(_ctx.TaskRuns);
        var payload = AcapPayload.Parse(run.PayloadJson);
        Assert.Equal((AcapAction.Install, "hello", "1.2.0", "file-1"), (payload.Action, payload.Application, payload.Version, payload.FileId));
        Assert.Equal(["aarch64", "armv7hf"], payload.AllFiles().Select(f => f.Architecture!));
        Assert.True(payload.StartAfterInstall);
        Assert.False(payload.AllowDowngrade);
        Assert.Equal("Install Hello World 1.2.0", payload.TaskName());
        Assert.Equal([a.Id, b.Id], run.DeviceIds);
        var marked = (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices;
        Assert.All(marked, d => Assert.Equal((AppActivities.Installing, 0), (d.Activity, d.ActivityApplications.Count))); // every row of the device
        Assert.All(_ctx.Cameras.Values, c => Assert.Equal(0, c.Writes));
    }

    [Fact]
    public async Task Install_needs_its_rights_and_one_application_with_packages()
    {
        var a = Add("10.0.0.48");
        var install = new AppInstall { Files = [new AcapPackageFile("file-1")], Version = "1.0" };
        RunRequest Request(AppInstall? with, int items = 1) => new()
        {
            Action = AcapAction.Install,
            Items = [.. Enumerable.Range(0, items).Select(i => new RunItem { Application = "hello" + i, DeviceIds = [a.Id] })],
            Install = with,
        };

        var noInstall = new DeniedCall(_ctx.CallAsAllDevices(), PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Install));
        Assert.Equal("Your role does not allow: Install and upgrade applications.",
            (await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => CallAsync<RunReply>(noInstall, AppManagementMethods.Run, Request(install)))).Message);
        var noDowngrade = new DeniedCall(_ctx.CallAsAllDevices(), PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Downgrade));
        Assert.Equal("Your role does not allow: Downgrade applications.",
            (await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => CallAsync<RunReply>(noDowngrade, AppManagementMethods.Run, Request(install with { AllowDowngrade = true })))).Message);

        var call = _ctx.CallAsAllDevices();
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<RunReply>(call, AppManagementMethods.Run, Request(install, items: 2)));
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<RunReply>(call, AppManagementMethods.Run, Request(new AppInstall())));
        Assert.Empty(_ctx.TaskRuns);
    }

    [Fact]
    public async Task Install_licenses_starts_one_run_per_application_with_its_keys_and_needs_the_license_right()
    {
        var a = Add("10.0.0.48");
        var b = Add("10.0.0.49");
        var key = new LicenseKeyEntry("B8A44F000048", "<LicenseKey/>");
        var request = new RunRequest
        {
            Action = AcapAction.InstallLicense,
            Items =
            [
                new RunItem { Application = "lpv", DisplayName = "License Plate Verifier", DeviceIds = [a.Id], LicenseKeys = [key], LicenseApplicationId = "415090" },
                new RunItem { Application = "aoa", DeviceIds = [b.Id], LicenseKeys = [key with { DeviceId = "B8A44F000049" }] },
            ],
        };

        var limited = new DeniedCall(_ctx.CallAsAllDevices(), PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.License));
        Assert.Equal("Your role does not allow: Install license keys.",
            (await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => CallAsync<RunReply>(limited, AppManagementMethods.Run, request))).Message);
        Assert.Empty(_ctx.TaskRuns);

        await CallAsync<RunReply>(_ctx.CallAsAllDevices(), AppManagementMethods.Run, request);

        Assert.Equal(2, _ctx.TaskRuns.Count);
        var lpv = AcapPayload.Parse(_ctx.TaskRuns[0].PayloadJson);
        Assert.Equal((AcapAction.InstallLicense, "lpv", "415090"), (lpv.Action, lpv.Application, lpv.LicenseApplicationId));
        Assert.Equal([key], lpv.LicenseKeys);
        Assert.Equal("Install license of License Plate Verifier", lpv.TaskName());
        Assert.Equal([b.Id], _ctx.TaskRuns[1].DeviceIds);

        // Without keys the Applications plugin's own parser refuses it: nothing starts.
        var before = _ctx.TaskRuns.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<RunReply>(_ctx.CallAsAllDevices(), AppManagementMethods.Run,
            request with { Items = [request.Items[0] with { LicenseKeys = [] }] }));
        Assert.Equal(before, _ctx.TaskRuns.Count);
    }

    [Fact]
    public async Task A_group_user_cannot_start_tasks_on_other_devices()
    {
        var mine = Add("10.0.0.48");
        var other = Add("10.0.0.49");
        var call = _ctx.CallAsGroup([mine.Id]);
        var request = new RunRequest
        {
            Action = AcapAction.Stop,
            Items = [new RunItem { Application = "lpv", DeviceIds = [mine.Id] }, new RunItem { Application = "vmd", DeviceIds = [other.Id] }],
        };

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => CallAsync<RunReply>(call, AppManagementMethods.Run, request));
        Assert.Empty(_ctx.TaskRuns); // never a run in part
    }

    [Fact]
    public async Task A_device_is_read_again_when_its_task_ended()
    {
        var device = Add("10.0.0.48");
        var call = _ctx.CallAsAllDevices();
        await ReadAsync(call, new ReadRequest { DeviceIds = [device.Id] });
        var camera = _ctx.Cameras[device.Id];
        Assert.Equal(1, Reads(camera));

        var reply = await CallAsync<RunReply>(call, AppManagementMethods.Run,
            new RunRequest { Action = AcapAction.Stop, Items = [new RunItem { Application = "lpv", DeviceIds = [device.Id] }] });
        var marked = (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices.Single();
        Assert.Equal(AppActivities.Stopping, marked.Activity);
        Assert.Equal(["lpv"], marked.ActivityApplications); // only that application's row shows it

        // Still running: nothing is read.
        _time.Advance(AppManagementInfo.FollowInterval);
        await Eventually.StaysTrueAsync(() => Reads(camera) == 1, "no read while the task runs", TimeSpan.FromMilliseconds(200));

        _ctx.EndTask(reply.TaskIds[0]);
        await Eventually.TrueAsync(() =>
        {
            _time.Advance(AppManagementInfo.FollowInterval);
            return Reads(camera) == 2;
        }, "the device was read again");
        await Eventually.TrueAsync(async () => (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices.Single().Activity is null, "the row lost its activity");
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task The_dialogs_tasks_are_followed_and_their_devices_read_again()
    {
        var device = Add("10.0.0.48");
        var call = _ctx.CallAsAllDevices();
        await ReadAsync(call, new ReadRequest { DeviceIds = [device.Id] });
        var tasks = await _ctx.Tasks.RunAsync(AcapPlugin.Id, [device.Id], "{}", "admin", CancellationToken.None);

        await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.Follow, AppManagementJson.Serialize(new FollowRequest { TaskIds = tasks, DeviceIds = [device.Id] }));
        var installing = (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices.Single();
        Assert.Equal(AppActivities.Installing, installing.Activity);
        Assert.Empty(installing.ActivityApplications); // no application named: the whole device

        _ctx.EndTask(tasks[0]);
        var camera = _ctx.Cameras[device.Id];
        await Eventually.TrueAsync(() =>
        {
            _time.Advance(AppManagementInfo.FollowInterval);
            return Reads(camera) == 2;
        }, "the device was read again");
    }

    [Fact]
    public async Task A_failed_task_leaves_its_message_in_the_row_until_the_user_reads_again()
    {
        var device = Add("10.0.0.48");
        var other = Add("10.0.0.49");
        var call = _ctx.CallAsAllDevices();
        await ReadAsync(call, new ReadRequest { DeviceIds = [device.Id, other.Id] });
        var reply = await CallAsync<RunReply>(call, AppManagementMethods.Run, new RunRequest
        {
            Action = AcapAction.Start,
            Items = [new RunItem { Application = "lpv", DeviceIds = [device.Id, other.Id] }, new RunItem { Application = "vmd", DeviceIds = [device.Id] }],
        });

        // The task's own words, as the tasks pane shows them (the server part asks the task runner, SDK 0.7.0).
        var runs = _ctx.TaskRuns.SelectMany(r => r.TaskIds.Select((t, i) => (Task: t, Device: r.DeviceIds[i], Payload: r.PayloadJson))).ToList();
        var lpvOnDevice = runs.Single(r => r.Device == device.Id && r.Payload!.Contains("\"lpv\"", StringComparison.Ordinal)).Task;
        _ctx.EndTask(lpvOnDevice, Oadm.Sdk.Tasks.TaskEndState.Failed, "The application did not start. Nothing was changed.");
        _ctx.EndTask(runs.Single(r => r.Device == device.Id && r.Task != lpvOnDevice).Task, Oadm.Sdk.Tasks.TaskEndState.DoneWithWarnings, "No license key for this device.");
        _ctx.EndTask(runs.Single(r => r.Device == other.Id).Task);
        Assert.Equal(3, reply.TaskIds.Count);

        // The camera answers before the read stores its row: wait for the rows themselves.
        var cameras = new[] { _ctx.Cameras[device.Id], _ctx.Cameras[other.Id] };
        Dictionary<Guid, DeviceApps> rows = [];
        await Eventually.TrueAsync(async () =>
        {
            _time.Advance(AppManagementInfo.FollowInterval);
            if (!cameras.All(c => Reads(c) == 2))
            {
                return false;
            }

            rows = (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices.ToDictionary(r => r.DeviceId);
            return rows.TryGetValue(device.Id, out var row) && row.TaskResults.Count == 2;
        }, "both devices were read again with the task results");
        Assert.Equal(
            [("lpv", true, "The application did not start. Nothing was changed."), ("vmd", false, "No license key for this device.")],
            rows[device.Id].TaskResults.Select(r => (r.Application, r.Failed, r.Message)).OrderBy(r => r.Application).Select(r => (r.Application!, r.Failed, r.Message)));
        Assert.Empty(rows[other.Id].TaskResults); // Done: nothing to say

        // The next read the user starts replaces them.
        rows = (await ReadAsync(call, new ReadRequest { DeviceIds = [device.Id, other.Id] })).ToDictionary(r => r.DeviceId);
        Assert.Empty(rows[device.Id].TaskResults);
    }

    [Fact]
    public async Task A_failed_dialog_task_names_its_device_and_the_application_when_the_page_knew_it()
    {
        var device = Add("10.0.0.48");
        var call = _ctx.CallAsAllDevices();
        await ReadAsync(call, new ReadRequest { DeviceIds = [device.Id] });
        var tasks = await _ctx.Tasks.RunAsync(AcapPlugin.Id, [device.Id], "{}", "admin", CancellationToken.None);
        await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.Follow,
            AppManagementJson.Serialize(new FollowRequest { TaskIds = tasks, DeviceIds = [device.Id], Application = "lpv" }));

        _ctx.EndTask(tasks[0], Oadm.Sdk.Tasks.TaskEndState.Failed);
        await Eventually.TrueAsync(async () =>
        {
            _time.Advance(AppManagementInfo.FollowInterval);
            return (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices.Single().TaskResults.Count == 1;
        }, "the row has the task's end");
        var result = (await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest())).Devices.Single().TaskResults.Single();
        Assert.Equal("lpv", result.Application);
        Assert.Equal(AppManagementInfo.TaskFailed, result.Message); // a task without a message of its own
    }

    [Fact]
    public async Task A_group_user_learns_the_end_only_of_tasks_on_the_groups_devices()
    {
        var inside = Add("10.0.0.48");
        var outside = Add("10.0.0.49");
        var task = (await _ctx.Tasks.RunAsync(AcapPlugin.Id, [outside.Id], "{}", "admin", CancellationToken.None))[0];
        var own = (await _ctx.Tasks.RunAsync(AcapPlugin.Id, [inside.Id], "{}", "admin", CancellationToken.None))[0];
        _ctx.EndTask(task, Oadm.Sdk.Tasks.TaskEndState.Failed, "secret");
        _ctx.EndTask(own, Oadm.Sdk.Tasks.TaskEndState.Failed, "visible");

        var group = _ctx.CallAsGroup([inside.Id]);
        Assert.Null(await group.Tasks.GetOutcomeAsync(task, CancellationToken.None));
        Assert.Equal("visible", (await group.Tasks.GetOutcomeAsync(own, CancellationToken.None))?.Message);
        Assert.Equal(inside.Id, (await _ctx.CallAsAllDevices().Tasks.GetOutcomeAsync(own, CancellationToken.None))?.DeviceId);
    }

    [Fact]
    public async Task Rows_come_in_pages()
    {
        for (var i = 0; i < AppManagementInfo.RowsPerPage + 5; i++)
        {
            Add("10.0." + (i / 250) + "." + (i % 250), api: false);
        }

        var call = _ctx.CallAsAllDevices();
        await ReadAsync(call, new ReadRequest { All = true });
        var first = await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest());
        Assert.Equal(AppManagementInfo.RowsPerPage, first.Devices.Count);
        Assert.Equal(AppManagementInfo.RowsPerPage, first.Next);
        var second = await CallAsync<RowsPage>(call, AppManagementMethods.GetRows, new RowsRequest { Skip = first.Next!.Value });
        Assert.Equal(5, second.Devices.Count);
        Assert.Null(second.Next);
    }

    /// <summary>How often the device was read (its list.cgi requests; each read also asks two optional values).</summary>
    private static int Reads(FakeCamera camera) => camera.Requests.Count(r => r.EndsWith(ApplicationApiClient.ListPath, StringComparison.Ordinal));
}

internal sealed class DeniedCall(ICallContext inner, params string[] denied) : ICallContext
{
    public string UserName => inner.UserName;

    public string SessionId => inner.SessionId;

    public string DeviceScopeKey => inner.DeviceScopeKey;

    public bool HasAllDevices => inner.HasAllDevices;

    public Oadm.Sdk.Devices.IDeviceRepository Devices => inner.Devices;

    public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

    public Oadm.Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

    public Oadm.Sdk.Devices.IDeviceEventStreams? EventStreams => inner.EventStreams;

    public bool Has(string permission) => !denied.Contains(permission) && inner.Has(permission);

    public bool Contains(Oadm.Sdk.Devices.IDeviceInfo device) => inner.Contains(device);

}
