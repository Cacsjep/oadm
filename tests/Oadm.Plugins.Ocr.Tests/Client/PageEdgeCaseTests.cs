using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>
/// The page where the server and the user overtake each other (docs/spec/ocr.md "Page"): stored frame rates no longer
/// offered, refusals of the area list and of a box, opens that fail or come late, newest camera options, readings matched by
/// the area's place, cut-outs that are missing, the readers' camera texts, small areas on the video.
/// </summary>
public sealed class PageEdgeCaseTests
{
    private static OcrViewModel Page(FakePageContext ctx) => new(ctx) { DecodePicture = _ => Task.FromResult<Bitmap?>(null), ReconnectDelay = TimeSpan.FromMilliseconds(10) };

    private static async Task<OcrViewModel> LoadedAsync(FakePageContext ctx)
    {
        var vm = Page(ctx);
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task A_stored_frame_rate_no_longer_offered_shows_the_offered_one_and_is_not_saved()
    {
        var ctx = Samples.Context();
        ctx.Answers[OcrMethods.GetReader] = json => OcrJson.Serialize(Samples.ById(OcrJson.Deserialize<ReaderRequest>(json).ReaderId) with { Fps = 10 });
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;

        Assert.Equal(8, editor.Fps); // the highest offered below it, as the server reads it
        Assert.True(editor.IsDirty);
        Assert.Equal(OcrTexts.NotSaved, editor.SaveStatus);
        Assert.True(editor.SaveCommand.CanExecute(null));

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(8, OcrJson.Deserialize<SaveReaderRequest>(ctx.LastPayloadOf(OcrMethods.SaveReader)).Reader.Fps);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task The_frame_rate_choices_follow_the_server()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        editor.Fps = 8;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.IsDirty);

        // The list brings fewer choices: the select offers them, the rate goes to the highest below it, Not saved.
        vm.ApplyList(Samples.ListJson().Replace("[1,2,4,8,12,15]", "[1,2,4]", StringComparison.Ordinal));
        Assert.Equal([1, 2, 4], editor.FpsChoices);
        Assert.Equal(4, editor.Fps);
        Assert.True(editor.IsDirty);

        // The engine brings them back: offered again, the rate stays.
        vm.ApplyEngine(Samples.Engine);
        Assert.Equal([1, 2, 4, 8, 12, 15], editor.FpsChoices);
        Assert.Equal(4, editor.Fps);
        Assert.Equal(OcrLimits.DefaultFps, ReaderEditorViewModel.OfferedFps(0, OcrLimits.FpsChoices));
    }

    [Fact]
    public void Areas_keep_the_servers_minimum_size() => Assert.Equal(OcrLimits.MinAreaSide, AreaGeometry.MinSize);

    [Fact]
    public async Task A_refusal_of_the_area_list_shows_below_the_areas_until_they_change()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        editor.Name = "Container gate 2";
        ctx.Answers[OcrMethods.SaveReader] = _ => OcrJson.Serialize(new SaveReaderReply { Errors = [new FieldError(OcrFields.Areas, ReaderRules.TooManyAreas)] });

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ReaderRules.TooManyAreas, editor.AreasError);
        Assert.True(editor.HasAreasError);
        Assert.Empty(ctx.Messages); // never in the host's message
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.RemoveAreaCommand.Execute(editor.Areas[^1]);
        Assert.Null(editor.AreasError);
        Assert.False(editor.HasAreasError);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refusal_of_a_box_stays_through_a_rename_and_goes_with_a_move()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var area = editor.Areas[0];

        Assert.True(editor.ShowFieldError(new FieldError(OcrFields.AreaBox(0), ReaderRules.AreaOutside)));
        Assert.Equal(ReaderRules.AreaOutside, area.NameError); // below the name in the row
        Assert.Null(area.ErrorOf(nameof(AreaViewModel.Name)));

        area.Name = "Number";
        Assert.Equal(ReaderRules.AreaOutside, area.NameError);

        editor.EditArea(area, AreaHandle.Move, 0.01, 0);
        Assert.Null(area.NameError);
        Assert.True(area.IsValid);
    }

    [Fact]
    public async Task A_failed_open_keeps_the_shown_reader_selected()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var shown = vm.SelectedReader;
        Assert.Equal("r1", vm.Editor!.Id);
        ctx.Answers[OcrMethods.GetReader] = _ => throw new InvalidOperationException("The reader no longer exists.");

        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);

        Assert.Equal("r1", vm.Editor!.Id);
        Assert.Same(shown, vm.SelectedReader);
        Assert.Equal("The reader no longer exists.", vm.ErrorText);
        Assert.False(vm.IsOpening);
    }

    [Fact]
    public async Task A_late_open_never_replaces_a_new_draft()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AsyncAnswers[OcrMethods.GetReader] = _ => late.Task;
        vm.PickCamera = _ => Task.FromResult<Guid?>(Samples.Plant.Id);

        var open = vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);
        Assert.True(vm.IsOpening);
        await vm.NewCommand.ExecuteAsync(null);
        Assert.True(vm.Editor!.IsNew);
        Assert.False(vm.IsOpening);

        late.SetResult(OcrJson.Serialize(Samples.DockSign));
        await open;
        Assert.True(vm.Editor!.IsNew);
        Assert.Equal(Samples.Plant.Id, vm.Editor.DeviceId);
        Assert.False(vm.IsOpening);
    }

    [Fact]
    public async Task The_newest_camera_options_win()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answers = new Queue<TaskCompletionSource<string?>>([first, second]);
        ctx.AsyncAnswers[OcrMethods.GetCameraOptions] = _ => answers.Dequeue().Task;

        var older = editor.LoadCameraOptionsAsync(CancellationToken.None);
        var newer = editor.LoadCameraOptionsAsync(CancellationToken.None);
        second.SetResult(OcrJson.Serialize(new CameraOptions { VideoSources = [new CameraSource { Number = 1, Name = "View area 1" }], Resolutions = [new(1280, 720)] }));
        await newer;
        Assert.False(editor.IsLoadingCamera);
        first.SetResult(OcrJson.Serialize(Samples.CameraOptions));
        await older;

        Assert.Equal([new Resolution(1280, 720)], editor.Resolutions);
        Assert.Single(editor.VideoSources);
        Assert.False(editor.IsLoadingCamera);
    }

    [Fact]
    public async Task Results_belong_to_the_area_at_the_same_place_also_with_repeated_names()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.ShowEditor(new Reader
        {
            Id = "r1",
            Name = "Container gate",
            DeviceId = Samples.Gate.Id,
            Areas =
            [
                new Area { Name = "Area 1", Box = new(0.1, 0.1, 0.2, 0.1), Expected = ExpectedText.WithFormat("@#") },
                new Area { Name = "Area 1", Box = new(0.5, 0.5, 0.2, 0.1) },
            ],
        }, isNew: false);
        var reading = new Reading
        {
            ReaderId = "r1",
            Time = Samples.Time,
            Areas =
            [
                new AreaResult { Area = "Area 1", Text = "G3", TextRead = "G3", Confidence = 90, MatchesFormat = true },
                new AreaResult { Area = "Area 1", Text = "22G1", TextRead = "22G1", Confidence = 80 },
            ],
        };

        editor.ApplyReading(reading, [new AreaDetails { Area = "Area 1" }, new AreaDetails { Area = "Area 1", TooSmall = true }]);

        Assert.True(editor.HeldAreas[0].IsMatch);
        Assert.False(editor.HeldAreas[0].TooSmall);
        Assert.False(editor.HeldAreas[1].HasFormat);
        Assert.True(editor.HeldAreas[1].TooSmall);
    }

    [Fact]
    public async Task Readings_without_cut_outs_show_the_text_only()
    {
        // A user without Live view gets no cut-outs, and no reading carries the picture.
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        Assert.Equal(2, editor.Results.Count);
        Assert.All(editor.Results, r => Assert.False(r.HasCutOut));
        Assert.Equal("MSKU 123456 7", editor.Results[1].Text);
        Assert.Empty(ctx.Messages);

        // A cut-out that does not decode: no image, no error.
        var broken = Samples.Context();
        broken.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.WithCutout(Samples.GateExtras, [1, 2, 3]), null);
        using var other = await LoadedAsync(broken);
        Assert.Equal(2, other.Editor!.Results.Count);
        Assert.False(other.Editor.Results[1].HasCutOut);
        Assert.Null(other.ErrorText);
        Assert.Empty(broken.Messages);
    }

    [Fact]
    public async Task Camera_texts_follow_device_changes_while_the_page_shows()
    {
        var ctx = Samples.Context();
        using var vm = Page(ctx);
        vm.StatsTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        vm.Activate();
        await Eventually.TrueAsync(() => vm.Readers.Count == 3 && vm.Editor is not null, "the list");
        var gate = vm.Readers.Single(r => r.Id == "r1");
        Assert.Equal("AXIS Q1798-LE · 10.0.0.48", gate.Camera);

        // Shown: one pass over the devices per change, only the readers' cameras.
        ctx.DeviceList[0] = Samples.Gate with { Address = "10.0.0.49" };
        var reads = ctx.DeviceReads;
        ctx.RaiseDevicesChanged();
        Assert.Equal("AXIS Q1798-LE · 10.0.0.49", gate.Camera);
        Assert.Equal(reads + 1, ctx.DeviceReads);

        // Hidden: nothing is read; once shown again the texts follow.
        vm.Deactivate();
        ctx.DeviceList[0] = Samples.Gate with { Address = "10.0.0.50" };
        reads = ctx.DeviceReads;
        ctx.RaiseDevicesChanged();
        ctx.RaiseDevicesChanged();
        Assert.Equal(reads, ctx.DeviceReads);
        Assert.Equal("AXIS Q1798-LE · 10.0.0.49", gate.Camera);
        vm.Activate();
        Assert.Equal("AXIS Q1798-LE · 10.0.0.50", gate.Camera);
        vm.Deactivate();
    }

    [Fact]
    public async Task Camera_texts_take_one_pass_at_the_device_limit()
    {
        var ctx = Samples.Context();
        var cameras = ctx.DeviceList.ToList();
        ctx.DeviceList.Clear();
        ctx.DeviceList.AddRange(Enumerable.Range(0, Oadm.Sdk.Devices.DeviceLimits.MaxDevices - cameras.Count)
            .Select(i => (Oadm.Sdk.Devices.IDeviceInfo)new TestDevice($"10.1.{i / 250}.{i % 250}", "AXIS M3215-LVE")));
        ctx.DeviceList.AddRange(cameras); // the readers' cameras last: the pass reads them all
        var reads = ctx.DeviceReads;
        using var vm = await LoadedAsync(ctx);

        Assert.Equal("AXIS P3268-LV · 10.0.0.12", vm.Readers.Single(r => r.Id == "r3").Camera);
        Assert.True(ctx.DeviceReads - reads <= 2, $"{ctx.DeviceReads - reads} reads of the device list"); // the list's cameras, the new reader's
    }

    [Fact]
    public async Task A_view_model_that_leaves_the_view_is_disposed()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var first = await LoadedAsync(ctx);
            var view = new OcrView { DataContext = first };
            var video = ctx.Video!.Created[^1];
            using var second = Page(ctx);

            view.DataContext = second;

            Assert.True(video.IsDisposed); // the first page's reader went with it
            return true; // Dispatch has no overload for a plain Task: without a value nothing after the first await is awaited
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_small_area_moves_from_inside_and_resizes_from_just_outside()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var vm = Page(ctx);
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            var editor = vm.Editor!;
            var area = editor.Areas[0];
            editor.SetAreaBox(area, new RelativeBox(0.5, 0.5, AreaGeometry.MinSize, AreaGeometry.MinSize));
            Headless.Pump();

            var picture = view.PictureControl;
            var rect = picture.PictureRect;
            var box = new Rect(rect.X + (0.5 * rect.Width), rect.Y + (0.5 * rect.Height), AreaGeometry.MinSize * rect.Width, AreaGeometry.MinSize * rect.Height);
            Assert.True(box.Width < ReadingPicture.SmallArea && box.Height < ReadingPicture.SmallArea, $"a small area: {box.Size}");

            Assert.Equal((area, AreaHandle.Move), picture.HitTest(box.Center));
            Assert.Equal((area, AreaHandle.Move), picture.HitTest(box.BottomRight - new Vector(1, 1)));
            Assert.Equal((area, AreaHandle.BottomRight), picture.HitTest(box.BottomRight + new Vector(4, 4)));
            Assert.Equal((area, AreaHandle.TopLeft), picture.HitTest(box.TopLeft - new Vector(4, 4)));

            // A large area keeps its handles inside too.
            var big = editor.Areas[1];
            editor.SelectedArea = big;
            Headless.Pump();
            var bigRect = new Rect(rect.X + (big.Box.X * rect.Width), rect.Y + (big.Box.Y * rect.Height), big.Box.Width * rect.Width, big.Box.Height * rect.Height);
            Assert.Equal((big, AreaHandle.BottomRight), picture.HitTest(bigRect.BottomRight - new Vector(3, 3)));
            Assert.NotNull(view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "AreasError"));
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
