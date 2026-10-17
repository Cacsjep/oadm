using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using HostPermissionKeys = Oadm.Sdk.Plugins.HostPermissionKeys;

namespace Oadm.Client.Tests;

/// <summary>The Logs page: Server first (with its permission), Client and Audit; 200 entries a page, newest first.</summary>
public sealed class LogsPageTests
{
    private static (LogsViewModel Logs, LogStore Store, UserSession Session) Create(FakeOadmApi api, bool serverLog)
    {
        var session = new UserSession();
        session.SignIn(serverLog
            ? new UserInfo { UserName = "admin", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true }
            : new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, Permissions = { "devices.refresh" } }, "fake");
        var store = new LogStore(new ImmediateUiDispatcher());
        return (new LogsViewModel(store, new AuditLogViewModel(api), session, new ServerLogViewModel(api)), store, session);
    }

    [Fact]
    public async Task The_server_log_comes_first_and_pages_200_entries()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var (logs, _, _) = Create(api, serverLog: true);
        Assert.True(logs.CanSeeServer && logs.CanSeeAudit && logs.ShowSwitch);
        Assert.True(logs.IsServerTab);

        await logs.OnShownAsync();
        var server = logs.Server!;
        Assert.Equal(200, server.Entries.Count);
        Assert.Equal("Entries 1 to 200", server.PageText);
        Assert.True(server.Entries[0].Time > server.Entries[199].Time); // newest first
        Assert.False(server.NewerCommand.CanExecute(null));
        Assert.True(server.OlderCommand.CanExecute(null));

        await server.OlderCommand.ExecuteAsync(null);
        await server.OlderCommand.ExecuteAsync(null);
        await server.OlderCommand.ExecuteAsync(null);
        Assert.Equal((3, 50, false), (server.Page, server.Entries.Count, server.HasMore)); // 650 entries
        Assert.Equal("Entries 601 to 650", server.PageText);
        await server.NewestCommand.ExecuteAsync(null);
        Assert.Equal(0, server.Page);

        // Problems of event rule actions are in the server log with their reason.
        server.SelectedLevel = LogsViewModel.Levels.Single(l => l.Title == "Warnings and errors");
        await server.LoadAsync(0);
        Assert.All(server.Entries, e => Assert.True(e.IsWarning || e.IsError));
        Assert.Contains(server.Entries, e => e.SourceText == "ActionRunner" && e.HasException);
        server.SearchText = "cannot be reached";
        await server.LoadAsync(0);
        Assert.All(server.Entries, e => Assert.Contains("cannot be reached", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_permission_only_the_client_log_shows()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var (logs, store, session) = Create(api, serverLog: false);
        Assert.False(logs.CanSeeServer || logs.CanSeeAudit || logs.ShowSwitch);
        Assert.True(logs.IsClientTab);
        await logs.OnShownAsync();
        Assert.Empty(logs.Server!.Entries);

        // The client log pages too: 450 entries are three pages, the newest on the first.
        for (var i = 0; i < 450; i++)
        {
            store.Add(new LogEntry(DateTime.Now.AddSeconds(i), "Information", "Entry " + i));
        }

        Assert.Equal(200, logs.Entries.Count);
        Assert.Equal("Entry 449", logs.Entries[0].Message);
        Assert.Equal("Entries 1 to 200 of 450", logs.PageText);
        logs.OlderCommand.Execute(null);
        logs.OlderCommand.Execute(null);
        Assert.Equal(("Entry 49", 50), (logs.Entries[0].Message, logs.Entries.Count));
        Assert.False(logs.OlderCommand.CanExecute(null));
        store.Add(new LogEntry(DateTime.Now, "Warning", "Newest"));
        Assert.Equal("Entry 49", logs.Entries[0].Message); // an older page stays where it is
        logs.NewestCommand.Execute(null);
        Assert.Equal("Newest", logs.Entries[0].Message);

        // The role gets "See the server log": the Server tab appears and comes first.
        session.SignIn(new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, Permissions = { HostPermissionKeys.LogsServer } }, "fake");
        Assert.True(logs.CanSeeServer);
        Assert.True(logs.IsServerTab);
    }

    [Fact]
    public async Task Logs_page_renders_the_server_log()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var (logs, store, _) = Create(api, serverLog: true);
            store.Add(new LogEntry(DateTime.Now, "Information", "Connected to localhost:5080"));
            await logs.OnShownAsync();
            var window = new Window { Width = 1400, Height = 820, Content = new Border { Padding = new Avalonia.Thickness(20), Child = new LogsView { DataContext = logs } } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Entries 1 to 200", texts);
            Assert.Contains("ActionRunner", texts);
            Save(window, outDir, "client-logs-server.png");

            logs.ShowClientLogCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Save(window, outDir, "client-logs-client.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
