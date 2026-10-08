using System.Collections.Specialized;
using System.Diagnostics;

using Google.Protobuf.WellKnownTypes;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

/// <summary>Scale helpers of the client tests: 5,000 devices, 50,000 tasks (CLAUDE.md "scale to thousands of devices").</summary>
internal static class ClientScale
{
    public const int Devices = 5_000;
    public const int Tasks = 50_000;

    public static TimeSpan Measure(ITestOutputHelper output, string what, TimeSpan budget, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        output.WriteLine($"{what}: {watch.Elapsed.TotalMilliseconds:F1} ms (budget {budget.TotalMilliseconds:F0} ms)");
        Assert.True(watch.Elapsed < budget, $"{what} took {watch.Elapsed.TotalMilliseconds:F0} ms, budget {budget.TotalMilliseconds:F0} ms");
        return watch.Elapsed;
    }

    public static string DeviceId(int i) => $"00000000-0000-0000-0000-{i:D12}";

    /// <summary>5,000 devices with 100 APIs each, three models, every 50th unreachable.</summary>
    public static List<Device> MakeDevices(int count = Devices, int version = 0)
    {
        var apis = Enumerable.Range(0, 100).Select(i => new DeviceApi { Id = $"api-{i:D3}", Version = "1.0", Name = $"Some API {i}", Status = "official" }).ToList();
        var list = new List<Device>(count);
        for (int i = 0; i < count; i++)
        {
            Device device = TestSupport.Device(DeviceId(i), $"ACCC8E{i:X6}", $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                (i % 3) switch { 0 => "P3265-V", 1 => "M3106-L", _ => "Q6135-LE" },
                i % 50 == 0 ? DeviceStatus.Unreachable : DeviceStatus.Ok);
            device.FirmwareVersion = version == 0 ? "12.11.77" : "12.11." + (77 + version);
            device.Apis.AddRange(apis);
            list.Add(device);
        }

        return list;
    }

    public static TaskInfo Task(int i, string deviceId, TaskState state, DateTime created) => new()
    {
        Id = $"task-{i:D6}",
        PluginId = "oadm.restart",
        Name = "Restart",
        State = state,
        Owner = "tester",
        Created = Timestamp.FromDateTime(created),
        Started = Timestamp.FromDateTime(created.AddSeconds(1)),
        Progress = state == TaskState.Done ? 100 : 40,
        DeviceId = deviceId,
        BatchId = $"batch-{i / Devices}",
        Devices = { new TaskDeviceResult { DeviceId = deviceId, State = state, Progress = 100 } },
        Steps =
        {
            new TaskStep { Index = 0, Name = "Restart the device", State = TaskStepState.Done, Progress = 100 },
            new TaskStep { Index = 1, Name = "Wait for the device to come back", State = state == TaskState.Done ? TaskStepState.Done : TaskStepState.Running, Progress = 50 },
        },
        CurrentStepIndex = 1,
    };

    public static TaskPluginInfo RunnableOnAll(string id, IEnumerable<string> ids)
    {
        var info = TestSupport.Plugin(id, "Plugin " + id, toolbar: true, dialog: false);
        info.RunnableDeviceIds.AddRange(ids);
        info.Group = "Maintenance";
        return info;
    }
}

/// <summary>Device store, Devices page filter, selection and context menu with 5,000 devices.</summary>
[Trait("Category", "Perf")]
public sealed class DeviceListScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void Store_loads_5000_devices_once_and_an_identical_snapshot_changes_nothing()
    {
        var store = new DeviceStore();
        int collectionEvents = 0;
        store.Devices.CollectionChanged += (_, _) => collectionEvents++;
        List<Device> devices = ClientScale.MakeDevices();

        ClientScale.Measure(output, "DeviceStore.Reset 5,000 devices (100 APIs each)", TimeSpan.FromSeconds(3), () => store.Reset(devices));
        Assert.Equal(ClientScale.Devices, store.Devices.Count);
        Assert.Equal(1, collectionEvents);
        Assert.Same(store.Devices[0].Apis, store.Devices[1].Apis);

        ClientScale.Measure(output, "DeviceStore.Reset with the same 5,000 devices (reconnect)", TimeSpan.FromSeconds(2), () => store.Reset(ClientScale.MakeDevices()));
        Assert.Equal(1, collectionEvents);
    }

    [Fact]
    public void A_change_costs_O1_and_a_batch_raises_one_event()
    {
        var store = new DeviceStore();
        store.Reset(ClientScale.MakeDevices());
        var changedEvents = new List<DeviceStoreChangedEventArgs>();
        int collectionEvents = 0;
        store.Changed += (_, e) => changedEvents.Add(e);
        store.Devices.CollectionChanged += (_, _) => collectionEvents++;
        List<Device> newer = ClientScale.MakeDevices(version: 1);

        ClientScale.Measure(output, "1,000 single DeviceChanged updates on a 5,000-device store", TimeSpan.FromMilliseconds(500), () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = newer[i] });
            }
        });
        Assert.Equal(1000, changedEvents.Count);
        Assert.All(changedEvents, e => Assert.Single(e.DeviceIds));
        Assert.Equal(0, collectionEvents);

        changedEvents.Clear();
        var batch = newer.Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d }).ToList();
        ClientScale.Measure(output, "ApplyBatch of 5,000 updates", TimeSpan.FromSeconds(1), () => store.ApplyBatch(batch));
        DeviceStoreChangedEventArgs only = Assert.Single(changedEvents);
        Assert.Equal(ClientScale.Devices - 1000, only.DeviceIds.Count); // the first 1,000 did not change again

        changedEvents.Clear();
        ClientScale.Measure(output, "ApplyBatch of 5,000 unchanged devices", TimeSpan.FromSeconds(1), () => store.ApplyBatch(batch));
        Assert.Empty(changedEvents);

        var removals = Enumerable.Range(0, 2500).Select(i => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Removed, Device = new Device { Id = ClientScale.DeviceId(i * 2) } }).ToList();
        ClientScale.Measure(output, "ApplyBatch removing 2,500 of 5,000 devices", TimeSpan.FromSeconds(1), () => store.ApplyBatch(removals));
        Assert.Equal(2500, store.Devices.Count);
        Assert.Equal(1, collectionEvents);
    }

    [Fact]
    public async Task Devices_page_search_select_all_and_context_menu_with_5000_devices()
    {
        using var fixture = new DevicesFixture();
        fixture.SeedDevices([.. ClientScale.MakeDevices()]);
        DevicesViewModel vm = fixture.Devices;
        Assert.Equal(ClientScale.Devices, vm.FilteredDevices.Count);
        IEnumerable<string> runnable = fixture.Store.Devices.Where(d => d.ContractStatus == DeviceStatus.Ok).Select(d => d.Id);
        await fixture.SetPluginsAsync([.. Enumerable.Range(0, 10).Select(i => ClientScale.RunnableOnAll("perf.plugin" + i, runnable))]);

        int filterEvents = 0;
        vm.FilteredDevices.CollectionChanged += (_, _) => filterEvents++;
        string[] keystrokes = ["1", "10", "10.", "10.0", "10.0.1", "10.0.1.", "10.0.1.2", "P", "P3", "P32", "P326", "", "Q6135", "unreachable", ""];
        ClientScale.Measure(output, $"Search, {keystrokes.Length} keystrokes over 5,000 devices", TimeSpan.FromSeconds(2), () =>
        {
            foreach (string text in keystrokes)
            {
                vm.SearchText = text;
            }
        });
        Assert.True(filterEvents <= keystrokes.Length, "at most one notification per keystroke");
        Assert.Equal(ClientScale.Devices, vm.FilteredDevices.Count);

        int selectionEvents = 0;
        vm.SelectedDevices.CollectionChanged += (_, _) => selectionEvents++;
        List<DeviceRowViewModel> healthy = [.. vm.FilteredDevices.Where(d => d.ContractStatus == DeviceStatus.Ok)];
        ClientScale.Measure(output, "Select all 4,900 runnable devices + context menu with 10 plugins", TimeSpan.FromSeconds(1), () =>
            vm.SelectedDevices.ReplaceAll(healthy));
        Assert.Equal(1, selectionEvents);
        MenuEntryViewModel group = Assert.Single(vm.ContextMenuEntries, e => e.Header == "Maintenance");
        Assert.Equal(10, group.Items!.Count);
        Assert.True(vm.RunPluginCommand.CanExecute(fixture.Catalog.Plugins[0]));
        Assert.True(vm.ToolbarContext.CanRunTask("perf.plugin3"));

        ClientScale.Measure(output, "Select all 5,000 incl. unreachable (no plugin runnable)", TimeSpan.FromSeconds(1), () =>
            vm.SelectedDevices.ReplaceAll(vm.FilteredDevices));
        MenuEntryViewModel greyed = Assert.Single(vm.ContextMenuEntries, e => e.Header == "Maintenance");
        Assert.All(greyed.Items!, e => Assert.False(e.IsEnabled));
        Assert.Equal("Not supported on this device: 100 of 5000 selected devices", greyed.Items![0].ToolTip);
        Assert.Contains("5000 selected", vm.StatusLine.Replace(",", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Runnable_checks_use_sets_for_both_the_full_and_the_compact_form()
    {
        List<string> ids = [.. Enumerable.Range(0, ClientScale.Devices).Select(ClientScale.DeviceId)];
        TaskPluginInfo full = ClientScale.RunnableOnAll("full", ids.Skip(1));
        var compact = new TaskPluginInfo { Id = "compact", RunnableOnAllExcept = true, NotRunnableDeviceIds = { ids[0] } };
        bool all = false;
        ClientScale.Measure(output, "RunnableFor 2 plugins x 4,999 selected devices, 100 times", TimeSpan.FromSeconds(1), () =>
        {
            for (int i = 0; i < 100; i++)
            {
                all = TaskPluginCatalog.RunnableFor([full, compact], ids.Skip(1).ToList()).Count() == 2;
            }
        });
        Assert.True(all);
        Assert.Empty(TaskPluginCatalog.RunnableFor([full, compact], [ids[0]]));
        Assert.True(TaskPluginCatalog.CanRun(compact, ids[4999]));
        Assert.False(TaskPluginCatalog.CanRun(compact, ids[0]));
    }

    [Fact]
    public async Task Plugin_catalog_refreshes_are_throttled_not_starved_under_constant_device_changes()
    {
        IOadmApi api = Substitute.For<IOadmApi>();
        api.ListTaskPluginsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TaskPluginInfo>());
        var catalog = new TaskPluginCatalog(api, new ImmediateUiDispatcher(), NullLogger<TaskPluginCatalog>.Instance)
        {
            DebounceDelay = TimeSpan.FromMilliseconds(50),
            MinRefreshInterval = TimeSpan.FromMilliseconds(500),
        };

        // A device change every 10 ms for 1.5 s (a 5,000-device site with polling and tasks running).
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            catalog.RequestRefresh();
            await Task.Delay(10);
        }

        await TestSupport.WaitUntilAsync(() => catalog.RefreshCount >= 3, 5000);
        await Task.Delay(800);
        output.WriteLine($"ListTaskPlugins calls for ~150 requests in 1.5 s: {catalog.RefreshCount}");
        Assert.InRange(catalog.RefreshCount, 3, 6); // a debounce alone would have starved (0 calls) until the changes stop
    }
}

/// <summary>Task store and Tasks pane with 50,000 tasks on the server and 5,000-task runs.</summary>
[Trait("Category", "Perf")]
public sealed class TaskListScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void Snapshot_of_50000_tasks_keeps_the_newest_10000_plus_all_active()
    {
        var devices = new DeviceStore();
        devices.Reset(ClientScale.MakeDevices());
        var store = new TaskStore(devices);
        int collectionEvents = 0;
        store.Tasks.CollectionChanged += (_, _) => collectionEvents++;
        DateTime start = DateTime.UtcNow.AddDays(-1);
        List<TaskInfo> snapshot = [.. Enumerable.Range(0, ClientScale.Tasks).Select(i =>
            ClientScale.Task(i, ClientScale.DeviceId(i % ClientScale.Devices), i < 100 ? TaskState.Queued : TaskState.Done, start.AddSeconds(i)))];

        ClientScale.Measure(output, "TaskStore.Reset with 50,000 tasks", TimeSpan.FromSeconds(5), () => store.Reset(snapshot));
        Assert.Equal(TaskStore.MaxTasks, store.Tasks.Count);
        Assert.Equal(1, collectionEvents);
        Assert.Equal(100, store.ActiveCount);
        Assert.Equal("task-049999", store.Tasks[0].Id);
        Assert.All(Enumerable.Range(0, 100), i => Assert.NotNull(store.Find($"task-{i:D6}"))); // old but active: kept
    }

    [Fact]
    public void Device_changes_relabel_only_the_tasks_of_that_device()
    {
        var devices = new DeviceStore();
        devices.Reset(ClientScale.MakeDevices());
        var store = new TaskStore(devices);
        DateTime start = DateTime.UtcNow.AddHours(-1);
        store.Reset(Enumerable.Range(0, TaskStore.MaxTasks).Select(i => ClientScale.Task(i, ClientScale.DeviceId(i % ClientScale.Devices), TaskState.Done, start.AddSeconds(i))));
        List<Device> renamed = ClientScale.MakeDevices();
        foreach (Device device in renamed)
        {
            device.Address = "192.168." + device.Address[3..];
        }

        // Before: every device change re-labelled all tasks (5,000 changes x 10,000 tasks = 50 million label updates).
        ClientScale.Measure(output, "1,000 single device changes with 10,000 tasks in the store", TimeSpan.FromMilliseconds(500), () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                devices.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = renamed[i] });
            }
        });
        Assert.StartsWith("192.168.", store.Find("task-000000")!.DeviceText, StringComparison.Ordinal);
        Assert.StartsWith("10.", store.Find("task-001001")!.DeviceText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_on_5000_devices_arrives_as_batches_with_one_notification_each()
    {
        var devices = new DeviceStore();
        devices.Reset(ClientScale.MakeDevices());
        var store = new TaskStore(devices);
        int collectionEvents = 0;
        int changedEvents = 0;
        store.Tasks.CollectionChanged += (_, _) => collectionEvents++;
        store.Changed += (_, _) => changedEvents++;
        DateTime now = DateTime.UtcNow;
        List<TaskChanged> added = [.. Enumerable.Range(0, ClientScale.Devices).Select(i => new TaskChanged
        {
            Kind = TaskChanged.Types.Kind.Added,
            Task = ClientScale.Task(i, ClientScale.DeviceId(i), TaskState.Queued, now),
        })];

        ClientScale.Measure(output, "ApplyBatch of 5,000 Added tasks", TimeSpan.FromSeconds(2), () => store.ApplyBatch(added));
        Assert.Equal(ClientScale.Devices, store.ActiveCount);
        Assert.Equal(1, collectionEvents);
        Assert.Equal(1, changedEvents);

        List<TaskChanged> done = [.. added.Select(c => new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = ClientScale.Task(int.Parse(c.Task.Id[5..], System.Globalization.CultureInfo.InvariantCulture), c.Task.DeviceId, TaskState.Done, now) })];
        ClientScale.Measure(output, "ApplyBatch of 5,000 Updated (Done) tasks", TimeSpan.FromSeconds(2), () => store.ApplyBatch(done));
        Assert.Equal(0, store.ActiveCount);
        Assert.Equal(1, collectionEvents);

        List<TaskChanged> removed = [.. added.Select(c => new TaskChanged { Kind = TaskChanged.Types.Kind.Removed, Task = new TaskInfo { Id = c.Task.Id } })];
        ClientScale.Measure(output, "ApplyBatch of 5,000 Removed (Delete all)", TimeSpan.FromSeconds(1), () => store.ApplyBatch(removed));
        Assert.Empty(store.Tasks);
        Assert.Equal(2, collectionEvents);
    }

    [Fact]
    public async Task Changes_from_a_background_stream_reach_the_UI_in_few_batches()
    {
        var ui = new QueuedUiDispatcher();
        var applied = new List<int>();
        int batches = 0;
        var batcher = new ChangeBatcher<int>(ui, batch =>
        {
            batches++;
            applied.AddRange(batch);
        });

        await Task.Run(() =>
        {
            for (int i = 0; i < ClientScale.Devices; i++)
            {
                batcher.Add(i);
            }
        });
        ui.RunAll();

        Assert.Equal(Enumerable.Range(0, ClientScale.Devices), applied);
        output.WriteLine($"5,000 changes from a stream: {ui.Posts} dispatcher posts, {batches} UI batches");
        Assert.Equal(1, ui.Posts); // the former code posted 5,000 times
    }

    [Fact]
    public void Add_page_handles_5000_discovered_devices_and_select_all()
    {
        IOadmApi api = Substitute.For<IOadmApi>();
        var page = new AddDevicesViewModel(api, new ImmediateUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.IpRange);
        List<DiscoveredDevice> found = [.. Enumerable.Range(0, ClientScale.Devices).Select(i => new DiscoveredDevice
        {
            DiscoveredId = "d" + i,
            Serial = $"ACCC8E{i:X6}",
            Address = $"10.0.{i / 256}.{i % 256}",
            Model = "P3265-V",
            Scheme = "https",
            Status = DeviceStatus.Ok,
            AuthState = i % 10 == 0 ? AuthState.LoginFailed : AuthState.Authenticated,
        })];

        ClientScale.Measure(output, "5,000 discovered devices + 5,000 login results (IP range of a /16)", TimeSpan.FromSeconds(3), () =>
        {
            foreach (DiscoveredDevice device in found)
            {
                page.OnDiscovered("s1", new DiscoveredDevice(device) { AuthState = AuthState.Pending });
            }

            foreach (DiscoveredDevice device in found)
            {
                page.OnDiscovered("s1", device);
            }
        });
        Assert.Equal(ClientScale.Devices, page.Rows.Count);
        Assert.Contains("4500 ready to add", page.SummaryText.Replace(",", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("500 need a login", page.SummaryText.Replace(",", "", StringComparison.Ordinal), StringComparison.Ordinal);

        ClientScale.Measure(output, "Select all 4,500 authenticated devices", TimeSpan.FromSeconds(2), () => page.SelectAllAuthenticatedCommand.Execute(null));
        Assert.Equal(4500, page.SelectedCount);

        ClientScale.Measure(output, "Search the add page list", TimeSpan.FromMilliseconds(500), () =>
        {
            page.SearchText = "10.0.1";
            page.SearchText = "";
        });
        Assert.Equal(ClientScale.Devices, page.FilteredRows.Count);
    }

    /// <summary>Dispatcher that queues posts until <see cref="RunAll"/>, like the UI thread while it is busy.</summary>
    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

        public int Posts { get; private set; }

        public void Post(Action action)
        {
            Posts++;
            _queue.Enqueue(action);
        }

        public void RunAll()
        {
            while (_queue.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }
}
