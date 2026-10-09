using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Client.Api;
using Oadm.Plugins.SnapshotReport.Client;

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>Minimal host: dark theme variant plus the real OADM theme (styles, colors, icons), like the client app.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.SnapshotReport.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>
/// Tests that start an Avalonia headless session run one after another: two sessions at the same time in one test
/// process deadlock.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessSessions
{
    public const string Name = "Avalonia headless";
}

/// <summary>Renders the page, the preview and the export dialog offscreen with fake data. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessPageTests
{
    [Fact]
    public async Task Page_renders_the_grid_with_a_multisensor_device_and_error_tiles()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        var settings = new ExportSettingsStore(TempFiles.NewSettingsPath());
        settings.Save(new ExportSettings { Site = "Headquarters Vienna", Technician = "Jane Doe" });

        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var (tiles, errors) = await session.Dispatch(async () =>
        {
            var view = new SnapshotReportView();
            var vm = new SnapshotReportViewModel(new FakeApiContext(api), view, settings);
            view.DataContext = vm;
            // Like the client shell: page title above one card that hosts the plugin view.
            var title = new TextBlock { Text = "Snapshot report", Margin = new Thickness(4, 0, 0, 16) };
            title.Classes.Add("pageTitle");
            var card = new Border { Child = view };
            card.Classes.Add("card");
            Grid.SetRow(card, 1);
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            layout.Children.Add(title);
            layout.Children.Add(card);
            var window = new Window { Width = 1500, Height = 980, Content = new Border { Padding = new Thickness(16), Child = layout } };
            window.Show();
            Pump();
            Capture(window, outDir, "snapshot-report-before-create.png");
            await vm.RefreshAllAsync();
            vm.Tiles.First(t => t.Title == "10.0.0.48 - View Area 2").IsSelected = false;
            Pump();
            Capture(window, outDir, "snapshot-report-page.png");

            vm.TileWidth = 220;
            vm.SearchText = "P3727";
            Pump();
            Capture(window, outDir, "snapshot-report-page-small-search.png");

            var preview = new SnapshotPreviewWindow();
            preview.Attach(new SnapshotPreviewViewModel(vm.Tiles.First(t => t.Title == "10.0.0.32 - Sensor 2"), view));
            preview.Show();
            Pump();
            Capture(preview, outDir, "snapshot-report-preview.png");
            preview.Close();

            var export = new ExportReportViewModel(new FakeApiContext(api), [.. vm.SelectedTiles], settings);
            var dialog = new ExportReportWindow();
            dialog.Attach(export);
            dialog.Show();
            Pump();
            Capture(dialog, outDir, "snapshot-report-export.png");
            Assert.False(export.HasErrors); // remembered values, nothing edited

            // Errors directly below their fields, Export disabled with the reason as tooltip.
            export.Site = " ";
            export.DateText = "07.10.2026";
            Pump();
            Assert.Equal("Enter the site or customer name.", export.ErrorOf(nameof(export.Site)));
            Assert.Equal("Enter the date as yyyy-MM-dd.", export.ErrorOf(nameof(export.DateText)));
            Assert.False(export.ExportCommand.CanExecute(null));
            Assert.Equal("Enter the site or customer name.", export.ExportBlockedReason);
            Capture(dialog, outDir, "snapshot-report-export-errors.png");
            dialog.Close();

            var result = (vm.Tiles.Count, vm.Tiles.Count(t => t.IsError));
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.True(tiles >= 10, $"{tiles} tiles");
        Assert.True(errors >= 2, $"{errors} error tiles");
    }

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
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
