using System.Text.Json;

using Oadm.Samples.FirmwareOverview.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Samples.FirmwareOverview.Tests;

public sealed class FirmwareOverviewTests
{
    [Fact]
    public async Task Refresh_reads_the_chosen_devices_and_publishes_the_table()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"), ("10.0.0.49", DeviceStatus.Ok, "11.11.124"), ("10.0.0.50", DeviceStatus.Ok, "10.12.1"));
        var chosen = server.DeviceList.Take(2).Select(d => d.Id).ToList();

        await CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(false, chosen));
        var state = await NextStateAsync(server, s => !s.Reading);

        Assert.Equal(["10.0.0.48 12.11.77", "10.0.0.49 11.11.124"], state.Rows.Select(r => $"{r.Address} {r.Version}"));
        Assert.All(state.Rows, r => Assert.Null(r.Error));
        Assert.Equal(0, server.Cameras[server.DeviceList[2].Id].Reads); // not chosen: not contacted
    }

    [Fact]
    public async Task All_devices_is_one_short_request_and_nothing_chosen_is_refused()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"), ("10.0.0.49", DeviceStatus.Ok, "11.11.124"));

        await CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(true, []));
        Assert.Equal(2, (await NextStateAsync(server, s => !s.Reading)).Rows.Count);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(false, [])));
        Assert.Equal(FirmwareOverview.NothingChosen, refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(server, plugin, FirmwareOverview.Refresh, null));
    }

    [Fact]
    public async Task Devices_OADM_cannot_log_in_to_are_not_contacted()
    {
        var (plugin, server) = await StartAsync(("10.0.0.50", DeviceStatus.CredentialsRequired, "12.11.77"));

        await CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(true, []));
        var row = Assert.Single((await NextStateAsync(server, s => !s.Reading)).Rows);

        Assert.Equal(DeviceMessages.CredentialsRequired, row.Error);
        Assert.Equal(0, server.Cameras.Values.Single().Reads);
    }

    [Fact]
    public async Task An_unknown_method_is_refused()
    {
        var (plugin, server) = await StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(server, plugin, "format", null));
    }

    [Fact]
    public async Task The_page_reads_nothing_before_a_choice()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"));
        using var page = new FirmwareOverviewViewModel(new FakePageContext(plugin, server));

        Assert.True(page.Devices.IsEmpty);
        Assert.Equal("Choose devices", page.Devices.ButtonText);
        Assert.False(page.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_page_shows_the_table_and_ignores_older_states()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"));
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = [server.DeviceList[0]] };
        using var page = new FirmwareOverviewViewModel(ctx); // starts with the Devices page selection

        Assert.Equal("Selected devices: 10.0.0.48", page.Devices.ButtonText);
        await page.RefreshCommand.ExecuteAsync(null);
        var state = await NextStateAsync(server, s => !s.Reading);
        page.Apply(JsonSerializer.Serialize(state, FirmwareOverview.Json));
        page.Apply(JsonSerializer.Serialize(state with { Version = 1, Rows = [] }, FirmwareOverview.Json)); // older: ignored

        Assert.Equal("12.11.77", Assert.Single(page.Rows).Version);
        Assert.Equal("1 device", page.Summary);
        Assert.False(page.IsReading);
    }

    [Fact]
    public async Task A_user_with_a_device_group_reads_and_sees_only_its_devices()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"), ("10.0.0.49", DeviceStatus.Ok, "11.11.124"));
        var key = server.FakeScopes.Add("Building B", [server.DeviceList[0].Id]);
        var operatorB = FakeCallContext.WithGroup(server, [server.DeviceList[0].Id], groupId: key);

        await CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(true, []), operatorB);
        var state = await NextStateAsync(server, s => !s.Reading);

        Assert.Equal("10.0.0.48", Assert.Single(state.Rows).Address); // "all" = the user's devices
        Assert.Equal(0, server.Cameras[server.DeviceList[1].Id].Reads);
        Assert.Empty(server.EventsSeenBy(server.CallAsAllDevices())); // the table of the group goes to its users only
    }

    [Fact]
    public async Task A_device_that_left_the_group_drops_out_of_its_table()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"), ("10.0.0.49", DeviceStatus.Ok, "11.11.124"));
        var ids = server.DeviceList.Select(d => d.Id).ToList();
        var key = server.FakeScopes.Add("Building B", ids);
        await CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(true, []), FakeCallContext.WithGroup(server, ids, groupId: key));
        Assert.Equal(2, (await NextStateAsync(server, s => !s.Reading)).Rows.Count);

        // A tag change moves 10.0.0.49 out of the group; the plugin has not been told yet.
        server.FakeScopes.Add("Building B", [ids[0]], key);
        var now = FakeCallContext.WithGroup(server, [ids[0]], groupId: key);
        var state = JsonSerializer.Deserialize<FirmwareState>((await server.InvokeAsync(plugin, now, FirmwareOverview.GetState, null))!, FirmwareOverview.Json)!;

        Assert.Equal("10.0.0.48", Assert.Single(state.Rows).Address);
    }

    [Fact]
    public async Task A_group_change_during_a_read_keeps_the_page_reading()
    {
        var server = new FakeCoreContext();
        var camera = new HeldCamera();
        server.Add(new FakeDevice { Address = "10.0.0.48", Status = DeviceStatus.Ok }, camera);
        var plugin = new FirmwareOverviewPlugin();
        await plugin.StartAsync(server, CancellationToken.None);
        var ids = server.DeviceList.Select(d => d.Id).ToList();
        var key = server.FakeScopes.Add("Building B", ids);

        await CallAsync(server, plugin, FirmwareOverview.Refresh, new RefreshRequest(true, []), FakeCallContext.WithGroup(server, ids, groupId: key));
        await NextStateAsync(server, s => s.Reading);
        await camera.Started.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the read is on the camera now

        // The group changes while the read runs: the old rows go, the page keeps showing that it reads.
        server.FakeScopes.Delete(key);
        var changed = await NextStateAsync(server, _ => true);
        Assert.True(changed.Reading);
        Assert.Empty(changed.Rows);

        camera.Release.SetResult();
        Assert.False((await NextStateAsync(server, s => !s.Reading)).Reading);
    }

    /// <summary>A camera whose firmware read waits until the test releases it (no timeout can end it first).</summary>
    private sealed class HeldCamera : FakeCamera
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Oadm.Sdk.Vapix.BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task;
            return Info;
        }
    }

    /// <summary>The canary: a user whose group holds only "inside" calls every method with "outside" and with an unknown id.</summary>
    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"), ("10.0.0.49", DeviceStatus.Ok, "11.11.124"));

        await new PluginCanary(server, plugin, inside: server.DeviceList[0], outside: server.DeviceList[1])
            .Method(FirmwareOverview.Refresh, """{"all":false,"deviceIds":["{inside}","{device}"]}""")
            .WithoutDevices(FirmwareOverview.GetState, "the table of the user's devices")
            .RunAsync();
    }

    private static async Task<(FirmwareOverviewPlugin Plugin, FakeCoreContext Server)> StartAsync(params (string Address, DeviceStatus Status, string Version)[] devices)
    {
        var server = new FakeCoreContext();
        foreach (var (address, status, version) in devices)
        {
            var camera = new FakeCamera();
            camera.Info = camera.Info with { Version = version };
            server.Add(new FakeDevice { Address = address, Status = status }, camera);
        }

        var plugin = new FirmwareOverviewPlugin();
        await plugin.StartAsync(server, CancellationToken.None);
        return (plugin, server);
    }

    /// <summary>Calls the server part like OADM does: as <paramref name="user"/>, else as a user with all devices.</summary>
    private static Task<string?> CallAsync(FakeCoreContext server, FirmwareOverviewPlugin plugin, string method, RefreshRequest? request, ICallContext? user = null) =>
        server.InvokeAsync(plugin, user ?? server.CallAsAllDevices(), method, request is null ? null : JsonSerializer.Serialize(request, FirmwareOverview.Json));

    /// <summary>The next published state that matches, within 30 s (a busy test machine may be slow).</summary>
    private static async Task<FirmwareState> NextStateAsync(FakeCoreContext server, Func<FirmwareState, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var item in server.Published.Reader.ReadAllAsync(timeout.Token))
        {
            var state = JsonSerializer.Deserialize<FirmwareState>(item.PayloadJson!, FirmwareOverview.Json)!;
            if (match(state))
            {
                return state;
            }
        }

        throw new TimeoutException();
    }
}
