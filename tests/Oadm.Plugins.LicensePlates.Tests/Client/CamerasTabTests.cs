using Avalonia;

using Oadm.Sdk.Client.Controls;
using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>The Cameras tab, Set up, Copy settings, Settings and Stats against the sample server; no Avalonia.</summary>
public sealed class CamerasTabTests
{
    private static readonly string CamerasRight = PermissionKeys.Of(LicensePlatesInfo.PluginId, LicensePlatePermissions.Cameras);

    [Fact]
    public async Task The_header_says_how_many_cameras_are_connected()
    {
        var (page, _, _) = await Samples.OpenAsync();
        Assert.Equal("2 of 11 cameras connected", page.ConnectedText);
        Assert.True(page.SomeNotConnected);
        Assert.False(page.AllConnected);
    }

    [Fact]
    public async Task Every_status_has_its_chip_and_the_image_settings_their_check_or_triangle()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var rows = page.Cameras.Rows;
        Assert.Equal(11, rows.Count);

        var connected = rows[0];
        Assert.Equal("Garage Nord entry", connected.Camera); // its own name in the plugin
        Assert.True(connected.IsOk);
        Assert.Equal(LicensePlateTexts.Connected, connected.StatusText);
        Assert.Equal(["Garage Nord", "Staff", "Visitors"], connected.AllowedTags.Select(t => t.Name));
        Assert.True(connected.ImageSettingsOk);
        Assert.Equal(PageTexts.ImageSettingsMatch, connected.ImageSettingsTitle);

        var differs = rows[1];
        Assert.True(differs.ImageSettingsDiffer);
        Assert.Equal(PageTexts.ImageSettingsDiffer, differs.ImageSettingsTitle);
        Assert.Equal(["WDR", "Contrast", "Local contrast", "Tone mapping", "Max gain", "Shutter speed"], differs.ImageValues.Select(v => v.Name));
        Assert.Equal(new ImageValueRow("Shutter speed", "1/60", "1/100", false), differs.ImageValues[^1]);
        Assert.True(differs.ImageValues[0].Matches);

        Assert.Equal("Connection lost since 13:58", rows[2].StatusText);
        Assert.True(rows[2].IsWarning);
        Assert.True(rows[3].IsWarning); // events off
        Assert.Equal(LicensePlateTexts.EventsOff, rows[3].StatusText);
        Assert.True(rows[4].IsError); // ALPV stopped
        Assert.True(rows[5].IsError);
        Assert.False(rows[5].HasImageSettings); // not read: no icon
        Assert.Equal(LicensePlateTexts.AlpvNeedsVersion3, rows[7].StatusText);
        Assert.Equal(LicensePlateTexts.OtherSensor, rows[8].StatusText);
        Assert.True(rows[9].IsConnecting);
        Assert.Equal("Site offline: Site B", rows[10].StatusText);
        Assert.True(rows[10].IsWarning);
        Assert.Equal("Site B", rows[10].Site);
    }

    [Fact]
    public async Task The_toolbar_follows_the_selection_and_the_rights()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var cameras = page.Cameras;
        Assert.True(cameras.AddCamerasCommand.CanExecute(null));
        Assert.False(cameras.SetUpCommand.CanExecute(null));
        Assert.False(cameras.RemoveCommand.CanExecute(null));
        Assert.True(cameras.SettingsCommand.CanExecute(null)); // an administrator

        cameras.SetSelection([cameras.Rows[0]]);
        Assert.True(cameras.SetUpCommand.CanExecute(null));
        Assert.True(cameras.CopySettingsCommand.CanExecute(null));
        cameras.SetSelection([cameras.Rows[0], cameras.Rows[1]]);
        Assert.False(cameras.SetUpCommand.CanExecute(null)); // one camera at a time
        Assert.False(cameras.CopySettingsCommand.CanExecute(null));
        Assert.True(cameras.AllowedTagsCommand.CanExecute(null));
        Assert.True(cameras.UseRecommendedCommand.CanExecute(null));
        Assert.True(cameras.RemoveCommand.CanExecute(null));

        ctx.Denied.Add(CamerasRight);
        ctx.Admin = false;
        ctx.RaisePermissionsChanged();
        Assert.False(cameras.MayChange);
        Assert.False(cameras.IsAdministrator);
        Assert.False(cameras.AddCamerasCommand.CanExecute(null));
        Assert.False(cameras.RemoveCommand.CanExecute(null));
        Assert.False(cameras.SettingsCommand.CanExecute(null));
        Assert.True(cameras.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Add_cameras_offers_only_cameras_not_in_the_plugin_yet()
    {
        var (page, ctx, dialogs) = await Samples.OpenAsync();
        dialogs.PickedCameras = [Samples.CameraId(12)];

        await page.Cameras.AddCamerasCommand.ExecuteAsync(null);
        var offered = Assert.IsAssignableFrom<IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo>>(dialogs.Shown.Single());
        Assert.Equal([Samples.CameraId(12)], offered.Select(d => d.Id));
        Assert.Equal([Samples.CameraId(12)], ctx.LastPayload<DevicesRequest>(LicensePlatesMethods.AddCameras).DeviceIds);
    }

    [Fact]
    public async Task Use_recommended_image_settings_asks_with_the_count_then_starts_a_task_each()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var cameras = page.Cameras;
        cameras.SetSelection(cameras.Rows.Take(3));

        await cameras.UseRecommendedCommand.ExecuteAsync(null);
        Assert.Equal("Use the image settings ALPV recommends on 3 cameras? One task per camera.", ctx.Confirmations.Single().Message);
        Assert.Equal(3, ctx.LastPayload<DevicesRequest>(LicensePlatesMethods.UseRecommendedImageSettings).DeviceIds.Count);

        ctx.ConfirmAnswer = false;
        await cameras.UseRecommendedCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(LicensePlatesMethods.UseRecommendedImageSettings));
    }

    [Fact]
    public async Task Remove_asks_in_red_and_nothing_on_the_camera_changes()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var cameras = page.Cameras;
        cameras.SetSelection([cameras.Rows[10]]);

        await cameras.RemoveCommand.ExecuteAsync(null);
        var (_, message, confirm) = ctx.Confirmations.Single();
        Assert.Equal("Remove 1 camera from License Plate Center? Nothing on the cameras changes.", message);
        Assert.Equal("Remove", confirm);
        Assert.Equal(10, cameras.Rows.Count);
        Assert.Empty(cameras.Selected);
        Assert.Equal("2 of 10 cameras connected", page.ConnectedText);
    }

    [Fact]
    public async Task Allowed_tags_sends_what_was_clicked_for_the_selected_cameras()
    {
        var (page, ctx, dialogs) = await Samples.OpenAsync();
        var cameras = page.Cameras;
        cameras.SetSelection([cameras.Rows[0], cameras.Rows[1]]);
        dialogs.Handle = dialog =>
        {
            var tags = (TagsDialogViewModel)dialog;
            Assert.Equal(PageTexts.AllowedTags, tags.Title);
            Assert.Null(tags.Rows.Single(r => r.Name == "Visitors").IsChecked); // one of two cameras
            tags.Rows.Single(r => r.Name == "Delivery").ToggleCommand.Execute(null);
            tags.OkCommand.Execute(null);
            return Task.FromResult(true);
        };

        await cameras.AllowedTagsCommand.ExecuteAsync(null);
        var request = ctx.LastPayload<AllowedTagsRequest>(LicensePlatesMethods.SetAllowedTags);
        Assert.Equal([Samples.Delivery], request.Add);
        Assert.Empty(request.Remove!);
        Assert.Null(request.Set);
    }

    [Fact]
    public async Task Set_up_reads_the_camera_and_saves_only_what_changed()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        using var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
        await setup.LoadAsync();

        Assert.Equal((Samples.CameraId(1), 1), ctx.Video.Started.Single()); // the first sensor
        Assert.Equal(2, setup.Areas.Count);
        Assert.True(setup.Areas[0].IsSelected);
        Assert.Equal(180, setup.Areas[0].Angle);
        Assert.Equal(new Point(0.12, 0.42), setup.Areas[0].Points[0]);
        Assert.Equal(5, setup.Areas[1].Points.Count);
        Assert.True(setup.HasRegion && setup.HasCameraLocation && setup.HasPlateWidth && setup.HasCharacters && setup.HasThreshold);

        var unchanged = setup.ToRequest();
        Assert.Null(unchanged.Setup.Areas);
        Assert.Null(unchanged.Setup.Recognition);
        Assert.Null(unchanged.AllowedTags);

        setup.Apply(new AreaChange(1, [new Point(0.1, 0.4), new Point(0.6, 0.4), new Point(0.6, 0.9)], null));
        setup.Apply(new AreaChange(2, null, 90));
        setup.PlateWidthMax = 260;
        var changed = setup.ToRequest();
        Assert.Equal([new AreaPoint(100, 400), new AreaPoint(600, 400), new AreaPoint(600, 900)], changed.Setup.Areas![0].Corners);
        Assert.Equal(90_000, changed.Setup.Areas[1].Angle);
        Assert.Equal(new RecognitionSettings { PlateWidthMax = 260 }, changed.Setup.Recognition);

        setup.RemoveArea2Command.Execute(null);
        Assert.False(setup.HasArea2);
        Assert.True(setup.CanAddArea2);
        Assert.Equal([1], setup.ToRequest().Setup.Areas!.Select(a => a.Id)); // area 2 missing: turned off

        var closed = false;
        setup.CloseRequested += (_, saved) => closed = saved;
        await setup.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(LicensePlatesMethods.SaveSetup));
        Assert.True(closed); // saved: the dialog closes
        Assert.Empty(ctx.OpenedPages); // no link to ALPV's page (decision 2026-11-28)
    }

    [Fact]
    public async Task An_area_keeps_3_to_8_corners_and_a_range_shows_its_error_below_the_field()
    {
        var (page, _, _) = await Samples.OpenAsync();
        using var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
        await setup.LoadAsync();

        setup.Apply(new AreaChange(1, [new Point(0.1, 0.1), new Point(0.2, 0.2)], null));
        Assert.Equal(4, setup.Areas[0].Points.Count); // two corners are no area

        setup.CharactersMin = 9;
        setup.CharactersMax = 5;
        Assert.Equal("Enter at least the smallest value.", setup.ErrorOf(nameof(SetupViewModel.CharactersMax)));
        Assert.False(setup.SaveCommand.CanExecute(null));
        setup.CharactersMax = 10;
        Assert.True(setup.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_rotated_picture_keeps_its_areas_for_the_ALPV_page()
    {
        var ctx = Samples.Context();
        ctx.Answers[LicensePlatesMethods.ReadSetup] = _ => Samples.Serialize(Samples.Setup(rotated: true));
        var (page, _, _) = await Samples.OpenAsync(ctx);
        using var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
        await setup.LoadAsync();

        Assert.True(setup.Rotated);
        Assert.False(setup.MayEditAreas);
        setup.Apply(new AreaChange(1, [new Point(0, 0), new Point(1, 0), new Point(1, 1)], null));
        Assert.Equal(4, setup.Areas[0].Points.Count);
        Assert.Null(setup.ToRequest().Setup.Areas);
    }

    [Fact]
    public async Task Copy_settings_needs_targets_and_copies_areas_only_when_asked()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var copy = new CopySettingsViewModel(page, page.Cameras.Rows[0]);
        Assert.False(copy.CopyCommand.CanExecute(null));
        Assert.Equal(PageTexts.ChooseTargetsFirst, copy.CopyBlockedReason);
        Assert.Null(copy.TargetsError); // untouched

        copy.SetTargets([Samples.CameraId(1), Samples.CameraId(2), Samples.CameraId(3)]);
        Assert.Equal("2 cameras", copy.TargetsText); // never onto itself
        await copy.CopyCommand.ExecuteAsync(null);
        var request = ctx.LastPayload<CopySettingsRequest>(LicensePlatesMethods.CopySettings);
        Assert.Equal(Samples.CameraId(1), request.SourceId);
        Assert.False(request.Areas);
    }

    [Fact]
    public async Task Settings_keep_Save_off_while_the_folder_has_a_problem_and_show_the_servers_refusal_below_it()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var settings = new SettingsViewModel(page);
        await settings.LoadAsync();
        Assert.Equal(@"D:\Plates", settings.Folder);
        Assert.Equal(1, settings.SelectedDeviation.Value);
        Assert.Equal(3, settings.WaitSeconds);
        Assert.True(settings.SaveCommand.CanExecute(null));

        settings.FolderProblem = Oadm.Sdk.Plugins.ServerFolderTexts.SystemFolder;
        Assert.False(settings.SaveCommand.CanExecute(null));
        Assert.Equal(Oadm.Sdk.Plugins.ServerFolderTexts.SystemFolder, settings.SaveBlockedReason);
        settings.FolderProblem = null;

        settings.WaitSeconds = 12;
        Assert.Equal("Enter 1 to 10 seconds.", settings.ErrorOf(nameof(SettingsViewModel.WaitSeconds)));
        Assert.False(settings.SaveCommand.CanExecute(null));
        settings.WaitSeconds = 5;

        settings.Folder = @"C:\Windows\Plates";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Oadm.Sdk.Plugins.ServerFolderTexts.SystemFolder, settings.ErrorOf(nameof(SettingsViewModel.Folder)));
        Assert.Equal(5, ctx.LastPayload<LicensePlateSettings>(LicensePlatesMethods.SaveSettings).WaitSeconds);
    }
    [Fact]
    public async Task Stats_show_the_cards_a_bar_per_camera_and_the_lines_of_total_and_the_cameras()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        page.Show(page.StatsTab);
        await page.Stats.LoadTask;
        var stats = page.Stats;

        Assert.Equal(["96", "1,812", "11.9k", "49.6k"], new[] { stats.LastHourText, stats.Last24HoursText, stats.Last7DaysText, stats.TotalText });
        Assert.Equal("Allowed 1,204 · Not allowed 86 · Unknown plate 211 · Average confidence 86 %", stats.ResultsText);

        // One bar per camera by name: the average, the Log's colours below 75 and below 60, the reads on hover.
        Assert.Equal(["nord-entry", "nord-exit", "sued-entry", "sued-exit"], stats.PlateBars.Select(b => b.Label));
        Assert.Equal([91.4, 89.9, 78.2, 58.5], stats.PlateBars.Select(b => b.Value));
        Assert.Equal([ChartColor.Primary, ChartColor.Primary, ChartColor.Primary, ChartColor.Error], stats.PlateBars.Select(b => b.Color));
        Assert.EndsWith(" reads", stats.PlateBars[0].Details![0], StringComparison.Ordinal);

        // sued-exit sends no vehicle confidence: no bar, not 0.
        Assert.True(stats.HasVehicleConfidence);
        Assert.True(double.IsNaN(stats.VehicleBars[3].Value));
        Assert.Equal(87.9, stats.VehicleBars[0].Value);

        // Total first (strong), then the cameras busiest first in the palette; all shown with 4 cameras.
        Assert.Equal(["Total", "nord-entry", "nord-exit", "sued-entry", "sued-exit"], stats.Legend.Select(l => l.Name));
        Assert.Equal(ChartColor.Strong, stats.Legend[0].Color);
        Assert.Equal(ChartColor.Palette1, stats.Legend[1].Color);
        Assert.All(stats.Legend, l => Assert.True(l.IsShown));
        Assert.Equal(5, stats.ReadSeries.Count);
        Assert.Equal(15, stats.ReadSeries[0].Values.Count);
        Assert.Equal(TimeSpan.FromHours(1), stats.ChartStep);

        // A click in the legend hides the line in both charts.
        stats.Legend[2].IsShown = false;
        Assert.Equal(["Total", "nord-entry", "sued-entry", "sued-exit"], stats.ReadSeries.Select(s => s.Name));
        Assert.Equal(4, stats.ConfidenceSeries.Count);

        stats.SelectedRange = StatsViewModel.RangeChoices[3];
        await stats.LoadTask;
        var request = ctx.LastPayload<StatsRequest>(LicensePlatesMethods.Stats);
        Assert.Equal(StatsStep.Day, request.Step);
        Assert.Equal(new DateTimeOffset(Samples.Now.Date.AddDays(-29), Samples.Now.Offset), request.From);
        Assert.Equal("Test/Vienna", request.TimeZone);
        Assert.Null(request.CameraIds);
        Assert.False(stats.Legend[2].IsShown); // the choice stays across a reload
    }

    [Fact]
    public async Task Stats_show_total_and_the_10_busiest_cameras_at_first_the_others_in_the_legend()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        ctx.Answers[LicensePlatesMethods.Stats] = _ => Samples.Serialize(Samples.Stats(cameras: 55));
        page.Show(page.StatsTab);
        await page.Stats.LoadTask;
        var stats = page.Stats;

        Assert.Equal(56, stats.Legend.Count);
        Assert.Equal(11, stats.Legend.Count(l => l.IsShown));
        Assert.True(stats.Legend.Take(11).All(l => l.IsShown));
        Assert.All(stats.Legend.Skip(1), l => Assert.NotNull(l.Detail)); // the reads of each camera
        Assert.Equal(11, stats.ReadSeries.Count);
        Assert.Equal(55, stats.PlateBars.Count);

        stats.Legend[30].IsShown = true;
        Assert.Equal(12, stats.ReadSeries.Count);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(9_999, "9,999")]
    [InlineData(49_631, "49.6k")]
    [InlineData(123_456, "123k")]
    [InlineData(4_412_000, "4.4M")]
    public void Stats_cards_use_compact_numbers(long value, string text) => Assert.Equal(text, StatsViewModel.Compact(value));
}
