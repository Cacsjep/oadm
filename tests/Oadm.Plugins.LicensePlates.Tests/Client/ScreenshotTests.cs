using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>
/// The page's tabs and dialogs rendered offscreen like the client shell with the sample server (Austrian plates, garages as
/// tags, every camera status); OADM_SCREENSHOT_DIR writes PNGs, OADM_TEST_APPEARANCE=Light the light theme.
/// </summary>
public sealed class ScreenshotTests
{
    private static string Suffix => Headless.Appearance;

    private static List<TextBlock> VisibleTexts(Visual root, string text) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text == text)];

    private static int PrimaryButtons(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));

    private static async Task<(LicensePlatesViewModel Page, LicensePlatesView View, Window Window, FakePageContext Ctx)> ShowPageAsync(FakePageContext? ctx = null)
    {
        ctx ??= Samples.Context();
        var page = new LicensePlatesViewModel(ctx, Samples.Clock());
        page.Log.Decode = bytes => Headless.PlateCrop(Encoding.UTF8.GetString(bytes));
        var view = new LicensePlatesView { DataContext = page };
        var window = Headless.Host(view);
        window.Show();
        await Samples.SettleAsync(page);
        Headless.Pump();
        await Eventually.TrueAsync(() => page.Log.PicturesPending == 0, "the plate crops read");
        Headless.Pump();
        return (page, view, window, ctx);
    }

    private static void Close(LicensePlatesViewModel page, Window window)
    {
        page.Deactivate();
        window.Close();
    }

    [Fact]
    public async Task The_log_renders_with_filters_pictures_results_and_the_export_menu()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window, _) = await ShowPageAsync();
            page.Log.Range.Choose(DateRangePreset.Last24Hours);
            page.Log.SelectedArea = LogViewModel.AreaChoices[1];
            await page.Log.LoadTask;
            Headless.Pump();
            await Eventually.TrueAsync(() => page.Log.PicturesPending == 0, "the plate crops read");
            Headless.Pump();

            var grid = view.FindControl<LogView>("LogPage")!.Grid;
            Assert.InRange(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), 1, 40); // virtualized
            Assert.Equal(0, PrimaryButtons(window)); // command buttons only
            Assert.Contains(grid.GetVisualDescendants().OfType<OadmIcon>(), c => c.Name == "DeviationIcon" && c.IsEffectivelyVisible);
            Assert.Single(VisibleTexts(window, "2 of 11 cameras connected"));
            Assert.Single(VisibleTexts(window, PageTexts.Subtitle));
            Assert.True(view.FindControl<LogView>("LogPage")!.FindControl<ToolbarButton>("ClearFiltersButton")!.IsEffectivelyEnabled);
            var confidences = grid.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is "72" or "54").ToList();
            Assert.Contains(confidences, t => t.Text == "72" && t.Classes.Contains("warning"));
            Assert.Contains(confidences, t => t.Text == "54" && t.Classes.Contains("error"));
            Headless.Capture(window, $"log-{Suffix}.png");

            var export = view.FindControl<LogView>("LogPage")!.FindControl<ExportButton>("ExportButton")!;
            export.Flyout!.ShowAt(export);
            Dispatcher.UIThread.RunJobs();
            Headless.Pump();
            Headless.Capture(window, $"log-export-menu-{Suffix}.png");
            export.Flyout.Hide();
            Close(page, window);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_plates_render_with_their_tags_and_an_ended_plate()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window, _) = await ShowPageAsync();
            page.Show(page.PlatesTab);
            Headless.Pump();
            var plates = view.FindControl<PlatesView>("PlatesPage")!;
            plates.Grid.SelectedItems.Add(page.Plates.Rows[1]);
            plates.Grid.SelectedItems.Add(page.Plates.Rows[3]);
            Headless.Pump();

            Assert.Equal("10 plates · 2 selected", page.Plates.Footer);
            Assert.Contains(plates.Grid.GetVisualDescendants().OfType<TagChips>(), c => c.IsEffectivelyVisible && c.ShownCount > 0);
            Assert.Contains(plates.Grid.GetVisualDescendants().OfType<OadmIcon>(), c => c.Name == "EndedIcon" && c.IsEffectivelyVisible);
            Assert.Contains("danger", plates.FindControl<ToolbarButton>("DeleteButton")!.Classes);
            Assert.Equal(0, PrimaryButtons(window));
            Headless.Capture(window, $"plates-{Suffix}.png");
            Close(page, window);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_add_plate_dialog_shows_its_error_below_the_plate()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, _, pageWindow, _) = await ShowPageAsync();
            var editor = new PlateEditorViewModel(page, null, null, page.Plates.Exists);
            var window = Headless.Dialog(new PlateEditorWindow { DataContext = editor });
            window.Show();
            Headless.Pump();
            editor.Text = "W 12345 A"; // typed: a plate of the list
            editor.Name = "Anna Huber, second car";
            editor.ToDate = new DateTimeOffset(2027, 3, 31, 0, 0, 0, TimeSpan.Zero);
            editor.ToTime = new TimeSpan(18, 0, 0);
            editor.TagChoices.Items.Single(c => c.Name == "Garage Nord").Chosen = true;
            Headless.Pump();

            Assert.Equal(LicensePlateTexts.PlateExists, editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
            Headless.Capture(window, $"plate-editor-error-{Suffix}.png");
            Assert.Single(VisibleTexts(window, LicensePlateTexts.PlateExists)); // directly below the plate
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<CheckBox>("DeleteWhenEndsBox")!.IsChecked);
            Assert.True(window.FindControl<CheckBox>("DeleteWhenEndsBox")!.IsEffectivelyEnabled);
            Assert.Equal(1, PrimaryButtons(window));
            Headless.Capture(window, $"plate-editor-error-{Suffix}.png");
            window.Close();
            Close(page, pageWindow);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_import_preview_shows_refused_rows_with_their_error_in_the_status_column()
    {
        var folder = Directory.CreateTempSubdirectory("lpc-shot-").FullName;
        var path = Path.Combine(folder, "garage-plates.csv");
        await File.WriteAllTextAsync(path, "plate,name\n");
        try
        {
            await Headless.Session.Dispatch(async () =>
            {
                var (page, _, pageWindow, _) = await ShowPageAsync();
                var import = new ImportViewModel(page);
                import.TagChoices.Items.Single(c => c.Name == "Visitors").Chosen = true;
                var window = Headless.Dialog(new ImportWindow { DataContext = import });
                window.Show();
                await import.UseFileAsync(path);
                Headless.Pump();

                Assert.Single(VisibleTexts(window, "3 new, 1 changed, 4 unchanged, 2 refused"));
                Assert.Contains(window.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.IsError && c.Detail == "A plate has 2 to 12 characters.");
                Assert.True(window.FindControl<Button>("ImportButton")!.IsEffectivelyEnabled);
                Assert.Equal(1, PrimaryButtons(window));
                Headless.Capture(window, $"import-preview-{Suffix}.png");
                window.Close();
                Close(page, pageWindow);
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_cameras_render_every_status_and_the_image_settings_tooltip()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window, _) = await ShowPageAsync();
            page.Show(page.CamerasTab);
            Headless.Pump();
            var cameras = view.FindControl<CamerasView>("CamerasPage")!;

            Assert.Equal(0, PrimaryButtons(window));
            Assert.Contains("danger", cameras.FindControl<ToolbarButton>("RemoveButton")!.Classes);
            Assert.Single(VisibleTexts(window, "Connection lost since 13:58"));
            Assert.Single(VisibleTexts(window, "Site offline: Site B"));
            Headless.Capture(window, $"cameras-{Suffix}.png");

            // The rich tooltip of the camera whose image settings differ, as when the mouse rests on its cell.
            var cell = cameras.Grid.GetVisualDescendants().OfType<Panel>()
                .First(p => p.Name == "ImageSettingsCell" && p.DataContext is CameraRowViewModel { ImageSettingsDiffer: true });
            var pointer = cell.TranslatePoint(new Point(8, cell.Bounds.Height / 2), window) ?? default;
            window.MouseMove(pointer);
            ToolTip.SetIsOpen(cell, true);
            Dispatcher.UIThread.RunJobs();
            Headless.Pump();
            var tip = Assert.IsType<ImageSettingsTip>(Assert.IsType<ToolTip>(ToolTip.GetTip(cell)).Content);
            Assert.Equal(6, tip.FindControl<ItemsControl>("Values")!.ItemCount);
            Headless.Capture(window, $"cameras-image-tooltip-{Suffix}.png");
            ToolTip.SetIsOpen(cell, false);
            Close(page, window);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_set_up_dialog_shows_both_areas_on_the_live_picture()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, _, pageWindow, _) = await ShowPageAsync();
            using var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
            var window = Headless.Dialog(new SetupWindow { DataContext = setup });
            window.Show();
            await setup.LoadAsync();
            setup.Select(2);
            Headless.Pump();

            var picture = ((SetupWindow)window).PictureControl;
            Assert.Equal(2, picture.Areas!.Count);
            Assert.True(picture.Areas[1].IsSelected);
            Assert.True(picture.PictureRect.Width > 400);
            Assert.True(window.FindControl<ToolbarButton>("RemoveArea2Button")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<ToolbarButton>("AddArea2Button")!.IsEffectivelyVisible);
            Assert.Equal(1, PrimaryButtons(window));
            Assert.Single(VisibleTexts(window, PageTexts.EventsWillTurnOn));
            Headless.Capture(window, $"setup-{Suffix}.png");
            window.Close();
            Close(page, pageWindow);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_stats_render_the_counts_the_hours_and_the_top_plates()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window, _) = await ShowPageAsync();
            page.Show(page.StatsTab);
            await page.Stats.LoadTask;
            Headless.Pump();

            var stats = view.FindControl<StatsView>("StatsPage")!;
            Assert.Equal(24, stats.FindControl<BarChart>("HourChart")!.Bars!.Count);
            Assert.Single(VisibleTexts(window, "1,501"));
            Assert.Equal(0, PrimaryButtons(window));
            Headless.Capture(window, $"stats-{Suffix}.png");
            Close(page, window);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_settings_dialog_has_the_server_folder_picker()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, _, pageWindow, _) = await ShowPageAsync();
            var settings = new SettingsViewModel(page);
            var window = Headless.Dialog(new SettingsWindow { DataContext = settings });
            window.Show();
            await settings.LoadAsync();
            Headless.Pump();

            var picker = window.FindControl<ServerFolderPicker>("FolderPicker")!;
            Assert.Equal(@"D:\Plates", picker.Path);
            Assert.True(picker.Button.IsEffectivelyEnabled); // the server's folders are there
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
            Assert.Equal(1, PrimaryButtons(window));
            Headless.Capture(window, $"settings-{Suffix}.png");
            window.Close();
            Close(page, pageWindow);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_change_tags_dialog_shows_all_some_and_none()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, _, pageWindow, _) = await ShowPageAsync();
            var tags = TagsDialogViewModel.ForPlates(page, [Samples.Plates()[0], Samples.Plates()[3]]);
            var window = Headless.Dialog(new TagsWindow { DataContext = tags });
            window.Show();
            Headless.Pump();

            Assert.Equal(5, window.FindControl<ItemsControl>("TagList")!.ItemCount);
            Assert.Equal(1, PrimaryButtons(window));
            Headless.Capture(window, $"change-tags-{Suffix}.png");
            window.Close();
            Close(page, pageWindow);
            return true;
        }, CancellationToken.None);
    }
}
