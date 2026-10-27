using System.Net;
using System.Net.Sockets;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Dialogs;
using Oadm.Client.Help;
using Oadm.Client.Shell;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>
/// The Help window: one at a time, closes with the main window and on logout, the texts without docs or without an
/// embedded browser, links that leave the docs, and the Help button in the title bar. No real browser (headless).
/// OADM_SCREENSHOT_DIR writes client-help-titlebar-dark.png / -light.png and client-help-no-docs-*.png.
/// </summary>
public sealed class HelpWindowTests : IDisposable
{
    private readonly string _docs = Path.Combine(Path.GetTempPath(), "oadm-help-window-" + Guid.NewGuid().ToString("N"));

    public HelpWindowTests()
    {
        Directory.CreateDirectory(_docs);
        File.WriteAllText(Path.Combine(_docs, "index.html"), "<html>start page</html>");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_docs, recursive: true);
        }
        catch (IOException)
        {
            // still in use by a closing server; the temp folder is cleaned by the system
        }
    }

    private static string Appearance =>
        string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

    private static HelpWindowViewModel Help(string? docs, EmbeddedBrowserState? browser = null, IUrlLauncher? launcher = null) =>
        new(docs, () => browser ?? EmbeddedBrowserState.Available, launcher, NullLogger.Instance);

    [Fact]
    public void Without_docs_the_window_says_so_and_starts_no_server()
    {
        var checks = 0;
        using var help = new HelpWindowViewModel(null, () => { checks++; return EmbeddedBrowserState.Available; }, null, NullLogger.Instance);

        help.Start();

        Assert.Equal("Help is not part of this build.", help.MissingDocsText);
        Assert.True(help.HasMissingDocs);
        Assert.False(help.HasMissingBrowser);
        Assert.Null(help.StartAddress);
        Assert.Null(help.Server);
        Assert.Equal(0, checks); // no browser needed without docs
        Assert.Equal("Help · OADM", HelpWindowViewModel.WindowTitle);
    }

    [Fact]
    public void Without_an_embedded_browser_the_window_says_what_to_install()
    {
        using var help = Help(_docs, new EmbeddedBrowserState(false, EmbeddedBrowserTexts.WindowsMissing));

        help.Start();

        Assert.Equal(EmbeddedBrowserTexts.WindowsMissing, help.MissingBrowserText);
        Assert.True(help.HasMissingBrowser);
        Assert.False(help.HasMissingDocs);
        Assert.Null(help.Server);
    }

    [Fact]
    public async Task With_docs_and_browser_the_docs_server_runs_until_the_window_closes()
    {
        var help = Help(_docs);
        help.Start();
        Assert.NotNull(help.Server);
        Assert.Equal(help.Server.Origin, help.StartAddress);
        var port = help.StartAddress!.Port;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
        }

        help.Dispose();

        Assert.True(help.IsDisposed);
        Assert.Null(help.StartAddress);
        var again = new TcpListener(IPAddress.Loopback, port); // the port is free again
        again.Start();
        again.Stop();
    }

    [Fact]
    public async Task Links_that_leave_the_docs_open_in_the_system_browser()
    {
        var launcher = Substitute.For<IUrlLauncher>();
        using var help = Help(_docs, launcher: launcher);
        help.Start();
        var docs = help.StartAddress!;

        Assert.False(help.LeavesDocs(new Uri(docs, "/getting-started/")));
        Assert.True(help.LeavesDocs(new Uri("https://oadm.app/dev/plugin-sdk/")));
        Assert.True(help.LeavesDocs(new Uri("http://127.0.0.1:" + (docs.Port == 1 ? 2 : docs.Port - 1) + "/")));
        Assert.True(help.LeavesDocs(new Uri("mailto:someone@example.com")));
        Assert.False(help.LeavesDocs(new Uri("about:blank")));
        Assert.False(help.LeavesDocs(null));

        await help.OpenOutsideAsync(new Uri("https://oadm.app/activate/"));
        await launcher.Received(1).OpenAsync(new Uri("https://oadm.app/activate/"));
    }

    [Fact]
    public void Help_again_shows_the_same_window_and_logout_closes_it()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        var created = new List<HelpWindowViewModel>();
        var shell = f.CreateShell(connection, help: () =>
        {
            var help = Help(null);
            created.Add(help);
            return help;
        });
        var shown = new List<HelpWindowViewModel>();
        var closed = new List<HelpWindowViewModel>();
        shell.HelpWindowRequested += (_, h) => shown.Add(h);
        shell.CloseHelpRequested += (_, h) => closed.Add(h);

        shell.OpenHelpCommand.Execute(null);
        shell.OpenHelpCommand.Execute(null);

        var first = Assert.Single(created);
        Assert.Equal([first, first], shown);
        Assert.Same(first, shell.Help);
        Assert.True(first.HasMissingDocs); // started when it opened

        shell.Stop(); // logout
        Assert.Equal([first], closed);
        Assert.True(first.IsDisposed);
        Assert.Null(shell.Help);

        shell.OpenHelpCommand.Execute(null);
        Assert.Equal(2, created.Count);
        Assert.NotSame(first, shell.Help);
    }

    [Fact]
    public async Task The_window_is_one_at_a_time_and_closes_with_the_main_window_and_on_logout()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var shell = f.CreateShell(connection, help: () => Help(null));
            var main = new MainWindow { DataContext = shell, Width = 1400, Height = 800 };
            main.Show();

            shell.OpenHelpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var window = main.CurrentHelpWindow;
            Assert.NotNull(window);
            var help = shell.Help!;
            shell.OpenHelpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(window, main.CurrentHelpWindow); // brought to the front, not a second one

            // The user closes it: the docs server stops, Help opens a new one.
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(main.CurrentHelpWindow);
            Assert.True(help.IsDisposed);
            Assert.Null(shell.Help);

            // Logout closes it.
            shell.OpenHelpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var second = main.CurrentHelpWindow!;
            var closedOnLogout = false;
            second.Closed += (_, _) => closedOnLogout = true;
            shell.Stop();
            Dispatcher.UIThread.RunJobs();
            Assert.True(closedOnLogout);
            Assert.Null(main.CurrentHelpWindow);

            // The main window closing closes it too.
            shell.OpenHelpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var third = main.CurrentHelpWindow!;
            var thirdHelp = shell.Help!;
            var closedWithMain = false;
            third.Closed += (_, _) => closedWithMain = true;
            main.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(closedWithMain);
            Assert.True(thirdHelp.IsDisposed);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_window_shows_the_docs_in_the_browser_and_drops_it_when_it_closes()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var factory = HelpWindow.BrowserFactory;
            try
            {
                Uri? opened = null;
                var browser = new DisposableText();
                HelpWindow.BrowserFactory = (_, start) =>
                {
                    opened = start;
                    return browser;
                };
                var help = Help(_docs);
                help.Start();
                var window = new HelpWindow { DataContext = help };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(help.StartAddress, opened);
                Assert.Same(browser, window.Browser);
                Assert.False(window.FindControl<TextBlock>("MissingDocs")!.IsVisible);
                Assert.False(window.FindControl<StatusChip>("MissingBrowser")!.IsVisible);

                window.Close();
                help.Dispose();
                Dispatcher.UIThread.RunJobs();
                Assert.True(browser.IsDisposed);
                Assert.Null(window.Browser);
            }
            finally
            {
                HelpWindow.BrowserFactory = factory;
            }

            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    private sealed class DisposableText : TextBlock, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task The_window_without_docs_or_browser_shows_the_text_and_no_browser()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var factory = HelpWindow.BrowserFactory;
            try
            {
                HelpWindow.BrowserFactory = (_, _) => throw new InvalidOperationException("no browser without docs");
                using (var help = Help(null))
                {
                    help.Start();
                    var window = new HelpWindow { DataContext = help, Width = 900, Height = 420 };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    var text = window.FindControl<TextBlock>("MissingDocs")!;
                    Assert.True(text.IsEffectivelyVisible);
                    Assert.Equal("Help is not part of this build.", text.Text);
                    Assert.Contains("secondary", text.Classes);
                    Assert.False(window.FindControl<StatusChip>("MissingBrowser")!.IsVisible);
                    Assert.Null(window.Browser);
                    Assert.Equal("Help · OADM", window.Title);
                    Save(window.CaptureRenderedFrame(), outDir, $"client-help-no-docs-{Appearance}.png");
                    window.Close();
                }

                using (var help = Help(_docs, new EmbeddedBrowserState(false, EmbeddedBrowserTexts.LinuxMissing)))
                {
                    help.Start();
                    var window = new HelpWindow { DataContext = help, Width = 900, Height = 420 };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var chip = window.FindControl<StatusChip>("MissingBrowser")!;
                    Assert.True(chip.IsEffectivelyVisible);
                    Assert.Equal(EmbeddedBrowserTexts.LinuxMissing, chip.Text);
                    Assert.False(window.FindControl<TextBlock>("MissingDocs")!.IsVisible);
                    Assert.Null(window.Browser);
                    window.Close();
                }
            }
            finally
            {
                HelpWindow.BrowserFactory = factory;
            }

            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_title_bar_has_the_Help_button_left_of_the_user()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var session = new UserSession();
            session.SignIn(new Contracts.V1.UserInfo { UserName = "anna", Role = Contracts.V1.UserRole.Admin, RoleName = "Administrator", AllPermissions = true }, "token");
            var shown = 0;
            var shell = f.CreateShell(connection, session, help: () => Help(null));
            shell.HelpWindowRequested += (_, _) => shown++;
            var main = new MainWindow { DataContext = shell, Width = 1400, Height = 600 };
            main.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var button = main.FindControl<ToolbarButton>("TitleHelp")!;
            Assert.Equal("help", button.IconKey);
            Assert.True(button.IsIconOnly);
            Assert.Contains("iconOnly", button.Classes);
            Assert.Equal("Help", ToolTip.GetTip(button));
            Assert.True(Application.Current!.TryGetResource("Icon.help", null, out var icon) && icon is Avalonia.Media.Geometry);
            // Directly left of the user and Log out, in the same row.
            var row = (StackPanel)button.GetVisualParent()!;
            var order = row.Children.ToList();
            Assert.Equal(order.IndexOf(main.FindControl<Button>("TitleUser")!) - 1, order.IndexOf(button));
            Assert.Contains(order, c => c is ToolbarButton { Text: "Log out" });

            button.Command!.Execute(null);
            Assert.Equal(1, shown);

            var frame = main.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                // The right end of the title bar: Help, the user, Log out.
                var width = 520;
                var height = 48;
                var source = new PixelRect(frame.PixelSize.Width - width, 0, width, height);
                using var crop = new RenderTargetBitmap(new PixelSize(width, height), frame.Dpi);
                using (var context = crop.CreateDrawingContext())
                {
                    context.DrawImage(frame, source.ToRect(1), new Rect(0, 0, width, height));
                }

                Save(crop, outDir, $"client-help-titlebar-{Appearance}.png");
            }

            shell.Stop();
            main.Close();
            Dispatcher.UIThread.RunJobs();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Save(Bitmap? frame, string? outDir, string name)
    {
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
