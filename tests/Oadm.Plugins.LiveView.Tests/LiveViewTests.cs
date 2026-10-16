using System.Diagnostics;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Oadm.Plugins.LiveView.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.LiveView.Tests;

/// <summary>Live View: the stored views and their rules, the layout of a view, and the page.</summary>
public sealed class LiveViewTests(ITestOutputHelper output)
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static SaveViewRequest New(string name, params ViewTile[] tiles) => new(null, name, 0, tiles);

    // ------------------------------------------------------------------ server part

    [Fact]
    public async Task Views_are_stored_sorted_by_name_versioned_and_announced()
    {
        using var host = new PluginHost();
        var hall = await host.Plugin.SaveAsync(New("Hall", new ViewTile(A, 0, 0, 0, 10, 10)), CancellationToken.None);
        var entrance = await host.Plugin.SaveAsync(New(" Entrance ", new ViewTile(B, 2, 10, 0, 10, 10)), CancellationToken.None);
        Assert.Equal(1, hall.Version);
        Assert.Equal("Entrance", entrance.Name); // trimmed

        var list = await host.Plugin.ListAsync(CancellationToken.None);
        Assert.Equal(["Entrance", "Hall"], list.Views.Select(v => v.Name));
        Assert.Equal(2, host.Events.Published.Count(e => e.Topic == LiveViewPluginInfo.ViewsTopic));

        var changed = await host.Plugin.SaveAsync(new SaveViewRequest(hall.Id, "Hall", hall.Version, []), CancellationToken.None);
        Assert.Equal(2, changed.Version);
        Assert.Empty(changed.Tiles);

        // A second plugin instance (server restart) reads the same views.
        using var restarted = new PluginHost();
        restarted.Settings.Values["views"] = host.Settings.Values["views"];
        Assert.Equal(2, (await restarted.Plugin.ListAsync(CancellationToken.None)).Views.Count);

        await host.Plugin.DeleteAsync(hall.Id, CancellationToken.None);
        Assert.Equal(["Entrance"], (await host.Plugin.ListAsync(CancellationToken.None)).Views.Select(v => v.Name));
    }

    [Fact]
    public async Task A_save_on_an_older_version_a_taken_name_and_a_bad_layout_are_refused()
    {
        using var host = new PluginHost();
        var hall = await host.Plugin.SaveAsync(New("Hall"), CancellationToken.None);
        await host.Plugin.SaveAsync(new SaveViewRequest(hall.Id, "Hall", hall.Version, [new ViewTile(A, 0, 0, 0, 4, 4)]), CancellationToken.None);

        var stale = await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(new SaveViewRequest(hall.Id, "Hall", hall.Version, []), CancellationToken.None));
        Assert.Equal(ViewRules.ChangedMeanwhile("Hall"), stale.Message);
        Assert.Equal(ViewRules.NameTaken("hall"), (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(New("hall"), CancellationToken.None))).Message);
        Assert.Equal(ViewRules.NameMissing, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(New("  "), CancellationToken.None))).Message);
        Assert.Equal(ViewRules.Overlap, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(
            New("Two", new ViewTile(A, 0, 0, 0, 4, 4), new ViewTile(B, 0, 3, 3, 4, 4)), CancellationToken.None))).Message);
        Assert.Equal(ViewRules.Outside, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(
            New("Out", new ViewTile(A, 0, 18, 0, 4, 4)), CancellationToken.None))).Message);
        Assert.Equal(ViewRules.Outside, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(
            New("Tiny", new ViewTile(A, 0, 0, 0, 1, 4)), CancellationToken.None))).Message);
        var seventeen = Enumerable.Range(0, 17).Select(i => new ViewTile(Guid.NewGuid(), 0, i % 5 * 4, i / 5 * 4, 2, 2)).ToArray();
        Assert.Equal(ViewRules.TooManyTiles, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(New("Wall", seventeen), CancellationToken.None))).Message);
        Assert.Equal(ViewRules.Gone, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(new SaveViewRequest("nope", "X", 1, []), CancellationToken.None))).Message);

        for (var i = 1; i < LiveViewPluginInfo.MaxViews; i++)
        {
            await host.Plugin.SaveAsync(New($"V{i}"), CancellationToken.None);
        }

        Assert.Equal(ViewRules.TooManyViews, (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(New("One more"), CancellationToken.None))).Message);
    }

    [Fact]
    public void Reading_needs_the_page_and_changing_needs_Change_views_which_the_Viewer_lacks()
    {
        using var plugin = new LiveViewPlugin();
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission("list"));
        Assert.Equal(LiveViewPluginInfo.ManageKey, plugin.RequiredPermission("save"));
        Assert.Equal(LiveViewPluginInfo.ManageKey, plugin.RequiredPermission("delete"));
        Assert.True(plugin.IsAudited("save"));
        Assert.False(plugin.IsAudited("list"));
        var manage = Assert.Single(plugin.Permissions);
        Assert.True(manage.OperatorDefault);
        Assert.False(manage.ViewerDefault);
        Assert.Equal(CorePluginGroup.Monitoring, plugin.Group);
    }

    // ------------------------------------------------------------------ layout

    [Fact]
    public void A_dropped_camera_takes_the_largest_free_square_at_the_drop_point()
    {
        var first = ViewLayout.Place([], A, 0, 3, 3)!;
        Assert.Equal((0, 0, 10, 10), (first.X, first.Y, first.Width, first.Height));

        var second = ViewLayout.Place([first], B, 0, 15, 4)!;
        Assert.Equal((10, 0, 10, 10), (second.X, second.Y, second.Width, second.Height));

        // On a taken cell: the nearest largest free square.
        var third = ViewLayout.Place([first, second], Guid.NewGuid(), 0, 5, 5)!;
        Assert.Equal((0, 10, 10, 10), Rect(third));

        // A gap of 3 x 3 cells takes a 3 x 3 camera.
        ViewTile[] almostFull = [new(A, 0, 0, 0, 20, 17), new(B, 0, 3, 17, 17, 3)];
        var small = ViewLayout.Place(almostFull, Guid.NewGuid(), 0, 1, 18)!;
        Assert.Equal((0, 17, 3, 3), (small.X, small.Y, small.Width, small.Height));
        Assert.Null(ViewLayout.Place([new ViewTile(A, 0, 0, 0, 20, 20)], B, 0, 5, 5));

        var sixteen = Enumerable.Range(0, 16).Select(i => new ViewTile(Guid.NewGuid(), 0, i % 4 * 2, i / 4 * 2, 2, 2)).ToList();
        Assert.Null(ViewLayout.Place(sixteen, A, 0, 15, 15)); // 16 cameras at most
    }

    [Fact]
    public void Moving_and_resizing_stays_inside_the_grid_and_never_overlaps()
    {
        var tile = new ViewTile(A, 0, 0, 0, 10, 10);
        ViewTile[] others = [new(B, 0, 10, 0, 10, 10)];
        Assert.Null(ViewLayout.Fit(others, tile, 5, 0, 10, 10)); // onto the other camera
        Assert.Equal((0, 10, 10, 10), Rect(ViewLayout.Fit(others, tile, -3, 14, 10, 10)!)); // clamped inside
        Assert.Equal((0, 0, 2, 2), Rect(ViewLayout.Fit(others, tile, 0, 0, 0, 1)!)); // at least 2 x 2
        Assert.Equal((0, 0, 10, 20), Rect(ViewLayout.Fit(others, tile, 0, 0, 10, 40)!));
    }

    private static (int, int, int, int) Rect(ViewTile t) => (t.X, t.Y, t.Width, t.Height);

    // ------------------------------------------------------------------ page

    /// <summary>The page runs on the UI thread like in the client: server events and saves never race.</summary>
    private static readonly HeadlessUnitTestSession Ui = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

    private static async Task OnUi(Func<Task> test) =>
        Assert.True(await Ui.Dispatch(async () =>
        {
            await test();
            return true;
        }, CancellationToken.None));

    private static (PluginHost Host, TestContext Ctx, LiveViewPageViewModel Vm, TestDevice Cam1, TestDevice Cam2) Page(params ViewTile[] _)
    {
        var host = new PluginHost();
        var cam1 = new TestDevice("10.0.0.48", "P3265-V", "Building A");
        var cam2 = new TestDevice("10.0.0.9", "Q6135-LE");
        var io = new TestDevice("10.0.0.120", "A9210") { Category = DeviceCategory.IoModule };
        var ctx = new TestContext(host, [cam1, cam2, io]) { Video = new FakeVideoAccess() };
        return (host, ctx, new LiveViewPageViewModel(ctx), cam1, cam2);
    }

    [Fact]
    public Task A_new_view_is_edited_filled_saved_and_opened_by_everyone() => OnUi(async () =>
    {
        var (host, ctx, vm, cam1, cam2) = Page();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded));
            Assert.True(vm.ShowNoViews);
            Assert.Equal(["10.0.0.48", "10.0.0.9"], vm.Tree.DeviceRows.Select(r => r.Address)); // only cameras, Building A first

            vm.NewViewCommand.Execute(null);
            var tab = vm.ActiveTab!;
            Assert.True(tab.IsEditing);
            Assert.Equal("View 1", tab.Name);
            Assert.True(tab.ShowEmptyHint);

            Assert.True(tab.Drop(cam1, 2, 2));
            vm.ClickCamera(cam2); // a click on a camera while editing: the first free place
            Assert.Equal(2, tab.Tiles.Count);
            var video = (FakeVideo)tab.Tiles[0].Video!;
            Assert.Equal(cam1.Id, video.Shown);
            Assert.False(video.Place.ControlsOpen);
            Assert.True(video.Place.FullscreenControlsOpen);

            ((FakeVideo)tab.Tiles[1].Video!).Choose(2); // the channel chosen while editing is saved
            tab.Name = "Entrance";
            await vm.SaveViewCommand.ExecuteAsync(null);
            Assert.False(tab.IsEditing);
            Assert.Null(tab.Error);
            Assert.Equal(["Entrance"], vm.Views.Select(v => v.Name));
            var stored = (await host.Plugin.ListAsync(CancellationToken.None)).Views.Single();
            Assert.Equal(2, stored.Tiles.Single(t => t.DeviceId == cam2.Id).Camera);

            // Another page (another user) opens it from the list.
            using var other = new LiveViewPageViewModel(new TestContext(host, [cam1, cam2]) { Video = new FakeVideoAccess() });
            Assert.True(await Headless.UntilAsync(() => other.Views.Count == 1));
            other.OpenViewCommand.Execute(other.Views[0]);
            Assert.Equal(2, other.ActiveTab!.Tiles.Count);
            Assert.False(other.ActiveTab.IsEditing);
            Assert.True(other.Views[0].IsActive);
        }
    });

    [Fact]
    public Task A_click_on_a_camera_outside_editing_opens_it_alone_in_its_own_tab() => OnUi(async () =>
    {
        var (host, _, vm, cam1, cam2) = Page();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded));
            vm.ClickCamera(cam1);
            var single = vm.ActiveTab!;
            Assert.True(single.IsSingleCamera);
            Assert.False(single.IsEditing);
            Assert.False(vm.CanEdit);
            Assert.Equal("10.0.0.48", single.Title);
            var tile = Assert.Single(single.Tiles);
            Assert.Equal(new ViewTile(cam1.Id, 0, 0, 0, LiveViewPluginInfo.GridSize, LiveViewPluginInfo.GridSize), tile.ToTile());
            Assert.Equal(cam1.Id, ((FakeVideo)tile.Video!).Shown);

            vm.ClickCamera(cam2);
            Assert.Equal(2, vm.Tabs.Count);
            vm.ClickCamera(cam1); // the same camera again: its tab to the front, no second tab
            Assert.Same(single, vm.ActiveTab);
            Assert.Equal(2, vm.Tabs.Count);

            await single.CloseCommand.ExecuteAsync(null); // nothing to save: closes without asking
            Assert.Single(vm.Tabs);
        }
    });

    [Fact]
    public Task Cancel_puts_the_stored_view_back_and_a_change_by_another_user_reaches_open_tabs() => OnUi(async () =>
    {
        var (host, ctx, vm, cam1, cam2) = Page();
        using (host)
        using (vm)
        {
            var saved = await host.Plugin.SaveAsync(New("Hall", new ViewTile(cam1.Id, 0, 0, 0, 10, 10)), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 1));
            vm.OpenViewCommand.Execute(vm.Views[0]);
            var tab = vm.ActiveTab!;
            var picture = tab.Tiles[0].Video;

            vm.EditViewCommand.Execute(vm.Views[0]); // the pen of the row
            Assert.True(tab.TryFit(tab.Tiles[0], 5, 5, 4, 4));
            tab.Drop(cam2, 15, 15);
            tab.Name = "Changed";
            vm.CancelEditCommand.Execute(null);
            Assert.False(tab.IsEditing);
            Assert.Equal("Hall", tab.Name);
            Assert.Equal((0, 0, 10, 10), (tab.Tiles.Single().X, tab.Tiles[0].Y, tab.Tiles[0].Width, tab.Tiles[0].Height));
            Assert.Same(picture, tab.Tiles[0].Video); // the camera kept its picture

            // Another user adds a camera: the open tab follows; deleting the view closes it.
            await host.Plugin.SaveAsync(new SaveViewRequest(saved.Id, "Hall", saved.Version, [new ViewTile(cam1.Id, 0, 0, 0, 10, 10), new ViewTile(cam2.Id, 0, 10, 10, 10, 10)]), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => tab.Tiles.Count == 2));
            Assert.Same(picture, tab.Tiles[0].Video);
            await host.Plugin.DeleteAsync(saved.Id, CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Tabs.Count == 0 && vm.Views.Count == 0));
            Assert.True(((FakeVideo)picture!).IsDisposed);
        }
    });

    [Fact]
    public Task A_save_after_another_users_change_is_refused_below_the_toolbar() => OnUi(async () =>
    {
        var (host, ctx, vm, cam1, _) = Page();
        using (host)
        using (vm)
        {
            var saved = await host.Plugin.SaveAsync(New("Hall"), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 1));
            vm.EditViewCommand.Execute(vm.Views[0]); // opens the tab in edit mode
            Assert.True(vm.ActiveTab!.IsEditing);
            vm.ActiveTab.Drop(cam1, 0, 0);
            await host.Plugin.SaveAsync(new SaveViewRequest(saved.Id, "Hall", saved.Version, []), CancellationToken.None);
            await Task.Delay(50);

            await vm.SaveViewCommand.ExecuteAsync(null);
            Assert.Equal(ViewRules.ChangedMeanwhile("Hall"), vm.ActiveTab.Error);
            Assert.True(vm.ActiveTab.IsEditing); // the changes stay; Cancel discards them
        }
    });

    [Fact]
    public Task The_bin_of_a_row_asks_then_deletes_the_view_for_everyone_and_closes_its_tab() => OnUi(async () =>
    {
        var (host, ctx, vm, _, _) = Page();
        using (host)
        using (vm)
        {
            await host.Plugin.SaveAsync(New("Hall"), CancellationToken.None);
            await host.Plugin.SaveAsync(New("Yard"), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 2));
            vm.OpenViewCommand.Execute(vm.Views[0]);

            ctx.Confirm = false;
            await vm.DeleteViewCommand.ExecuteAsync(vm.Views[0]);
            Assert.Equal(2, (await host.Plugin.ListAsync(CancellationToken.None)).Views.Count); // kept
            ctx.Confirm = true;
            await vm.DeleteViewCommand.ExecuteAsync(vm.Views[0]);
            Assert.Equal("Delete the view Hall for every user?", ctx.Confirmations[^1]);
            Assert.Equal(["Yard"], (await host.Plugin.ListAsync(CancellationToken.None)).Views.Select(v => v.Name));
            Assert.Empty(vm.Tabs);
            Assert.Equal(["Yard"], vm.Views.Select(v => v.Name));
        }
    });

    [Fact]
    public Task Names_are_checked_below_the_field_and_closing_with_changes_asks() => OnUi(async () =>
    {
        var (host, ctx, vm, cam1, _) = Page();
        using (host)
        using (vm)
        {
            await host.Plugin.SaveAsync(New("Hall"), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 1));
            vm.NewViewCommand.Execute(null);
            var tab = vm.ActiveTab!;
            tab.Name = "hall";
            Assert.Equal(ViewRules.NameTaken("hall"), tab.GetErrors(nameof(ViewTab.Name)).Cast<object>().Single().ToString());
            Assert.False(vm.SaveViewCommand.CanExecute(null));
            tab.Name = "Yard";
            Assert.True(vm.SaveViewCommand.CanExecute(null));

            tab.Drop(cam1, 0, 0);
            ctx.Confirm = false;
            await vm.CloseAllTabsCommand.ExecuteAsync(null);
            Assert.Single(vm.Tabs); // kept
            Assert.Single(ctx.Confirmations);
            ctx.Confirm = true;
            await tab.CloseCommand.ExecuteAsync(null);
            Assert.Empty(vm.Tabs);
        }
    });

    [Fact]
    public Task Without_Change_views_there_is_no_editing_and_without_Live_view_no_picture() => OnUi(async () =>
    {
        var (host, ctx, vm, cam1, _) = Page();
        using (host)
        using (vm)
        {
            await host.Plugin.SaveAsync(New("Hall", new ViewTile(cam1.Id, 0, 0, 0, 10, 10)), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 1));
            vm.NewViewCommand.Execute(null); // a new view in edit mode, then the role changes
            ctx.Denied.Add(PermissionKeys.Of(LiveViewPluginInfo.PluginId, LiveViewPluginInfo.ManageKey));
            ctx.RaisePermissionsChanged();
            Assert.Empty(vm.Tabs); // the unsaved view closed
            Assert.False(vm.NewViewCommand.CanExecute(null));
            vm.OpenViewCommand.Execute(vm.Views[0]);
            Assert.False(vm.EditViewCommand.CanExecute(vm.Views[0]));
            Assert.False(vm.DeleteViewCommand.CanExecute(vm.Views[0]));
            Assert.NotNull(vm.ActiveTab!.Tiles[0].Video);

            ctx.Denied.Add(HostPermissionKeys.DevicesLiveView);
            ctx.RaisePermissionsChanged();
            Assert.Null(vm.ActiveTab.Tiles[0].Video);
            Assert.Equal(LiveViewTexts.NotAllowed, vm.PictureProblem);
        }
    });

    [Fact]
    public Task A_removed_camera_keeps_its_place_and_says_so() => OnUi(async () =>
    {
        var (host, ctx, vm, cam1, _) = Page();
        using (host)
        using (vm)
        {
            await host.Plugin.SaveAsync(New("Hall", new ViewTile(cam1.Id, 0, 0, 0, 10, 10)), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 1));
            vm.OpenViewCommand.Execute(vm.Views[0]);
            var tile = vm.ActiveTab!.Tiles[0];
            ctx.Items.Remove(cam1);
            ctx.RaiseDevicesChanged();
            Assert.True(tile.IsMissing);
            Assert.Equal(DeviceTexts.Removed(cam1.Id.ToString("N")), tile.Title);
            Assert.Null(((FakeVideo)tile.Video!).Shown);
        }
    });

    [Fact]
    [Trait("Category", "Perf")]
    public void The_camera_tree_of_5000_devices_builds_and_searches_fast()
    {
        using var host = new PluginHost();
        var devices = Enumerable.Range(0, 5000).Select(i => (IDeviceInfo)new TestDevice($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", "P3265-V", i % 3 == 0 ? ["Building A"] : [])
        { Category = i % 10 == 0 ? DeviceCategory.IoModule : DeviceCategory.Camera }).ToList();
        var ctx = new TestContext(host, devices);
        var watch = Stopwatch.StartNew();
        using var vm = new LiveViewPageViewModel(ctx);
        var build = watch.ElapsedMilliseconds;
        watch.Restart();
        vm.SearchText = "10.0.1";
        vm.SearchText = string.Empty;
        output.WriteLine($"build {build} ms, search + clear {watch.ElapsedMilliseconds} ms, {vm.Tree.Rows.Count} rows");
        Assert.Equal(4500, vm.Tree.DeviceRows.Select(r => r.Device.Id).Distinct().Count());
        Assert.InRange(build, 0, 2000);
    }

    // ------------------------------------------------------------------ headless

    [Fact]
    public async Task The_page_shows_a_view_with_live_cameras_and_the_edit_frame()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var done = await session.Dispatch(async () =>
        {
            using var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var devices = (await api.ListDevicesAsync(CancellationToken.None)).Select(d => (IDeviceInfo)new TestDevice(d.Address, d.Model, [.. d.Tags])
            {
                Id = Guid.Parse(d.Id),
                Category = (DeviceCategory)(int)d.Category,
                Status = DeviceStatus.Ok,
            }).ToList();
            var cameras = devices.Where(d => d.HasVideo).Take(3).ToList();
            using var host = new PluginHost();
            await host.Plugin.SaveAsync(New("Entrance",
                new ViewTile(cameras[0].Id, 0, 0, 0, 12, 12),
                new ViewTile(cameras[1].Id, 0, 12, 0, 8, 6),
                new ViewTile(cameras[2].Id, 0, 12, 6, 8, 6)), CancellationToken.None);
            await host.Plugin.SaveAsync(New("Parking"), CancellationToken.None);

            var settings = new Oadm.Client.LiveView.LiveVideoSettings();
            var access = new Oadm.Client.LiveView.LiveVideoAccess(api, new Oadm.Client.LiveView.FfmpegVideoDecoderFactory(), new Oadm.Client.Infrastructure.AvaloniaUiDispatcher(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.LiveView.LiveVideoAccess>.Instance, settings);
            var ctx = new TestContext(host, devices) { Video = access };
            var view = (LiveViewPageView)new LiveViewPage().CreateView(ctx);
            var vm = (LiveViewPageViewModel)view.DataContext!;
            var window = Headless.Host(view);
            window.Show();
            Assert.True(await Headless.UntilAsync(() => vm.Views.Count == 2));
            vm.OpenViewCommand.Execute(vm.Views[0]);
            Headless.Pump();
            var tiles = vm.ActiveTab!.Tiles;
            Assert.True(await Headless.UntilAsync(() => tiles.All(t => ((Oadm.Client.LiveView.LiveVideoViewModel)t.Video!.View.DataContext!).Image is not null), 30));
            Headless.Capture(window, "plugin-live-view.png");

            // The camera under the pointer gets a thin purple frame (not while editing: the edit frame has its own).
            var frame = view.GetVisualDescendants().OfType<TileFrame>().First(f => f.Item == tiles[0]);
            var hover = frame.GetVisualChildren().OfType<Border>().Single(b => b.Classes.Contains("tileHover"));
            Assert.False(hover.IsVisible);
            window.MouseMove(frame.TranslatePoint(new Point(frame.Bounds.Width / 2, frame.Bounds.Height / 2), window)!.Value);
            Headless.Pump();
            Assert.True(hover.IsVisible);
            Headless.Capture(window, "plugin-live-view-hover.png");

            // Edit: the cell lines and a frame per camera; a camera dragged in lands on the free cells.
            vm.EditViewCommand.Execute(vm.Views[0]);
            Headless.Pump();
            var grid = view.ActiveGrid!;
            var free = new Point(grid.Bounds.Width * 0.3, grid.Bounds.Height * 0.8);
            var camera = vm.Tree.DeviceRows.First(r => r.Device.Id == devices.Where(d => d.HasVideo).ElementAt(3).Id);
            var preview = LiveViewPageView.PreviewAt(grid, vm.ActiveTab, camera, free);
            Assert.NotNull(preview);
            grid.ShowPreview(preview!.Rect, camera.Address); // the frame where it would land, while dragging
            Headless.Pump();
            Headless.Capture(window, "plugin-live-view-drop.png");
            grid.ShowPreview(null);
            Assert.Null(LiveViewPageView.PreviewAt(grid, vm.ActiveTab, camera, new Point(-5, 10)));
            Assert.True(LiveViewPageView.DropOn(grid, vm.ActiveTab, vm.Tree.DeviceRows.First(r => r.Device.Id == devices.Where(d => d.HasVideo).ElementAt(3).Id), free));
            Assert.Equal(4, tiles.Count);
            Assert.Equal(preview, tiles[3].ToTile()); // it landed where the preview showed
            Assert.False(LiveViewPageView.DropOn(grid, vm.ActiveTab, vm.Tree.DeviceRows[0], new Point(-5, 10))); // outside the grid
            Headless.Pump();
            Assert.False(view.GetVisualDescendants().OfType<TileFrame>().First().GetVisualChildren().OfType<Border>().Single(b => b.Classes.Contains("tileHover")).IsVisible);
            Headless.Capture(window, "plugin-live-view-edit.png");

            // The left card collapses like the navigation rail: the views stay as icons, the grid gets the room.
            vm.CancelEditCommand.Execute(null);
            var before = grid.Bounds.Width;
            vm.TogglePanelCommand.Execute(null);
            Headless.Pump();
            Assert.True(view.ActiveGrid!.Bounds.Width > before + 200);
            Headless.Capture(window, "plugin-live-view-collapsed.png");

            // A click on a camera outside editing: the camera alone in its own tab.
            vm.TogglePanelCommand.Execute(null);
            vm.ClickCamera(cameras[1]);
            Headless.Pump();
            Assert.True(vm.ActiveTab!.IsSingleCamera);
            var single = vm.ActiveTab.Tiles.Single();
            Assert.True(await Headless.UntilAsync(() => ((Oadm.Client.LiveView.LiveVideoViewModel)single.Video!.View.DataContext!).Image is not null, 30));
            Headless.Capture(window, "plugin-live-view-single.png");
            window.Close();
            vm.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
