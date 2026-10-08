using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Client.Tags;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// The Tags column, group mode and the Tags dialog rendered headless against the fake server's tags. Set
/// OADM_SCREENSHOT_DIR for client-tags-column.png, client-tags-group-mode.png and client-tags-dialog.png.
/// </summary>
public sealed class DeviceTagsHeadlessTests
{
    private static async Task<DevicesFixture> FakeFixtureAsync(FakeOadmApi api, Shell.UserSession? session = null)
    {
        var fixture = new DevicesFixture(api, session: session);
        fixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
        fixture.Store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
        return fixture;
    }

    [Fact]
    public async Task Tags_column_sorts_and_shows_chips_and_group_mode_shows_a_group_per_tag()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            using DevicesFixture fixture = await FakeFixtureAsync(api);
            DevicesViewModel vm = fixture.Devices;
            var window = new Window { Width = 1600, Height = 800, Content = new DevicesView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            DataGridColumn tags = grid.Columns.Single(c => c.Header as string == "Tags");
            Assert.Equal(6, tags.DisplayIndex); // after Firmware
            Assert.Contains(vm.Columns.Choosable, c => c.Key == "tags");

            // Chips in the tag colors; the cell of a device with three tags.
            List<TagChipList> chipLists = grid.GetVisualDescendants().OfType<TagChipList>().ToList();
            Assert.NotEmpty(chipLists);
            TagChipList three = chipLists.First(l => l.Tags?.Count == 3);
            Assert.Equal(["Building C", "Outdoor", "PTZ"], three.Tags!.Select(t => t.Name));
            Assert.Equal("Building C" + Environment.NewLine + "Outdoor" + Environment.NewLine + "PTZ", ToolTip.GetTip(three));
            Border chip = three.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tagChip"));
            Assert.Equal(TagPalette.Brush(TagColor.Green), chip.GetVisualDescendants().OfType<TextBlock>().First().Foreground);
            Capture(window, outDir, "client-tags-column.png");

            // A narrow column: "+N" for the chips that do not fit.
            tags.Width = new DataGridLength(110);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(three.ShownCount < 3);
            Assert.Equal("+" + (3 - three.ShownCount), three.MoreText);
            tags.Width = new DataGridLength(150, DataGridLengthUnitType.Star);

            // Sortable by the sorted tag list text.
            tags.Sort(ListSortDirection.Ascending);
            Dispatcher.UIThread.RunJobs();
            List<string> sorted = grid.GetVisualDescendants().OfType<DataGridRow>()
                .OrderBy(r => r.Bounds.Top).Select(r => ((IDeviceGridItem)r.DataContext!).Row.TagsText).ToList();
            Assert.Equal(sorted.Order(StringComparer.CurrentCulture).ToList(), sorted);
            tags.ClearSort();

            // Group mode: a header per tag, a device under every one of its tags, "No tag" last.
            vm.GroupByTag = true;
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("tagGroups", grid.Classes);
            Assert.IsType<Avalonia.Collections.DataGridCollectionView>(grid.ItemsSource);
            List<string> headers = grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>()
                .OrderBy(h => h.Bounds.Top)
                .Select(h => h.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("groupHeader")).Text ?? "").ToList();
            Assert.Equal("Building A · 5 devices", headers[0]);
            Assert.Equal(["Building A", "Building C", "Outdoor", "PTZ", "No tag"], vm.TagGrouping.Groups.Select(g => g.Name));
            Assert.Equal(vm.TagGrouping.Rows.Count, vm.FilteredDevices.Sum(d => Math.Max(1, d.Tags.Count)));
            Button toggle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "GroupByTagButton");
            Assert.Contains("active", toggle.Classes);
            Assert.Equal("Group by tag", ToolTip.GetTip(toggle));

            // Select all: every device once.
            grid.SelectAll();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.FilteredDevices.Count, vm.SelectedDevices.Count);
            grid.SelectedItems.Clear();
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "client-tags-group-mode.png");

            vm.GroupByTag = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.FilteredDevices, grid.ItemsSource);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Tags_dialog_renders_three_states_the_new_tag_form_and_admin_actions()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            using DevicesFixture fixture = await FakeFixtureAsync(api);
            List<DeviceRowViewModel> selected = fixture.Store.Devices.Take(3).ToList(); // Building A, Building A, Building A + PTZ
            var dialog = new DeviceTagsViewModel(api, fixture.Store.Tags, selected, fixture.Store.Devices, isAdmin: true, (_, _, _) => Task.FromResult(true));
            Assert.True(dialog.Find("Building A")!.IsChecked);
            Assert.Null(dialog.Find("PTZ")!.IsChecked);
            Assert.False(dialog.Find("Outdoor")!.IsChecked);

            var window = new DeviceTagsWindow();
            window.Attach(dialog);
            window.Show();
            dialog.StartNewTagCommand.Execute(null);
            dialog.NewTagName = "Building a";
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("A tag named \"Building a\" already exists.", texts);
            Assert.Contains("Building C", texts);
            List<CheckBox> boxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Contains(boxes, b => b.IsChecked is null);
            Assert.Equal(dialog.Choices.Count, window.GetVisualDescendants().OfType<Button>().Count(b => ToolTip.GetTip(b) as string == "Delete tag"));
            Assert.Equal(8, window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("colorSwatch") && b.IsEffectivelyVisible));
            Capture(window, outDir, "client-tags-dialog.png");

            // Operators see no rename and delete buttons.
            window.Close();
            var operatorDialog = new DeviceTagsViewModel(api, fixture.Store.Tags, selected, fixture.Store.Devices, isAdmin: false, (_, _, _) => Task.FromResult(true));
            var operatorWindow = new DeviceTagsWindow();
            operatorWindow.Attach(operatorDialog);
            operatorWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(operatorWindow.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == "Delete tag" && b.IsEffectivelyVisible);
            operatorWindow.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
