using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Plugins;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Tests;

/// <summary>Production hardening 4: broken plugin parts and unhandled errors never take the client down.</summary>
public sealed class CrashHandlingTests
{
    [Fact]
    public async Task A_throwing_plugin_dialog_shows_the_error_and_runs_nothing()
    {
        var dialog = new FakeDialog("oadm.password", null);
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog("oadm.password").Returns(dialog);
        using var f = new DevicesFixture(registry: registry);
        f.SeedDevices(TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"));
        f.Dialogs.ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>()).ThrowsAsync(new InvalidOperationException("boom"));

        IReadOnlyList<string>? ids = await f.Runner.RunAsync(TestSupport.Plugin("oadm.password", "Change password", false, true, "1"), [f.Store.Find("1")!], CancellationToken.None);

        Assert.Null(ids);
        await f.Dialogs.Received(1).ShowMessageAsync("Change password", "The Change password dialog failed: boom");
        await f.Api.DidNotReceiveWithAnyArgs().RunTaskAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task A_throwing_plugin_page_shows_the_error_in_the_page_and_a_message()
    {
        ICorePluginPage page = Substitute.For<ICorePluginPage>();
        page.PluginId.Returns("oadm.broken");
        page.CreateView(Arg.Any<ICorePluginClientContext>()).Throws(new InvalidOperationException("no view"));
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.FindPage("oadm.broken").Returns(page);
        using var f = new DevicesFixture(registry: registry);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = Shell(f, connection);

        vm.SyncCorePluginPages([new CorePluginInfo { Id = "oadm.broken", DisplayName = "Broken" }, new CorePluginInfo { Id = "oadm.ntp", DisplayName = "NTP server" }]);

        var broken = Assert.IsType<CorePluginPageViewModel>(vm.NavItems.Single(n => n.Key == "plugin:oadm.broken").Page);
        Assert.False(broken.HasView);
        Assert.Equal("The Broken page failed: no view", broken.NoViewText);
        Assert.Contains(vm.NavItems, n => n.Key == "plugin:oadm.ntp"); // the other pages still load
        await f.Dialogs.Received(1).ShowMessageAsync("Broken", "The Broken page failed: no view");
    }

    [Fact]
    public async Task A_throwing_toolbar_plugin_is_left_out_with_a_message()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(async () =>
        {
            IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
            registry.ToolbarPlugins.Returns([new ThrowingToolbarPlugin()]);
            using var f = new DevicesFixture(registry: registry);
            var panel = new StackPanel();
            var window = new Window { Width = 1200, Height = 200, Content = panel };
            window.Show();

            f.Devices.Toolbar.AttachTo(panel, f.Devices.ToolbarContext);
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(panel.Children, c => c.Name == "x.broken");
            Assert.Contains(panel.Children, c => c.Name == "oadm.toolbar.scan");
            await f.Dialogs.Received(1).ShowMessageAsync("Toolbar", "The toolbar entry x.broken failed: no control");
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Ui_errors_are_logged_with_the_log_path_shown_and_survived()
    {
        var logger = new ListLogger();
        var shown = new List<(string Title, string Message)>();
        var handling = new CrashHandling(logger, "/data/logs/client-20261008.log", (t, m) =>
        {
            shown.Add((t, m));
            return Task.CompletedTask;
        });

        Assert.True(handling.OnUiException(new InvalidOperationException("binding broke")));
        await Task.Yield();

        var (title, message) = Assert.Single(shown);
        Assert.Equal("Something went wrong", title);
        Assert.Contains("binding broke", message, StringComparison.Ordinal);
        Assert.Contains("/data/logs/client-20261008.log", message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("/data/logs/client-20261008.log", StringComparison.Ordinal));
    }

    [Fact]
    public void Fatal_errors_end_the_app_after_logging_and_background_errors_are_logged()
    {
        var logger = new ListLogger();
        var handling = new CrashHandling(logger, "client.log");

        Assert.False(handling.OnUiException(new InvalidProgramException("corrupt")));
        handling.OnUnobserved(new AggregateException(new IOException("lost")));
        handling.OnUnhandled(new InvalidOperationException("thread"), terminating: true);

        Assert.Equal([LogLevel.Critical, LogLevel.Error, LogLevel.Critical], logger.Entries.Select(e => e.Level).ToArray());
        Assert.All(logger.Entries, e => Assert.Contains("client.log", e.Text, StringComparison.Ordinal));
        Assert.EndsWith(".log", CrashHandling.TodaysLogFile("logs"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_replaced_banner_shows_until_dismissed_on_this_client()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel vm = Shell(f, connection);
        var notice = new KeyReplacedNotice { Id = "k1", Message = "The server's key was replaced: 3 devices need their credentials again." };

        Assert.False(vm.HasKeyNotice);
        vm.ApplyKeyNotice(notice);
        Assert.True(vm.HasKeyNotice);
        Assert.Equal(notice.Message, vm.KeyNoticeText);

        vm.DismissKeyNoticeCommand.Execute(null);
        Assert.False(vm.HasKeyNotice);
        Assert.Contains("k1", f.Settings.Current.DismissedKeyNotices);

        MainWindowViewModel again = Shell(f, connection);
        again.ApplyKeyNotice(notice);
        Assert.False(again.HasKeyNotice);
        again.ApplyKeyNotice(new KeyReplacedNotice { Id = "k2", Message = "again" });
        Assert.True(again.HasKeyNotice);
    }

    private static MainWindowViewModel Shell(DevicesFixture f, ServerConnection connection) => new(
        connection,
        f.Devices,
        new LogsViewModel(new LogStore(f.Ui)),
        new SettingsViewModel(f.Api, connection, f.Clipboard, NullLogger<SettingsViewModel>.Instance),
        f.Catalog,
        f.Registry,
        f.Api,
        f.Settings,
        NullLogger<MainWindowViewModel>.Instance);

    private sealed class ThrowingToolbarPlugin : IToolbarPlugin
    {
        public string Id => "x.broken";

        public int Order => 0;

        public ToolbarGroup Group => ToolbarGroup.Plugins;

        public Control CreateControl(IToolbarContext ctx) => throw new InvalidOperationException("no control");
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
