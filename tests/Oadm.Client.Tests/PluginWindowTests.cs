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

/// <summary>"Open in new window" of plugin pages (user decision 2026-10-10: plugin pages only). OADM_SCREENSHOT_DIR writes client-plugin-window.png.</summary>
public sealed class PluginWindowTests
{
    private sealed class CountingPage : ICorePluginPage
    {
        public int Views;

        public string PluginId => "test.monitor";

        public string Title => "Monitor";

        public Control CreateView(ICorePluginClientContext ctx)
        {
            Views++;
            return new TextBlock { Text = $"Copy {Views}" };
        }
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
            Assert.Contains("Monitor · OADM", texts);
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
