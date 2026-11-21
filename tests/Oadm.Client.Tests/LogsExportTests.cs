using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Google.Protobuf.WellKnownTypes;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Shell;
using Oadm.Contracts.Logging;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

using HostPermissionKeys = Oadm.Sdk.Plugins.HostPermissionKeys;

namespace Oadm.Client.Tests;

/// <summary>The date range of the Logs page (DateRangeChoice, DateRangeButton) and its export (LogExportViewModel).</summary>
public sealed class LogsExportTests
{
    /// <summary>21 Nov 2026 10:30 UTC; this computer is at UTC+1.</summary>
    private static FakeTimeProvider Clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 10, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("OADM test +1", TimeSpan.FromHours(1), "OADM test +1", "OADM test +1"));
        return time;
    }

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(1));

    private static (LogsViewModel Logs, LogStore Store, UserSession Session, IDialogService Dialogs) Create(FakeOadmApi api, TimeProvider time, bool admin = true, params string[] permissions)
    {
        var session = new UserSession();
        var user = admin
            ? new UserInfo { UserName = "admin", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true, AllDevices = true }
            : new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, AllDevices = true };
        user.Permissions.AddRange(permissions);
        session.SignIn(user, "fake");
        var store = new LogStore(new ImmediateUiDispatcher());
        var dialogs = Substitute.For<IDialogService>();
        var logs = new LogsViewModel(store, new AuditLogViewModel(api, time), session, new ServerLogViewModel(api, time), dialogs, api, time);
        return (logs, store, session, dialogs);
    }

    private static AuditEntry Audit(DateTimeOffset time, string user, string action, string target, string detail = "") => new()
    {
        Time = Timestamp.FromDateTimeOffset(time),
        UserName = user,
        ClientAddress = "10.0.0.15",
        Action = action,
        Target = target,
        Detail = detail,
    };

    [Fact]
    public void A_preset_is_relative_and_resolves_at_the_time_of_the_query()
    {
        var time = Clock();
        var range = new DateRangeChoice(time);
        var changes = 0;
        range.Changed += (_, _) => changes++;
        Assert.Equal(("All", true, (DateTimeOffset?)null, (DateTimeOffset?)null), (range.Text, range.IsAll, range.Resolve().From, range.Resolve().To));

        range.IsOpen = true;
        range.Choose(DateRangePreset.LastHour);
        Assert.Equal(("Last hour", false, 1), (range.Text, range.IsOpen, changes));
        Assert.Equal(time.GetUtcNow().AddHours(-1), range.Resolve().From);
        Assert.Null(range.Resolve().To);
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(time.GetUtcNow().AddHours(-1), range.Resolve().From); // moves with the clock

        range.Choose(DateRangePreset.Today);
        Assert.Equal(Local(21, 0), range.Resolve().From); // midnight of this computer's day
        range.Choose(DateRangePreset.Last24Hours);
        Assert.Equal(time.GetUtcNow().AddHours(-24), range.Resolve().From);
        range.Choose(DateRangePreset.Last7Days);
        Assert.Equal(time.GetUtcNow().AddDays(-7), range.Resolve().From);
        Assert.Equal("Last 7 days", range.Text);
        range.Choose(DateRangePreset.Last7Days);
        Assert.Equal(4, changes); // the same choice again changes nothing

        range.Reset();
        Assert.True(range.IsAll);
        Assert.Equal(5, changes);
        Assert.Equal(["Last hour", "Today", "Last 24 hours", "Last 7 days", "All", "Custom"], DateRangeChoice.Options.Select(o => o.Title));
    }

    [Fact]
    public void Custom_shows_To_before_From_below_To_and_applies_only_a_valid_range()
    {
        var time = Clock();
        var range = new DateRangeChoice(time);
        range.Choose(DateRangePreset.Last24Hours);
        range.OnOpening();
        Assert.Equal(DateRangePreset.Last24Hours, range.Marked);
        range.Choose(DateRangePreset.Custom);
        Assert.True(range.IsCustomShown);
        Assert.Equal(DateRangePreset.Last24Hours, range.Preset); // nothing applied yet

        // Filled with the range applied before: from 24 hours ago (local 11:30) to the next full hour; no error on an untouched form.
        Assert.Equal((new TimeSpan(11, 30, 0), new TimeSpan(12, 0, 0)), (range.FromTime!.Value, range.ToTime!.Value));
        Assert.Equal(new DateTime(2026, 11, 20), range.FromDate!.Value.Date);
        Assert.Null(range.FromError);
        Assert.Null(range.ToError);

        range.ToDate = new DateTimeOffset(new DateTime(2026, 11, 19), TimeSpan.FromHours(1));
        Assert.Equal("To must be after From.", range.ToError);
        Assert.Null(range.FromError);
        Assert.False(range.ApplyCustomCommand.CanExecute(null));
        Assert.Equal("To must be after From.", range.FormError);

        range.FromDate = new DateTimeOffset(new DateTime(2026, 11, 12), TimeSpan.FromHours(1));
        range.FromTime = new TimeSpan(9, 0, 0);
        range.ToDate = new DateTimeOffset(new DateTime(2026, 11, 14), TimeSpan.FromHours(1));
        range.ToTime = new TimeSpan(18, 0, 0);
        Assert.Null(range.ToError);
        range.IsOpen = true;
        range.ApplyCustomCommand.Execute(null);
        Assert.Equal((DateRangePreset.Custom, false), (range.Preset, range.IsOpen));
        Assert.Equal("12 Nov 09:00 – 14 Nov 18:00", range.Text);
        Assert.Equal((Local(12, 9), Local(14, 18)), (range.Resolve().From!.Value, range.Resolve().To!.Value));

        range.SetCustom(new DateTimeOffset(2025, 12, 30, 8, 0, 0, TimeSpan.FromHours(1)), Local(2, 8));
        Assert.Equal("30 Dec 2025 08:00 – 2 Nov 08:00", range.Text); // the year only when it is not this year
    }

    [Fact]
    public async Task The_server_tab_sends_its_range_and_the_client_tab_filters_its_memory()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var time = Clock();
        var (logs, store, _, _) = Create(api, time);
        await logs.OnShownAsync();
        Assert.Equal(200, logs.Server!.Entries.Count);

        // The fake server log runs from 17 Oct 2026 08:00 (+02:00), one entry each 37 s: an hour holds 98 of them.
        logs.Server.Range.SetCustom(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(2)));
        await Eventually.TrueAsync(() => logs.Server.Entries.Count is > 90 and < 100, "the server tab reads the hour");
        var filter = logs.Server.Filter();
        Assert.Equal(("2026-10-17T07:00:00.000Z", "2026-10-17T08:00:00.000Z"), (filter.NotBefore, filter.NotAfter));

        var now = time.GetLocalNow().DateTime;
        store.Add(new LogEntry(now.AddHours(-3), "Information", "Three hours ago"));
        store.Add(new LogEntry(now.AddMinutes(-20), "Warning", "Twenty minutes ago"));
        store.Add(new LogEntry(now.AddMinutes(-5), "Information", "Five minutes ago"));
        logs.ShowClientLogCommand.Execute(null);
        logs.Range.Choose(DateRangePreset.LastHour);
        Assert.Equal(["Five minutes ago", "Twenty minutes ago"], logs.Entries.Select(e => e.Message));
        Assert.Equal("2 of 3 entries", logs.StatusLine);
        logs.Range.Choose(DateRangePreset.All);
        Assert.Equal(3, logs.Entries.Count);
    }

    [Fact]
    public async Task The_audit_tab_searches_on_the_server_and_pages_by_id_so_old_entries_are_found()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var time = Clock();
        var start = Local(1, 0);
        api.AddAuditEntries([Audit(start, "Müller", "Changed user", "contractor", "the oldest entry")]);
        api.AddAuditEntries(Enumerable.Range(0, 12_000).Select(i => Audit(start.AddMinutes(i + 1), "tech" + (i % 5), "Started task", "Restart", "2 devices: 2 done")));
        var (logs, _, _, _) = Create(api, time);
        await logs.ShowAuditCommand.ExecuteAsync(null);
        var audit = logs.Audit!;
        Assert.Equal(200, audit.Entries.Count);
        var total = audit.TotalCount;
        Assert.True(total > 12_000);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Entries 1 to 200 of {total:N0}"), audit.PageText);
        Assert.False(audit.NewerCommand.CanExecute(null));

        await audit.OlderCommand.ExecuteAsync(null);
        await audit.OlderCommand.ExecuteAsync(null);
        Assert.Equal(2, audit.Page);
        Assert.StartsWith("Entries 401 to 600 of", audit.PageText, StringComparison.Ordinal);
        var oldestOnPage = audit.Entries[^1].Id;
        api.AddAuditEntries([Audit(time.GetUtcNow(), "admin", "Logged in", "admin")]);
        await audit.NewerCommand.ExecuteAsync(null);
        await audit.OlderCommand.ExecuteAsync(null);
        Assert.Equal(oldestOnPage, audit.Entries[^1].Id); // a new entry never moves an older page

        // The search goes to the server: the oldest entry, beyond the newest 10,000, is found, in any case.
        audit.SearchText = "MÜLLER";
        await audit.LoadAsync(0);
        Assert.Equal("the oldest entry", Assert.Single(audit.Entries).Detail);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"1 of {total + 1:N0} entries"), audit.StatusLine);
        Assert.Equal("MÜLLER", audit.Filter().Search);

        audit.SearchText = string.Empty;
        audit.Range.SetCustom(start.AddMinutes(100), start.AddMinutes(199));
        await Eventually.TrueAsync(() => audit.MatchCount == 100 && audit.Entries.Count == 100, "the range is read");
    }

    [Fact]
    public async Task An_export_over_5000_asks_first_and_saves_the_newest_5000_with_the_filter_of_the_tab()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var time = Clock();
        api.AddAuditEntries(Enumerable.Range(0, 6_000).Select(i => Audit(Local(1, 0).AddMinutes(i), "tech1", "Started task", "Restart")));
        var (logs, _, _, dialogs) = Create(api, time);
        await logs.ShowAuditCommand.ExecuteAsync(null);
        var export = logs.AuditExport!;
        var count = await logs.Audit!.CountForExportAsync(CancellationToken.None);
        Assert.True(count.OverLimit);

        string expected = string.Create(System.Globalization.CultureInfo.CurrentCulture,
            $"The export holds the newest 5,000 of {count.Total:N0} entries. Narrow the dates or the search to get the rest.");
        dialogs.ConfirmAsync(default!, default!, default!, default!).ReturnsForAnyArgs(false);
        await export.CsvCommand.ExecuteAsync(null);
        await dialogs.Received(1).ConfirmAsync("Export audit log", expected, "Export 5,000 entries", "Cancel");
        await dialogs.DidNotReceiveWithAnyArgs().CreateFileAsync(default!, default!, default!);
        Assert.Empty(api.LogExports);

        var file = new MemoryStream();
        dialogs.ConfirmAsync(default!, default!, default!, default!).ReturnsForAnyArgs(true);
        dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("oadm-audit-2026-11-21.csv", file));
        logs.Audit.SearchText = "tech1";
        await logs.Audit.LoadAsync(0);
        await export.CsvCommand.ExecuteAsync(null);
        await dialogs.Received(1).CreateFileAsync("Export audit log", "oadm-audit-2026-11-21.csv", LogExportViewModel.CsvFile);
        var request = Assert.IsType<ExportAuditRequest>(Assert.Single(api.LogExports));
        Assert.Equal(("tech1", LogExportFormat.Csv, "OADM test +1", 60), (request.Filter.Search, request.Options.Format, request.Options.TimeZone, request.Options.UtcOffsetMinutes));
        var lines = System.Text.Encoding.UTF8.GetString(file.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5_001, lines.Length); // the header and the newest 5,000
        Assert.Equal("﻿Time,User,Client,Action,Target,Detail", lines[0]);
        Assert.False(export.IsBusy);

        // The server log has no total: "more than 5,000".
        Assert.Equal("The export holds the newest 5,000 of more than 5,000 entries. Narrow the dates or the search to get the rest.",
            LogExportViewModel.LimitMessage(new LogExportCount(Any: true, OverLimit: true, Total: null)));
    }

    [Fact]
    public async Task The_client_tab_sends_the_rows_it_shows_and_an_empty_tab_says_so()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var time = Clock();
        var (logs, store, _, dialogs) = Create(api, time);
        logs.ShowClientLogCommand.Execute(null);
        logs.SearchText = "nothing like this";
        await logs.ClientExport!.PdfCommand.ExecuteAsync(null);
        await dialogs.Received(1).ShowMessageAsync("Export client log", "There are no entries to export.");

        for (var i = 0; i < 30; i++)
        {
            store.Add(new LogEntry(time.GetLocalNow().DateTime.AddMinutes(-i), i % 3 == 0 ? "Warning" : "Information", "Entry " + i));
        }

        logs.SearchText = string.Empty;
        logs.SelectedLevel = LogsViewModel.Levels.Single(l => l.Title == "Warnings and errors");
        var file = new MemoryStream();
        dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("oadm-client-2026-11-21.pdf", file));
        await logs.ClientExport.PdfCommand.ExecuteAsync(null);
        await dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default!, default!);
        await dialogs.Received(1).CreateFileAsync("Export client log", "oadm-client-2026-11-21.pdf", LogExportViewModel.PdfFile);
        var request = Assert.IsType<ExportClientLogRequest>(Assert.Single(api.LogExports));
        Assert.Equal(10, request.Entries.Count);
        Assert.All(request.Entries, e => Assert.Equal("Warning", e.Level));
        Assert.Equal(("Warning", LogExportFormat.Pdf), (request.MinLevel, request.Options.Format));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(file.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_the_file_is_made_Export_is_off_and_Cancel_removes_the_cut_file()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var time = Clock();
        var (logs, _, _, dialogs) = Create(api, time);
        await logs.OnShownAsync();
        var deleted = false;
        dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("oadm-server-2026-11-21.csv", new MemoryStream())
        {
            Delete = () =>
            {
                deleted = true;
                return Task.CompletedTask;
            },
        });
        api.LogExportGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var export = logs.ServerExport!;
        var running = export.CsvCommand.ExecuteAsync(null);
        await Eventually.TrueAsync(() => export.IsBusy && !export.CsvCommand.CanExecute(null) && !export.PdfCommand.CanExecute(null)
            && export.CancelCommand.CanExecute(null) && export.ProgressText == "Making the CSV file", "the export waits for the server");
        export.CancelCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((false, true), (export.IsBusy, deleted));
        await dialogs.DidNotReceiveWithAnyArgs().ShowMessageAsync(default!, default!);
    }

    [Fact]
    public void Export_shows_with_the_permission_Export_logs_only()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var (without, _, _, _) = Create(api, Clock(), admin: false, HostPermissionKeys.LogsServer);
        Assert.False(without.CanExport);
        var (with, _, session, _) = Create(api, Clock(), admin: false, HostPermissionKeys.LogsExport);
        Assert.True(with.CanExport);
        Assert.True(with.IsClientTab); // the client log only: its export needs nothing else
        session.SignIn(new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, AllDevices = true }, "fake");
        Assert.False(with.CanExport);
    }

    [Fact]
    public async Task Logs_page_renders_the_range_and_the_export_on_each_tab()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var time = Clock();
            var (logs, store, _, dialogs) = Create(api, time);
            store.Add(new LogEntry(DateTime.Now.AddMinutes(-2), "Information", "Connected to localhost:5080"));
            store.Add(new LogEntry(DateTime.Now.AddMinutes(-1), "Warning", "The live view of 10.0.0.48 stopped: the camera closed the stream"));
            store.Add(new LogEntry(DateTime.Now, "Information", "Opened the Logs page"));
            await logs.OnShownAsync();
            var view = new LogsView { DataContext = logs };
            var overlay = new Canvas();
            var window = new Window { Width = 1400, Height = 820, Content = new Border { Padding = new Thickness(20), Child = new Panel { Children = { view, overlay } } } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();

            // Each tab: the range left of the search, Export after the commands.
            var buttons = Visible<DateRangeButton>(window);
            var range = Assert.Single(buttons);
            Assert.Equal("All", range.Text);
            var search = Visible<SearchBox>(window).Single();
            Assert.True(range.TranslatePoint(new Point(range.Bounds.Width, 0), window)!.Value.X <= search.TranslatePoint(default, window)!.Value.X);
            Assert.Equal("Export", Visible<ExportButton>(window).Single().Text);
            Assert.Equal(["CSV", "PDF"], Visible<ExportButton>(window).Single().Entries.Select(e => (string)e.Header!));
            Save(window, outDir, "logs-server.png");

            logs.ShowClientLogCommand.Execute(null);
            Pump();
            Assert.Single(Visible<DateRangeButton>(window));
            Save(window, outDir, "logs-client.png");

            await logs.ShowAuditCommand.ExecuteAsync(null);
            Pump();
            Assert.Contains("Entries 1 to", Texts(window).Single(t => t.StartsWith("Entries 1 to", StringComparison.Ordinal)), StringComparison.Ordinal);
            Save(window, outDir, "logs-audit.png");

            // The range flyout (shown as it opens below the button): the presets, then Custom with From and To.
            await logs.ShowServerLogCommand.ExecuteAsync(null);
            logs.Server!.Range.Choose(DateRangePreset.Last24Hours);
            Pump();
            range = Visible<DateRangeButton>(window).Single();
            Assert.Equal("Last 24 hours", range.Text);
            var flyout = Below(overlay, range, new FlyoutPresenter { Content = new DateRangePanel { DataContext = logs.Server.Range } }, alignRight: true);
            logs.Server.Range.OnOpening();
            Pump();
            Assert.True(((DateRangePanel)flyout.Content!).ChoiceOf(DateRangePreset.Last24Hours).IsChecked);
            Save(window, outDir, "logs-range-presets.png");

            logs.Server.Range.Choose(DateRangePreset.Custom);
            Pump();
            Assert.Equal(2, flyout.GetVisualDescendants().OfType<DateTimeInput>().Count(i => i.IsEffectivelyVisible));
            Save(window, outDir, "logs-range-custom.png");

            logs.Server.Range.ToDate = logs.Server.Range.FromDate!.Value.AddDays(-1);
            Pump();
            var toRow = flyout.GetVisualDescendants().OfType<FormField>().Single(f => f.Label == "To");
            Assert.True(toRow.HasError);
            Assert.Contains("To must be after From.", Texts(flyout));
            Save(window, outDir, "logs-range-error.png");
            overlay.Children.Clear();

            // The Export menu.
            var exportButton = Visible<ExportButton>(window).Single();
            Below(overlay, exportButton, new MenuFlyoutPresenter { ItemsSource = new[] { new MenuItem { Header = "CSV" }, new MenuItem { Header = "PDF" } } });
            Pump();
            Save(window, outDir, "logs-export-menu.png");
            overlay.Children.Clear();

            // While the file is made: Export off, the progress row with Cancel (All: the fake log is from October).
            logs.Server.Range.Choose(DateRangePreset.All);
            dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("oadm-server-2026-11-21.pdf", new MemoryStream()));
            api.LogExportGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = logs.ServerExport!.PdfCommand.ExecuteAsync(null);
            Pump();
            Assert.True(logs.ServerExport.IsBusy);
            Assert.False(exportButton.IsEffectivelyEnabled);
            Assert.Contains("Making the PDF file", Texts(window));
            Save(window, outDir, "logs-export-busy.png");
            api.LogExportGate.SetResult();
            await running;
            Pump();
            Assert.True(exportButton.IsEffectivelyEnabled);

            var limit = new MessageWindow
            {
                Heading = "Export audit log",
                Message = LogExportViewModel.LimitMessage(LogExportCount.Of(12_345)),
                ConfirmText = LogExportViewModel.ExportText,
            };
            Oadm.Client.App.ApplyCrispText(limit);
            limit.Show();
            Pump();
            Assert.False(limit.IsDanger);
            Save(limit, outDir, "logs-export-limit.png");
            limit.Close();
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>
    /// Places a presenter right below a button, as its flyout opens (headless popups are windows of their own): left edges
    /// aligned, or right edges (the range flyout opens towards the page).
    /// </summary>
    private static T Below<T>(Canvas overlay, Control anchor, T presenter, bool alignRight = false)
        where T : Control
    {
        overlay.Children.Add(presenter);
        if (alignRight)
        {
            var corner = anchor.TranslatePoint(new Point(anchor.Bounds.Width, anchor.Bounds.Height + 4), overlay)!.Value;
            Canvas.SetTop(presenter, corner.Y);
            Canvas.SetRight(presenter, overlay.Bounds.Width - corner.X);
            return presenter;
        }

        var at = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + 4), overlay)!.Value;
        Canvas.SetLeft(presenter, at.X);
        Canvas.SetTop(presenter, at.Y);
        return presenter;
    }

    private static List<T> Visible<T>(Visual root)
        where T : Control => [.. root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible)];

    private static List<string> Texts(Visual root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
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

    [Fact]
    public async Task Clear_with_a_range_set_reads_nothing_and_leaves_no_error()
    {
        var time = Clock();
        var api = Substitute.For<IOadmApi>();
        api.ListAuditAsync(Arg.Any<ListAuditRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<AuditList>>(_ => throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "down")));
        api.ListServerLogAsync(Arg.Any<ListServerLogRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ServerLogPage>>(_ => throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "down")));
        var audit = new AuditLogViewModel(api, time);
        var server = new ServerLogViewModel(api, time);
        audit.Range.SetCustom(Local(20, 9), Local(21, 9));
        server.Range.SetCustom(Local(20, 9), Local(21, 9));
        await Eventually.TrueAsync(() => audit.Error is not null && server.Error is not null, "the failed reads of the range");
        api.ClearReceivedCalls();

        audit.Clear();
        server.Clear();

        Assert.True(audit.Range.IsAll);
        Assert.True(server.Range.IsAll);
        Assert.Null(audit.Error);
        Assert.Null(server.Error);
        Assert.False(audit.IsLoading);
        Assert.False(server.IsLoading);
        await api.DidNotReceive().ListAuditAsync(Arg.Any<ListAuditRequest>(), Arg.Any<CancellationToken>());
        await api.DidNotReceive().ListServerLogAsync(Arg.Any<ListServerLogRequest>(), Arg.Any<CancellationToken>());
    }
}
