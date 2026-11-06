using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// Log out and log in again (another user, another server): the main window of the new login opens whatever page was
/// open, the plugin pages are new ones, and nothing of the last session is left in the lists.
/// </summary>
public sealed class LogoutLoginTests
{
    /// <summary>A plugin page whose view model remembers the context it was created with.</summary>
    private sealed class RecordingPage(string id) : ICorePluginPage
    {
        public List<PageModel> Models { get; } = [];

        public string PluginId => id;

        public string Title => id;

        public Control CreateView(ICorePluginClientContext ctx)
        {
            var model = new PageModel(ctx);
            Models.Add(model);
            return new TextBlock { Text = id, DataContext = model };
        }
    }

    private sealed class PageModel(ICorePluginClientContext ctx) : IDisposable
    {
        public ICorePluginClientContext Context { get; } = ctx;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static IClientPluginRegistry Registry(Dictionary<string, RecordingPage> pages)
    {
        var registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
        registry.ToolbarPlugins.Returns([]);
        registry.FindPage(Arg.Any<string>()).Returns(call =>
        {
            var id = call.Arg<string>();
            if (!pages.TryGetValue(id, out RecordingPage? page))
            {
                pages[id] = page = new RecordingPage(id);
            }

            return page;
        });
        return registry;
    }

    [Fact]
    public async Task Logging_in_again_after_a_logout_from_a_plugin_page_opens_the_main_window_with_new_pages()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            // The shell like the client composes it, against the fake server: its login needs no network.
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed with the fixture
            var pages = new Dictionary<string, RecordingPage>(StringComparer.Ordinal);
            var session = new UserSession();
            var ui = new AvaloniaUiDispatcher();
            using var f = new DevicesFixture(api, Registry(pages), session, catalogUi: ui);
            using var connection = new ServerConnection(api, f.Store, f.Tasks, ui, NullLogger<ServerConnection>.Instance, session);
            MainWindowViewModel main = f.CreateShell(connection, session, ui);
            var options = new AppOptions { UseFake = true };
            f.Settings.Current.AcceptedTerms = TermsViewModel.CurrentVersion;
            var logins = new List<LoginViewModel>();
            var shell = new AppShell(api, session, f.Settings, options, () =>
            {
                var login = new LoginViewModel(api, new NoServerTrust(), f.Settings, session, f.Dialogs, options, NullLogger<LoginViewModel>.Instance) { ConnectFeedback = TimeSpan.Zero };
                logins.Add(login);
                return login;
            }, main, NullLogger<AppShell>.Instance);
            var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            shell.Start(lifetime); // fake mode: the splash, then the main window as "admin"
            await TestSupport.WaitUntilAsync(() => lifetime.MainWindow is MainWindow { IsVisible: true } && main.NavItems.Any(n => n.Page is CorePluginPageViewModel)
                && main.Devices.FilteredDevices.Count > 0);
            var firstWindow = (MainWindow)lifetime.MainWindow!;
            int devices = main.Devices.FilteredDevices.Count;
            DevicesView firstDevicesView = firstWindow.GetVisualDescendants().OfType<DevicesView>().Single();
            Assert.Same(main.Devices, firstDevicesView.DataContext);

            // A plugin page is open when the user logs out.
            NavItemViewModel item = main.NavItems.First(n => n.Page is CorePluginPageViewModel);
            string key = item.Key;
            var firstPage = (CorePluginPageViewModel)item.Page;
            var firstView = (Control)firstPage.View!;
            var firstModel = (PageModel)firstView.DataContext!;
            main.NavigateTo(key);
            await TestSupport.WaitUntilAsync(() => ReferenceEquals(TopLevel.GetTopLevel(firstView), firstWindow));

            session.RequestLogout(null);
            await TestSupport.WaitUntilAsync(() => lifetime.MainWindow is LoginWindow { IsVisible: true } && !firstWindow.IsVisible);

            // Logged out: the Devices page, no plugin page, and nothing of the session left in the lists.
            Assert.Equal("devices", main.CurrentItem?.Key);
            Assert.DoesNotContain(main.NavItems, n => n.Page is CorePluginPageViewModel);
            Assert.True(firstPage.IsDisposed);
            Assert.True(firstModel.Disposed);
            Assert.Null(TopLevel.GetTopLevel(firstView)); // no longer in the closed window
            Assert.Null(firstWindow.DataContext);
            Assert.Null(firstDevicesView.DataContext); // the closed window's device grid no longer listens to the view model
            Assert.Empty(f.Store.Devices);
            Assert.Empty(f.Tasks.Tasks);
            Assert.Empty(main.Devices.FilteredDevices);
            Assert.True(main.Devices.IsLoading);

            // Log in again.
            var loginWindow = (LoginWindow)lifetime.MainWindow!;
            var login = (LoginViewModel)loginWindow.DataContext!;
            await TestSupport.WaitUntilAsync(() => !login.IsBusy);
            login.UserName = FakeOadmApi.FakeUserName;
            login.Password = "any";
            await login.SubmitCommand.ExecuteAsync(null);
            await TestSupport.WaitUntilAsync(() => lifetime.MainWindow is MainWindow { IsVisible: true } && !loginWindow.IsVisible);

            var secondWindow = (MainWindow)lifetime.MainWindow!;
            Assert.NotSame(firstWindow, secondWindow);
            await TestSupport.WaitUntilAsync(() => main.NavItems.Any(n => n.Key == key) && main.Devices.FilteredDevices.Count == devices);
            Assert.False(main.Devices.IsLoading);

            // The page of the new login is a new one, with its own context.
            var secondPage = (CorePluginPageViewModel)main.NavItems.Single(n => n.Key == key).Page;
            var secondModel = (PageModel)((Control)secondPage.View!).DataContext!;
            Assert.NotSame(firstPage, secondPage);
            Assert.NotSame(firstModel, secondModel);
            Assert.NotSame(firstModel.Context, secondModel.Context);
            Assert.False(secondModel.Disposed);
            main.NavigateTo(key);
            await TestSupport.WaitUntilAsync(() => ReferenceEquals(TopLevel.GetTopLevel((Control)secondPage.View!), secondWindow));

            main.Stop();
            secondWindow.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void Logging_out_goes_to_the_devices_page_and_disposes_every_plugin_page_and_tab()
    {
        var pages = new Dictionary<string, RecordingPage>(StringComparer.Ordinal);
        using var f = new DevicesFixture(registry: Registry(pages));
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel main = f.CreateShell(connection);
        CorePluginInfo[] listed =
        [
            new() { Id = "test.dhcp", DisplayName = "DHCP server", Group = "Utilities" },
            new() { Id = "test.ntp", DisplayName = "NTP server", Group = "Utilities" },
        ];
        main.SyncCorePluginPages(listed);
        main.NavigateTo("plugin:test.ntp");
        var closed = new List<string?>();
        main.ClosePluginWindowsRequested += (_, id) => closed.Add(id);

        main.Stop();

        Assert.Equal("devices", main.CurrentItem?.Key);
        Assert.Same(main.Devices, main.CurrentPage);
        Assert.DoesNotContain(main.NavItems, n => n.Page is CorePluginPageViewModel);
        Assert.All(pages.Values.SelectMany(p => p.Models), m => Assert.True(m.Disposed));
        Assert.Contains(null, closed); // every plugin window

        // The next login: the same plugins bring new pages.
        main.SyncCorePluginPages(listed);
        Assert.Equal(2, pages["test.dhcp"].Models.Count);
        Assert.Equal(2, pages["test.ntp"].Models.Count);
        Assert.False(pages["test.ntp"].Models[1].Disposed);
        Assert.NotSame(pages["test.ntp"].Models[0].Context, pages["test.ntp"].Models[1].Context);
        main.Stop();
    }

    [Fact]
    public async Task A_plugin_list_read_before_the_logout_brings_no_pages_after_it()
    {
        var pages = new Dictionary<string, RecordingPage>(StringComparer.Ordinal);
        await using var ui = new UiThread(); // the connect and the logout on one thread, like in the client
        using var f = new DevicesFixture(registry: Registry(pages), catalogUi: ui);
        var answer = new TaskCompletionSource<IReadOnlyList<CorePluginInfo>>();
        var asked = false;
        f.Api.ListCorePluginsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            asked = true;
            return answer.Task;
        });
        f.Api.WatchDevicesAsync(Arg.Any<CancellationToken>()).Returns(call => SnapshotThenWait([], null, call.Arg<CancellationToken>()));
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, ui, NullLogger<ServerConnection>.Instance);

        await ui.RunAsync(async () =>
        {
            MainWindowViewModel main = f.CreateShell(connection);
            main.Start(); // the connect reads the plugin list
            await TestSupport.WaitUntilAsync(() => asked);
            main.Stop(); // logged out while the server still answers
            answer.SetResult([new CorePluginInfo { Id = "test.monitor", DisplayName = "Monitor", Group = "Monitoring" }]);
            await Task.Yield(); // the old connect went on with the answer: here, or in a job queued before this one

            Assert.DoesNotContain(main.NavItems, n => n.Page is CorePluginPageViewModel);
            Assert.Empty(pages);
        });
    }

    [Fact]
    public async Task Rows_a_stopped_stream_still_had_on_their_way_do_not_come_back_after_the_logout()
    {
        using var f = new DevicesFixture();
        var ui = new QueuedUiDispatcher();
        f.Api.ServerAddress.Returns("https://a:5080");
        var handedOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Api.WatchDevicesAsync(Arg.Any<CancellationToken>()).Returns(call => SnapshotThenWait(
            [TestSupport.Device("1", "ACCC8E000001", "10.0.0.1", "AXIS P3265-V")], handedOver, call.Arg<CancellationToken>()));
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, ui, NullLogger<ServerConnection>.Instance);
        f.Store.Reset([TestSupport.Device("0", "ACCC8E000000", "10.0.0.9", "AXIS Q1656")]);
        f.Tasks.Reset([new TaskInfo { Id = "t", DeviceId = "0", State = TaskState.Done }]);
        Assert.True(f.Store.IsLoaded);

        connection.Start();
        await handedOver.Task.WaitAsync(Eventually.DefaultTimeout); // the snapshot waits for the UI thread
        connection.SignOut(); // the UI thread logs out before it gets to the snapshot
        Assert.Empty(f.Store.Devices);
        Assert.Empty(f.Tasks.Tasks);
        Assert.False(f.Store.IsLoaded);

        ui.RunAll();

        Assert.Empty(f.Store.Devices);
        Assert.False(f.Store.IsLoaded);
        Assert.Equal(ConnectionState.Connecting, connection.State);

        // The next login, on another server: its address shows while it connects, then its devices.
        f.Api.ServerAddress.Returns("https://b:5080");
        connection.Start();
        Assert.Equal("Connecting to https://b:5080", connection.StatusText);
        await Eventually.TrueAsync(() =>
        {
            ui.RunAll();
            return f.Store.Devices.Count == 1;
        }, "the devices of the new login");
        Assert.True(f.Store.IsLoaded);
        connection.Stop();
    }

    /// <summary>A device stream: the snapshot, then nothing until it is stopped. <paramref name="handedOver"/> ends once the reader took the whole snapshot.</summary>
    private static async IAsyncEnumerable<DeviceChanged> SnapshotThenWait(Device[] devices, TaskCompletionSource? handedOver,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (Device device in devices)
        {
            yield return new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = device };
        }

        yield return new DeviceChanged { Kind = DeviceChanged.Types.Kind.SnapshotEnd };
        handedOver?.TrySetResult(); // the reader asked for the next change: the snapshot is on its way to the UI thread
        await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>Keeps posts until the test runs them, like a UI thread that is busy.</summary>
    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

        public int Count => _queue.Count;

        public void Post(Action action) => _queue.Enqueue(action);

        public void RunAll()
        {
            while (_queue.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }
}
