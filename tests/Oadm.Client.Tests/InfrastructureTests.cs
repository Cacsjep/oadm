using Google.Protobuf.WellKnownTypes;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Tests;

public sealed class TaskPluginRunnerTests
{
    [Fact]
    public async Task Plugin_with_dialog_shows_dialog_and_sends_payload()
    {
        var dialog = new FakeDialog("oadm.password", "{\"password\":\"x\"}");
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog("oadm.password").Returns(dialog);
        using var f = new DevicesFixture(registry: registry);
        f.SeedDevices(TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"));
        f.Dialogs.ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>()).Returns("{\"password\":\"x\"}");
        f.Api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs("t1");

        string? id = await f.Runner.RunAsync(TestSupport.Plugin("oadm.password", "Change password", false, true, "1"), [f.Store.Find("1")!], CancellationToken.None);

        Assert.Equal("t1", id);
        await f.Api.Received(1).RunTaskAsync("oadm.password", Arg.Any<IReadOnlyCollection<string>>(), "{\"password\":\"x\"}",
            TaskPluginRunner.OwnerName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_dialog_does_not_run_the_task()
    {
        var dialog = new FakeDialog("oadm.password", null);
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog("oadm.password").Returns(dialog);
        using var f = new DevicesFixture(registry: registry);
        f.SeedDevices(TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"));
        f.Dialogs.ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>()).Returns((string?)null);

        string? id = await f.Runner.RunAsync(TestSupport.Plugin("oadm.password", "Change password", false, true, "1"), [f.Store.Find("1")!], CancellationToken.None);

        Assert.Null(id);
        await f.Api.DidNotReceiveWithAnyArgs().RunTaskAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task Missing_client_dialog_shows_message_and_does_not_run()
    {
        using var f = new DevicesFixture();
        f.SeedDevices(TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"));

        string? id = await f.Runner.RunAsync(TestSupport.Plugin("oadm.pki.deploy", "Deploy certificate", false, true, "1"), [f.Store.Find("1")!], CancellationToken.None);

        Assert.Null(id);
        await f.Dialogs.Received(1).ShowMessageAsync("Deploy certificate", Arg.Any<string>());
        await f.Api.DidNotReceiveWithAnyArgs().RunTaskAsync(default!, default!, default, default!, default);
    }
}

public sealed class StoreTests
{
    [Fact]
    public void Device_store_reset_reconciles_snapshot()
    {
        var store = new DeviceStore();
        store.Reset([TestSupport.Device("1", "A", "10.0.0.1", "M"), TestSupport.Device("2", "B", "10.0.0.2", "M")]);
        DeviceRowViewModel row1 = store.Find("1")!;

        store.Reset([TestSupport.Device("1", "A", "10.0.0.11", "M"), TestSupport.Device("3", "C", "10.0.0.3", "M")]);

        Assert.Equal(["1", "3"], store.Devices.Select(d => d.Id).ToArray());
        Assert.Same(row1, store.Find("1"));
        Assert.Equal("10.0.0.11", row1.Address);
    }

    [Fact]
    public void Task_store_keeps_newest_first_and_tracks_active_count()
    {
        var store = new TaskStore();
        DateTime now = DateTime.UtcNow;
        store.Reset(
        [
            new TaskInfo { Id = "old", Name = "Add devices", State = TaskState.Done, Created = Timestamp.FromDateTime(now.AddHours(-1)) },
            new TaskInfo { Id = "new", Name = "Restart", State = TaskState.Running, Progress = 40, Created = Timestamp.FromDateTime(now) },
        ]);

        Assert.Equal(["new", "old"], store.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(1, store.ActiveCount);

        store.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = new TaskInfo { Id = "new", Name = "Restart", State = TaskState.Done, Progress = 100 } });
        store.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = new TaskInfo { Id = "newest", Name = "Restart", State = TaskState.Queued } });
        store.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Removed, Task = new TaskInfo { Id = "old" } });

        Assert.Equal(["newest", "new"], store.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal("Done", store.Find("new")!.StateText);
        Assert.True(store.Find("new")!.IsStateOk);
        Assert.Equal(1, store.ActiveCount);
    }

    [Fact]
    public async Task Tasks_view_model_commands_follow_selected_task_state()
    {
        using var f = new DevicesFixture();
        f.Tasks.Reset([new TaskInfo { Id = "t", Name = "Restart", State = TaskState.Running }]);
        TasksViewModel vm = f.TasksVm;

        Assert.False(vm.CancelCommand.CanExecute(null));
        vm.SelectedTask = f.Tasks.Find("t");
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));

        f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = new TaskInfo { Id = "t", Name = "Restart", State = TaskState.Done } });
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));

        await vm.DeleteCommand.ExecuteAsync(null);
        await f.Api.Received(1).DeleteTaskAsync("t", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Bottom_pane_state_and_height_are_persisted()
    {
        using var f = new DevicesFixture();
        Assert.Equal(TasksViewModel.DefaultPaneHeight, f.TasksVm.PaneHeight);

        f.TasksVm.ToggleExpandedCommand.Execute(null);
        Assert.False(f.Settings.Current.BottomPaneExpanded);
        f.TasksVm.ShowTasksCommand.Execute(null);
        Assert.True(f.Settings.Current.BottomPaneExpanded);

        f.TasksVm.CommitPaneHeight(412.4);
        Assert.Equal(412, f.Settings.Current.TasksPaneHeight);
        f.TasksVm.CommitPaneHeight(10);
        Assert.Equal(TasksViewModel.MinPaneHeight, f.TasksVm.PaneHeight);
        Assert.True(f.Settings.SaveCount > 0);

        f.Settings.Current.TasksPaneHeight = 333;
        var restored = new TasksViewModel(f.Tasks, f.Store, f.Api, f.Dialogs, f.Settings, NullLogger<TasksViewModel>.Instance);
        Assert.Equal(333, restored.PaneHeight);
    }

    [Fact]
    public async Task Delete_all_asks_for_confirmation_then_calls_the_server()
    {
        using var f = new DevicesFixture();
        Assert.False(f.TasksVm.DeleteAllCommand.CanExecute(null));
        f.Tasks.Reset(
        [
            new TaskInfo { Id = "a", Name = "Restart", State = TaskState.Running },
            new TaskInfo { Id = "b", Name = "Restart", State = TaskState.Done },
        ]);
        Assert.True(f.TasksVm.DeleteAllCommand.CanExecute(null));

        f.Dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(false);
        await f.TasksVm.DeleteAllCommand.ExecuteAsync(null);
        await f.Api.DidNotReceiveWithAnyArgs().DeleteAllTasksAsync(default);

        f.Dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        await f.TasksVm.DeleteAllCommand.ExecuteAsync(null);
        await f.Dialogs.Received().ConfirmAsync("Delete all tasks", Arg.Is<string>(m => m.Contains("cancelled first", StringComparison.Ordinal)), "Delete all");
        await f.Api.Received(1).DeleteAllTasksAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fake_api_delete_all_cancels_and_removes_every_task()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        int before = (await api.ListTasksAsync(CancellationToken.None)).Count;
        Assert.True(before > 0);

        int deleted = await api.DeleteAllTasksAsync(CancellationToken.None);

        Assert.Equal(before, deleted);
        Assert.Empty(await api.ListTasksAsync(CancellationToken.None));
    }
}

public sealed class ColumnLayoutTests
{
    [Fact]
    public void Default_columns_follow_the_spec_order()
    {
        var layout = new ColumnLayoutViewModel(new InMemoryClientSettingsStore());

        Assert.Equal(
            ["icon", "mac", "status", "address", "model", "firmware", "dhcp", "https", "certExpires", "certTrust", "dot1x"],
            layout.Columns.Select(c => c.Key).ToArray());
        Assert.DoesNotContain(layout.Choosable, c => c.Key == "icon");
        Assert.Equal("IEEE 802.1X", layout.Find("dot1x")!.Header);
    }

    [Fact]
    public void Visibility_order_and_width_round_trip_through_settings()
    {
        var settings = new InMemoryClientSettingsStore();
        var layout = new ColumnLayoutViewModel(settings);

        layout.Find("dot1x")!.IsVisible = false;
        layout.Capture([("model", 1, 200.0), ("mac", 4, 150.0)]);

        var restored = new ColumnLayoutViewModel(settings);
        Assert.False(restored.Find("dot1x")!.IsVisible);
        Assert.Equal(200.0, restored.Find("model")!.Width);
        Assert.True(restored.Find("model")!.DisplayIndex < restored.Find("mac")!.DisplayIndex);
    }
}

public sealed class ServerConnectionTests
{
    [Fact]
    public async Task Connects_loads_devices_and_recovers_after_server_outage()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.SetOnline(false);
        var devices = new DeviceStore();
        var tasks = new TaskStore();
        using var connection = new ServerConnection(api, devices, tasks, new ImmediateUiDispatcher(), NullLogger<ServerConnection>.Instance);
        int connectedEvents = 0;
        connection.Connected += (_, _) => connectedEvents++;

        connection.Start();
        await TestSupport.WaitUntilAsync(() => connection.State == ConnectionState.Disconnected);
        Assert.Contains("unreachable", connection.StatusText, StringComparison.Ordinal);
        Assert.Empty(devices.Devices);

        api.SetOnline(true);
        await TestSupport.WaitUntilAsync(() => connection.IsConnected && devices.Devices.Count == 12 && tasks.Tasks.Count > 0, 10000);
        Assert.Equal(1, connectedEvents);

        // live update through the watch stream
        string id = devices.Devices[0].Id;
        await api.RemoveDevicesAsync([id], CancellationToken.None);
        await TestSupport.WaitUntilAsync(() => devices.Find(id) is null);
    }

    [Fact]
    public void Server_address_is_normalized()
    {
        Assert.Equal("http://server:5080", GrpcOadmApi.Normalize("server:5080"));
        Assert.Equal("https://server:5081", GrpcOadmApi.Normalize(" https://server:5081/ "));
        Assert.Equal(ClientSettings.DefaultServerAddress, GrpcOadmApi.Normalize(""));
    }

    [Fact]
    public async Task Grpc_api_against_closed_port_fails_without_crashing_the_connection()
    {
        using var api = new GrpcOadmApi("http://127.0.0.1:1");
        var devices = new DeviceStore();
        using var connection = new ServerConnection(api, devices, new TaskStore(), new ImmediateUiDispatcher(), NullLogger<ServerConnection>.Instance);

        connection.Start();

        await TestSupport.WaitUntilAsync(() => connection.State == ConnectionState.Disconnected, 10000);
        Assert.Contains("127.0.0.1:1", connection.StatusText, StringComparison.Ordinal);
    }
}

public sealed class AppOptionsTests
{
    [Fact]
    public void Parses_fake_and_server()
    {
        AppOptions options = AppOptions.Parse(["--fake", "--server", "http://nvr:5080"]);

        Assert.True(options.UseFake);
        Assert.Equal("http://nvr:5080", options.ServerAddress);
        Assert.False(AppOptions.Parse([]).UseFake);
    }

    [Theory]
    [InlineData("Oadm.Sdk", true)]
    [InlineData("Oadm.Sdk.Client", true)]
    [InlineData("Avalonia.Controls", true)]
    [InlineData("Newtonsoft.Json", false)]
    [InlineData("Oadm.Plugins.Pki.Client", false)]
    public void Plugin_load_context_shares_sdk_and_avalonia(string assembly, bool shared)
    {
        Assert.Equal(shared, PluginLoadContext.IsShared(assembly));
    }

    [Fact]
    public void Plugin_loader_tolerates_missing_and_broken_plugins()
    {
        string root = Path.Combine(Path.GetTempPath(), "oadm-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        File.WriteAllText(Path.Combine(root, "broken", "Broken.Client.dll"), "not an assembly");
        try
        {
            var loader = new ClientPluginLoader([root, Path.Combine(root, "missing")], NullLogger<ClientPluginLoader>.Instance);

            Assert.Empty(loader.Dialogs);
            Assert.Null(loader.FindDialog("oadm.restart"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
