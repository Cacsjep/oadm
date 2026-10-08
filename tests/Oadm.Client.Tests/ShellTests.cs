using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class ShellNavigationTests
{
    private static MainWindowViewModel CreateShell(DevicesFixture f, ServerConnection connection) => new(
        connection,
        f.Devices,
        new LogsViewModel(new LogStore(f.Ui)),
        new SettingsViewModel(f.Api, connection, f.Clipboard, NullLogger<SettingsViewModel>.Instance),
        f.Catalog,
        f.Registry,
        f.Api,
        f.Settings,
        NullLogger<MainWindowViewModel>.Instance);

    [Fact]
    public void Rail_has_devices_on_top_and_logs_settings_pinned_at_the_bottom()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = CreateShell(f, connection);

        NavItemViewModel devices = Assert.Single(vm.NavItems);
        Assert.Equal("Devices", devices.Title);
        Assert.Equal(["logs", "settings"], vm.BottomNavItems.Select(n => n.Key).ToArray());
        Assert.DoesNotContain(vm.NavItems.Concat(vm.BottomNavItems), n => n.Key is "tasks" or "about");
        Assert.Same(f.Devices, vm.CurrentPage);
        Assert.True(devices.IsSelected);

        vm.NavigateCommand.Execute(vm.BottomNavItems[0]);
        Assert.IsType<LogsViewModel>(vm.CurrentPage);
        Assert.False(devices.IsSelected);
    }

    [Fact]
    public async Task Settings_page_loads_and_saves_use_host_name()
    {
        var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        var settings = new SettingsViewModel(f.Api, connection, f.Clipboard, NullLogger<SettingsViewModel>.Instance);

        await settings.LoadAsync();
        Assert.False(settings.UseHostName);

        settings.UseHostName = true;
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True((await f.Api.GetSettingsAsync(CancellationToken.None)).UseHostName);
        settings.UseHostName = false;
        await settings.LoadAsync();
        Assert.True(settings.UseHostName);
    }

    [Fact]
    public void Core_plugin_pages_go_below_devices()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = CreateShell(f, connection);

        vm.SyncCorePluginPages([new CorePluginInfo { Id = "oadm.ntp", DisplayName = "NTP server" }]);

        Assert.Equal(["devices", "plugin:oadm.ntp"], vm.NavItems.Select(n => n.Key).ToArray());
        Assert.True(vm.NavItems[1].HasSeparatorBefore);
        Assert.Equal(["logs", "settings"], vm.BottomNavItems.Select(n => n.Key).ToArray());
    }

    [Fact]
    public void Rail_is_expanded_by_default_and_the_toggle_is_persisted()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = CreateShell(f, connection);

        Assert.True(vm.IsNavExpanded);
        Assert.Equal("Collapse", vm.NavToggleText);

        vm.ToggleNavCommand.Execute(null);

        Assert.False(vm.IsNavExpanded);
        Assert.Equal("Expand", vm.NavToggleText);
        Assert.False(f.Settings.Current.NavRailExpanded);
        Assert.False(CreateShell(f, connection).IsNavExpanded);
    }
}

public sealed class LogsViewModelTests
{
    [Fact]
    public void Level_filter_and_search_narrow_the_live_list()
    {
        var store = new LogStore(new ImmediateUiDispatcher());
        store.Add(new LogEntry(DateTime.Now, "Information", "Connected to server"));
        store.Add(new LogEntry(DateTime.Now, "Warning", "Could not list core plugins"));
        var vm = new LogsViewModel(store);
        store.Add(new LogEntry(DateTime.Now, "Error", "Restart failed on 10.0.0.30"));

        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal("Restart failed on 10.0.0.30", vm.Entries[0].Message);

        vm.SelectedLevel = LogsViewModel.Levels.Single(l => l.Title == "Warnings and errors");
        Assert.Equal(["Error", "Warning"], vm.Entries.Select(e => e.Level).ToArray());
        Assert.Equal("2 of 3 entries", vm.StatusLine);

        vm.SearchText = "core";
        Assert.Equal("Could not list core plugins", Assert.Single(vm.Entries).Message);

        store.Add(new LogEntry(DateTime.Now, "Information", "core plugin page opened"));
        Assert.Single(vm.Entries); // below the level filter

        vm.ClearSearchCommand.Execute(null);
        vm.SelectedLevel = LogsViewModel.Levels[0];
        Assert.Equal(4, vm.Entries.Count);
    }
}
