using NSubstitute;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class DevicesViewModelTests
{
    private static DevicesFixture CreateWithDevices()
    {
        var f = new DevicesFixture();
        f.SeedDevices(
            TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"),
            TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II"),
            TestSupport.Device("3", "B8A44F2E7A90", "10.0.0.23", "AXIS P1468-LE", DeviceStatus.CredentialsRequired),
            TestSupport.Device("4", "00408CA1B2C3", "10.0.0.30", "AXIS Q1798-LE", DeviceStatus.Unreachable));
        return f;
    }

    [Fact]
    public void Status_line_shows_device_and_selection_count()
    {
        using DevicesFixture f = CreateWithDevices();

        Assert.Equal("4 devices, 0 selected", f.Devices.StatusLine);

        f.Select("1", "3");

        Assert.Equal("4 devices, 2 selected", f.Devices.StatusLine);
    }

    [Fact]
    public void Status_line_follows_live_add_and_remove()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Select("4");

        f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = TestSupport.Device("5", "AAAAAAAAAAAA", "10.0.0.99", "AXIS M3215-LVE") });
        f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Removed, Device = new Device { Id = "4" } });

        Assert.Equal("4 devices, 0 selected", f.Devices.StatusLine);
        Assert.Contains(f.Devices.FilteredDevices, d => d.Id == "5");
        Assert.DoesNotContain(f.Devices.FilteredDevices, d => d.Id == "4");
    }

    [Theory]
    [InlineData("p3265", new[] { "1" })]
    [InlineData("B8A44F", new[] { "1", "3" })]
    [InlineData("10.0.0.2", new[] { "2", "3" })]
    [InlineData("unreachable", new[] { "4" })]
    [InlineData("credentials", new[] { "3" })]
    [InlineData("nothing-matches", new string[0])]
    public void Search_filters_across_columns_case_insensitive(string search, string[] expectedIds)
    {
        using DevicesFixture f = CreateWithDevices();

        f.Devices.SearchText = search;

        Assert.Equal(expectedIds, f.Devices.FilteredDevices.Select(d => d.Id).Order().ToArray());
    }

    [Fact]
    public void Clearing_search_restores_all_rows_and_new_devices_respect_filter()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Devices.SearchText = "P3265";

        f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = TestSupport.Device("5", "AAAAAAAAAAAA", "10.0.0.99", "AXIS M3215-LVE") });
        Assert.Single(f.Devices.FilteredDevices);

        f.Devices.ClearSearchCommand.Execute(null);

        Assert.Equal(5, f.Devices.FilteredDevices.Count);
    }

    [Fact]
    public void Live_update_changes_row_in_place()
    {
        using DevicesFixture f = CreateWithDevices();
        DeviceRowViewModel row = f.Store.Find("1")!;

        f.Store.Apply(new DeviceChanged
        {
            Kind = DeviceChanged.Types.Kind.Updated,
            Device = TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V", DeviceStatus.Unreachable),
        });

        Assert.Same(row, f.Store.Find("1"));
        Assert.Equal("Unreachable", row.StatusText);
        Assert.True(row.IsStatusError);
    }

    [Fact]
    public void Row_texts_follow_the_spec()
    {
        Device device = TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V");
        device.Dot1XEnabled = false;
        device.ClearHttpsEnabled();
        var row = new DeviceRowViewModel(device);

        Assert.Equal("Yes", row.DhcpText);
        Assert.Equal("", row.HttpsText);
        Assert.Equal("Disabled", row.Dot1xText);
        Assert.Equal("10.0.0.48", row.DisplayAddress);
    }

    [Fact]
    public async Task Context_menu_has_core_actions_and_plugins_runnable_for_whole_selection()
    {
        using DevicesFixture f = CreateWithDevices();
        await f.SetPluginsAsync(
            TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2"),
            TestSupport.Plugin("oadm.password", "Change password", toolbar: false, dialog: true, "1", "2", "3"),
            TestSupport.Plugin("oadm.only-one", "Only device 1", toolbar: false, dialog: false, "1"));

        f.Select("1", "2");

        string[] headers = f.Devices.ContextMenuEntries.Select(e => e.Header).ToArray();
        Assert.Equal(["Open web interface", "Refresh", "Remove", "-", "Restart", "Change password..."], headers);
        Assert.False(f.Devices.ContextMenuEntries[0].IsEnabled); // web UI needs exactly one device
    }

    [Fact]
    public async Task Context_menu_without_runnable_plugins_has_no_separator()
    {
        using DevicesFixture f = CreateWithDevices();
        await f.SetPluginsAsync(TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2"));

        f.Select("4");

        Assert.Equal(["Open web interface", "Refresh", "Remove"], f.Devices.ContextMenuEntries.Select(e => e.Header).ToArray());
        Assert.True(f.Devices.ContextMenuEntries[0].IsEnabled);
    }

    [Fact]
    public async Task Context_menu_is_empty_without_selection()
    {
        using DevicesFixture f = CreateWithDevices();
        await f.SetPluginsAsync(TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1"));

        f.Select();

        Assert.Empty(f.Devices.ContextMenuEntries);
    }

    [Fact]
    public async Task Toolbar_shows_only_show_in_toolbar_plugins_and_enables_by_selection()
    {
        using DevicesFixture f = CreateWithDevices();
        TaskPluginInfo restart = TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2");
        await f.SetPluginsAsync(restart, TestSupport.Plugin("oadm.identify", "Identify", toolbar: false, dialog: false, "1"));

        Assert.Equal(["oadm.restart"], f.Devices.ToolbarActions.Select(p => p.Id).ToArray());
        Assert.False(f.Devices.RunPluginCommand.CanExecute(restart));

        f.Select("1");
        Assert.True(f.Devices.RunPluginCommand.CanExecute(restart));

        f.Select("1", "4");
        Assert.False(f.Devices.RunPluginCommand.CanExecute(restart));
    }

    [Fact]
    public async Task Running_a_plugin_without_dialog_calls_server_with_selection()
    {
        using DevicesFixture f = CreateWithDevices();
        TaskPluginInfo restart = TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2");
        await f.SetPluginsAsync(restart);
        f.Api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs("task-1");
        f.Select("1", "2");
        f.TasksVm.IsExpanded = false;

        await f.Devices.RunPluginCommand.ExecuteAsync(restart);

        await f.Api.Received(1).RunTaskAsync("oadm.restart",
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "1", "2" })),
            Arg.Is<string?>(p => p == null), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(f.TasksVm.IsExpanded); // a started task brings the collapsed pane back
    }

    [Fact]
    public async Task Remove_asks_for_confirmation()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Select("1");
        f.Dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(false);

        await f.Devices.RemoveCommand.ExecuteAsync(null);
        await f.Api.DidNotReceiveWithAnyArgs().RemoveDevicesAsync(default!, default);

        f.Dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        await f.Devices.RemoveCommand.ExecuteAsync(null);
        await f.Api.Received(1).RemoveDevicesAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_without_selection_refreshes_all()
    {
        using DevicesFixture f = CreateWithDevices();

        await f.Devices.RefreshCommand.ExecuteAsync(null);

        await f.Api.Received(1).RefreshDevicesAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 4), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Address_link_opens_web_ui()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Api.GetWebUiUrlAsync("1", Arg.Any<CancellationToken>()).Returns("https://10.0.0.48/");

        await f.Devices.OpenWebUiCommand.ExecuteAsync(f.Store.Find("1"));

        await f.Launcher.Received(1).OpenAsync(new Uri("https://10.0.0.48/"));
    }
}
