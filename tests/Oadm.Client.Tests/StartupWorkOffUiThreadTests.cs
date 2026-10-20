using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Client.Tags;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// The work of the start that does not need the UI thread runs elsewhere (the splash keeps its frames): device and task
/// rows are built off the UI thread and swapped in, the task list is read off it, and the plugin pages are created one
/// UI job at a time.
/// </summary>
public sealed class StartupWorkOffUiThreadTests
{
    [Fact]
    public async Task Device_rows_built_off_the_ui_thread_take_the_shared_tags_and_existing_rows_stay()
    {
        var tags = new TagStore();
        tags.Reset([new DeviceTag { Name = "Building A", Color = TagColor.Blue }]);
        var store = new DeviceStore(tags);
        Device first = TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "P3265-V");
        store.Reset([first]);
        DeviceRowViewModel existing = store.Find("d1")!;

        Device moved = first.Clone();
        moved.Address = "10.0.0.11";
        Device tagged = TestSupport.Device("d2", "ACCC8E000002", "10.0.0.2", "M3106-L");
        tagged.Tags.Add("Building A");
        PreparedDeviceSnapshot prepared = await Task.Run(() => DeviceStore.Prepare([moved, tagged, tagged]));
        Assert.Equal(2, prepared.Count);
        store.Reset(prepared);

        Assert.Same(existing, store.Find("d1"));
        Assert.Equal("10.0.0.11", existing.Address);
        DeviceRowViewModel added = store.Find("d2")!;
        Assert.Same(tags.Find("Building A"), Assert.Single(added.TagChips));
        Assert.Same(store.Compliance.For("d2"), added.Compliance);
        Assert.Equal(["d1", "d2"], store.Devices.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task Task_rows_built_off_the_ui_thread_name_their_device_when_they_are_swapped_in()
    {
        var devices = new DeviceStore();
        devices.Reset([TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "P3265-V")]);
        var tasks = new TaskStore(devices);
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        PreparedTaskSnapshot prepared = await Task.Run(() => TaskStore.Prepare(
            [Perf.ClientScale.Task(1, "d1", TaskState.Running, created), Perf.ClientScale.Task(2, "d1", TaskState.Done, created.AddMinutes(1))]));
        tasks.Reset(prepared);

        Assert.Equal(["task-000002", "task-000001"], tasks.Tasks.Select(t => t.Id).ToArray());
        Assert.All(tasks.Tasks, t => Assert.Equal("10.0.0.1", t.DeviceText));
        Assert.Equal(1, tasks.ActiveCount);

        devices.Reset([TestSupport.Device("d1", "ACCC8E000001", "10.0.0.9", "P3265-V")]);
        Assert.All(tasks.Tasks, t => Assert.Equal("10.0.0.9", t.DeviceText));
    }

    [Fact]
    public async Task The_task_list_is_read_off_the_ui_thread_also_when_the_server_answers_at_once()
    {
        await using var ui = new UiThread();
        var api = Substitute.For<IOadmApi>();
        int? readOn = null;
        api.ListTaskPluginsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            readOn = Environment.CurrentManagedThreadId;
            return Task.FromResult<IReadOnlyList<TaskPluginInfo>>([TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false)]);
        });
        var catalog = new TaskPluginCatalog(api, ui, NullLogger<TaskPluginCatalog>.Instance);

        (int uiThread, int changedOn, bool loaded) = await ui.RunAsync(async () =>
        {
            int changed = -1;
            catalog.Changed += (_, _) => changed = Environment.CurrentManagedThreadId;
            await catalog.RefreshAsync(CancellationToken.None);
            return (Environment.CurrentManagedThreadId, changed, catalog.IsLoaded); // applied when the refresh ends
        });

        Assert.NotEqual(uiThread, readOn);
        Assert.Equal(uiThread, changedOn);
        Assert.True(loaded);
    }

    [Fact]
    public async Task Plugin_pages_are_created_one_ui_job_each_and_a_newer_list_takes_over()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        var jobs = new ManualJobs();
        MainWindowViewModel vm = f.CreateShell(connection, ui: jobs);

        Task first = vm.SyncCorePluginPagesAsync([Page("oadm.ntp", "Servers"), Page("oadm.dhcp", "Servers")]);
        Assert.Equal(["devices"], Keys(vm)); // nothing created before its job runs
        jobs.RunNext();
        Assert.Equal(["devices", "plugin:oadm.ntp"], Keys(vm));

        // The plugins change before the second page is created: the newer list wins, the older sync stops.
        Task second = vm.SyncCorePluginPagesAsync([Page("oadm.pki", "Security")]);
        Assert.Equal(["devices"], Keys(vm)); // NTP is off now
        jobs.RunNext(); // the older sync's DHCP job: skipped
        await first;
        jobs.RunNext();
        await second;

        Assert.Equal(["devices", "plugin:oadm.pki"], Keys(vm));
        Assert.Equal(0, jobs.Count);
    }

    private static CorePluginInfo Page(string id, string group) => new() { Id = id, DisplayName = id, Group = group };

    private static string[] Keys(MainWindowViewModel vm) => [.. vm.NavItems.Where(n => n.Key == "devices" || n.Key.StartsWith("plugin:", StringComparison.Ordinal)).Select(n => n.Key)];

    /// <summary>Runs posts at once and keeps the background jobs until the test runs them, one by one.</summary>
    private sealed class ManualJobs : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Done)> _jobs = new();

        public int Count => _jobs.Count;

        public void Post(Action action) => action();

        public Task RunInBackgroundAsync(Action action)
        {
            var done = new TaskCompletionSource();
            _jobs.Enqueue((action, done));
            return done.Task;
        }

        public void RunNext()
        {
            (Action action, TaskCompletionSource done) = _jobs.Dequeue();
            action();
            done.SetResult(); // continues the sync inline: its next job is queued before this returns
        }
    }
}
