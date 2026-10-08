using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class ShellNavigationTests
{
    private static MainWindowViewModel CreateShell(DevicesFixture f, ServerConnection connection, UserSession? session = null) =>
        f.CreateShell(connection, session);

    private static UserSession SignedIn(UserRole role)
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = role == UserRole.Admin ? "anna" : "otto", Role = role }, "https://localhost:5080");
        return session;
    }

    [Fact]
    public void Rail_has_devices_on_top_and_the_operator_pages_pinned_at_the_bottom()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = CreateShell(f, connection, SignedIn(UserRole.Operator));

        NavItemViewModel devices = Assert.Single(vm.NavItems);
        Assert.Equal("Devices", devices.Title);
        Assert.Equal(["logs", "settings", "about"], vm.BottomNavItems.Select(n => n.Key).ToArray());
        Assert.DoesNotContain(vm.NavItems.Concat(vm.BottomNavItems), n => n.Key is "tasks");
        Assert.Same(f.Devices, vm.CurrentPage);
        Assert.True(devices.IsSelected);

        vm.NavigateCommand.Execute(vm.BottomNavItems[0]);
        Assert.IsType<LogsViewModel>(vm.CurrentPage);
        Assert.False(devices.IsSelected);
    }

    [Fact]
    public void Administrators_see_users_and_credentials_above_logs()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = CreateShell(f, connection, SignedIn(UserRole.Admin));

        Assert.Equal(["users", "credentials", "logs", "settings", "about"], vm.BottomNavItems.Select(n => n.Key).ToArray());
        Assert.Equal(["Users", "Credentials", "Logs", "Settings", "About"], vm.BottomNavItems.Select(n => n.Title).ToArray());
        Assert.Equal(["users", "key", "logs", "settings", "info"], vm.BottomNavItems.Select(n => n.IconKey).ToArray());
        Assert.IsType<UsersViewModel>(vm.BottomNavItems[0].Page);
        Assert.IsType<CredentialsViewModel>(vm.BottomNavItems[1].Page);
        Assert.IsType<SettingsViewModel>(vm.BottomNavItems[3].Page);
        Assert.IsType<AboutViewModel>(vm.BottomNavItems[4].Page);
    }

    [Fact]
    public async Task Admin_entries_follow_the_session_role_live()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        UserSession session = SignedIn(UserRole.Admin);
        MainWindowViewModel vm = CreateShell(f, connection, session);
        vm.NavigateCommand.Execute(vm.BottomNavItems.Single(n => n.Key == "credentials"));
        Assert.IsType<CredentialsViewModel>(vm.CurrentPage);

        // Another user logs in as operator: the admin pages disappear and the open one falls back to Devices.
        session.SignIn(new UserInfo { UserName = "otto", Role = UserRole.Operator }, "https://localhost:5080");
        Assert.Equal(["logs", "settings", "about"], vm.BottomNavItems.Select(n => n.Key).ToArray());
        Assert.Same(f.Devices, vm.CurrentPage);

        // An operator cannot open them by key either.
        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.Users);
        Assert.Same(f.Devices, vm.CurrentPage);

        // Back to an administrator: the entries come back, the current page stays.
        vm.NavigateCommand.Execute(vm.BottomNavItems.Single(n => n.Key == "about"));
        session.SignIn(new UserInfo { UserName = "anna", Role = UserRole.Admin }, "https://localhost:5080");
        Assert.Equal(["users", "credentials", "logs", "settings", "about"], vm.BottomNavItems.Select(n => n.Key).ToArray());
        Assert.IsType<AboutViewModel>(vm.CurrentPage);
        Assert.True(vm.BottomNavItems[4].IsSelected);
    }

    [Theory]
    [InlineData("users", typeof(UsersViewModel))]
    [InlineData("credentials", typeof(CredentialsViewModel))]
    [InlineData("logs", typeof(LogsViewModel))]
    [InlineData("settings", typeof(SettingsViewModel))]
    [InlineData("about", typeof(AboutViewModel))]
    public async Task Host_pages_open_by_key(string key, Type page)
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = CreateShell(f, connection, SignedIn(UserRole.Admin));

        await f.Devices.ToolbarContext.OpenAsync(key);

        Assert.IsType(page, vm.CurrentPage);
        Assert.True(vm.CurrentItem!.IsSelected);
    }

    [Fact]
    public async Task Settings_page_loads_and_saves_use_host_name()
    {
        var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        var settings = new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance);

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

        // A core plugin without a page (toolbar only, e.g. System report) gets no rail entry.
        vm.SyncCorePluginPages([new CorePluginInfo { Id = "oadm.ntp", DisplayName = "NTP server" }, new CorePluginInfo { Id = "oadm.system-report", DisplayName = "System report", NoPage = true }]);

        Assert.Equal(["devices", "plugin:oadm.ntp"], vm.NavItems.Select(n => n.Key).ToArray());
        Assert.True(vm.NavItems[1].HasSeparatorBefore);
        Assert.Equal(["logs", "settings", "about"], vm.BottomNavItems.Select(n => n.Key).ToArray());
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
