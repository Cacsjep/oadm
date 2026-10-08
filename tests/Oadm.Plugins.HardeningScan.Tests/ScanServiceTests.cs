using Oadm.Plugins.HardeningScan.Scanning;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>The scan jobs against fake cameras: parallelism, timeouts, refused statuses, stop, persistence, events.</summary>
public sealed class ScanServiceTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(int devices, HardeningScanOptions? options = null, MemorySettings? settings = null)
        {
            Settings = settings ?? new MemorySettings();
            for (var i = 0; i < devices; i++)
            {
                var device = new TestDevice { Address = $"10.0.{i / 250}.{(i % 250) + 1}", Serial = $"B8A44F{i:X6}" };
                Devices.Items.Add(device);
                Vapix.Add(device.Id, Camera);
            }

            Plugin = new HardeningScanPlugin(options ?? Fast());
        }

        public FakeDevices Devices { get; } = new();

        public FakeVapixFactory Vapix { get; } = new();

        /// <summary>One camera object behind every device (concurrency is counted over all of them).</summary>
        public FakeCamera Camera { get; } = new();

        public MemorySettings Settings { get; }

        public RecordingEvents Events { get; } = new();

        public HardeningScanPlugin Plugin { get; }

        public HardeningScanService Service => Plugin.Service!;

        public static HardeningScanOptions Fast() => new()
        {
            EventInterval = TimeSpan.FromMilliseconds(20),
            FlushInterval = TimeSpan.FromMilliseconds(50),
        };

        public async Task<Rig> StartAsync()
        {
            await Plugin.StartAsync(new TestCoreContext(Devices, Vapix, Settings, Events), CancellationToken.None);
            return this;
        }

        public async Task<ScanJobStatus> ScanAsync(ScanLevel level = ScanLevel.Basic, params Guid[] ids)
        {
            var json = await Plugin.InvokeAsync(HardeningMethods.StartScan, HardeningJson.Serialize(new StartScanRequest { Level = level, DeviceIds = ids }), CancellationToken.None);
            return HardeningJson.Deserialize<ScanJobStatus>(json);
        }

        public async Task<HardeningState> StateAsync() =>
            HardeningJson.Deserialize<HardeningState>(await Plugin.InvokeAsync(HardeningMethods.GetState, null, CancellationToken.None));

        public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
    }

    [Fact]
    public async Task A_scan_reads_every_device_at_most_16_at_a_time_and_streams_the_results()
    {
        await using var rig = await new Rig(40).StartAsync();
        rig.Camera.Delay = TimeSpan.FromMilliseconds(15);

        var started = await rig.ScanAsync();
        Assert.True(started.IsRunning);
        Assert.Equal(40, started.Total);
        var again = await rig.ScanAsync(ScanLevel.Extended);
        Assert.Equal(started.JobId, again.JobId); // a start while a scan runs returns the running scan
        await rig.Service.WaitAsync();

        Assert.InRange(rig.Camera.MaxConcurrent, 2, 16);
        Assert.Empty(rig.Camera.Violations);
        Assert.Equal(40, rig.Events.Results.Select(r => r.DeviceId).Distinct().Count());
        var last = rig.Events.Progress[^1];
        Assert.Equal(ScanJobStates.Done, last.State);
        Assert.Equal(40, last.Done);
        Assert.Equal(0, last.Failed);

        var state = await rig.StateAsync();
        Assert.Equal(40, state.Results.Count);
        Assert.Equal(HardeningCatalog.ColumnIds, state.Columns);
        var row = state.Results[0];
        Assert.Equal(HardeningCatalog.Columns.Count, row.States.Length);
        Assert.Equal('f', row.States[HardeningCatalog.ColumnIndex[HardeningCatalog.Ssh]]);
        Assert.Equal('-', row.States[HardeningCatalog.ColumnIndex[HardeningCatalog.Snmp]]);
        Assert.Equal("SSH on", row.Values[HardeningCatalog.ColumnIndex[HardeningCatalog.Ssh]]);
    }

    [Fact]
    public async Task Refused_statuses_are_not_contacted_and_keep_the_cache_only_checks()
    {
        await using var rig = await new Rig(0).StartAsync();
        var refused = new[]
        {
            new TestDevice { Status = DeviceStatus.CertificateChanged },
            new TestDevice { Status = DeviceStatus.CredentialsRequired },
            new TestDevice { Status = DeviceStatus.PasswordNotSet },
            new TestDevice { Status = DeviceStatus.Unreachable },
        };
        foreach (var device in refused)
        {
            rig.Devices.Items.Add(device);
            rig.Vapix.Add(device.Id);
        }

        await rig.ScanAsync();
        await rig.Service.WaitAsync();

        Assert.Equal(0, rig.Vapix.Created);
        var results = (await rig.StateAsync()).Results.ToDictionary(r => r.DeviceId);
        Assert.Equal("Certificate changed. Remove the device and add it again to trust the new certificate.", results[refused[0].Id].Status);
        Assert.Equal("Credentials required - the device rejects the stored credentials", results[refused[1].Id].Status);
        Assert.Equal("Password not set - the device is in factory default", results[refused[2].Id].Status);
        Assert.StartsWith("Unreachable", results[refused[3].Id].Status, StringComparison.Ordinal);
        var states = results[refused[1].Id].States;
        Assert.Equal('i', states[HardeningCatalog.ColumnIndex[HardeningCatalog.AxisOs]]);
        Assert.Equal('p', states[HardeningCatalog.ColumnIndex[HardeningCatalog.Uart]]);
        Assert.Equal('e', states[HardeningCatalog.ColumnIndex[HardeningCatalog.Ssh]]);
        Assert.Equal(4, rig.Events.Progress[^1].Failed);
    }

    [Fact]
    public async Task A_hanging_device_ends_with_the_device_timeout_and_the_others_finish()
    {
        var options = Rig.Fast();
        options.DeviceTimeout = TimeSpan.FromMilliseconds(300);
        options.RequestTimeout = TimeSpan.FromSeconds(10);
        await using var rig = await new Rig(3, options).StartAsync();
        var slow = new TestDevice { Address = "10.0.9.9" };
        var camera = new FakeCamera();
        camera.Hangs.Add("axis-cgi/param.cgi");
        rig.Devices.Items.Add(slow);
        rig.Vapix.Add(slow.Id, camera);

        await rig.ScanAsync();
        await rig.Service.WaitAsync();

        var results = (await rig.StateAsync()).Results.ToDictionary(r => r.DeviceId);
        Assert.Equal(4, results.Count);
        Assert.Equal("Timeout after 0.3 s", results[slow.Id].Status);
        Assert.Null(results[rig.Devices.Items[0].Id].Status);
    }

    [Fact]
    public async Task Stop_ends_the_scan_and_keeps_what_was_scanned()
    {
        var options = Rig.Fast();
        options.Parallelism = 2;
        await using var rig = await new Rig(30, options).StartAsync();
        rig.Camera.Delay = TimeSpan.FromMilliseconds(20);

        var job = await rig.ScanAsync();
        await Wait.UntilAsync(() => rig.Service.CurrentJob!.Done >= 2);
        var cancelled = HardeningJson.Deserialize<ScanJobStatus>(await rig.Plugin.InvokeAsync(HardeningMethods.CancelScan, HardeningJson.Serialize(new CancelScanRequest { JobId = job.JobId }), CancellationToken.None));
        Assert.Equal(job.JobId, cancelled.JobId);
        await rig.Service.WaitAsync();

        var final = rig.Service.CurrentJob!;
        Assert.Equal(ScanJobStates.Cancelled, final.State);
        Assert.InRange(final.Done, 2, 29);
        Assert.Equal(final.Done, (await rig.StateAsync()).Results.Count);
        Assert.Null(await rig.Plugin.InvokeAsync(HardeningMethods.CancelScan, HardeningJson.Serialize(new CancelScanRequest { JobId = "unknown" }), CancellationToken.None));
    }

    [Fact]
    public async Task Results_are_kept_per_device_and_level_across_a_restart_and_removed_devices_are_dropped()
    {
        var settings = new MemorySettings();
        Guid first;
        Guid second;
        await using (var rig = await new Rig(2, settings: settings).StartAsync())
        {
            await rig.ScanAsync(ScanLevel.Basic);
            await rig.Service.WaitAsync();
            await rig.ScanAsync(ScanLevel.Extended, rig.Devices.Items[0].Id);
            await rig.Service.WaitAsync();
            first = rig.Devices.Items[0].Id;
            second = rig.Devices.Items[1].Id;
        }

        Assert.NotNull(settings.Values[Scanning.ResultStore.SettingKey]);
        await using var restarted = new Rig(0, settings: settings);
        restarted.Devices.Items.Add(new TestDevice { Id = first });
        restarted.Devices.Items.Add(new TestDevice { Id = second, Address = "10.0.0.2" });
        await restarted.StartAsync();

        var state = await restarted.StateAsync();
        Assert.Equal(3, state.Results.Count);
        Assert.Contains(state.Results, r => r.DeviceId == first && r.Level == ScanLevel.Extended && r.States[HardeningCatalog.ColumnIndex[HardeningCatalog.Snmp]] == 'p');
        Assert.Contains(state.Results, r => r.DeviceId == second && r.Level == ScanLevel.Basic);

        var detail = HardeningJson.Deserialize<DetailReply>(await restarted.Plugin.InvokeAsync(HardeningMethods.GetDetail, HardeningJson.Serialize(new DetailRequest { DeviceIds = [first] }), CancellationToken.None));
        Assert.Equal(2, detail.Devices.Count);
        Assert.Contains(detail.Devices.Single(d => d.Level == ScanLevel.Basic).Checks, c => c.Id == HardeningCatalog.Applications && c.Detail!.Contains("Object Analytics", StringComparison.Ordinal));

        restarted.Devices.Items.RemoveAt(1); // a removed device's result is dropped
        state = await restarted.StateAsync();
        Assert.DoesNotContain(state.Results, r => r.DeviceId == second);
    }

    [Fact]
    public async Task Many_results_are_split_into_batches_of_at_most_1000_rows()
    {
        var options = Rig.Fast();
        options.EventInterval = TimeSpan.FromSeconds(30); // everything arrives in the final publish
        options.Parallelism = 64;
        await using var rig = await new Rig(1_200, options).StartAsync();

        await rig.ScanAsync();
        await rig.Service.WaitAsync();

        var batches = rig.Events.Events.Where(e => e.Topic == HardeningMethods.ResultsTopic).ToList();
        Assert.InRange(batches.Count, 2, 4);
        Assert.All(batches, b => Assert.InRange(b.PayloadJson!.Length, 1, Oadm.Sdk.Plugins.IPluginEvents.MaxPayloadLength));
        Assert.All(batches, b => Assert.InRange(HardeningJson.Deserialize<ResultsEvent>(b.PayloadJson).Results.Count, 1, HardeningScanPluginInfo.EventBatchRows));
        Assert.Equal(1_200, rig.Events.Results.Count);
    }

    [Fact]
    public async Task The_parallelism_setting_is_read_from_the_plugin_settings()
    {
        var settings = new MemorySettings();
        settings.Values[HardeningScanService.ConfigKey] = """{"parallelism":3}""";
        await using var rig = await new Rig(20, settings: settings).StartAsync();
        rig.Camera.Delay = TimeSpan.FromMilliseconds(10);
        Assert.Equal(3, rig.Service.Parallelism);

        await rig.ScanAsync();
        await rig.Service.WaitAsync();
        Assert.InRange(rig.Camera.MaxConcurrent, 1, 3);
    }

    [Fact]
    public async Task The_store_keeps_at_most_the_configured_devices_per_level()
    {
        var store = new ResultStore(maxPerLevel: 3);
        for (var i = 0; i < 5; i++)
        {
            store.Set(new DeviceDetail { DeviceId = Guid.NewGuid(), Level = ScanLevel.Basic, ScannedUtc = DateTimeOffset.UtcNow.AddMinutes(i) });
        }

        Assert.Equal(3, store.Count);
        Assert.All(store.All(), d => Assert.True(d.ScannedUtc > DateTimeOffset.UtcNow.AddMinutes(1)));
        var copy = new ResultStore();
        copy.Load(store.Serialize());
        Assert.Equal(3, copy.Count);
        Assert.False(store.IsDirty);
        copy.Load("{broken");
        Assert.Equal(3, copy.Count);
        await Task.CompletedTask;
    }
}
