using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>A plugin's command on a task (ITaskAction): records its runs, fits failed tasks of one plugin.</summary>
internal sealed class RecordingTaskAction(string? permission = null) : ITaskAction
{
    public List<IReadOnlyList<TaskActionTask>> Runs { get; } = [];

    public Exception? Fails { get; set; }

    public string Id => "test.run-again";

    public string Text => "Run again";

    public string? IconKey => "refresh";

    public string? RequiredPermission => permission;

    public bool AppliesTo(TaskActionTask task) => task.PluginId == "test.task" && task.Status == TaskRunStatus.Failed;

    public Task RunAsync(IToolbarContext ctx, IReadOnlyList<TaskActionTask> tasks, CancellationToken ct)
    {
        Runs.Add(tasks);
        return Fails is { } fails ? Task.FromException(fails) : Task.CompletedTask;
    }
}

/// <summary>Commands of plugins on the selected task of the tasks pane, and the add page while provisioning is on.</summary>
public sealed class TaskActionsTests
{
    private static readonly string DeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString();

    private static IClientPluginRegistry Registry(ITaskAction action)
    {
        var registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
        registry.FindPage(Arg.Any<string>()).Returns((ICorePluginPage?)null);
        registry.ToolbarPlugins.Returns([]);
        registry.TaskActions.Returns([action]);
        registry.PackageOf(Arg.Any<object>()).Returns((string?)null);
        return registry;
    }

    private static TaskInfo NewTask(string id, TaskState state, string plugin = "test.task") =>
        new() { Id = id, Name = "Provision with Lobby", PluginId = plugin, State = state, DeviceId = DeviceId };

    [Fact]
    public async Task The_pane_offers_a_plugins_command_only_while_it_fits_the_selected_task()
    {
        var action = new RecordingTaskAction();
        using var f = new DevicesFixture(registry: Registry(action));
        f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = NewTask("t1", TaskState.Running) });
        f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = NewTask("t2", TaskState.Failed, "other.task") });
        var vm = f.TasksVm;
        Assert.False(vm.HasSelectedActions);

        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "t1");
        Assert.False(vm.HasSelectedActions); // still running

        f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = NewTask("t1", TaskState.Failed) });
        var item = Assert.Single(vm.SelectedActions);
        Assert.True(vm.HasSelectedActions);
        Assert.Equal("Run again", item.Text);

        await item.RunCommand.ExecuteAsync(null);
        var run = Assert.Single(action.Runs);
        Assert.Equal(("t1", Guid.Parse(DeviceId), TaskRunStatus.Failed), (run[0].TaskId, run[0].DeviceId, run[0].Status));

        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "t2");
        Assert.Empty(vm.SelectedActions); // another plugin's task
    }

    [Fact]
    public async Task A_failing_command_shows_its_reason_and_the_role_hides_a_command_it_lacks()
    {
        var action = new RecordingTaskAction("test:manage") { Fails = new InvalidOperationException("The camera is busy.") };
        using var f = new DevicesFixture(registry: Registry(action));
        f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = NewTask("t1", TaskState.Failed) });
        f.TasksVm.SelectedTask = f.TasksVm.Tasks.Single();
        await f.TasksVm.SelectedActions.Single().RunCommand.ExecuteAsync(null);
        await f.Dialogs.Received(1).ShowMessageAsync("Run again", "The camera is busy.");

        f.Devices.Toolbar.ApplyPermissions(p => p != "test:manage");
        Assert.Empty(f.TasksVm.SelectedActions);
        f.Devices.Toolbar.ApplyPermissions(_ => true);
        Assert.Single(f.TasksVm.SelectedActions);
    }

    // ------------------------------------------------------------------ headless screenshots

    [Fact]
    public async Task The_add_page_shows_provisioning_for_factory_new_devices_and_the_tasks_pane_shows_run_again()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var fake = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { ProvisioningReady = true };
            var page = new AddDevicesViewModel(fake, new AvaloniaUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan);
            var window = new AddDevicesWindow { DataContext = page, Width = 1040 };
            window.Show();
            await page.OpenAsync();
            await PumpUntilAsync(() => page.ProvisioningRead is { IsCompleted: true } && page.Rows.Count == 11 && page.Rows.All(r => r.AuthState != AuthState.Pending));
            await PumpUntilAsync(() => true);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
            Assert.Single(texts, t => t.Name == "ProvisioningText" && t.Text == DeviceTexts.Provisioning); // the other factory-new device is at a site
            Capture(window, outDir, "client-add-provisioning.png");
            await page.DisposeAsync();
            window.Close();

            // The tasks pane: Run again in its header while a failed task it fits is selected.
            var action = new RecordingTaskAction();
            using var f = new DevicesFixture(registry: Registry(action), catalogUi: new AvaloniaUiDispatcher());
            f.SeedDevices(TestSupport.Device(DeviceId, "B8A44F7788AA", "10.0.0.90", "AXIS M3215-LVE", DeviceStatus.Ok));
            f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = ProvisionTask() });
            f.TasksVm.IsExpanded = true;
            f.TasksVm.SelectedTask = f.TasksVm.Tasks.Single();
            var pane = new Window { Width = 1300, Height = 300, Content = new TasksPaneView { DataContext = f.TasksVm } };
            App.ApplyCrispText(pane);
            pane.Show();
            await PumpUntilAsync(() => true);
            var button = pane.GetVisualDescendants().OfType<ToolbarButton>().Single(b => b.IsEffectivelyVisible && b.Text == "Run again");
            Assert.True(button.IsEffectivelyEnabled);
            Capture(pane, outDir, "client-tasks-run-again.png");
            pane.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>A failed "Provision with" task as the server reports it: its steps, the Network step red.</summary>
    private static TaskInfo ProvisionTask()
    {
        var task = NewTask("t1", TaskState.Failed);
        task.Name = "Provision with Lobby cameras";
        task.Owner = "anna";
        task.Created = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(new DateTime(2026, 11, 19, 9, 12, 0, DateTimeKind.Utc));
        task.Started = task.Created;
        task.Devices.Add(new TaskDeviceResult { DeviceId = DeviceId, State = TaskState.Failed, Message = "No free address left in the pool." });
        string[] names = ["Check that the camera is new", "Set the password", "Set the network settings", "Set the date and time"];
        for (var i = 0; i < names.Length; i++)
        {
            task.Steps.Add(new TaskStep { Index = i, Name = names[i], State = i < 2 ? TaskStepState.Done : i == 2 ? TaskStepState.Failed : TaskStepState.Pending });
        }

        task.CurrentStepIndex = 2;
        return task;
    }

    private static async Task PumpUntilAsync(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string what = "")
    {
        DateTime end = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await System.Threading.Tasks.Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
        while (!condition() && DateTime.UtcNow < end);

        Assert.True(condition(), "UI condition not met in time: " + what);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
