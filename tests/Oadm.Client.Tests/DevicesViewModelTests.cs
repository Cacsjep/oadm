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

        // Plugins without a group land in "General"; a group is always a submenu, the host appends no "...".
        string[] headers = f.Devices.ContextMenuEntries.Select(e => e.Header).ToArray();
        Assert.Equal(["Open web interface", "Refresh", "Tags", "Remove", "-", "General"], headers);
        Assert.False(f.Devices.ContextMenuEntries[0].IsEnabled); // web UI needs exactly one device
        Assert.Equal(["Change password", "Restart"], f.Devices.ContextMenuEntries[5].Items!.Select(e => e.Header).ToArray());
    }

    [Fact]
    public async Task Context_menu_groups_tasks_in_sorted_submenus_and_strips_ellipses()
    {
        using DevicesFixture f = CreateWithDevices();
        TaskPluginInfo Grouped(string id, string name, string group, string icon)
        {
            TaskPluginInfo info = TestSupport.Plugin(id, name, toolbar: false, dialog: true, "1", "2");
            info.Group = group;
            info.IconKey = icon;
            return info;
        }

        await f.SetPluginsAsync(
            Grouped("oadm.restart", "Restart", "Maintenance", "restart"),
            Grouped("oadm.firmware", "Upgrade firmware...", "Maintenance", "firmware"),
            Grouped("oadm.network", "Network settings…", "Network", "network"),
            Grouped("oadm.network.assign-ip", "Assign IP address", "Network", "network"),
            Grouped("oadm.users", "Users......", "Users", "users"),
            Grouped("oadm.acap", "Applications (ACAP)", "Applications", ""),
            Grouped("x.custom", "A task with a far too long display name for menus", "Custom tools", "custom"),
            Grouped("x.nogroup", "Identify", "", "identify"));

        f.Select("1", "2");

        IReadOnlyList<MenuEntryViewModel> entries = f.Devices.ContextMenuEntries;
        Assert.Equal(["Open web interface", "Refresh", "Tags", "Remove", "-", "Applications", "Custom tools", "General", "Maintenance", "Network", "Users"],
            entries.Select(e => e.Header).ToArray());
        Assert.All(entries.Skip(5), g => Assert.NotNull(g.Items)); // one submenu per group, even with one entry
        Assert.Equal("network", entries.Single(e => e.Header == "Network").IconKey);
        Assert.Equal("plugin", entries.Single(e => e.Header == "Custom tools").IconKey);

        MenuEntryViewModel Group(string name) => entries.Single(e => e.Header == name);
        Assert.Equal(["Restart", "Upgrade firmware"], Group("Maintenance").Items!.Select(e => e.Header).ToArray());
        Assert.Equal(["Assign IP address", "Network settings"], Group("Network").Items!.Select(e => e.Header).ToArray());
        Assert.Equal("Users", Group("Users").Items!.Single().Header);
        Assert.Equal("Identify", Group("General").Items!.Single().Header);
        Assert.Equal("plugin", Group("Applications").Items!.Single().IconKey); // no icon key: the plugin icon
        string longName = Group("Custom tools").Items!.Single().Header;
        Assert.Equal(Oadm.Sdk.Plugins.TaskPluginNames.MaxDisplayNameLength, longName.Length);
        Assert.EndsWith("…", longName, StringComparison.Ordinal);
        Assert.DoesNotContain(entries.SelectMany(e => e.Items ?? []), e => e.Header.EndsWith("...", StringComparison.Ordinal));

        MenuEntryViewModel restart = Group("Maintenance").Items![0];
        Assert.Same(f.Devices.RunPluginCommand, restart.Command);
        Assert.Equal("oadm.restart", ((TaskPluginInfo)restart.CommandParameter!).Id);
    }

    [Fact]
    public async Task Context_menu_without_runnable_plugins_has_no_separator()
    {
        using DevicesFixture f = CreateWithDevices();
        await f.SetPluginsAsync(TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2"));

        f.Select("4");

        Assert.Equal(["Open web interface", "Refresh", "Tags", "Remove"], f.Devices.ContextMenuEntries.Select(e => e.Header).ToArray());
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
    public async Task Toolbar_context_reports_task_plugins_and_runnability_for_the_selection()
    {
        using DevicesFixture f = CreateWithDevices();
        TaskPluginInfo restart = TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2");
        await f.SetPluginsAsync(restart, TestSupport.Plugin("oadm.identify", "Identify", toolbar: false, dialog: false, "1"));
        var ctx = f.Devices.ToolbarContext;
        int selectionEvents = 0;
        ctx.SelectionChanged += (_, _) => selectionEvents++;

        Assert.Equal(["oadm.restart"], ctx.TaskPlugins.Where(p => p.ShowInToolbar).Select(p => p.Id).ToArray());
        Assert.Equal(4, ctx.Devices.Count);
        Assert.False(ctx.CanRunTask("oadm.restart"));
        Assert.False(f.Devices.RunPluginCommand.CanExecute(restart));

        f.Select("1");
        Assert.True(ctx.CanRunTask("oadm.restart"));
        Assert.Equal("10.0.0.48", Assert.Single(ctx.SelectedDevices).Address);
        Assert.True(selectionEvents > 0);

        f.Select("1");
        Assert.True(f.Devices.RunPluginCommand.CanExecute(restart));

        f.Select("1", "4");
        Assert.False(f.Devices.RunPluginCommand.CanExecute(restart));
        Assert.False(ctx.CanRunTask("oadm.restart"));
        Assert.False(ctx.CanRunTask("unknown"));
    }

    [Fact]
    public async Task Toolbar_context_runs_tasks_on_the_selection_and_opens_host_pages()
    {
        using DevicesFixture f = CreateWithDevices();
        await f.SetPluginsAsync(TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2"));
        f.Api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs((IReadOnlyList<string>)["task-1"]);
        f.Select("2");
        f.TasksVm.IsExpanded = false;

        IReadOnlyList<string>? ids = await f.Devices.ToolbarContext.RunTaskAsync("oadm.restart", CancellationToken.None);

        Assert.Equal(["task-1"], ids);
        await f.Api.Received(1).RunTaskAsync("oadm.restart", Arg.Is<IReadOnlyCollection<string>>(x => x.Single() == "2"), Arg.Is<string?>(p => p == null), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(f.TasksVm.IsExpanded);

        string? navigated = null;
        f.Devices.NavigateRequested += (_, key) => navigated = key;
        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.Settings);
        Assert.Equal("settings", navigated);

        f.Dialogs.ShowAddDevicesAsync(default!).ReturnsForAnyArgs(true);
        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.AddManually);
        await f.Dialogs.Received(1).ShowAddDevicesAsync(Arg.Is<Discovery.AddDevicesViewModel>(p => p.IsManualMode));
    }

    [Fact]
    public async Task Running_a_plugin_without_dialog_calls_server_with_selection()
    {
        using DevicesFixture f = CreateWithDevices();
        TaskPluginInfo restart = TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1", "2");
        await f.SetPluginsAsync(restart);
        f.Api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs((IReadOnlyList<string>)["task-1", "task-2"]);
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
        const string id = "6f1b2c3d-0000-4000-8000-000000000001";
        f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = TestSupport.Device(id, "B8A44F000001", "10.0.0.60", "AXIS M1135") });
        f.Select(id);
        f.Dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(false);

        await f.Devices.RemoveCommand.ExecuteAsync(null);
        await f.Api.DidNotReceiveWithAnyArgs().RemoveDevicesAsync(default!, default);

        f.Dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        await f.Devices.RemoveCommand.ExecuteAsync(null);
        await f.Api.Received(1).RemoveDevicesAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Single() == id), Arg.Any<CancellationToken>());
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
