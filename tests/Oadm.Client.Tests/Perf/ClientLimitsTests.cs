using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Devices;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

/// <summary>
/// Where the client stops scaling: the real Devices page rendered headless and the task store.
/// Opt-in: <c>OADM_LIMITS_DEVICES</c> = the device counts to try, e.g. "5000,25000,50000". Nothing is asserted on time.
/// </summary>
[Trait("Category", "Perf")]
public sealed class ClientLimitsTests(ITestOutputHelper output)
{
    private static readonly string[] TagNames = [.. Enumerable.Range(0, 10).Select(i => $"Building {(char)('A' + i)}")];

    private static IReadOnlyList<int> Sizes =>
        (Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToList();

    private static List<Device> Tagged(int size, int version = 0)
    {
        List<Device> devices = ClientScale.MakeDevices(size, version);
        for (int i = 0; i < devices.Count; i++)
        {
            devices[i].Tags.AddRange(Enumerable.Range(0, i % 4).Select(k => TagNames[(i + k + version) % TagNames.Length]).Order(StringComparer.Ordinal));
        }

        return devices;
    }

    [Fact]
    public async Task DevicesPageAtEverySize()
    {
        foreach (int size in Sizes)
        {
            List<string> lines = await HeadlessSession.Shared.Dispatch(() =>
            {
                var log = new List<string> { $"== {size:N0} devices" };
                double heapStart = HeapMb();
                using var f = new DevicesFixture();
                f.Store.Tags.Reset(TagNames.Select((n, i) => new DeviceTag { Name = n, Color = (TagColor)(i % 8 + 1), Defined = true }));
                List<Device> devices = Tagged(size);
                Time(log, "Device list arrives (store, rows, filter)", () => f.SeedDevices([.. devices]));
                devices = [];
                log.Add($"  Client heap with the devices: {HeapMb() - heapStart:N0} MB");
                DevicesViewModel vm = f.Devices;

                var window = new Window { Width = 1440, Height = 900, Content = new DevicesView { DataContext = vm } };
                Time(log, "First render of the grid", () =>
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                });
                DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");

                string[] keystrokes = ["1", "10", "10.", "10.0", "10.0.1", "10.0.1.", "10.0.1.2", "P", "P3", "P32", "P326", ""];
                var slowest = TimeSpan.Zero;
                foreach (string text in keystrokes)
                {
                    var watch = Stopwatch.StartNew();
                    vm.SearchText = text;
                    Dispatcher.UIThread.RunJobs();
                    slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
                }

                log.Add($"  Search: slowest keystroke {slowest.TotalMilliseconds:N0} ms");
                Time(log, "Select all in the grid (+ context menu)", () =>
                {
                    grid.SelectAll();
                    Dispatcher.UIThread.RunJobs();
                });
                Time(log, "Sort by Model", () =>
                {
                    grid.Columns.First(c => c.Header as string == "Model").Sort(ListSortDirection.Descending);
                    Dispatcher.UIThread.RunJobs();
                });
                List<Device> newer = Tagged(size, version: 2);
                Time(log, "Every device changes at once (a poll round with news), sorted", () =>
                {
                    f.Store.ApplyBatch([.. newer.Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]);
                    Dispatcher.UIThread.RunJobs();
                });
                newer = [];
                Time(log, "Group by tag on and render", () =>
                {
                    vm.GroupByTag = true;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                });
                log.Add($"  Group rows: {vm.TagGrouping.Rows.Count:N0}");
                Time(log, "Select all in group mode", () =>
                {
                    grid.SelectAll();
                    Dispatcher.UIThread.RunJobs();
                });
                Time(log, "1,000 tag changes while grouped", () =>
                {
                    f.Store.ApplyBatch([.. Tagged(1000, version: 3).Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                });
                log.Add($"  Client heap with the page open: {HeapMb() - heapStart:N0} MB");
                window.Close();
                return Task.FromResult(log);
            }, CancellationToken.None);

            foreach (string line in lines)
            {
                output.WriteLine(line);
            }
        }
    }

    [Fact]
    public void TaskStoreForARunOnEveryDevice()
    {
        foreach (int size in Sizes)
        {
            var log = new List<string> { $"== {size:N0} devices, a Run on all of them" };
            var devices = new DeviceStore();
            devices.Reset(ClientScale.MakeDevices(size));
            var store = new TaskStore(devices);
            DateTime now = DateTime.UtcNow;
            List<TaskChanged> added = [.. Enumerable.Range(0, size).Select(i => new TaskChanged
            {
                Kind = TaskChanged.Types.Kind.Added,
                Task = ClientScale.Task(i, ClientScale.DeviceId(i), TaskState.Queued, now),
            })];
            Time(log, "Tasks pane: every task added", () => store.ApplyBatch(added));
            List<TaskChanged> done = [.. added.Select(c => new TaskChanged
            {
                Kind = TaskChanged.Types.Kind.Updated,
                Task = ClientScale.Task(int.Parse(c.Task.Id[5..], System.Globalization.CultureInfo.InvariantCulture), c.Task.DeviceId, TaskState.Done, now),
            })];
            Time(log, "Tasks pane: every task done", () => store.ApplyBatch(done));
            log.Add($"  Tasks kept: {store.Tasks.Count:N0}");
            foreach (string line in log)
            {
                output.WriteLine(line);
            }
        }
    }

    [Fact]
    public async Task DevicePickerTreeAndAddPageAtEverySize()
    {
        foreach (int size in Sizes)
        {
            List<string> lines = await HeadlessSession.Shared.Dispatch(() =>
            {
                var log = new List<string> { $"== {size:N0} devices: device selector, device tree, add page" };
                List<LimitDevice> devices = [.. Enumerable.Range(0, size).Select(i => new LimitDevice(
                    $"10.{i / 65536}.{i / 256 % 256}.{i % 256}", (i % 3) switch { 0 => "P3265-V", 1 => "M3106-L", _ => "Q6135-LE" }, $"ACCC8E{i:X6}")
                {
                    Tags = [.. Enumerable.Range(0, i % 4).Select(k => TagNames[(i + k) % TagNames.Length])],
                })];

                Oadm.Sdk.Client.Controls.DevicePickerViewModel picker = null!;
                Time(log, "Selector opens (all options, tags)", () => picker = new Oadm.Sdk.Client.Controls.DevicePickerViewModel(
                    devices, Oadm.Sdk.Client.Controls.DeviceChoice.Devices(devices.Take(100).Select(d => d.Id), ["Building C"]),
                    Oadm.Sdk.Client.Controls.DevicePickerOptions.Full(null, TagNames)));
                var window = new Oadm.Sdk.Client.Controls.DevicePickerWindow(picker);
                Time(log, "Selector window renders", () =>
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                });
                SlowestKeystroke(log, "Selector search", text =>
                {
                    picker.SearchText = text;
                    Dispatcher.UIThread.RunJobs();
                });
                Time(log, "Selector: select all, then none", () =>
                {
                    picker.SelectAllCommand.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    picker.SelectNoneCommand.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                });
                Time(log, "Selector: check a tag", () =>
                {
                    picker.Tags[0].IsChecked = true;
                    Dispatcher.UIThread.RunJobs();
                });
                log.Add($"  Selector summary: {picker.Summary}");
                window.Close();

                var tree = new Oadm.Sdk.Client.Controls.DeviceTreeModel();
                Time(log, "Device tree (Web View, Live View) builds", () => tree.Rebuild(devices, string.Empty));
                log.Add($"  Tree rows: {tree.Rows.Count:N0}");
                SlowestKeystroke(log, "Device tree search", text => tree.Rebuild(devices, text));

                var page = new Oadm.Client.Discovery.AddDevicesViewModel(NSubstitute.Substitute.For<Oadm.Client.Api.IOadmApi>(), new Oadm.Client.Infrastructure.ImmediateUiDispatcher(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Discovery.AddDevicesViewModel>.Instance, Oadm.Client.Discovery.AddDevicesMode.IpRange);
                List<DiscoveredDevice> found = [.. Enumerable.Range(0, size).Select(i => new DiscoveredDevice
                {
                    DiscoveredId = "d" + i,
                    Serial = $"ACCC8E{i:X6}",
                    Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                    Model = "P3265-V",
                    Scheme = "https",
                    Status = DeviceStatus.Ok,
                    AuthState = i % 10 == 0 ? AuthState.LoginFailed : AuthState.Authenticated,
                })];
                Time(log, "Add page: every device found and logged in", () =>
                {
                    foreach (DiscoveredDevice device in found)
                    {
                        page.OnDiscovered("s1", device);
                    }
                });
                Time(log, "Add page: select all logged in", () => page.SelectAllAuthenticatedCommand.Execute(null));
                SlowestKeystroke(log, "Add page search", text => page.SearchText = text);
                return Task.FromResult(log);
            }, CancellationToken.None);

            foreach (string line in lines)
            {
                output.WriteLine(line);
            }
        }
    }

    private static void SlowestKeystroke(List<string> log, string what, Action<string> type)
    {
        string[] keystrokes = ["1", "10", "10.", "10.0", "10.0.1", "10.0.1.", "10.0.1.2", "P", "P3", "P32", "P326", "Building", ""];
        var slowest = TimeSpan.Zero;
        foreach (string text in keystrokes)
        {
            var watch = Stopwatch.StartNew();
            type(text);
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
        }

        log.Add($"  {what}: slowest keystroke {slowest.TotalMilliseconds:N0} ms");
    }

    private sealed record LimitDevice(string Address, string? Model, string Serial) : Oadm.Sdk.Devices.IDeviceInfo
    {
        public IReadOnlyList<string> Tags { get; init; } = [];

        public Guid Id { get; } = Guid.NewGuid();

        public string? HostName => null;

        public string? FirmwareVersion => "12.11.77";

        public Oadm.Sdk.Devices.DeviceStatus Status => Oadm.Sdk.Devices.DeviceStatus.Ok;

        public Oadm.Sdk.Devices.DeviceCategory Category => Oadm.Sdk.Devices.DeviceCategory.Camera;

        public bool HasVideo => true;

        public IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi> Apis => [];
    }

    private static void Time(List<string> log, string what, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        log.Add($"  {what}: {watch.Elapsed.TotalMilliseconds:N0} ms");
    }

    private static double HeapMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
    }
}
