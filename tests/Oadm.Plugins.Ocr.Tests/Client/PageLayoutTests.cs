using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>
/// The page rendered headless (docs/spec/ocr.md "Page"): fixed readers and settings columns, the video and the results in two
/// cards with a splitter between them and its limits, and a real pointer drag of an area that stays where it ends (the
/// snap-back), the lines hidden meanwhile.
/// </summary>
public sealed class PageLayoutTests
{
    [Fact]
    public async Task The_columns_are_fixed_and_the_results_sit_in_a_card_below_the_video()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            Headless.Pump();

            // One splitter, between the two cards of the middle: the readers column and the settings column keep their width, the
            // video and the results take the rest.
            var splitter = Assert.Single(view.GetVisualDescendants().OfType<GridSplitter>());
            Assert.Contains("paneSplitter", splitter.Classes);
            var panes = view.FindControl<Grid>("Panes")!;
            Assert.Equal(new GridLength(OcrView.ReadersWidth), panes.ColumnDefinitions[0].Width);
            Assert.True(panes.ColumnDefinitions[1].Width.IsStar);
            Assert.Equal(OcrView.SettingsWidth, view.FindControl<Grid>("SettingsColumn")!.Width); // fixed, in a card of its own
            Assert.Contains("card", view.FindControl<Border>("SettingsCard")!.Classes);

            // Four cards: the readers, the video, the results below it, the settings.
            var video = view.FindControl<Panel>("VideoPanel")!;
            var results = view.FindControl<DataGrid>("ResultsGrid")!;
            var videoCard = view.FindControl<Border>("VideoCard")!;
            var resultsCard = view.FindControl<Border>("ResultsCard")!;
            var column = view.FindControl<Grid>("VideoColumn")!;
            Assert.Equal(4, view.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("card") && b.IsEffectivelyVisible));
            Assert.Same(videoCard, video.FindAncestorOfType<Border>());
            Assert.Same(resultsCard, results.FindAncestorOfType<Border>());
            Assert.Same(column, splitter.Parent);
            Assert.True(Grid.GetRow(videoCard) < Grid.GetRow(splitter) && Grid.GetRow(splitter) < Grid.GetRow(resultsCard));
            Assert.True(results.IsEffectivelyVisible);
            // No "Use as format" in the results (decision 2026-11-03).
            Assert.DoesNotContain(results.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Use as format"));

            // The form: Name, Mode and Stream settings with its summary below it; no camera, no stream selects of their own.
            var settings = view.FindControl<Grid>("SettingsColumn")!;
            var labels = settings.GetVisualDescendants().OfType<FormField>().Where(f => f.IsEffectivelyVisible && f.Label is { Length: > 0 }).Select(f => f.Label).ToList();
            Assert.Equal(["Name", "Mode", "Stream"], labels);
            var stream = view.FindControl<Button>("StreamButton")!;
            Assert.Contains("secondary", stream.Classes);
            Assert.Equal("Configure · 1920x1080 @ 1 fps", stream.GetVisualDescendants().OfType<IconLabel>().Single().Text);
            Assert.Null(view.FindControl<FormField>("StreamField")!.Hint); // the summary is the button itself
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible && c.Name is not ("ModeBox" or "ReaderModeBox"));
            // The reader's mode is a select next to the name (decision 2026-11-03).
            var readerMode = view.FindControl<ComboBox>("ReaderModeBox")!;
            Assert.Equal(OcrTexts.Watching, readerMode.SelectedItem);

            // No status line under the video: the reader's state is in the reader list.
            Assert.DoesNotContain(videoCard.GetVisualDescendants().OfType<StatusChip>(), c => c.Name != "PausedChip");
            Assert.Contains(settings.GetVisualDescendants().OfType<StatusChip>(), c => c.Name == "NoteChip");

            // The areas header: New at the right (no count); one entry per area: the name with Rename and Delete in the first
            // line, Mode and Format indented in the second.
            var newArea = view.FindControl<ToolbarButton>("NewAreaButton")!;
            Assert.Equal("New", newArea.Text);
            Assert.Equal(OcrTexts.NewAreaTip, ToolTip.GetTip(newArea));
            Assert.Equal(1, Grid.GetColumn(newArea));
            var list = view.FindControl<ListBox>("AreaList")!;
            var editorVm = vm.Editor!;
            Assert.Equal(3, editorVm.Areas.Count);
            foreach (var area in editorVm.Areas)
            {
                var row = (Control)list.ContainerFromItem(area)!;
                var fields = row.GetVisualDescendants().OfType<FormField>().ToList();
                Assert.All(fields, f => Assert.Null(f.Label));
                Assert.Equal(area.Name, Assert.IsType<TextBlock>(fields[0].Input).Text); // the name as plain text, Rename changes it
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBox>(), t => t.TemplatedParent is null && t.Name != "FormatBox");
                Assert.Equal(RowActions.Tip, ToolTip.GetTip(row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AreaActions")));
                Assert.Equal([OcrTexts.UseReadTextAsFormat, OcrTexts.Rename, "-", "Delete"], editorVm.ActionsOf(area).Select(a => a.Text));
                var format = Assert.IsType<TextBox>(fields[1].Input);
                Assert.True(format.IsEffectivelyEnabled);
                Assert.Empty(row.GetVisualDescendants().OfType<ComboBox>()); // no mode select: empty reads any text
                var help = Assert.IsAssignableFrom<Control>(ToolTip.GetTip(format));
                Assert.Contains(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(help).OfType<TextBlock>(), t => t.Text == "@@@U ###### #");
                Assert.Empty(row.GetVisualDescendants().OfType<RadioButton>());
            }

            // Selecting a row selects the area on the video (and the other way round).
            list.SelectedItem = editorVm.Areas[2];
            Headless.Pump();
            Assert.Same(editorVm.Areas[2], editorVm.SelectedArea);
            editorVm.SelectedArea = editorVm.Areas[0];
            Headless.Pump();
            Assert.Same(editorVm.Areas[0], list.SelectedItem);

            // One format field: empty reads any text.
            var typeRow = (Control)list.ContainerFromItem(editorVm.Areas[1])!;
            var typeFormat = typeRow.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FormatBox");
            Assert.Equal(string.Empty, typeFormat.Text);
            Assert.Equal(OcrTexts.AnyText, typeFormat.PlaceholderText);


            window.Close();

            // A value: the async lambda is then awaited to its end (Dispatch has no overload for a plain Task, so its
            // assertions after the first await would otherwise never reach the test).
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task On_a_wide_screen_the_video_shrinks_and_the_results_stay_on_the_page()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            ctx.Answers[OcrMethods.GetReading] = json => OcrJson.Deserialize<ReaderRequest>(json).ReaderId == "r1"
                ? Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null)
                : "{}";
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view, 2000, 870);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Ok, ReaderRules.KeepUp, 42));
            Headless.Pump();
            Headless.Capture(window, "ocr-wide-screen.png");

            var video = view.FindControl<Border>("VideoCard")!;
            var results = view.FindControl<DataGrid>("ResultsGrid")!;
            var column = view.FindControl<Grid>("VideoColumn")!;
            var picture = view.PictureControl;
            Assert.Equal(16.0 / 9, picture.PictureRect.Width / picture.PictureRect.Height, 2);
            Assert.True(video.Bounds.Height + OcrView.ResultsMinHeight <= column.Bounds.Height + 0.5, "the results keep their height");
            Assert.True(view.FindControl<Border>("ResultsCard")!.Bounds.Height >= OcrView.ResultsMinHeight - 0.5);
            var bottom = results.TranslatePoint(new Point(0, results.Bounds.Height), column)!.Value.Y;
            Assert.True(results.Bounds.Height > 0 && bottom <= column.Bounds.Height + 0.5, "the results end inside the page");

            // The status and the load in the page header; the readers list scrolls by itself above Settings.
            var stats = view.FindControl<StackPanel>("StatsPanel")!;
            Assert.NotNull(stats.FindAncestorOfType<Oadm.Client.Controls.PageTitleBar>());
            Assert.True(stats.IsEffectivelyVisible);
            Assert.True(view.FindControl<ListBox>("ReaderList")!.Bounds.Height > 0);

            // The format field's tooltip on its own (a headless picture of the page has no open tooltips).
            var firstRow = (Control)view.FindControl<ListBox>("AreaList")!.ContainerFromIndex(0)!;
            var tip = (Control)ToolTip.GetTip(firstRow.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FormatBox"))!;
            var tipWindow = new Window { Width = 340, Height = 320, Content = new Border { Padding = new Thickness(12), Child = tip } };
            tipWindow.Show();
            Headless.Pump();
            Headless.Capture(tipWindow, "ocr-format-tooltip.png");
            tipWindow.Close();

            var movingTip = (Control)ToolTip.GetTip(view.FindControl<CheckBox>("MovingObjectsBox")!)!;
            var movingWindow = new Window { Width = 340, Height = 160, Content = new Border { Padding = new Thickness(12), Child = movingTip } };
            movingWindow.Show();
            Headless.Pump();
            Headless.Capture(movingWindow, "ocr-moving-objects-tooltip.png");
            movingWindow.Close();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task With_eight_areas_in_a_short_window_only_the_rows_scroll_and_New_and_Save_stay_in_reach()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view, 1500, 640);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            var editor = vm.Editor!;
            while (editor.Areas.Count < OcrLimits.MaxAreas)
            {
                editor.AddArea(new RelativeBox(0.05 * editor.Areas.Count, 0.8, 0.04, 0.1));
            }

            editor.SelectedArea = editor.Areas[^1];
            Headless.Pump();

            var column = view.FindControl<Grid>("SettingsColumn")!;
            var list = view.FindControl<ListBox>("AreaList")!;
            var bounds = new Rect(column.Bounds.Size);
            bool Inside(Control control) =>
                control.TranslatePoint(new Point(0, 0), column) is { } top && control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), column) is { } bottom
                && bounds.Contains(top) && bounds.Contains(bottom);

            // The rows are taller than their place: the list scrolls them, New above and Save below stay inside the column.
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
            Assert.Equal(2, Grid.GetRow((Control)list.Parent!)); // the rows with the list's error below them
            Assert.True(column.RowDefinitions[2].Height.IsStar);
            // New ends level with the fields: its padding lies outside the column (theme class atEnd), so only check it is in reach.
            var newArea = view.FindControl<ToolbarButton>("NewAreaButton")!;
            Assert.True(newArea.TranslatePoint(new Point(0, 0), column) is { } newTop && bounds.Contains(newTop) && newArea.IsEffectivelyVisible);
            Assert.True(Inside(view.FindControl<Button>("StreamButton")!));
            Assert.True(Inside(column.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save"))));
            Headless.Capture(window, $"ocr-eight-areas-{Headless.Appearance}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>A real pointer drag of the splitter by <paramref name="by"/> pixels.</summary>
    private static void DragSplitter(OcrView view, Window window, double by)
    {
        var splitter = view.FindControl<GridSplitter>("ResultsSplitter")!;
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        var to = from + new Point(0, by);
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(0, by / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Headless.Pump();
    }

    private static void AssertInsideColumn(OcrView view)
    {
        var column = view.FindControl<Grid>("VideoColumn")!;
        foreach (var name in new[] { "VideoCard", "ResultsCard" })
        {
            var card = view.FindControl<Border>(name)!;
            var top = card.TranslatePoint(default, column)!.Value.Y;
            Assert.True(card.Bounds.Height > 0 && top >= -0.5 && top + card.Bounds.Height <= column.Bounds.Height + 0.5, $"{name} stays inside the column: top {top}, height {card.Bounds.Height}, column {column.Bounds.Height}");
        }
    }

    [Fact]
    public async Task A_pointer_drag_on_the_splitter_gives_the_table_more_height_up_to_the_limits()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            var reads = Samples.WithCutOuts(Samples.Reads(OcrLimits.RecentReads));
            ctx.Answers[OcrMethods.GetReading] = json => OcrJson.Deserialize<ReaderRequest>(json).ReaderId == "r1"
                ? Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads)
                : "{}";
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view, 2000, 870);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Ok, ReaderRules.KeepUp, 42));
            Headless.Pump();
            var video = view.FindControl<Border>("VideoCard")!;
            var results = view.FindControl<Border>("ResultsCard")!;
            var column = view.FindControl<Grid>("VideoColumn")!;
            var picture = view.PictureControl;
            var videoMin = OcrView.VideoMinShare * column.Bounds.Height;
            Headless.Capture(window, $"ocr-split-default-{suffix}.png");
            WriteMeasures(view, window, $"ocr-split-default-{suffix}.txt");

            // By hand: up gives the table more height, the video follows and keeps its shape.
            var videoBefore = video.Bounds.Height;
            var resultsBefore = results.Bounds.Height;
            DragSplitter(view, window, -60);
            Assert.True(video.Bounds.Height < videoBefore, "the video card got smaller");
            Assert.True(results.Bounds.Height > resultsBefore, "the results card got taller");
            Assert.Equal(videoBefore + resultsBefore, video.Bounds.Height + results.Bounds.Height, 0);
            Assert.Equal(16.0 / 9, picture.PictureRect.Width / picture.PictureRect.Height, 2);
            AssertInsideColumn(view);

            // Far up: it stops at the 30 %, and a second drag changes nothing.
            DragSplitter(view, window, -2000);
            Assert.InRange(video.Bounds.Height, videoMin - 0.5, videoMin + 40);
            var atLimit = video.Bounds.Height;
            Headless.Capture(window, $"ocr-split-dragged-{suffix}.png");
            WriteMeasures(view, window, $"ocr-split-dragged-{suffix}.txt");
            DragSplitter(view, window, -100);
            Assert.Equal(atLimit, video.Bounds.Height, 0);
            Assert.Equal(16.0 / 9, picture.PictureRect.Width / picture.PictureRect.Height, 2);
            AssertInsideColumn(view);

            // Another reader keeps the position.
            await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
            Headless.Pump();
            Assert.Equal(atLimit, video.Bounds.Height, 0);

            // Far down: it stops at the results' minimum, below the height of the first layout.
            DragSplitter(view, window, 2000);
            Assert.InRange(results.Bounds.Height, OcrView.ResultsDragMinHeight - 0.5, OcrView.ResultsMinHeight - 1);
            var atOtherLimit = results.Bounds.Height;
            DragSplitter(view, window, 100);
            Assert.Equal(atOtherLimit, results.Bounds.Height, 0);
            Assert.True(view.FindControl<DataGrid>("ResultsGrid")!.Bounds.Height > 0);
            AssertInsideColumn(view);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>The edges to measure in the screenshot, in window pixels; a text file, never an assertion.</summary>
    private static void WriteMeasures(OcrView view, Window window, string file)
    {
        if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        string Edges(Control c)
        {
            var at = c.TranslatePoint(default, window)!.Value;
            return string.Create(CultureInfo.InvariantCulture, $"{c.Name} top {at.Y} bottom {at.Y + c.Bounds.Height} left {at.X} right {at.X + c.Bounds.Width}");
        }

        var picture = view.PictureControl;
        var rect = picture.PictureRect;
        var place = picture.TranslatePoint(new Point(rect.X, rect.Y), window)!.Value;
        var button = view.FindControl<Button>("PauseButton")!;
        var lines = new[] { "VideoColumn", "VideoCard", "ResultsCard" }.Select(n => Edges(view.FindControl<Control>(n)!))
            .Concat([Edges(view.FindControl<Control>("ResultsSplitter")!), Edges(view.FindControl<Control>("SettingsCard")!), Edges(button)])
            .Append(string.Create(CultureInfo.InvariantCulture, $"Picture left {place.X} top {place.Y} width {rect.Width} height {rect.Height}"));
        File.WriteAllLines(Path.Combine(dir, file), lines);
    }

    [Fact]
    public async Task In_a_short_window_the_cards_never_overflow_and_the_video_keeps_its_share_first()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view, 1500, 640);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            Headless.Pump();
            var video = view.FindControl<Border>("VideoCard")!;
            var results = view.FindControl<Border>("ResultsCard")!;
            var column = view.FindControl<Grid>("VideoColumn")!;
            AssertInsideColumn(view);
            Assert.True(results.Bounds.Height >= OcrView.ResultsDragMinHeight);

            // The window shrinks under a dragged position, and further than both limits fit: nothing leaves the column.
            DragSplitter(view, window, 2000);
            foreach (var height in new[] { 420, 300, 240 })
            {
                window.Height = height;
                Headless.Pump();
                AssertInsideColumn(view);
                Assert.True(video.Bounds.Height >= (OcrView.VideoMinShare * column.Bounds.Height) - 0.5, "the video keeps its share");
            }

            // Larger again: the position comes back within its limits.
            window.Height = 870;
            Headless.Pump();
            AssertInsideColumn(view);
            Assert.True(results.Bounds.Height >= OcrView.ResultsDragMinHeight - 0.5);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1000, 16, 92, 300, 844, 140)] // room for all
    [InlineData(300, 16, 92, 92, 144, 140)] // the note needs more than the 30 %
    [InlineData(200, 16, 60, 60, 60, 124)] // the video keeps its 30 %, the results take what is left
    [InlineData(400, 16, 200, 200, 244, 140)] // a long note: the results keep their minimum beside it
    [InlineData(250, 16, 200, 94, 94, 140)] // no room for the note and the results: their minimum first
    [InlineData(100, 16, 92, 30, 30, 54)] // far below the window's minimum: the 30 % first, the column still holds
    public void The_limits_win_in_their_order(double height, double splitter, double chrome, double videoMin, double videoMax, double resultsMin)
    {
        var limits = OcrView.LimitsOf(height, splitter, chrome);
        Assert.Equal(videoMin, limits.VideoMin, 6);
        Assert.Equal(videoMax, limits.VideoMax, 6);
        Assert.Equal(resultsMin, limits.ResultsMin, 6);
        Assert.Equal(height - splitter, limits.VideoMax + limits.ResultsMin, 6);
        Assert.InRange(limits.VideoFirst, limits.VideoMin, limits.VideoMax);
    }

    [Fact]
    public async Task A_press_beside_every_area_clears_the_selection()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            Headless.Pump();
            var editor = vm.Editor!;
            var picture = view.PictureControl;
            Point At(double x, double y)
            {
                var rect = picture.PictureRect;
                return picture.TranslatePoint(new Point(rect.X + (x * rect.Width), rect.Y + (y * rect.Height)), window)!.Value;
            }

            editor.SelectedArea = editor.Areas[1];
            Headless.Pump();
            Headless.Capture(window, "ocr-area-grey-selected.png");

            window.MouseDown(At(0.5, 0.05), MouseButton.Left);
            window.MouseUp(At(0.5, 0.05), MouseButton.Left);
            Headless.Pump();
            Headless.Capture(window, "ocr-area-grey-none.png");

            Assert.Null(editor.SelectedArea);
            Assert.Null(view.FindControl<ListBox>("AreaList")!.SelectedItem);
            Assert.All(editor.Areas, a => Assert.False(a.IsSelected));

            // A press inside an area selects it again.
            window.MouseDown(At(0.53, 0.44), MouseButton.Left);
            window.MouseUp(At(0.53, 0.44), MouseButton.Left);
            Headless.Pump();
            Assert.Same(editor.Areas[1], editor.SelectedArea);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_pointer_drag_moves_an_area_and_it_stays_there()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            Headless.Pump();
            var editor = vm.Editor!;
            var picture = view.PictureControl;
            var area = editor.Areas[1]; // "Type" at 0.47, 0.40, 0.12 x 0.08
            var before = area.Box;
            Point At(double x, double y)
            {
                var rect = picture.PictureRect;
                return picture.TranslatePoint(new Point(rect.X + (x * rect.Width), rect.Y + (y * rect.Height)), window)!.Value;
            }

            // Press inside the area, drag by 10 % right and 20 % down: the lines hide during the drag.
            window.MouseDown(At(0.53, 0.44), MouseButton.Left);
            window.MouseMove(At(0.58, 0.54));
            window.MouseMove(At(0.63, 0.64));
            Headless.Pump();
            Assert.True(picture.IsDragging);
            Assert.True(editor.IsAdjustingArea);
            Assert.Empty(editor.ShownLines);
            Assert.Equal(before, area.Box); // the view model changes when the drag ends

            window.MouseUp(At(0.63, 0.64), MouseButton.Left);
            Headless.Pump();

            // The area stays where the drag ended; the lines are back.
            Assert.False(picture.IsDragging);
            Assert.False(editor.IsAdjustingArea);
            Assert.Equal(before.X + 0.10, area.Box.X, 2);
            Assert.Equal(before.Y + 0.20, area.Box.Y, 2);
            Assert.Equal(before.Width, area.Box.Width, 6);
            Assert.Same(area, editor.SelectedArea);
            Assert.True(editor.IsDirty);
            Assert.Equal(2, editor.ShownLines.Count);

            // A preview reading and a render later it is still there.
            var moved = area.Box;
            editor.ApplyReading(Samples.GateReading("TGHU 765432 1"), Samples.GateExtras);
            Headless.Pump();
            Assert.Equal(moved, area.Box);

            // A resize by the bottom right handle the same way.
            var corner = (X: moved.X + moved.Width, Y: moved.Y + moved.Height);
            window.MouseDown(At(corner.X, corner.Y), MouseButton.Left);
            window.MouseMove(At(corner.X + 0.05, corner.Y + 0.05));
            window.MouseUp(At(corner.X + 0.05, corner.Y + 0.05), MouseButton.Left);
            Headless.Pump();
            Assert.Equal(moved.X, area.Box.X, 6);
            Assert.Equal(moved.Width + 0.05, area.Box.Width, 2);
            Assert.Equal(moved.Height + 0.05, area.Box.Height, 2);
            window.Close();

            // A value: the async lambda is then awaited to its end (Dispatch has no overload for a plain Task, so its
            // assertions after the first await would otherwise never reach the test).
            return true;
        }, CancellationToken.None);
    }
}
