using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>
/// The decisions of 2026-11-27: fixed dialogs, the camera's own name, the threshold in percent, the Log's chosen columns
/// (kept on this computer, the export in their order) and the date on every row.
/// </summary>
public sealed class SetupAndColumnsTests
{
    [Fact]
    public async Task Every_dialog_of_the_plugin_has_a_fixed_size()
    {
        var windows = typeof(SetupWindow).Assembly.GetTypes().Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.Equal(6, windows.Count); // Set up, Copy settings, Settings, Plate editor, Import, Tags
        var resizable = await Headless.Session.Dispatch(
            () => windows.Where(t => ((Window)Activator.CreateInstance(t)!).CanResize).Select(t => t.Name).ToList(), CancellationToken.None);
        Assert.Empty(resizable);
    }

    [Theory]
    [InlineData(0.25, "25")]
    [InlineData(0.2, "20")]
    [InlineData(1.0, "100")]
    [InlineData(0.55, "55")]
    public void The_threshold_shows_in_percent(double alpv, string text) => Assert.Equal(text, SetupViewModel.PercentOf(alpv));

    [Fact]
    public async Task The_threshold_slider_goes_from_20_to_100_percent_and_saves_ALPVs_fraction()
    {
        var (page, _, _) = await Samples.OpenAsync();
        using var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
        await setup.LoadAsync();

        Assert.Equal("25", setup.ThresholdText); // ALPV keeps 0.25
        Assert.Equal((20, 100, 5), (SetupViewModel.MinThreshold, SetupViewModel.MaxThreshold, SetupViewModel.ThresholdStep));
        Assert.Null(setup.ToRequest().Setup.Recognition);

        setup.ThresholdText = "20";
        Assert.Equal(0.2, setup.ToRequest().Setup.Recognition!.ConfidenceThreshold);
        setup.ThresholdText = "100 %";
        Assert.Equal(1.0, setup.ToRequest().Setup.Recognition!.ConfidenceThreshold);
        Assert.Null(setup.ErrorOf(nameof(SetupViewModel.ThresholdText)));

        foreach (var wrong in new[] { "15", "105", "abc", string.Empty })
        {
            setup.ThresholdText = wrong;
            Assert.Equal(PageTexts.ThresholdRange, setup.ErrorOf(nameof(SetupViewModel.ThresholdText)));
            Assert.False(setup.SaveCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task A_camera_name_is_required_short_and_unique_and_saved_before_the_setup()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        ctx.Answers[LicensePlatesMethods.RenameCamera] = _ => Samples.Serialize(new RenameCameraReply());
        Assert.Equal("Garage Nord entry", page.Cameras.Rows[0].Camera); // the camera's own name
        Assert.Equal("sued-entry", page.Cameras.AllRows.Single(r => r.DeviceId == Samples.CameraId(3)).Camera); // none: the device's name
        using var setup = new SetupViewModel(page, page.Cameras.AllRows.Single(r => r.DeviceId == Samples.CameraId(1)));
        await setup.LoadAsync();
        Assert.Equal("Garage Nord entry", setup.Name);

        setup.Name = "  ";
        Assert.Equal(PageTexts.EnterName, setup.ErrorOf(nameof(SetupViewModel.Name)));
        setup.Name = new string('x', 65);
        Assert.Equal(PageTexts.NameTooLong, setup.ErrorOf(nameof(SetupViewModel.Name)));
        setup.Name = "GARAGE NORD EXIT"; // another camera's, in any case
        Assert.Equal(LicensePlateTexts.CameraNameTaken, setup.ErrorOf(nameof(SetupViewModel.Name)));
        Assert.False(setup.SaveCommand.CanExecute(null));

        setup.Name = "Main gate";
        Assert.Null(setup.ErrorOf(nameof(SetupViewModel.Name)));
        await setup.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new RenameCameraRequest(Samples.CameraId(1), "Main gate"), ctx.LastPayload<RenameCameraRequest>(LicensePlatesMethods.RenameCamera));
        var methods = ctx.Calls.Select(c => c.Method).Where(m => m is LicensePlatesMethods.RenameCamera or LicensePlatesMethods.SaveSetup).ToList();
        Assert.Equal([LicensePlatesMethods.RenameCamera, LicensePlatesMethods.SaveSetup], methods);
    }

    [Fact]
    public async Task A_refused_name_shows_below_the_field_and_the_setup_is_not_saved()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        ctx.Answers[LicensePlatesMethods.RenameCamera] = _ => Samples.Serialize(new RenameCameraReply(LicensePlateTexts.CameraNameTaken));
        using var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
        await setup.LoadAsync();

        setup.Name = "Taken meanwhile";
        await setup.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LicensePlateTexts.CameraNameTaken, setup.ErrorOf(nameof(SetupViewModel.Name)));
        Assert.Equal(0, ctx.CallsOf(LicensePlatesMethods.SaveSetup));
    }

    [Fact]
    public async Task The_log_shows_the_default_columns_keeps_the_choice_and_exports_in_its_order()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var columns = page.Log.Columns;
        Assert.Equal(LogColumns.Defaults, columns.VisibleKeys);
        Assert.Equal(["time", "camera", "picture", "plate", "name", "tags", "result", "direction", "confidence"], LogColumns.Defaults);
        Assert.Equal(LogColumns.All.Count, columns.Choosable.Count());

        columns.Find(LogColumns.State)!.IsVisible = true;
        columns.Find(LogColumns.Picture)!.IsVisible = false;
        columns.Capture([(LogColumns.Confidence, 0, 125), (LogColumns.Time, 1, 160)]);
        Assert.Equal(["confidence", "time", "camera", "plate", "name", "tags", "result", "direction", "state"], columns.VisibleKeys);

        // Kept on this computer: the next page opens with the same columns.
        var saved = ctx.GetLocalSetting(LogViewModel.ColumnsSetting);
        Assert.NotNull(saved);
        var (again, _, _) = await Samples.OpenAsync(ctx);
        Assert.Equal(columns.VisibleKeys, again.Log.Columns.VisibleKeys);

        await again.Log.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(columns.VisibleKeys, ctx.LastPayload<LogExportRequest>(LicensePlatesMethods.ExportLog).Columns);

        Assert.Empty(LogViewModel.LoadColumns("not json")); // a broken setting: the defaults
    }

    [Fact]
    public async Task The_log_grid_follows_the_chosen_columns_and_the_time_has_the_date()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var shown = await Headless.Session.Dispatch(() =>
        {
            var view = new LogView { DataContext = page.Log };
            var window = Headless.Host(view);
            Headless.Pump();
            page.Log.Columns.Find(LogColumns.VehicleMake)!.IsVisible = true;
            page.Log.Columns.Find(LogColumns.Tags)!.IsVisible = false;
            Headless.Pump();
            var keys = view.Grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).Select(c => (string)c.Tag!).ToList();
            var chooser = view.GetLogicalDescendants().OfType<ColumnChooserButton>().Single();
            var choosable = chooser.Columns!.Count();
            window.Close();
            return (keys, choosable);
        }, CancellationToken.None);

        Assert.Contains(LogColumns.VehicleMake, shown.keys);
        Assert.DoesNotContain(LogColumns.Tags, shown.keys);
        Assert.Equal(LogColumns.All.Count, shown.choosable);
        Assert.Equal("27 Nov 2026 14:04:14", page.Log.Rows[0].TimeText); // today too
        Assert.Equal(LogColumns.All.Count, await Headless.Session.Dispatch(() => new LogView().Grid.Columns.Count(c => c.Tag is string), CancellationToken.None));
    }
}
