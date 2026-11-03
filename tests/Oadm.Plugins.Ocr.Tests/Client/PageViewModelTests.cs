using Avalonia;
using Avalonia.Media.Imaging;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>The page against the fake server (docs/spec/ocr.md "Page"): list, form, areas, validation, New, Delete, Save, live readings.</summary>
public sealed class PageViewModelTests
{
    private static OcrViewModel Page(FakePageContext ctx) => new(ctx) { DecodePicture = _ => Task.FromResult<Bitmap?>(null), ReconnectDelay = TimeSpan.FromMilliseconds(10) };

    private static async Task<OcrViewModel> LoadedAsync(FakePageContext ctx)
    {
        var vm = Page(ctx);
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task Load_lists_the_readers_and_opens_the_first_with_its_reading()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);

        Assert.Equal(["r1", "r3", "r2"], vm.Readers.Select(r => r.Id));
        Assert.Equal("Watching · 1 fps", vm.Readers[0].ModeText);
        Assert.True(vm.Readers[0].IsWatching);
        Assert.Equal("AXIS Q1798-LE · 10.0.0.48", vm.Readers[0].Camera);
        Assert.Equal("MSKU 123456 7", vm.Readers[0].LastText);
        Assert.Equal(OcrTexts.NotReachable, vm.Readers[1].ModeText);
        Assert.True(vm.Readers[1].IsProblem);
        Assert.Equal(OcrTexts.OnDemand, vm.Readers[2].ModeText);
        Assert.Equal(OcrTexts.NoReading, vm.Readers[2].LastText);
        Assert.Equal("Runs on: NVIDIA GeForce RTX 5070 Ti", vm.RunsOnText);
        Assert.True(vm.CanSetGpu);

        var editor = vm.Editor!;
        Assert.Same(vm.Readers[0], vm.SelectedReader);
        Assert.Equal("Container gate", editor.Name);
        Assert.False(editor.IsDirty);
        Assert.Equal(3, editor.Areas.Count);
        Assert.Equal([1, 2, 4, 8, 15], editor.FpsOptions.Select(o => o.Value));
        Assert.Equal(2, editor.VideoSources.Count);
        Assert.Equal(new Resolution(1920, 1080), editor.SelectedResolution);
        Assert.Equal("Watching · 1 fps", editor.ModeChipText);
        Assert.True(editor.IsModeOk);

        // The results: a match, Any text, a format without a match whose lines were too small.
        Assert.Equal(3, editor.Results.Count);
        Assert.True(editor.Results[0].IsMatch);
        Assert.Equal("96 %", editor.Results[0].ConfidenceText);
        Assert.Equal(OcrTexts.NoFormat, editor.Results[1].MatchText);
        Assert.True(editor.Results[2].IsNoMatch);
        Assert.Equal("G3", editor.Results[2].Text);
        Assert.True(editor.Results[2].TooSmall);
        Assert.Equal(3, editor.Lines.Count);
        Assert.StartsWith("Last reading ", editor.PictureText, StringComparison.Ordinal);
        Assert.Equal("{\"readerId\":\"r1\"}", ctx.LastPayloadOf(OcrMethods.GetReading));
    }

    [Fact]
    public async Task An_older_server_without_the_page_additions_still_works()
    {
        var ctx = Samples.Context();
        ctx.Answers.Remove(OcrMethods.GetEngine);
        ctx.Answers.Remove(OcrMethods.GetCameraOptions);
        ctx.Answers[OcrMethods.ListReaders] = _ => OcrJson.Serialize(new ReaderList
        {
            Readers = [new ReaderSummary { Id = "r1", Name = "Container gate", DeviceId = Samples.Gate.Id, Mode = ReaderMode.Watching }],
        });
        using var vm = await LoadedAsync(ctx);

        Assert.Equal(string.Empty, vm.RunsOnText);
        Assert.Equal("Watching · 1 fps", vm.Readers[0].ModeText);
        Assert.Equal([1, 2, 4], vm.Editor!.FpsOptions.Select(o => o.Value));
        Assert.Single(vm.Editor.VideoSources);
        Assert.Contains(new Resolution(1920, 1080), vm.Editor.Resolutions);
        Assert.False(vm.Editor.IsDirty);
    }

    [Fact]
    public async Task The_search_filters_by_name_and_camera()
    {
        using var vm = await LoadedAsync(Samples.Context());

        vm.SearchText = "dock";
        Assert.Equal(["r3"], vm.Readers.Select(r => r.Id));
        vm.SearchText = "10.0.0.202";
        Assert.Equal(["r2"], vm.Readers.Select(r => r.Id));
        vm.SearchText = "nothing";
        Assert.Empty(vm.Readers);
        Assert.True(vm.ShowNoMatch);
        Assert.False(vm.ShowEmpty);
        vm.SearchText = string.Empty;
        Assert.Equal(3, vm.Readers.Count);
    }

    [Fact]
    public async Task An_empty_server_shows_the_empty_page()
    {
        var ctx = Samples.Context();
        ctx.Answers[OcrMethods.ListReaders] = _ => "{}";
        using var vm = await LoadedAsync(ctx);

        Assert.True(vm.ShowEmpty);
        Assert.Null(vm.Editor);
        Assert.Equal("Create a reader to read text from a camera.", OcrViewModel.EmptyText);
    }

    [Theory]
    [InlineData("AAAU ###### #", null)]
    [InlineData("####.#", null)]
    [InlineData("*", null)]
    [InlineData("", "Enter a format.")]
    [InlineData("   ", "Enter a format.")]
    [InlineData("1234", "A format needs at least one #, A or *.")]
    [InlineData("#################################################################", "A format has at most 64 characters.")]
    [InlineData("##\t#", "Use letters, digits and signs only.")]
    public void The_format_is_checked_with_the_servers_rule(string format, string? error) => Assert.Equal(error, ReaderRules.FormatProblem(format));

    [Fact]
    public async Task A_wrong_format_shows_below_its_field_and_keeps_Save_off()
    {
        using var vm = await LoadedAsync(Samples.Context());
        var editor = vm.Editor!;
        var area = editor.Areas[0];

        Assert.Null(area.ErrorOf(nameof(AreaViewModel.FormatText)));
        area.FormatText = "1234";

        Assert.Equal("A format needs at least one #, A or *.", area.ErrorOf(nameof(AreaViewModel.FormatText)));
        Assert.True(editor.IsDirty);
        Assert.Equal(OcrTexts.NotSaved, editor.SaveStatus);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal("A format needs at least one #, A or *.", editor.SaveBlockedReason);

        area.FormatText = "AAAU ###### #";
        Assert.Null(area.ErrorOf(nameof(AreaViewModel.FormatText)));
        Assert.False(editor.IsDirty); // back to the saved state
        Assert.Null(editor.SaveStatus);

        // Any text needs no format.
        area.FormatText = string.Empty;
        Assert.False(editor.SaveCommand.CanExecute(null));
        area.IsAnyText = true;
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Area_and_reader_names_are_unique()
    {
        using var vm = await LoadedAsync(Samples.Context());
        var editor = vm.Editor!;

        editor.Areas[1].Name = "lane";
        Assert.Equal(ReaderRules.AreaNameTaken("lane"), editor.Areas[1].ErrorOf(nameof(AreaViewModel.Name)));
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Areas[1].Name = "Type";
        Assert.Null(editor.Areas[1].ErrorOf(nameof(AreaViewModel.Name)));

        editor.Name = "Water meter";
        Assert.Equal(ReaderRules.NameTaken("Water meter"), editor.ErrorOf(nameof(ReaderEditorViewModel.Name)));
        editor.Name = " ";
        Assert.Equal(ReaderRules.NameMissing, editor.ErrorOf(nameof(ReaderEditorViewModel.Name)));
    }

    [Fact]
    public async Task Changing_the_mode_shows_the_frame_rate_and_needs_a_save_before_Read_now()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
        var editor = vm.Editor!;

        Assert.True(editor.IsOnDemand);
        Assert.False(editor.IsWatching);
        Assert.Equal(OcrTexts.OnDemand, editor.ModeChipText);
        Assert.Equal(OcrTexts.ReadNowHint, editor.ResultsEmptyText);
        Assert.Equal(OcrTexts.NoReading, editor.PictureText);
        Assert.True(editor.ReadNowCommand.CanExecute(null));

        editor.IsWatching = true;
        editor.FpsOptions.Single(o => o.Value == 4).IsChecked = true;
        Assert.Equal(4, editor.Fps);
        Assert.Equal("Watching · 4 fps", editor.ModeChipText);
        Assert.True(editor.IsDirty);
        Assert.False(editor.ReadNowCommand.CanExecute(null));
        Assert.Equal(OcrTexts.SaveFirst, editor.ReadNowBlockedReason);

        await editor.SaveCommand.ExecuteAsync(null);
        var saved = OcrJson.Deserialize<SaveReaderRequest>(ctx.LastPayloadOf(OcrMethods.SaveReader)).Reader;
        Assert.Equal(ReaderMode.Watching, saved.Mode);
        Assert.Equal(4, saved.Fps);
        Assert.Equal("r2", saved.Id);
        Assert.Equal(2, saved.Areas.Count);
        Assert.Equal("####.#", saved.Areas[0].Expected.Format);
        Assert.Equal(OcrTexts.Saved, editor.SaveStatus);
        Assert.True(editor.ReadNowCommand.CanExecute(null));
        Assert.Equal("Watching · 4 fps", vm.Readers.Single(r => r.Id == "r2").ModeText);
    }

    [Fact]
    public async Task Read_now_shows_the_new_reading_and_the_servers_refusal()
    {
        var ctx = Samples.Context();
        ctx.Answers[OcrMethods.ReadNow] = _ => Samples.ReplyJson(Samples.GateReading("TGHU 765432 1"), Samples.GateExtras, null);
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;

        await editor.ReadNowCommand.ExecuteAsync(null);
        Assert.Equal("TGHU 765432 1", editor.Results[0].Text);
        Assert.Equal("{\"readerId\":\"r1\"}", ctx.LastPayloadOf(OcrMethods.ReadNow));

        ctx.Answers[OcrMethods.ReadNow] = _ => throw new InvalidOperationException(OcrInfo.AlreadyReading);
        await editor.ReadNowCommand.ExecuteAsync(null);
        Assert.Contains(ctx.Messages, m => m.Message.Contains(OcrInfo.AlreadyReading, StringComparison.Ordinal));
        Assert.False(editor.IsReading);
    }

    [Fact]
    public async Task New_starts_from_the_camera_and_Save_adds_the_reader()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        Guid? offered = Guid.NewGuid();
        vm.PickCamera = selected =>
        {
            offered = selected;
            return Task.FromResult<Guid?>(null);
        };

        // Cancelled: nothing changes.
        await vm.NewCommand.ExecuteAsync(null);
        Assert.Null(offered);
        Assert.Equal("r1", vm.Editor!.Id);

        vm.PickCamera = _ => Task.FromResult<Guid?>(Samples.Plant.Id);
        await vm.NewCommand.ExecuteAsync(null);
        var editor = vm.Editor!;
        Assert.True(editor.IsNew);
        Assert.Equal("Reader 4", editor.Name);
        Assert.Equal("AXIS M3215-LVE · 10.0.0.202", editor.CameraText);
        Assert.Null(vm.SelectedReader);
        Assert.Equal(OcrTexts.NotSaved, editor.SaveStatus);
        Assert.False(editor.ReadNowCommand.CanExecute(null));
        Assert.False(editor.HasAreas);
        Assert.Equal(OcrTexts.NoAreas, ReaderEditorViewModel.NoAreasText);

        var area = editor.AddArea(new RelativeBox(0.1, 0.2, 0.3, 0.1))!;
        Assert.Equal("Area 1", area.Name);
        Assert.Same(area, editor.SelectedArea);
        Assert.True(area.IsSelected);
        area.IsFormat = true;
        area.FormatText = "#####";
        Assert.True(editor.SaveCommand.CanExecute(null));

        await editor.SaveCommand.ExecuteAsync(null);
        var saved = OcrJson.Deserialize<SaveReaderRequest>(ctx.LastPayloadOf(OcrMethods.SaveReader)).Reader;
        Assert.Equal(string.Empty, saved.Id);
        Assert.Equal(Samples.Plant.Id, saved.DeviceId);
        Assert.Equal(new RelativeBox(0.1, 0.2, 0.3, 0.1), saved.Areas.Single().Box);
        Assert.Equal(ExpectedTextKind.Format, saved.Areas[0].Expected.Kind);
        Assert.Equal("r9", editor.Id);
        Assert.False(editor.IsNew);
        Assert.Equal(4, vm.Readers.Count);
        Assert.Equal("r9", vm.SelectedReader!.Id);
        Assert.Equal(OcrTexts.Saved, editor.SaveStatus);
    }

    [Fact]
    public async Task At_most_eight_areas_are_drawn()
    {
        using var vm = await LoadedAsync(Samples.Context());
        var editor = vm.Editor!;
        editor.DrawAreaCommand.Execute(null);
        Assert.True(editor.IsDrawing);
        editor.StopDrawing();
        Assert.False(editor.IsDrawing);

        for (var i = editor.Areas.Count; i < OcrLimits.MaxAreas; i++)
        {
            Assert.NotNull(editor.AddArea(new RelativeBox(0.05 * i, 0.7, 0.04, 0.04)));
        }

        Assert.Equal("8 of 8", editor.AreaCountText);
        Assert.False(editor.CanAddArea);
        Assert.False(editor.DrawAreaCommand.CanExecute(null));
        Assert.Equal(ReaderRules.TooManyAreas, editor.DrawBlockedReason);
        Assert.Null(editor.AddArea(new RelativeBox(0.1, 0.1, 0.1, 0.1)));

        editor.RemoveAreaCommand.Execute(editor.Areas[^1]);
        Assert.True(editor.CanAddArea);
        editor.SelectAreaAt(new Point(0.5, 0.3));
        Assert.Equal("Container number", editor.SelectedArea!.Name);
    }

    [Fact]
    public async Task Delete_asks_first_and_shows_the_next_reader()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);

        ctx.ConfirmAnswer = false;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(OcrMethods.DeleteReader));
        Assert.Equal(("Delete reader", "Delete \"Container gate\"? Rules that use it stop reading.", "Delete"), ctx.Confirmations.Single());

        ctx.ConfirmAnswer = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("{\"readerId\":\"r1\"}", ctx.LastPayloadOf(OcrMethods.DeleteReader));
        Assert.Equal(["r3", "r2"], vm.Readers.Select(r => r.Id));
        Assert.Equal("r3", vm.Editor!.Id);
        Assert.Equal(OcrTexts.NotReachable, vm.Readers[0].ModeText);
        Assert.True(vm.Editor.IsModeError);
        Assert.Equal("Camera not reachable, trying again in 30 s", vm.Editor.ModeChipText);
    }

    [Fact]
    public async Task Unsaved_changes_are_discarded_only_after_asking()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        vm.Editor!.Name = "Gate 3";

        ctx.ConfirmAnswer = false;
        vm.SelectedReader = vm.Readers.Single(r => r.Id == "r2");
        await Eventually.TrueAsync(() => ctx.Confirmations.Count == 1, "the question");
        Assert.Equal("r1", vm.Editor.Id);
        Assert.Equal("r1", vm.SelectedReader!.Id);

        ctx.ConfirmAnswer = true;
        vm.SelectedReader = vm.Readers.Single(r => r.Id == "r2");
        await Eventually.TrueAsync(() => vm.Editor?.Id == "r2", "the other reader");
        Assert.False(vm.Editor!.IsDirty);
    }

    [Fact]
    public async Task Readings_arrive_live_and_only_the_newest_picture_is_fetched()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var before = ctx.CallsOf(OcrMethods.GetReading);

        // A fetch that is held: two more readings meanwhile give one more fetch, not two.
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = true;
        ctx.AsyncAnswers[OcrMethods.GetReading] = _ =>
        {
            if (held)
            {
                held = false;
                return gate.Task;
            }

            return Task.FromResult<string?>(Samples.ReplyJson(Samples.GateReading("TGHU 765432 1"), Samples.GateExtras, null));
        };
        var first = editor.RefreshReadingAsync(CancellationToken.None);
        await editor.RefreshReadingAsync(CancellationToken.None);
        await editor.RefreshReadingAsync(CancellationToken.None);
        Assert.Equal(before + 1, ctx.CallsOf(OcrMethods.GetReading));
        gate.SetResult(Samples.ReplyJson(Samples.GateReading("MSKU 000000 0"), Samples.GateExtras, null));
        await first;
        Assert.Equal(before + 2, ctx.CallsOf(OcrMethods.GetReading));
        Assert.Equal("TGHU 765432 1", editor.Results[0].Text);

        // The live event: the list's last text and the shown reader's picture.
        ctx.AsyncAnswers.Remove(OcrMethods.GetReading);
        vm.Activate();
        try
        {
            ctx.Push(OcrMethods.ReadingTopic, new ReadingEvent { Reading = Samples.GateReading("CMAU 111111 1") with { Time = Samples.Time.AddSeconds(1) } });
            await Eventually.TrueAsync(() => vm.Readers[0].LastText == "CMAU 111111 1", "the list's last text");
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.GetReading) >= before + 3, "the picture fetched");

            // A reading of another reader fetches nothing.
            var calls = ctx.CallsOf(OcrMethods.GetReading);
            ctx.Push(OcrMethods.ReadingTopic, new ReadingEvent { Reading = new Reading { ReaderId = "r2", Time = Samples.Time, Areas = [new AreaResult { Area = "Reading", Text = "0482.6" }] } });
            await Eventually.TrueAsync(() => vm.Readers.Single(r => r.Id == "r2").LastText == "0482.6", "the other row");
            Assert.Equal(calls, ctx.CallsOf(OcrMethods.GetReading));

            // The readers event: a deleted reader goes, the shown one stays.
            ctx.Push(OcrMethods.ReadersTopic, new ReaderList
            {
                Readers = [new ReaderSummary { Id = "r1", Name = "Container gate", DeviceId = Samples.Gate.Id, Mode = ReaderMode.Watching }],
            });
            await Eventually.TrueAsync(() => vm.Readers.Count == 1, "the deleted readers gone");
            Assert.Equal("r1", vm.Editor!.Id);
        }
        finally
        {
            vm.Deactivate();
        }
    }

    [Fact]
    public async Task A_refusal_of_the_watching_limit_shows_below_Mode_until_the_readers_change()
    {
        const string Refusal = "At most 4 readers can watch at once at the site Building B. Set another reader there to On demand first.";
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
        var editor = vm.Editor!;
        ctx.Answers[OcrMethods.SaveReader] = _ => OcrJson.Serialize(new SaveReaderReply { Errors = [new FieldError(OcrFields.Mode, Refusal)] });

        editor.IsWatching = true;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Refusal, editor.ModeError);
        Assert.Empty(ctx.Messages);
        Assert.True(editor.IsDirty);
        Assert.False(editor.SaveCommand.CanExecute(null));

        // Another reader set to On demand elsewhere: the readers event lifts it.
        vm.ApplyList(Samples.ListJson());
        Assert.Null(editor.ModeError);
        Assert.True(editor.SaveCommand.CanExecute(null));

        // A refusal that belongs to no field: the host's message.
        ctx.Answers[OcrMethods.SaveReader] = _ => throw new InvalidOperationException("The server is busy.");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("The server is busy.", ctx.Messages.Single().Message);
    }

    [Fact]
    public async Task Each_field_error_of_the_server_shows_below_its_field()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        Assert.True(editor.Areas.Count >= 2);
        editor.Areas[1].IsFormat = true;
        editor.Areas[1].FormatText = "####";
        ctx.Answers[OcrMethods.SaveReader] = _ => OcrJson.Serialize(new SaveReaderReply
        {
            Errors =
            [
                new FieldError(OcrFields.Name, ReaderRules.NameTaken("Container gate")),
                new FieldError(OcrFields.Camera, "The camera is not in your devices."),
                new FieldError(OcrFields.VideoSource, ReaderRules.SourceMissing),
                new FieldError(OcrFields.Resolution, ReaderRules.ResolutionMissing),
                new FieldError(OcrFields.AreaName(0), ReaderRules.AreaOutside),
                new FieldError(OcrFields.AreaFormat(1), ReaderRules.FormatWithoutPlaceholder),
                new FieldError("unknown", "Something else."),
            ],
        });

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ReaderRules.NameTaken("Container gate"), editor.ErrorOf(nameof(ReaderEditorViewModel.Name)));
        Assert.Equal("The camera is not in your devices.", editor.CameraError);
        Assert.Equal(ReaderRules.SourceMissing, editor.ErrorOf(nameof(ReaderEditorViewModel.SelectedSource)));
        Assert.Equal(ReaderRules.ResolutionMissing, editor.ErrorOf(nameof(ReaderEditorViewModel.SelectedResolution)));
        Assert.Equal(ReaderRules.AreaOutside, editor.Areas[0].ErrorOf(nameof(AreaViewModel.Name)));
        Assert.Equal(ReaderRules.FormatWithoutPlaceholder, editor.Areas[1].ErrorOf(nameof(AreaViewModel.FormatText)));
        Assert.Equal("Something else.", ctx.Messages.Single().Message); // a key without a field: the host's message
        Assert.False(editor.SaveCommand.CanExecute(null));

        // An edit clears the server's error of its field.
        editor.Name = "Container gate 2";
        Assert.Null(editor.ErrorOf(nameof(ReaderEditorViewModel.Name)));
        editor.Areas[1].FormatText = "#####";
        Assert.Null(editor.Areas[1].ErrorOf(nameof(AreaViewModel.FormatText)));
    }

    [Fact]
    public void Field_keys_of_the_areas_are_parsed()
    {
        Assert.Equal("areas[3].format", OcrFields.AreaFormat(3));
        Assert.Equal((3, "format"), OcrFields.ParseArea("areas[3].format"));
        Assert.Equal((0, "name"), OcrFields.ParseArea(OcrFields.AreaName(0)));
        Assert.Null(OcrFields.ParseArea("areas"));
        Assert.Null(OcrFields.ParseArea("areas[x].name"));
        Assert.Null(OcrFields.ParseArea("name"));
    }

    [Fact]
    public void The_page_uses_the_servers_texts()
    {
        Assert.Equal(ReaderRules.TooSmall, ResultRow.TooSmallText);
        Assert.Equal(ReaderRules.MaxNameLength, AreaViewModel.MaxNameLength);
        Assert.Equal(ReaderRules.MaxFormatLength, AreaViewModel.MaxFormatLength);
    }

    [Fact]
    public async Task A_problem_shows_in_either_mode()
    {
        var ctx = Samples.Context();
        ctx.Answers[OcrMethods.ListReaders] = _ => OcrJson.Serialize(new
        {
            readers = new[] { new { id = "r2", name = "Water meter", deviceId = Samples.Plant.Id, mode = "OnDemand", problem = "The camera refuses connections." } },
        });
        using var vm = await LoadedAsync(ctx);

        Assert.Equal(OcrTexts.NotReachable, vm.Readers[0].ModeText);
        Assert.True(vm.Editor!.IsModeError);
        Assert.Equal("The camera refuses connections.", vm.Editor.ModeChipText);
    }

    [Fact]
    public async Task Use_the_graphics_card_is_saved_at_once()
    {
        var ctx = Samples.Context();
        ctx.Answers[OcrMethods.SetUseGpu] = json => OcrJson.Serialize(new EngineInfo
        {
            RunsOn = "CPU (8 cores)",
            GpuAvailable = true,
            UseGpu = OcrJson.Deserialize<UseGpuRequest>(json).UseGpu,
        });
        using var vm = await LoadedAsync(ctx);

        vm.UseGpu = false;
        await Eventually.TrueAsync(() => vm.RunsOnText == "Runs on: CPU (8 cores)", "the new engine");
        Assert.Equal("{\"useGpu\":false}", ctx.LastPayloadOf(OcrMethods.SetUseGpu));
        Assert.False(vm.UseGpu);
    }

    [Fact]
    public async Task Without_the_change_right_the_page_only_shows()
    {
        var ctx = Samples.Context();
        ctx.Denied.Add(OcrViewModel.ChangePermissionKey);
        using var vm = await LoadedAsync(ctx);

        Assert.Equal("oadm.ocr:change", OcrViewModel.ChangePermissionKey);
        Assert.False(vm.MayChange);
        Assert.False(vm.NewCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.CanSetGpu);
        Assert.False(vm.Editor!.MayChange);
        Assert.False(vm.Editor.ReadNowCommand.CanExecute(null));
        Assert.False(vm.Editor.DrawAreaCommand.CanExecute(null));
        vm.Editor.Name = "Gate 3";
        Assert.False(vm.Editor.SaveCommand.CanExecute(null));

        ctx.Denied.Clear();
        ctx.RaisePermissionsChanged();
        Assert.True(vm.MayChange);
        Assert.True(vm.NewCommand.CanExecute(null));
    }

    [Fact]
    public async Task Removed_cameras_are_named_as_removed()
    {
        var ctx = Samples.Context();
        ctx.DeviceList.Remove(Samples.Dock);
        using var vm = await LoadedAsync(ctx);

        Assert.StartsWith("Removed device ", vm.Readers.Single(r => r.Id == "r3").Camera, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Cameras, d => d.Address == "10.0.0.90"); // the picker offers cameras only
    }
}
