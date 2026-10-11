using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>"Open in new window" of plugin pages (not host pages). OADM_SCREENSHOT_DIR writes client-plugin-window.png.</summary>
public sealed class PluginWindowTests
{
    private sealed class CountingPage : ICorePluginPage
    {
        public int Views;

        public List<PageModel> Models { get; } = [];

        public string PluginId => "test.monitor";

        public string Title => "Monitor";

        public Control CreateView(ICorePluginClientContext ctx)
        {
            Views++;
            var model = new PageModel(ctx);
            Models.Add(model);
            return new TextBlock { Text = $"Copy {Views}", DataContext = model };
        }
    }

    /// <summary>A plugin view model that subscribes with a lambda and never unsubscribes (like most plugins).</summary>
    private sealed class PageModel : IDisposable
    {
        public PageModel(ICorePluginClientContext ctx) => ctx.DevicesChanged += (_, _) => Changes++;

        public int Changes { get; private set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static (MainWindowViewModel Shell, CountingPage Page, DevicesFixture Fixture, ServerConnection Connection) Create()
    {
        var page = new CountingPage();
        var registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
        registry.FindPage("test.monitor").Returns(page);
        registry.ToolbarPlugins.Returns([]);
        var f = new DevicesFixture(registry: registry);
        var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        var shell = f.CreateShell(connection);
        shell.SyncCorePluginPages([new CorePluginInfo { Id = "test.monitor", DisplayName = "Monitor", Group = "Monitoring" }]);
        return (shell, page, f, connection);
    }

    [Fact]
    public void Only_plugin_pages_open_in_a_window_and_each_window_gets_its_own_copy()
    {
        var (shell, page, f, connection) = Create();
        using var _ = f;
        using var __ = connection;
        var plugin = shell.NavItems.Single(n => n.Key == "plugin:test.monitor");

        Assert.True(plugin.CanOpenInWindow);
        Assert.False(shell.NavItems.Single(n => n.Key == "devices").CanOpenInWindow);
        Assert.All(shell.BottomNavItems, n => Assert.False(n.CanOpenInWindow));

        var opened = new List<CorePluginPageViewModel>();
        shell.PluginWindowRequested += (_, p) => opened.Add(p);
        plugin.OpenInNewWindowCommand.Execute(null);
        plugin.OpenInNewWindowCommand.Execute(null);

        Assert.Equal(2, opened.Count);
        Assert.Equal(3, page.Views); // the rail page plus one per window
        Assert.NotSame(((CorePluginPageViewModel)plugin.Page).View, opened[0].View);
        Assert.NotSame(opened[0].View, opened[1].View);
        Assert.Equal("Monitor · OADM", opened[0].WindowTitle);
    }

    [Fact]
    public void Turning_the_plugin_off_and_logging_out_close_the_windows()
    {
        var (shell, _, f, connection) = Create();
        using var _ = f;
        using var __ = connection;
        var closed = new List<string?>();
        shell.ClosePluginWindowsRequested += (_, id) => closed.Add(id);

        shell.SyncCorePluginPages([]); // plugin turned off
        shell.Stop(); // logout

        Assert.Equal(["test.monitor", null], closed);
    }

    [Fact]
    public void A_closed_window_page_drops_its_device_subscriptions_and_disposes_its_view_model()
    {
        var (shell, page, f, connection) = Create();
        using var _ = f;
        using var __ = connection;
        CorePluginPageViewModel? opened = null;
        shell.PluginWindowRequested += (_, p) => opened = p;
        shell.NavItems.Single(n => n.Key == "plugin:test.monitor").OpenInNewWindowCommand.Execute(null);
        Assert.NotNull(opened);
        PageModel rail = page.Models[0], window = page.Models[1];

        f.SeedDevices(TestSupport.Device("1", "ACCC8E000001", "10.0.0.1", "AXIS P3265-V"));
        Assert.Equal((1, 1), (rail.Changes, window.Changes));

        opened.Dispose(); // the plugin window closed
        Assert.True(window.Disposed);
        f.SeedDevices(TestSupport.Device("2", "ACCC8E000002", "10.0.0.2", "AXIS P3265-V"));
        Assert.Equal((2, 1), (rail.Changes, window.Changes)); // the closed copy no longer hears the host

        shell.SyncCorePluginPages([]); // plugin turned off: the rail page is released too
        Assert.True(rail.Disposed);
        f.SeedDevices(TestSupport.Device("3", "ACCC8E000003", "10.0.0.3", "AXIS P3265-V"));
        Assert.Equal(2, rail.Changes);
    }

    [Fact]
    public void The_context_forwards_device_changes_until_it_is_disposed()
    {
        var host = Substitute.For<IToolbarContext>();
        var context = new CorePluginClientContext(Substitute.For<Oadm.Client.Api.IOadmApi>(), "test.monitor", host);
        Assert.False(context.IsAttached); // nothing on the host until the plugin subscribes
        var changes = 0;
        context.DevicesChanged += (_, _) => changes++;
        context.DevicesChanged += (_, _) => changes++;
        Assert.True(context.IsAttached);
        host.Received(1).DevicesChanged += Arg.Any<EventHandler>(); // one handler on the host for any number of plugin handlers

        host.DevicesChanged += Raise.Event();
        Assert.Equal(2, changes);

        context.Dispose();
        Assert.False(context.IsAttached);
        host.Received(1).DevicesChanged -= Arg.Any<EventHandler>();
        context.DevicesChanged += (_, _) => changes++; // after Dispose: ignored
        Assert.False(context.IsAttached);
    }

    [Fact]
    public async Task After_logging_out_and_in_twice_open_in_new_window_opens_one_window()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var (shell, page, f, connection) = Create();
            using var _ = f;
            using var __ = connection;
            var windows = new List<MainWindow>();
            for (var login = 0; login < 3; login++)
            {
                // AppShell: a new main window per login, the view model lives on; logout stops it and closes the window.
                if (windows.Count > 0)
                {
                    shell.Stop();
                    windows[^1].Close();
                }

                var main = new MainWindow { DataContext = shell, Width = 1800, Height = 900 };
                main.Show();
                windows.Add(main);
            }

            shell.NavItems.Single(n => n.Key == "plugin:test.monitor").OpenInNewWindowCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([0, 0, 1], windows.Select(w => w.PluginWindows.Count));
            Assert.Equal(2, page.Views); // the rail page plus exactly one window

            var pluginWindow = windows[^1].PluginWindows[0];
            pluginWindow.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(windows[^1].PluginWindows);
            Assert.True(page.Models[1].Disposed);
            Assert.True(((CorePluginPageViewModel)pluginWindow.DataContext!).IsDisposed);
            windows[^1].Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_window_shows_the_page_with_its_header()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var (shell, _, f, connection) = Create();
            using var _ = f;
            using var __ = connection;
            CorePluginPageViewModel? page = null;
            shell.PluginWindowRequested += (_, p) => page = p;
            shell.NavItems.Single(n => n.Key == "plugin:test.monitor").OpenInNewWindowCommand.Execute(null);

            var window = new PluginWindow { DataContext = page, Width = 900, Height = 420 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.DoesNotContain("Monitor · OADM", texts); // the title bar is empty, the page header names the page
            Assert.Contains("Copy 2", texts);
            Assert.Equal("Monitor · OADM", window.Title);
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-plugin-window.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }
}
