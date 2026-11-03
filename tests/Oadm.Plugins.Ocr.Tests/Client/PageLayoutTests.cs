using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>
/// The page rendered headless (docs/spec/ocr.md "Page"): fixed readers and settings columns without a splitter, the results
/// below the video, and a real pointer drag of an area that stays where it ends (the snap-back), the lines hidden meanwhile.
/// </summary>
public sealed class PageLayoutTests
{
    [Fact]
    public async Task The_columns_are_fixed_and_the_results_sit_below_the_video()
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

            // No splitter: the readers column and the settings column keep their width, the video takes the rest.
            Assert.Empty(view.GetVisualDescendants().OfType<GridSplitter>());
            var panes = view.FindControl<Grid>("Panes")!;
            Assert.Equal(new GridLength(OcrView.ReadersWidth), panes.ColumnDefinitions[0].Width);
            Assert.True(panes.ColumnDefinitions[1].Width.IsStar);
            var editor = view.FindControl<Grid>("EditorPanel")!;
            Assert.True(editor.ColumnDefinitions[0].Width.IsStar);
            Assert.Equal(new GridLength(OcrView.SettingsWidth), editor.ColumnDefinitions[1].Width);

            // The results in the video's column, in the row below it; the stats panel in the readers column.
            var video = view.FindControl<Panel>("VideoPanel")!;
            var results = view.FindControl<DataGrid>("ResultsGrid")!;
            var column = (Grid)video.Parent!;
            Assert.Equal(0, Grid.GetColumn(column));
            Assert.Same(column, results.FindAncestorOfType<Grid>());
            Assert.True(Grid.GetRow((Control)results.Parent!) > Grid.GetRow(video));
            Assert.True(results.IsEffectivelyVisible);
            // No "Use as format" in the results (decision 2026-11-03).
            Assert.DoesNotContain(results.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Use as format"));

            // The form: Name, Mode and Stream settings with its summary below it; no camera, no stream selects of their own.
            var settings = view.FindControl<Grid>("SettingsColumn")!;
            var labels = settings.GetVisualDescendants().OfType<FormField>().Where(f => f.IsEffectivelyVisible && f.Label is { Length: > 0 }).Select(f => f.Label).ToList();
            Assert.Equal(["Name", "Mode", "Stream"], labels);
            var stream = view.FindControl<Button>("StreamButton")!;
            Assert.Contains("secondary", stream.Classes);
            Assert.Equal(OcrTexts.Configure, stream.GetVisualDescendants().OfType<IconLabel>().Single().Text);
            Assert.Equal("View area 1 · 1920x1080 · 1 fps", view.FindControl<FormField>("StreamField")!.Hint);
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible && c.Name is not ("ModeBox" or "ReaderModeBox"));
            // The reader's mode is a select next to the name (decision 2026-11-03).
            var readerMode = view.FindControl<ComboBox>("ReaderModeBox")!;
            Assert.Equal(OcrTexts.Watching, readerMode.SelectedItem);

            // No status line under the video: the reader's state is in the reader list.
            Assert.DoesNotContain(column.Children.OfType<Grid>(), g => g.Children.OfType<StatusChip>().Any());

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
                Assert.Equal(OcrTexts.Rename, ToolTip.GetTip(row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RenameButton")));
                var mode = Assert.IsType<ComboBox>(fields[1].Input);
                Assert.Equal(area.Mode, mode.SelectedItem);
                var format = Assert.IsType<TextBox>(fields[2].Input);
                Assert.Equal(area.IsFormat, format.IsEffectivelyEnabled); // in place, off for Any text
                Assert.Equal(AreaViewModel.FormatHint, ToolTip.GetTip(format));
                Assert.Empty(row.GetVisualDescendants().OfType<RadioButton>());
            }

            // Selecting a row selects the area on the video (and the other way round).
            list.SelectedItem = editorVm.Areas[2];
            Headless.Pump();
            Assert.Same(editorVm.Areas[2], editorVm.SelectedArea);
            editorVm.SelectedArea = editorVm.Areas[0];
            Headless.Pump();
            Assert.Same(editorVm.Areas[0], list.SelectedItem);

            // The mode select: Any text turns the format field off and empties it.
            var typeRow = (Control)list.ContainerFromItem(editorVm.Areas[1])!;
            var typeMode = typeRow.GetVisualDescendants().OfType<ComboBox>().Single();
            var typeFormat = typeRow.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FormatBox");
            Assert.False(typeFormat.IsEffectivelyEnabled);
            typeMode.SelectedItem = OcrTexts.Format;
            Headless.Pump();
            Assert.True(editorVm.Areas[1].IsFormat);
            Assert.True(typeFormat.IsEffectivelyEnabled);
            typeMode.SelectedItem = OcrTexts.AnyText;
            Headless.Pump();
            Assert.False(typeFormat.IsEffectivelyEnabled);

            var statsCard = view.FindControl<StackPanel>("StatsPanel")!.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
            Assert.Equal(0, Grid.GetColumn(statsCard));
            window.Close();

            // A value: the async lambda is then awaited to its end (Dispatch has no overload for a plain Task, so its
            // assertions after the first await would otherwise never reach the test).
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
            Assert.Equal(2, Grid.GetRow(list));
            Assert.True(column.RowDefinitions[2].Height.IsStar);
            Assert.True(Inside(view.FindControl<ToolbarButton>("NewAreaButton")!));
            Assert.True(Inside(view.FindControl<Button>("StreamButton")!));
            Assert.True(Inside(column.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save"))));
            Headless.Capture(window, $"ocr-eight-areas-{Headless.Appearance}.png");
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
            Assert.Equal(3, editor.ShownLines.Count);

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
