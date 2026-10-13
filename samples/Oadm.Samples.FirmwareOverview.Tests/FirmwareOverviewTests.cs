using System.Text.Json;

using Oadm.Samples.FirmwareOverview.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Samples.FirmwareOverview.Tests;

public sealed class FirmwareOverviewTests
{
    [Fact]
    public async Task Refresh_reads_every_device_and_publishes_the_table()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"), ("10.0.0.49", DeviceStatus.Ok, "11.11.124"));

        await plugin.InvokeAsync(FirmwareOverview.Refresh, null, CancellationToken.None);
        var state = await NextStateAsync(server, s => !s.Reading);

        Assert.Equal(["10.0.0.48 12.11.77", "10.0.0.49 11.11.124"], state.Rows.Select(r => $"{r.Address} {r.Version}"));
        Assert.All(state.Rows, r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task Devices_OADM_cannot_log_in_to_are_not_contacted()
    {
        var (plugin, server) = await StartAsync(("10.0.0.50", DeviceStatus.CredentialsRequired, "12.11.77"));

        await plugin.InvokeAsync(FirmwareOverview.Refresh, null, CancellationToken.None);
        var row = Assert.Single((await NextStateAsync(server, s => !s.Reading)).Rows);

        Assert.Equal(DeviceMessages.CredentialsRequired, row.Error);
        Assert.Equal(0, server.Cameras.Values.Single().Reads);
    }

    [Fact]
    public async Task An_unknown_method_is_refused()
    {
        var (plugin, _) = await StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("format", null, CancellationToken.None));
    }

    [Fact]
    public async Task The_page_shows_the_table_and_ignores_older_states()
    {
        var (plugin, server) = await StartAsync(("10.0.0.48", DeviceStatus.Ok, "12.11.77"));
        using var page = new FirmwareOverviewViewModel(new FakePageContext(plugin, server));

        await page.RefreshCommand.ExecuteAsync(null);
        var state = await NextStateAsync(server, s => !s.Reading);
        page.Apply(JsonSerializer.Serialize(state, FirmwareOverview.Json));
        page.Apply(JsonSerializer.Serialize(state with { Version = 1, Rows = [] }, FirmwareOverview.Json)); // older: ignored

        Assert.Equal("12.11.77", Assert.Single(page.Rows).Version);
        Assert.Equal("1 device", page.Summary);
        Assert.False(page.IsReading);
    }

    private static async Task<(FirmwareOverviewPlugin Plugin, FakeCoreContext Server)> StartAsync(params (string Address, DeviceStatus Status, string Version)[] devices)
    {
        var server = new FakeCoreContext();
        foreach (var (address, status, version) in devices)
        {
            var device = new FakeDevice(address, status);
            server.DeviceList.Add(device);
            server.Cameras[device.Id] = new FakeCamera(version);
        }

        var plugin = new FirmwareOverviewPlugin();
        await plugin.StartAsync(server, CancellationToken.None);
        return (plugin, server);
    }

    /// <summary>The next published state that matches, within 5 s.</summary>
    private static async Task<FirmwareState> NextStateAsync(FakeCoreContext server, Func<FirmwareState, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
