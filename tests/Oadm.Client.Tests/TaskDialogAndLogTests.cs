using System.Security.Cryptography;

using Grpc.Core;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

public sealed class TaskDialogAndLogTests
{
    [Fact]
    public async Task DialogContextQueriesTheServerForItsOwnPlugin()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        (string Plugin, string Device, string Method, string? Payload)? seen = null;
        api.QueryHandler = (plugin, device, method, payload) =>
        {
            seen = (plugin, device, method, payload);
            return """{"users":["root"]}""";
        };
        var ctx = new TaskDialogContext(api, "oadm.users");
        var deviceId = Guid.NewGuid();

        var result = await ctx.QueryAsync(deviceId, "listUsers", "{}", CancellationToken.None);

        Assert.Equal("""{"users":["root"]}""", result);
        Assert.Equal(("oadm.users", deviceId.ToString(), "listUsers", (string?)"{}"), seen);
    }

    [Fact]
    public async Task DialogContextRunsItsTaskAndFollowsItUntilEveryTaskEnded()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var tasks = new TaskStore(new DeviceStore());
        var ctx = new TaskDialogContext(api, FakeOadmApi.RestartPluginId, tasks: tasks);
        Assert.True(ctx.CanRunTasks);
        Assert.False(new TaskDialogContext(api, FakeOadmApi.RestartPluginId).CanRunTasks);
        var devices = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var reports = new List<IReadOnlyList<Oadm.Sdk.Client.TaskRunState>>();

        var run = ctx.RunTaskAsync(devices, null, new SyncProgress<IReadOnlyList<Oadm.Sdk.Client.TaskRunState>>(reports.Add), CancellationToken.None);
        Assert.False(run.IsCompleted);
        var first = reports[^1];
        Assert.All(first, s => Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.Queued, s.Status)); // not in the mirror yet
        Assert.Equal(devices, first.Select(s => s.DeviceId));

        TaskInfo Task(int i, TaskState state, string message = "") => new()
        {
            Id = first[i].TaskId, PluginId = FakeOadmApi.RestartPluginId, State = state, DeviceId = devices[i].ToString(),
            Devices = { new TaskDeviceResult { DeviceId = devices[i].ToString(), State = state, Message = message } },
        };
        tasks.ApplyBatch([new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = Task(0, TaskState.Running) },
            new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = Task(1, TaskState.Queued) }]);
        tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = new TaskInfo { Id = "other", State = TaskState.Done } }); // not this run
        tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = Task(0, TaskState.Done) });
        Assert.False(run.IsCompleted);
        Assert.Equal([Oadm.Sdk.Client.TaskRunStatus.Done, Oadm.Sdk.Client.TaskRunStatus.Queued], reports[^1].Select(s => s.Status));

        tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = Task(1, TaskState.Failed, "Connection refused") });
        var ended = await run;
        Assert.Equal([Oadm.Sdk.Client.TaskRunStatus.Done, Oadm.Sdk.Client.TaskRunStatus.Failed], ended.Select(s => s.Status));
        Assert.Equal("Connection refused", ended[1].Message);
    }

    private static TaskChanged Change(string id, Guid device, TaskState state, string message = "", TaskChanged.Types.Kind kind = TaskChanged.Types.Kind.Updated) => new()
    {
        Kind = kind,
        Task = new TaskInfo
        {
            Id = id, PluginId = FakeOadmApi.RestartPluginId, State = state, DeviceId = device.ToString(),
            Devices = { new TaskDeviceResult { DeviceId = device.ToString(), State = state, Message = message } },
        },
    };

    [Fact]
    public async Task A_run_on_more_devices_than_the_task_list_keeps_ends_with_the_results_of_the_tasks_that_left_it()
    {
        // 10,002 tasks of one run: while more than the limit run, the mirror drops every task as soon as it ends.
        int count = TaskStore.MaxTasks + 2;
        string[] ids = [.. Enumerable.Range(0, count).Select(i => "t" + i)];
        Guid[] devices = [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];
        var api = Substitute.For<IOadmApi>();
        api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs((IReadOnlyList<string>)ids);
        var tasks = new TaskStore(new DeviceStore());
        var ctx = new TaskDialogContext(api, FakeOadmApi.RestartPluginId, tasks: tasks);
        IReadOnlyList<Oadm.Sdk.Client.TaskRunState>? last = null;

        var run = ctx.RunTaskAsync(devices, null, new SyncProgress<IReadOnlyList<Oadm.Sdk.Client.TaskRunState>>(s => last = s), CancellationToken.None);
        tasks.ApplyBatch([.. ids.Select((id, i) => Change(id, devices[i], TaskState.Running, kind: TaskChanged.Types.Kind.Added))]);
        Assert.Equal(count, tasks.Tasks.Count); // running tasks all stay

        tasks.ApplyBatch([Change(ids[0], devices[0], TaskState.Done), Change(ids[1], devices[1], TaskState.Failed, "Connection refused")]);
        Assert.Null(tasks.Find(ids[0])); // ended and beyond the limit: no longer in the mirror
        Assert.Null(tasks.Find(ids[1]));
        Assert.Equal(TaskStore.MaxTasks, tasks.Tasks.Count);
        Assert.NotNull(last);
        Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.Done, last[0].Status); // read as queued before, for ever
        Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.Failed, last[1].Status);
        Assert.Equal("Connection refused", last[1].Message);
        Assert.False(run.IsCompleted);

        tasks.ApplyBatch([.. ids.Skip(2).Select((id, i) => Change(id, devices[i + 2], TaskState.Done))]);
        var ended = await run.WaitAsync(Eventually.DefaultTimeout);

        Assert.Equal(count, ended.Count);
        Assert.Equal(count - 1, ended.Count(s => s.Status == Oadm.Sdk.Client.TaskRunStatus.Done));
        Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.Failed, ended[1].Status);
        Assert.Equal(devices, ended.Select(s => s.DeviceId));
    }

    [Fact]
    public async Task A_task_that_ended_and_left_a_full_task_list_before_the_run_call_answered_counts_as_ended()
    {
        var api = Substitute.For<IOadmApi>();
        var answer = new TaskCompletionSource<IReadOnlyList<string>>();
        api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs(answer.Task);
        var tasks = new TaskStore(new DeviceStore()) { Capacity = 2 };
        Guid other = Guid.NewGuid(), device = Guid.NewGuid();
        tasks.ApplyBatch([Change("a", other, TaskState.Running, kind: TaskChanged.Types.Kind.Added), Change("b", other, TaskState.Running, kind: TaskChanged.Types.Kind.Added)]);
        var ctx = new TaskDialogContext(api, FakeOadmApi.RestartPluginId, tasks: tasks);

        var run = ctx.RunTaskAsync([device], null, null, CancellationToken.None);
        // The stream is ahead of the call's answer: the task arrives, ends with a warning and is dropped (the list is full of running tasks).
        tasks.ApplyBatch([Change("t", device, TaskState.Running, kind: TaskChanged.Types.Kind.Added), Change("t", device, TaskState.DoneWithWarnings, "Restarted late")]);
        Assert.Null(tasks.Find("t"));
        answer.SetResult(["t"]);
        var ended = await run.WaitAsync(Eventually.DefaultTimeout);

        var state = Assert.Single(ended);
        Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.DoneWithWarnings, state.Status);
        Assert.Equal("Restarted late", state.Message);
        Assert.Equal(device, state.DeviceId);
    }

    [Fact]
    public async Task A_task_deleted_while_it_runs_ends_the_run_as_cancelled_without_a_message()
    {
        var api = Substitute.For<IOadmApi>();
        api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs((IReadOnlyList<string>)["t"]);
        var tasks = new TaskStore(new DeviceStore());
        Guid device = Guid.NewGuid();
        var ctx = new TaskDialogContext(api, FakeOadmApi.RestartPluginId, tasks: tasks);
        var reports = new List<IReadOnlyList<Oadm.Sdk.Client.TaskRunState>>();

        var run = ctx.RunTaskAsync([device], null, new SyncProgress<IReadOnlyList<Oadm.Sdk.Client.TaskRunState>>(reports.Add), CancellationToken.None);
        tasks.ApplyBatch([Change("t", device, TaskState.Running, kind: TaskChanged.Types.Kind.Added), Change("x", Guid.NewGuid(), TaskState.Done, kind: TaskChanged.Types.Kind.Added)]);
        tasks.Apply(Change("x", Guid.NewGuid(), TaskState.Done, kind: TaskChanged.Types.Kind.Removed)); // another task leaves: nothing for this run
        Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.Running, reports[^1][0].Status);
        Assert.False(run.IsCompleted);

        tasks.Apply(Change("t", device, TaskState.Running, kind: TaskChanged.Types.Kind.Removed)); // Delete all, by another user
        var ended = await run.WaitAsync(Eventually.DefaultTimeout);

        var state = Assert.Single(ended);
        Assert.Equal(Oadm.Sdk.Client.TaskRunStatus.Cancelled, state.Status);
        Assert.Equal(string.Empty, state.Message);
        Assert.Equal(device, state.DeviceId);
    }

    [Fact]
    public async Task APluginWithoutQueriesSurfacesAsUnimplemented()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var ctx = new TaskDialogContext(api, "oadm.restart");

        var ex = await Assert.ThrowsAsync<RpcException>(() => ctx.QueryAsync(Guid.NewGuid(), "x", null, CancellationToken.None));

        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task DialogContextUploadsWithProgress()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var ctx = new TaskDialogContext(api, "oadm.firmware");
        string path = Path.Combine(Path.GetTempPath(), "oadm-upload-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] content = RandomNumberGenerator.GetBytes((GrpcOadmApi.UploadChunkSize * 2) + 100);
        await File.WriteAllBytesAsync(path, content);
        var reports = new List<double>();
        try
        {
            var file = await ctx.UploadAsync(path, new SyncProgress(reports.Add), CancellationToken.None);

            Assert.Equal(Path.GetFileName(path), file.Name);
            Assert.Equal(content.Length, file.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), file.Sha256);
            Assert.True(api.Uploads.ContainsKey(file.Id));
            Assert.Equal(0, reports[0]);
            Assert.Equal(1, reports[^1]);
            Assert.True(reports.Count >= 4, "progress per 256 KB chunk");
            Assert.Equal(reports.Order(), reports);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DoneWithWarningsIsAWarnChip()
    {
        var row = new TaskRowViewModel(new TaskInfo { Id = "t1", Name = "Users", State = TaskState.DoneWithWarnings, Progress = 100 }, new DeviceStore());

        Assert.Equal("Done with warnings", row.StateText);
        Assert.Equal(PillKind.Warning, row.StateKind);
        Assert.True(row.IsStateWarning);
        Assert.False(row.IsStateOk);
        Assert.False(row.IsActive);
    }

    [Fact]
    public async Task DetailsLoadTheTaskLogWithDeviceAddresses()
    {
        using var api = new FakeOadmApi();
        var devices = new DeviceStore();
        devices.Reset(await api.ListDevicesAsync(CancellationToken.None));
        TaskInfo warned = (await api.ListTasksAsync(CancellationToken.None)).Single(t => t.State == TaskState.DoneWithWarnings);
        var details = new TaskDetailsViewModel(new TaskRowViewModel(warned, devices), devices);

        await details.LoadLogAsync(api, CancellationToken.None);

        Assert.Equal(3, details.Log.Count);
        Assert.Equal("3 log entries", details.LogStatus);
        TaskLogRow first = details.Log[0];
        Assert.Equal("Info", first.LevelText);
        Assert.True(first.IsInfo); // the blue info icon
        Assert.Equal("", first.Device); // task-level entry
        TaskLogRow warning = details.Log.Single(r => r.IsWarning);
        Assert.Equal("Warning", warning.LevelText);
        Assert.False(warning.IsInfo);
        Assert.Equal(devices.Find(warned.DeviceId)!.DisplayAddress, warning.Device);
        Assert.Contains(details.Rows, r => r.IsStateWarning && r.StateText == "Done with warnings");
    }

    [Fact]
    public async Task DetailsShowWhenTheLogCannotBeLoaded()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var details = new TaskDetailsViewModel(new TaskRowViewModel(new TaskInfo { Id = "gone", Name = "x" }, new DeviceStore()), new DeviceStore());

        await details.LoadLogAsync(api, CancellationToken.None);

        Assert.Empty(details.Log);
        Assert.StartsWith("The log could not be loaded", details.LogStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceRowsCarryTheApiList()
    {
        var device = TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "M3106");
        device.Apis.Add(new DeviceApi { Id = "user-management", Version = "1.2", Name = "User Management", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "fwmgr", Version = "1.10" });

        var row = new DeviceRowViewModel(device);

        Assert.Equal([new Oadm.Sdk.Vapix.DeviceApi("user-management", "1.2", "User Management", "official"), new("fwmgr", "1.10")], row.Apis);
        Assert.True(Oadm.Sdk.Vapix.DeviceApiExtensions.Supports(row.Apis, "fwmgr", "1.4"));
    }

    [Fact]
    public void ClientPluginsAreAlsoLoadedFromTheRepositoryArtifacts()
    {
        // A client started from its build output inside the repository (src/Oadm.Client/bin/...).
        string app = Oadm.Tests.Shared.TestRepository.Path("src", "Oadm.Client", "bin", "Debug", "net10.0");
        string? development = ClientPluginLoader.DevelopmentRoot(app);

        Assert.Equal(Oadm.Tests.Shared.TestRepository.DevelopmentPlugins, development);
        Assert.Contains(development, ClientPluginLoader.DefaultRoots(new Infrastructure.AppOptions(), app));
        Assert.Null(ClientPluginLoader.DevelopmentRoot(Path.GetPathRoot(Path.GetTempPath())!));
    }

    /// <summary>Progress&lt;T&gt; posts to the sync context; tests need the reports in order and immediately.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
