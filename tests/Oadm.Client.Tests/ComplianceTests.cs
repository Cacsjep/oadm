using System.Diagnostics;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Compliance;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// The Compliance page, the check dialog and the client store against the fake server. Set OADM_SCREENSHOT_DIR for
/// compliance-page.png, compliance-card.png, compliance-check.png and devices-compliance.png.
/// </summary>
public sealed class ComplianceTests(ITestOutputHelper output) : IDisposable
{
    private readonly UiThread _ui = new();

    public void Dispose() => _ui.Dispose();

    private static async Task FeedAsync(IOadmApi api, ComplianceStore store)
    {
        await foreach (var list in api.WatchComplianceChecksAsync(CancellationToken.None))
        {
            store.ApplyChecks(list);
            break;
        }

        await foreach (var batch in api.WatchComplianceResultsAsync(CancellationToken.None))
        {
            store.ApplyResults(batch.Results, reset: true);
            break;
        }
    }

    private static async Task<(FakeOadmApi Api, DeviceStore Store, IDialogService Dialogs)> CreateAsync()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var store = new DeviceStore();
        store.Reset(await api.ListDevicesAsync(CancellationToken.None));
        store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
        await FeedAsync(api, store.Compliance);
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        return (api, store, dialogs);
    }

    private static ComplianceCheckSummary Check(string id, string name, ComplianceSeverity severity = ComplianceSeverity.ComplianceProblem, bool enabled = true) =>
        new() { Id = id, Name = name, Severity = severity, Enabled = enabled };

    private static ComplianceResultEntry Result(string check, string device, ComplianceOutcome outcome, string detail = "") =>
        new() { CheckId = check, DeviceId = device, Outcome = outcome, Detail = detail };

    [Fact]
    public void The_worst_result_decides_with_the_severity_of_its_check()
    {
        var store = new ComplianceStore();
        var list = new ComplianceCheckList();
        list.Checks.Add(Check("a", "Reachable"));
        list.Checks.Add(Check("b", "Certificate", ComplianceSeverity.ComplianceWarning));
        list.Checks.Add(Check("c", "Off", enabled: false));
        store.ApplyChecks(list);
        store.ApplyResults(
        [
            Result("a", "1", ComplianceOutcome.ComplianceFailed, "Unreachable"),
            Result("b", "1", ComplianceOutcome.ComplianceFailed, "10 days left"),
            Result("a", "2", ComplianceOutcome.CompliancePassed),
            Result("b", "2", ComplianceOutcome.ComplianceFailed, "10 days left"),
            Result("a", "3", ComplianceOutcome.CompliancePassed),
            Result("c", "3", ComplianceOutcome.ComplianceFailed, "ignored: the check is off"),
            Result("a", "4", ComplianceOutcome.ComplianceNotChecked, "Unreachable"),
        ], reset: true);

        Assert.Equal((ComplianceLevel.Problem, "1 problem, 1 warning"), (store.For("1").Level, store.For("1").Text));
        Assert.Equal("Certificate: 10 days left\nReachable: Unreachable", store.For("1").ToolTip);
        Assert.Equal((ComplianceLevel.Warning, "1 warning"), (store.For("2").Level, store.For("2").Text));
        Assert.Equal((ComplianceLevel.Passed, "OK"), (store.For("3").Level, store.For("3").Text));
        Assert.Equal((ComplianceLevel.None, string.Empty), (store.For("4").Level, store.For("4").Text));
        Assert.True(store.For("1").SortKey > store.For("2").SortKey && store.For("2").SortKey > store.For("3").SortKey);

        // A removed result and a check turned to Warning change the level in place.
        store.ApplyResults([new ComplianceResultEntry { CheckId = "b", DeviceId = "1", Removed = true }], reset: false);
        Assert.Equal("1 problem", store.For("1").Text);
        var changed = list.Clone(); // every stream message is a new list
        changed.Checks[0].Severity = ComplianceSeverity.ComplianceWarning;
        store.ApplyChecks(changed);
        Assert.Equal((ComplianceLevel.Warning, "1 warning"), (store.For("1").Level, store.For("1").Text));
    }

    [Fact]
    public Task The_page_shows_problems_first_and_a_card_shows_the_devices_failing_its_check() => _ui.RunAsync(async () =>
    {
        var (api, store, dialogs) = await CreateAsync();
        using var page = new ComplianceViewModel(api, dialogs, store, _ui);
        page.Refilter();

        Assert.True(page.IsProblemsTab);
        Assert.NotEmpty(page.Rows);
        Assert.All(page.Rows, r => Assert.Equal(ComplianceLevel.Problem, r.Compliance.Level));
        Assert.StartsWith("Problems ", page.ProblemsText, StringComparison.Ordinal);
        Assert.Equal(["Login works", "Reachable", "Certificate 30 days", "Matches base-config.cfg", "Object Analytics running"], page.Cards.Select(c => c.Title));
        Assert.Equal(5, page.ColumnChecks.Count);

        var app = page.Cards.Single(c => c.Title == "Object Analytics running");
        Assert.True(app.IsError);
        page.SelectCardCommand.Execute(app);
        Assert.True(app.IsSelected);
        Assert.True(page.IsAllTab);
        Assert.Equal(app.Check.Failed, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal(ComplianceOutcome.ComplianceFailed, r.Compliance.Get(app.Id)!.Outcome));

        page.SearchText = page.Rows[0].DisplayAddress;
        Assert.Single(page.Rows);
        page.SearchText = string.Empty;

        page.ShowWarningsCommand.Execute(null);
        Assert.False(app.IsSelected);
        Assert.All(page.Rows, r => Assert.Equal(ComplianceLevel.Warning, r.Compliance.Level));
        api.Dispose();
    });

    [Fact]
    public Task A_new_check_reads_its_choices_from_a_device_and_is_saved() => _ui.RunAsync(async () =>
    {
        var (api, store, dialogs) = await CreateAsync();
        var catalog = await api.GetComplianceCatalogAsync(CancellationToken.None);
        var devices = store.Devices.Cast<Oadm.Sdk.Devices.IDeviceInfo>().ToList();
        var editor = new ComplianceCheckEditorViewModel(api, dialogs, catalog, devices);
        Assert.Equal("Enter a name.", editor.SaveBlockedReason);

        editor.SelectedType = editor.Types.Single(t => t.Value is ComplianceTypeEntry { Id: "oadm.acap.running" });
        Assert.Equal("Application running", editor.Name);
        Assert.True(editor.ReadsDevices);
        Assert.Equal("Choose at least one device or tag.", editor.SaveBlockedReason);
        var application = editor.Fields.Single(f => f.Key == "application");
        Assert.True(application.ReadsFromDevice);

        // Read from device: one device from the picker, then the applications it has.
        var camera = devices.First(d => d.HasVideo);
        editor.ShowPicker = picker =>
        {
            if (picker.Options.SingleDevice)
            {
                picker.Visible.First(i => i.Id == camera.Id).IsChecked = true;
            }
            else
            {
                picker.Kind = DeviceChoiceKind.All;
            }

            return Task.FromResult(true);
        };
        await application.ReadFromDeviceCommand.ExecuteAsync(null);
        Assert.Equal("Read from " + camera.Address, application.ReadFromText);
        application.SelectChoice("objectanalytics");
        await editor.Devices.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Null(editor.SaveBlockedReason);

        editor.IntervalText = "5";
        Assert.Equal("Enter 15 to 1440 minutes.", editor.SaveBlockedReason);
        editor.IntervalText = "30";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(editor.Saved);
        Assert.Equal(30, editor.Saved!.IntervalMinutes);

        var saved = await api.GetComplianceCheckAsync(editor.Saved.Id, CancellationToken.None);
        Assert.Equal("objectanalytics", (JsonNode.Parse(saved.SettingsJson) as JsonObject)!["application"]!.GetValue<string>());
        Assert.True(saved.Summary.Devices.All);
        api.Dispose();
    });

    [Fact]
    public Task A_default_check_changes_only_its_devices_and_cannot_be_deleted() => _ui.RunAsync(async () =>
    {
        var (api, store, dialogs) = await CreateAsync();
        var reachable = await api.GetComplianceCheckAsync("6d0a7c52-6f8e-4c4b-9a51-0c0a3b1f0001", CancellationToken.None);
        var editor = new ComplianceCheckEditorViewModel(api, dialogs, await api.GetComplianceCatalogAsync(CancellationToken.None), [.. store.Devices.Cast<Oadm.Sdk.Devices.IDeviceInfo>()], reachable);
        Assert.True(editor.IsBuiltIn);
        Assert.False(editor.CanChangeKind);
        Assert.Equal("Reachable", editor.Name);

        using var page = new ComplianceViewModel(api, dialogs, store, _ui);
        page.SelectCardCommand.Execute(page.Cards.Single(c => c.Title == "Reachable"));
        await page.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("Reachable cannot be deleted.", page.ErrorText);
        api.Dispose();
    });

    [Fact]
    public Task Page_dialog_and_devices_grid_render() => _ui.RunAsync(async () =>
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, store, dialogs) = await CreateAsync();
            using var page = new ComplianceViewModel(api, dialogs, store, new AvaloniaUiDispatcher());
            var view = new ComplianceView { DataContext = page };
            var window = new Window { Width = 1500, Height = 900, Content = new Border { Padding = new Avalonia.Thickness(20), Child = view } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            page.Refilter();
            Pump();
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal(5, grid.Columns.Count(c => c.Tag is string tag && tag.StartsWith("check:", StringComparison.Ordinal)));
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Object Analytics running", texts);
            Assert.Contains(page.ProblemsText, texts);
            Capture(window, outDir, "compliance-page.png");
            page.SelectCardCommand.Execute(page.Cards.Single(c => c.Title == "Matches base-config.cfg"));
            Pump();
            Capture(window, outDir, "compliance-card.png");
            window.Close();

            var editor = new ComplianceCheckEditorViewModel(api, dialogs, await api.GetComplianceCatalogAsync(CancellationToken.None),
                [.. store.Devices.Cast<Oadm.Sdk.Devices.IDeviceInfo>()], await api.GetComplianceCheckAsync(page.Cards.Single(c => c.Title == "Matches base-config.cfg").Id, CancellationToken.None));
            var dialog = new ComplianceCheckWindow();
            dialog.Attach(editor);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            Pump();
            var dialogTexts = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("base-config.cfg", dialogTexts);
            Assert.Contains("Check every (min)", dialogTexts);
            Capture(dialog, outDir, "compliance-check.png");
            dialog.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    });

    [Fact]
    public async Task The_devices_grid_shows_one_compliance_column_or_one_per_check()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            using var fixture = new DevicesFixture(api);
            fixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
            var vm = fixture.Devices;
            var window = new Window { Width = 1800, Height = 800, Content = new DevicesView { DataContext = vm } };
            window.Show();
            Pump();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            var summary = grid.Columns.Single(c => c.Tag as string == "compliance");
            Assert.False(vm.HasCompliance);
            Assert.False(summary.IsVisible); // no compliance for this role or server: no column

            await FeedAsync(api, fixture.Store.Compliance);
            Pump();
            Assert.True(vm.HasCompliance);
            Assert.True(summary.IsVisible);
            Assert.Contains(grid.GetVisualDescendants().OfType<ComplianceSummaryCell>(), c => c.Compliance?.Level == ComplianceLevel.Problem);

            vm.ToggleExpandComplianceCommand.Execute(null);
            Pump();
            Assert.False(summary.IsVisible);
            Assert.Equal(5, grid.Columns.Count(c => c.Tag is string tag && tag.StartsWith("check:", StringComparison.Ordinal)));
            Assert.True(fixture.Settings.Current.ExpandCompliance);
            Capture(window, outDir, "devices-compliance.png");

            vm.ToggleExpandComplianceCommand.Execute(null);
            Pump();
            Assert.True(summary.IsVisible);
            Assert.DoesNotContain(grid.Columns, c => c.Tag is string tag && tag.StartsWith("check:", StringComparison.Ordinal));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public Task Thirty_thousand_devices_with_ten_checks_filter_fast() => _ui.RunAsync(async () =>
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        var store = new DeviceStore();
        store.Reset(Enumerable.Range(0, 30_000).Select(i => new Device
        {
            Id = Guid.NewGuid().ToString(), Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}", Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture),
            Model = "P3265-V", Status = DeviceStatus.Ok,
        }));
        var list = new ComplianceCheckList();
        for (var c = 0; c < 10; c++)
        {
            list.Checks.Add(Check("c" + c, "Check " + c, c % 2 == 0 ? ComplianceSeverity.ComplianceProblem : ComplianceSeverity.ComplianceWarning));
        }

        var results = store.Devices.SelectMany((d, i) => list.Checks.Select((c, j) =>
            Result(c.Id, d.Id, (i + j) % 7 == 0 ? ComplianceOutcome.ComplianceFailed : ComplianceOutcome.CompliancePassed, "detail"))).ToList();
        var watch = Stopwatch.StartNew();
        store.Compliance.ApplyChecks(list);
        store.Compliance.ApplyResults(results, reset: true);
        var apply = watch.Elapsed;
        using var page = new ComplianceViewModel(api, Substitute.For<IDialogService>(), store, _ui);
        watch.Restart();
        page.Refilter();
        page.ShowAllCommand.Execute(null);
        page.SelectCardCommand.Execute(page.Cards[3]);
        page.SearchText = "10.0.1.";
        var filter = watch.Elapsed;
        output.WriteLine($"300,000 results applied in {apply.TotalMilliseconds:N0} ms; filter, tab, card and search in {filter.TotalMilliseconds:N0} ms");
        Assert.True(apply < TimeSpan.FromSeconds(10), $"apply took {apply}");
        Assert.True(filter < TimeSpan.FromSeconds(5), $"filter took {filter}");
        api.Dispose();
    });

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
