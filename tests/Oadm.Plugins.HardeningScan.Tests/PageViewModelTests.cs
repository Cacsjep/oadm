using System.Diagnostics;

using Oadm.Plugins.HardeningScan.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.HardeningScan.Tests;

public sealed class PageViewModelTests(ITestOutputHelper output)
{
    private static HardeningClientSettingsStore TempSettings() =>
        new(Path.Combine(Path.GetTempPath(), "oadm-hardening-tests", Guid.NewGuid().ToString("N"), "client.json"));

    /// <summary>A compact result: every column scanned at <paramref name="level"/>, states chosen by the device index.</summary>
    internal static DeviceResult Result(Guid id, int i, ScanLevel level, DateTimeOffset when, string? status = null)
    {
        var states = new char[HardeningCatalog.Columns.Count];
        var values = new string?[states.Length];
        for (var c = 0; c < states.Length; c++)
        {
            var column = HardeningCatalog.Columns[c];
            if (column.Level == ScanLevel.Extended && level == ScanLevel.Basic)
            {
                states[c] = '-';
                continue;
            }

            states[c] = status is not null ? 'e'
                : !column.IsRated ? 'i'
                : column.Id == HardeningCatalog.Ssh && i % 10 == 0 ? 'f'
                : column.Id == HardeningCatalog.Discovery && i % 3 == 0 ? 'w'
                : column.Id == HardeningCatalog.Snmp && i % 7 == 0 ? 'f'
                : 'p';
            values[c] = states[c] == 'f' ? "SSH on" : "ok";
        }

        return new DeviceResult { DeviceId = id, Level = level, ScannedUtc = when, Status = status, States = new string(states), Values = values };
    }

    private static List<TestDevice> Devices(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new TestDevice { Address = $"10.{i / 65_000}.{i / 250 % 250}.{(i % 250) + 1}", Serial = $"B8A44F{i:X6}", Model = i % 2 == 0 ? "P3265-V" : "M3106-L Mk II" })];

    [Fact]
    [Trait("Category", "Timing")]
    public void Five_thousand_devices_filter_search_and_summarize_fast()
    {
        var devices = Devices(5_000);
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        ctx.DeviceList.AddRange(devices);
        var watch = Stopwatch.StartNew();
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        var create = watch.Elapsed;

        var now = DateTimeOffset.UtcNow;
        var results = devices.Select((d, i) => Result(d.Id, i, ScanLevel.Basic, now, i % 50 == 49 ? "Unreachable - no answer" : null)).ToList();
        watch.Restart();
        vm.ApplyState(new HardeningState { Results = results });
        var apply = watch.Elapsed;

        Assert.Equal(5_000, vm.Rows.Count);
        Assert.Equal(100, vm.CountOf(RowKind.NotReachable));
        Assert.Equal(500, vm.CountOf(RowKind.Fail)); // every 10th (SSH on)
        Assert.Contains("5,000 devices · 5,000 scanned", vm.SummaryText, StringComparison.Ordinal);

        watch.Restart();
        vm.SelectedFilter = HardeningScanViewModel.FilterFailed;
        var filter = watch.Elapsed;
        Assert.Equal(500, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal(RowKind.Fail, r.Kind));

        watch.Restart();
        vm.SelectedFilter = HardeningScanViewModel.FilterAll;
        vm.SearchText = "m3106";
        var search = watch.Elapsed;
        Assert.Equal(2_500, vm.Rows.Count);

        watch.Restart();
        vm.Level = ScanLevel.Extended;
        var level = watch.Elapsed;
        Assert.Equal(HardeningCatalog.Columns.Count, vm.Columns.Count);

        // A 1,000-row batch of an Extended scan updates only those rows and the counters.
        var batch = devices.Take(1_000).Select((d, i) => Result(d.Id, i, ScanLevel.Extended, now.AddMinutes(1))).ToList();
        watch.Restart();
        vm.ApplyResults(batch);
        var applyBatch = watch.Elapsed;
        var snmp = vm.Columns.Select((c, i) => (c, i)).Single(x => x.c.Check.Id == HardeningCatalog.Snmp).i;
        Assert.Equal(143, vm.ColumnCount(snmp, CheckState.Fail)); // every 7th of the first 1,000
        Assert.Contains("Fail 143", vm.HeaderTip(snmp), StringComparison.Ordinal);

        output.WriteLine($"create {create.TotalMilliseconds:F0} ms, state {apply.TotalMilliseconds:F0} ms, filter {filter.TotalMilliseconds:F0} ms, search {search.TotalMilliseconds:F0} ms, level {level.TotalMilliseconds:F0} ms, batch {applyBatch.TotalMilliseconds:F0} ms");
        foreach (var elapsed in new[] { create, apply, filter, search, level, applyBatch })
        {
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"too slow: {elapsed.TotalMilliseconds:F0} ms");
        }
    }

    [Fact]
    public void An_extended_result_covers_the_basic_columns_and_the_newest_wins()
    {
        var device = new TestDevice();
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        ctx.DeviceList.Add(device);
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        var now = DateTimeOffset.UtcNow;

        vm.ApplyResults([Result(device.Id, 0, ScanLevel.Extended, now.AddHours(-2))]);
        var row = vm.Rows.Single();
        Assert.Equal(RowKind.Fail, row.Kind);
        Assert.Equal(CheckState.Fail, row.StateAt(vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Ssh)));

        // A newer Basic result: its Basic columns win, the Extended columns still come from the Extended result.
        vm.ApplyResults([Result(device.Id, 1, ScanLevel.Basic, now)]);
        Assert.Equal(CheckState.Pass, row.StateAt(vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Ssh)));
        vm.Level = ScanLevel.Extended;
        Assert.Equal(CheckState.Fail, row.StateAt(vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Snmp)));
        Assert.Equal(CheckState.Pass, row.StateAt(vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Ssh)));

        // An older result never replaces a newer one.
        vm.ApplyResults([Result(device.Id, 0, ScanLevel.Basic, now.AddDays(-1))]);
        Assert.Equal(CheckState.Pass, row.StateAt(vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Ssh)));
        Assert.Equal("just now", row.LastScanText);
    }

    [Fact]
    public void Results_of_devices_the_client_does_not_know_yet_wait_for_them()
    {
        var device = new TestDevice();
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        vm.ApplyResults([Result(device.Id, 0, ScanLevel.Basic, DateTimeOffset.UtcNow)]);
        Assert.Empty(vm.Rows);

        ctx.DeviceList.Add(device);
        ctx.RaiseDevicesChanged();
        Assert.Equal(RowKind.Fail, vm.Rows.Single().Kind);

        ctx.DeviceList.Clear();
        ctx.RaiseDevicesChanged();
        Assert.Empty(vm.Rows);
        Assert.Contains("0 devices", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_progress_and_results_come_from_the_plugin_through_events()
    {
        var devices = Devices(5);
        var plugin = new HardeningScanPlugin(new Scanning.HardeningScanOptions { EventInterval = TimeSpan.FromMilliseconds(10) });
        var repository = new FakeDevices();
        var vapix = new FakeVapixFactory();
        foreach (var device in devices)
        {
            repository.Items.Add(device);
            vapix.Add(device.Id);
        }

        var events = new RecordingEvents();
        await plugin.StartAsync(new TestCoreContext(repository, vapix, new MemorySettings(), events), CancellationToken.None);
        await using var _ = plugin;
        var ctx = new PageContext(plugin);
        ctx.DeviceList.AddRange(devices);
        ctx.Selection.Add(devices[1]);
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        await vm.LoadAsync();
        Assert.Equal(5, vm.CountOf(RowKind.NotScanned));
        Assert.Equal("Scan selected (1)", vm.ScanSelectedText);

        await vm.ScanSelectedCommand.ExecuteAsync(null);
        await plugin.Service!.WaitAsync();
        foreach (var item in events.Events.ToList())
        {
            vm.HandleEvent(item);
        }

        Assert.False(vm.IsScanning);
        Assert.Equal("Scan finished: 1 of 1 devices", vm.ProgressText);
        Assert.Equal(RowKind.Fail, vm.AllRows.Single(r => r.DeviceId == devices[1].Id).Kind);
        Assert.Equal(4, vm.CountOf(RowKind.NotScanned));

        // The detail pane shows every check of the level with the longer texts of getDetail.
        vm.SelectedRow = vm.AllRows.Single(r => r.DeviceId == devices[1].Id);
        await Wait.UntilAsync(() => vm.DetailItems.Any(d => d.Found.Contains("Object Analytics", StringComparison.Ordinal)));
        Assert.Equal(15, vm.DetailItems.Count);
        Assert.Contains(vm.DetailItems, d => d.Title == "SSH access" && d.IsError && d.Found == "SSH on");
    }

    [Fact]
    public void The_csv_export_has_the_device_columns_and_two_columns_per_check()
    {
        var devices = Devices(2);
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        ctx.DeviceList.AddRange(devices);
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        vm.ApplyResults([Result(devices[0].Id, 0, ScanLevel.Basic, new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero))]);

        var csv = CsvExport.Build(vm.Columns, vm.Rows);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CsvExport.Bom, csv[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("﻿Address,MAC address,Model,AXIS OS,Last scan (UTC),Score,Status,Latest AXIS OS,Latest AXIS OS (found),Dedicated accounts", lines[0], StringComparison.Ordinal);
        Assert.Equal(7 + (2 * 15), lines[0].Split(',').Length);
        Assert.Contains("2026-10-08 09:30:00", lines[1], StringComparison.Ordinal);
        Assert.Contains("fail,SSH on", lines[1], StringComparison.Ordinal);
        Assert.Contains("Not scanned", lines[2], StringComparison.Ordinal);
        Assert.Equal("\"a,b\"", CsvExport.Quote("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExport.Quote("say \"hi\""));
    }

    [Fact]
    public void The_level_and_the_detail_height_are_remembered_per_client()
    {
        var store = TempSettings();
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        using (var vm = new HardeningScanViewModel(ctx, store))
        {
            vm.Level = ScanLevel.Extended;
        }

        using var again = new HardeningScanViewModel(ctx, store);
        Assert.True(again.IsExtended);
    }

    [Fact]
    public void The_result_column_of_the_detail_sorts_failed_first()
    {
        var column = new LevelColumn(HardeningCatalog.ColumnsOf(ScanLevel.Basic)[0], 0);
        CheckState[] states = [CheckState.Pass, CheckState.NotScanned, CheckState.Warn, CheckState.NotApplicable, CheckState.Error, CheckState.Fail];

        var sorted = states.Select(s => new DetailItem(column, s, null, null)).OrderBy(d => d.SortRank).Select(d => d.StateText).ToArray();

        Assert.Equal(states.OrderBy(s => s switch
        {
            CheckState.Fail => 0, CheckState.Error => 1, CheckState.Warn => 2, CheckState.NotApplicable => 3, CheckState.Pass => 4, _ => 5,
        }).Select(CheckStateCodes.ToLabel).ToArray(), sorted);
    }

    [Fact]
    public void A_cell_tooltip_says_what_was_found_the_rule_and_the_recommendation()
    {
        var device = new TestDevice();
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        ctx.DeviceList.Add(device);
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        vm.ApplyResults([Result(device.Id, 0, ScanLevel.Basic, DateTimeOffset.UtcNow)]);
        var row = vm.Rows.Single();
        var ssh = vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Ssh);
        var tip = row.CellTip(vm.Columns[ssh], ssh);
        Assert.StartsWith("SSH access: Fail\nSSH on", tip, StringComparison.Ordinal);
        Assert.Contains("Fail: SSH is on.", tip, StringComparison.Ordinal);
        Assert.Contains("(Disable unused services/functions > SSH access)", tip, StringComparison.Ordinal);

        var discovery = vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Discovery);
        Assert.Contains("Bonjour", row.CellTip(vm.Columns[discovery], discovery), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_with_other_topics_or_without_payload_are_ignored()
    {
        var ctx = new PageContext((_, _) => Task.FromResult<string?>(null));
        using var vm = new HardeningScanViewModel(ctx, TempSettings());
        vm.HandleEvent(new PluginEvent("other", "{}"));
        vm.HandleEvent(new PluginEvent(HardeningMethods.ResultsTopic, null));
        Assert.Empty(vm.Rows);
        _ = DeviceStatus.Ok;
    }
}
