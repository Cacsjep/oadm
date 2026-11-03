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
        Assert.Equal(OcrTexts.OnAction, vm.Readers[2].ModeText);
        Assert.Equal(OcrTexts.NoReading, vm.Readers[2].LastText);
        Assert.Equal("Runs on: NVIDIA GeForce RTX 5070 Ti", vm.RunsOnText);
        Assert.True(vm.CanSetGpu);

        var editor = vm.Editor!;
        Assert.Same(vm.Readers[0], vm.SelectedReader);
        Assert.Equal("Container gate", editor.Name);
        Assert.False(editor.IsDirty);
        Assert.Equal(3, editor.Areas.Count);
        Assert.Equal([1, 2, 4, 8, 12, 15], editor.FpsChoices);
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
        Assert.Equal([1, 2, 4], vm.Editor!.FpsChoices);
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
    public async Task Changing_the_mode_shows_the_frame_rate_select_and_saves()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
        var editor = vm.Editor!;

        Assert.True(editor.IsOnAction);
        Assert.False(editor.IsWatching);
        Assert.Equal(OcrTexts.OnAction, editor.ModeChipText);
        Assert.Equal(OcrTexts.WaitingForPicture, editor.ResultsEmptyText);
        Assert.Equal(OcrTexts.WaitingForPicture, editor.PictureText);

        // Frames per second is one select of the server's choices.
        editor.IsWatching = true;
        Assert.Equal([1, 2, 4, 8, 12, 15], editor.FpsChoices);
        editor.Fps = 12;
        Assert.Equal("Watching · 12 fps", editor.ModeChipText);
        Assert.True(editor.IsDirty);

        await editor.SaveCommand.ExecuteAsync(null);
        var saved = OcrJson.Deserialize<SaveReaderRequest>(ctx.LastPayloadOf(OcrMethods.SaveReader)).Reader;
        Assert.Equal(ReaderMode.Watching, saved.Mode);
        Assert.Equal(12, saved.Fps);
        Assert.Equal("r2", saved.Id);
        Assert.Equal(2, saved.Areas.Count);
        Assert.Equal("####.#", saved.Areas[0].Expected.Format);
        Assert.Equal(OcrTexts.Saved, editor.SaveStatus);
        Assert.Equal("Watching · 12 fps", vm.Readers.Single(r => r.Id == "r2").ModeText);
    }

    [Fact]
    public async Task On_action_shows_its_short_message_once_per_reader_and_session()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!; // Container gate, watching

        editor.IsOnAction = true;
        var message = Assert.Single(ctx.Messages);
        Assert.Equal((OcrTexts.OnActionNoticeTitle, OcrTexts.OnActionNotice), message);
        Assert.Equal("Reads only on an action", message.Title);
        Assert.Equal("This reader reads only when an event rule runs the action Read text with it.", message.Message);

        // Back and forth: not again for this reader, also after opening it again.
        editor.IsWatching = true;
        editor.IsOnAction = true;
        Assert.Single(ctx.Messages);
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r1"), CancellationToken.None);
        vm.Editor!.IsOnAction = true;
        Assert.Single(ctx.Messages);

        // Another reader shows it once; opening an On action reader shows nothing.
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);
        vm.Editor!.IsOnAction = true;
        Assert.Equal(2, ctx.Messages.Count);
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
        Assert.Equal(2, ctx.Messages.Count);
    }

    [Fact]
    public async Task The_live_video_shows_the_readers_camera_and_source_with_the_picture_only()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var video = Assert.Single(ctx.Video!.Created);

        Assert.True(editor.HasLiveVideo);
        Assert.Equal([(Samples.Gate.Id, 1)], video.Starts);
        Assert.False(video.Place.ShowSourceSwitch);
        Assert.False(video.Place.AllowFullscreen);
        Assert.False(video.Place.ShowControlsButton);

        // Another video source and another camera: the video follows once each.
        editor.SelectedSource = editor.VideoSources[1];
        editor.SelectedSource = editor.VideoSources[1];
        editor.SetCamera(Samples.Dock.Id);
        Assert.Equal([(Samples.Gate.Id, 1), (Samples.Gate.Id, 2), (Samples.Dock.Id, 1)], video.Starts);

        // Another reader: the old video goes.
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
        Assert.True(video.IsDisposed);
        Assert.Equal(Samples.Plant.Id, ctx.Video.Created[^1].Shown);

        // An older OADM without live video: the overlay alone.
        var older = Samples.Context();
        older.Video = null;
        using var page = await LoadedAsync(older);
        Assert.False(page.Editor!.HasLiveVideo);
        Assert.Null(page.Editor.VideoView);
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
    public async Task The_preview_reads_live_and_only_the_newest_reading_is_fetched()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        await editor.SendPreviewAsync(CancellationToken.None);
        Assert.Equal("p1", editor.PreviewId);
        var before = ctx.CallsOf(OcrMethods.GetPreview);

        // A fetch that is held: two more readings meanwhile give one more fetch, not two.
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = true;
        ctx.AsyncAnswers[OcrMethods.GetPreview] = _ =>
        {
            if (held)
            {
                held = false;
                return gate.Task;
            }

            return Task.FromResult<string?>(Samples.ReplyJson(Samples.GateReading("TGHU 765432 1"), Samples.GateExtras, null));
        };
        var first = editor.RefreshPreviewAsync(CancellationToken.None);
        await editor.RefreshPreviewAsync(CancellationToken.None);
        await editor.RefreshPreviewAsync(CancellationToken.None);
        Assert.Equal(before + 1, ctx.CallsOf(OcrMethods.GetPreview));
        gate.SetResult(Samples.ReplyJson(Samples.GateReading("MSKU 000000 0"), Samples.GateExtras, null));
        await first;
        Assert.Equal(before + 2, ctx.CallsOf(OcrMethods.GetPreview));
        Assert.Equal("TGHU 765432 1", editor.Results[0].Text);
        Assert.Equal("{\"previewId\":\"p1\"}", ctx.LastPayloadOf(OcrMethods.GetPreview));

        // Live: the page's own preview fetches; the readings event updates the list's last text only.
        ctx.AsyncAnswers.Remove(OcrMethods.GetPreview);
        vm.Activate();
        try
        {
            await Eventually.TrueAsync(() => editor.IsPreviewing, "the shown reader is previewed");
            var calls = ctx.CallsOf(OcrMethods.GetPreview);
            ctx.Push(OcrMethods.PreviewTopic, new PreviewEvent { PreviewId = "p1", Reading = Samples.GateReading("CMAU 111111 1") });
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.GetPreview) > calls, "the preview's reading fetched");

            // Another page's preview and a saved reader's reading fetch nothing.
            calls = ctx.CallsOf(OcrMethods.GetPreview);
            ctx.Push(OcrMethods.PreviewTopic, new PreviewEvent { PreviewId = "p9", Reading = Samples.GateReading() });
            ctx.Push(OcrMethods.ReadingTopic, new ReadingEvent { Reading = Samples.GateReading("CMAU 222222 2") with { Time = Samples.Time.AddSeconds(1) } });
            await Eventually.TrueAsync(() => vm.Readers[0].LastText == "CMAU 222222 2", "the list's last text");
            Assert.Equal(calls, ctx.CallsOf(OcrMethods.GetPreview));

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

        // Hidden: the keep-alive stops and the server ends the preview.
        Assert.False(editor.IsPreviewing);
        Assert.Null(editor.PreviewId);
        await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StopPreview) == 1, "the preview ended");
        Assert.Equal("{\"previewId\":\"p1\"}", ctx.LastPayloadOf(OcrMethods.StopPreview));
    }

    [Fact]
    public async Task The_preview_takes_the_draft_on_every_change_and_keeps_alive()
    {
        var ctx = Samples.Context();
        var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Samples.Time);
        editor.PreviewTime = time;
        vm.Activate();
        try
        {
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) == 1 && editor.PreviewId == "p1", "the preview started");
            var started = OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.StartPreview));
            Assert.Equal(string.Empty, started.PreviewId);
            Assert.Equal("r1", started.Reader!.Id);
            Assert.Equal(3, started.Reader.Areas.Count);

            // An area moved: the draft goes at once, with the preview's id (unsaved).
            editor.EditArea(editor.Areas[0], AreaHandle.Move, 0.1, 0);
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) == 2, "the draft sent");
            var changed = OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.StartPreview));
            Assert.Equal("p1", changed.PreviewId);
            Assert.Equal(0.57, changed.Reader!.Areas[0].Box.X, 6);
            Assert.True(editor.IsDirty);

            // The keep-alive: once per 10 s of the clock (advanced a second per check: the wait may start after this line).
            await Eventually.TrueAsync(
                () =>
                {
                    time.Advance(TimeSpan.FromSeconds(1));
                    return ctx.CallsOf(OcrMethods.StartPreview) >= 3;
                },
                "kept alive");

            // A refusal of the server shows where the results are.
            ctx.Answers[OcrMethods.StartPreview] = _ => OcrJson.Serialize(new PreviewReply { PreviewId = "p1", Problem = ReaderRules.PreviewBusy });
            editor.Name = "Gate 3";
            await Eventually.TrueAsync(() => editor.PreviewProblem == ReaderRules.PreviewBusy, "the problem shown");
            ctx.Answers[OcrMethods.StartPreview] = _ => throw new InvalidOperationException(ReaderRules.TooManyPreviews);
            editor.Name = "Gate 4";
            await Eventually.TrueAsync(() => editor.PreviewProblem == ReaderRules.TooManyPreviews, "the refusal shown");
        }
        finally
        {
            vm.Dispose();
        }

        await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StopPreview) == 1, "the preview ended with the page");
    }

    [Fact]
    public async Task Areas_move_and_resize_inside_the_picture_and_mark_the_reader_not_saved()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var area = editor.Areas[0]; // 0.47, 0.27, 0.27 x 0.12
        Assert.False(editor.IsDirty);

        editor.EditArea(area, AreaHandle.Move, 0.03, -0.02);
        AssertBox(new RelativeBox(0.50, 0.25, 0.27, 0.12), area.Box);
        Assert.True(editor.IsDirty);
        Assert.Equal(OcrTexts.NotSaved, editor.SaveStatus);
        Assert.Same(area, editor.SelectedArea);

        // Moved far: held inside the picture, the size kept.
        editor.EditArea(area, AreaHandle.Move, 2, 2);
        AssertBox(new RelativeBox(0.73, 0.88, 0.27, 0.12), area.Box);
        editor.EditArea(area, AreaHandle.Move, -5, -5);
        AssertBox(new RelativeBox(0, 0, 0.27, 0.12), area.Box);

        // Resized by a corner and by an edge: the opposite edges stay.
        editor.EditArea(area, AreaHandle.BottomRight, 0.1, 0.05);
        AssertBox(new RelativeBox(0, 0, 0.37, 0.17), area.Box);
        editor.EditArea(area, AreaHandle.Left, 0.07, 0);
        AssertBox(new RelativeBox(0.07, 0, 0.30, 0.17), area.Box);
        editor.EditArea(area, AreaHandle.Top, 0, 0.05);
        AssertBox(new RelativeBox(0.07, 0.05, 0.30, 0.12), area.Box);

        // Never smaller than the drawing minimum, never past the picture's edge.
        editor.EditArea(area, AreaHandle.Right, -1, 0);
        AssertBox(new RelativeBox(0.07, 0.05, AreaGeometry.MinSize, 0.12), area.Box);
        editor.EditArea(area, AreaHandle.TopLeft, 0.5, 0.5);
        AssertBox(new RelativeBox(0.07, 0.15, AreaGeometry.MinSize, AreaGeometry.MinSize), area.Box);
        editor.EditArea(area, AreaHandle.BottomRight, 5, 5);
        AssertBox(new RelativeBox(0.07, 0.15, 0.93, 0.85), area.Box);
        editor.EditArea(area, AreaHandle.TopRight, 5, -5);
        AssertBox(new RelativeBox(0.07, 0, 0.93, 1), area.Box);
        Assert.True(area.Box.IsValid);

        // The arrow keys: 1 %, with Shift 5 %, inside the picture.
        var lane = editor.Areas[2]; // 0.88, 0.31, 0.10 x 0.13
        editor.SelectedArea = lane;
        editor.NudgeSelected(AreaGeometry.Nudge, 0);
        AssertBox(new RelativeBox(0.89, 0.31, 0.10, 0.13), lane.Box);
        editor.NudgeSelected(AreaGeometry.BigNudge, AreaGeometry.BigNudge);
        AssertBox(new RelativeBox(0.90, 0.36, 0.10, 0.13), lane.Box);

        // A box from the live video is held the same way; without the change right nothing moves.
        editor.SetAreaBox(lane, new RelativeBox(0.95, -0.1, 0.001, 0.2));
        AssertBox(new RelativeBox(0.95, 0, AreaGeometry.MinSize, 0.2), lane.Box);
        var viewer = Samples.Context();
        viewer.Denied.Add(OcrViewModel.ChangePermissionKey);
        using var readOnly = await LoadedAsync(viewer);
        var kept = readOnly.Editor!.Areas[0].Box;
        readOnly.Editor.EditArea(readOnly.Editor.Areas[0], AreaHandle.Move, 0.1, 0.1);
        Assert.Equal(kept, readOnly.Editor.Areas[0].Box);
    }

    private static void AssertBox(RelativeBox expected, RelativeBox actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    [Fact]
    public async Task New_readers_start_watching_and_Draw_area_sits_with_the_areas()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        vm.PickCamera = _ => Task.FromResult<Guid?>(Samples.Plant.Id);

        await vm.NewCommand.ExecuteAsync(null);

        var editor = vm.Editor!;
        Assert.True(editor.IsNew);
        Assert.Equal(ReaderMode.Watching, editor.Mode);
        Assert.Equal("Watching · 1 fps", editor.ModeChipText);
        Assert.True(editor.DrawAreaCommand.CanExecute(null));
        editor.DrawAreaCommand.Execute(null);
        Assert.True(editor.IsDrawing);

        // On action for a new reader: its message once, also after the save.
        editor.IsOnAction = true;
        Assert.Single(ctx.Messages);
        await editor.SaveCommand.ExecuteAsync(null);
        editor.IsWatching = true;
        editor.IsOnAction = true;
        Assert.Single(ctx.Messages);
    }


    [Fact]
    public async Task A_refusal_of_the_watching_limit_shows_below_Mode_until_the_readers_change()
    {
        const string Refusal = "At most 4 readers can watch at once at the site Building B. Set another reader there to On action first.";
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

        // Another reader set to On action elsewhere: the readers event lifts it.
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
            readers = new[] { new { id = "r2", name = "Water meter", deviceId = Samples.Plant.Id, mode = "OnAction", problem = "The camera refuses connections." } },
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
    public async Task A_user_with_a_device_group_cannot_change_the_server_wide_graphics_card_setting()
    {
        var ctx = Samples.Context();
        ctx.HasAllDevices = false;
        using var vm = await LoadedAsync(ctx);

        Assert.True(vm.MayChange);
        Assert.True(vm.GpuAvailable);
        Assert.False(vm.CanSetGpu);
        Assert.Equal("Only users with all devices can do this.", vm.GpuBlockedReason);
        Assert.True(vm.NewCommand.CanExecute(null)); // the readers of the group stay theirs to change
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
