using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Controls;
using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Client.Plugins.AddOns;
using Oadm.Client.Settings;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// The tiles of the Plugins page (docs/spec/plugins.md "Plugins on and off"): search, the quick filters with their
/// counts, a click or a key on a tile, the ring of a running switch, a refusal in the tile, a locked PRO plugin.
/// Screenshots with OADM_SCREENSHOT_DIR (OADM_TEST_APPEARANCE=Light for the light ones).
/// </summary>
public sealed class PluginTilesTests
{
    /// <summary>The 22 plugin packages a server lists today (the approved draft): name, id, group, on, description.</summary>
    private static readonly (string Name, string Id, string Group, bool On, string Description)[] Today =
    [
        ("DHCP server", "oadm.dhcp-server", "Servers", true, "Hands out IP addresses and can add new Axis devices automatically."),
        ("NTP server", "oadm.ntp-server", "Servers", true, "Serves the time to devices from this server."),
        ("Action Rule Editor", "oadm.action-rules", "Automation", false, "Changes, turns on or off and deletes the action rules of many cameras at once."),
        ("Device triggers", "oadm.device-triggers", "Automation", false, "Uses events of the devices in event rules."),
        ("VAPIX Commander", "oadm.vapix-commander", "Automation", true, "Sends VAPIX commands to many devices at once."),
        ("Hardening scan", "oadm.hardening-scan", "Security", false, "Checks devices against the AXIS OS hardening guide."),
        ("VPN Fleet", "oadm.vpn-fleet", "Security", false, "Sets up, installs and upgrades the camera VPN apps Tailscale, NetBird, ZeroTier, WireGuard and OpenVPN."),
        ("Dashboards", "oadm.dashboards", "Monitoring", true, "Live values of device triggers and live video on dashboards."),
        ("I/O Monitor", "oadm.io-monitor", "Monitoring", false, "Shows the inputs and outputs of a device, live."),
        ("Image Health Dashboard", "oadm.image-health", "Monitoring", false, "Shows blur, blocked and redirected views from AXIS Image Health Analytics."),
        ("Live View", "oadm.live-view", "Monitoring", true, "Saved views of several cameras at once, opened in tabs."),
        ("Load test", "oadm.load-test", "Monitoring", false, "Puts the chosen cameras under load and shows whether the network and the cameras carry it."),
        ("Metadata Monitor", "oadm.metadata-monitor", "Monitoring", false, "Shows the events a camera sends, live."),
        ("Text recognition", "oadm.ocr", "Vision", false, "Reads text in camera pictures."),
        ("Vision Center", "oadm.vision", "Vision", true, "Finds, tracks and counts objects in camera pictures."),
        ("License Plate Center", "oadm.license-plates", "Integrations", false, "One plate list for every camera with License Plate Verifier."),
        ("Snapshot report", "oadm.snapshot-report", "Reporting", true, "Snapshots of every camera and a PDF maintenance report."),
        ("Application management", "oadm.app-management", "Maintenance", false, "Shows the applications of many devices at once and starts, stops, installs or removes them."),
        ("Edge Recording Export", "oadm.edge-recording-export", "Maintenance", false, "Downloads the recordings on the cameras’ SD cards to this computer."),
        ("Edge storage", "oadm.edge-storage", "Maintenance", false, "Shows the SD cards of the cameras and formats, checks and encrypts them."),
        ("Web View", "oadm.web-view", "Utilities", true, "Opens the web interface of the devices in tabs, logged in with the stored login."),
        ("Text overlays", "oadm.overlays", "Extensions", true, "Add, replace or remove text overlays on cameras."),
    ];

    private const string Refusal = "The server is not reachable.";

    [Fact]
    public async Task The_search_needs_every_word_in_the_name_the_description_or_the_group()
    {
        var (_, _, vm) = await PageAsync();

        vm.SearchText = "sd card";
        Assert.Equal(["Edge Recording Export", "Edge storage"], Shown(vm));
        Assert.Equal((2, 0, 2, 0), Counts(vm));

        // Every word must occur: "sd" alone finds more, "sd vpn" nothing.
        vm.SearchText = "SD";
        Assert.Contains("Edge storage", Shown(vm));
        vm.SearchText = "sd vpn";
        Assert.Empty(Shown(vm));
        Assert.True(vm.NothingFound);

        // A description and a group match too; "plates" finds the plate list, case does not matter.
        vm.SearchText = "vision text";
        Assert.Equal(["Text recognition"], Shown(vm));
        vm.SearchText = "PLATES";
        Assert.Equal(["License Plate Center"], Shown(vm));
        vm.SearchText = "monitoring live";
        Assert.Equal(["Dashboards", "I/O Monitor", "Live View", "Metadata Monitor"], Shown(vm));
        Assert.All(vm.TileGroups, g => Assert.Equal("Monitoring", g.Title));

        // The words reach every tile for the marks in the name and the description.
        Assert.All(vm.Plugins, p => Assert.Equal(["monitoring", "live"], p.SearchWords));
        Assert.Equal(
            [("Shows the events a camera sends, ", false), ("live", true), (".", false)],
            HighlightText.Split("Shows the events a camera sends, live.", vm.Plugins[0].SearchWords));
        Assert.Equal([("Edge ", false), ("storage", true)], HighlightText.Split("Edge storage", ["STOR", "rage"]));
        Assert.Equal([("Edge storage", false)], HighlightText.Split("Edge storage", []));

        vm.SearchText = string.Empty;
        Assert.Equal(22, Shown(vm).Count);
        Assert.False(vm.NothingFound);
    }

    [Fact]
    public async Task The_quick_filters_count_and_choose_the_tiles()
    {
        var (_, _, vm) = await PageAsync();

        Assert.Equal(["All", "On", "Off", "Optional"], vm.Filters.Select(f => f.Label));
        Assert.Equal((22, 9, 13, 2), Counts(vm));
        Assert.Equal("9 of 22 on", vm.CountText);
        Assert.True(vm.Filters[0].IsSelected);

        // The groups keep the rail order and never show an empty heading.
        Assert.Equal(["Servers", "Automation", "Security", "Monitoring", "Vision", "Reporting", "Maintenance", "Integrations", "Utilities", "Extensions"],
            vm.TileGroups.Select(g => g.Title));

        vm.Filters[2].SelectCommand.Execute(null);
        Assert.Equal(PluginFilter.Off, vm.SelectedFilter);
        Assert.Equal([false, false, true, false], vm.Filters.Select(f => f.IsSelected));
        Assert.Equal(13, Shown(vm).Count);
        Assert.All(vm.TileGroups.SelectMany(g => g.Tiles), t => Assert.False(t.IsEnabled));
        Assert.DoesNotContain(vm.TileGroups, g => g.Title == "Servers");

        vm.Filters[3].SelectCommand.Execute(null);
        Assert.Equal(["Text recognition", "Vision Center"], Shown(vm));

        // The counts follow the search; the filter stays.
        vm.SearchText = "text";
        Assert.Equal((2, 1, 1, 1), Counts(vm)); // Text recognition and Text overlays
        Assert.Equal(["Text recognition"], Shown(vm));
        Assert.Equal("9 of 22 on", vm.CountText); // the header counts every plugin
    }

    [Fact]
    public async Task A_click_on_a_tile_turns_it_on_and_off_and_the_counts_follow()
    {
        var (api, _, vm) = await PageAsync();
        PluginRowViewModel hardening = Tile(vm, "oadm.hardening-scan");
        Assert.Equal("Click to turn on", hardening.TileTip);
        Assert.False(hardening.ShowCheck);

        await hardening.ToggleCommand.ExecuteAsync(null);
        await api.Received(1).SetPluginPackageEnabledAsync("oadm.hardening-scan", true, Arg.Any<CancellationToken>());
        hardening = Tile(vm, "oadm.hardening-scan");
        Assert.True(hardening.IsEnabled);
        Assert.True(hardening.ShowCheck);
        Assert.Equal("Click to turn off", hardening.TileTip);
        Assert.Equal("10 of 22 on", vm.CountText);
        Assert.Equal((22, 10, 12, 2), Counts(vm));

        await hardening.ToggleCommand.ExecuteAsync(null);
        await api.Received(1).SetPluginPackageEnabledAsync("oadm.hardening-scan", false, Arg.Any<CancellationToken>());
        Assert.False(Tile(vm, "oadm.hardening-scan").IsEnabled);
        Assert.Equal("9 of 22 on", vm.CountText);
    }

    [Fact]
    public async Task A_busy_tile_takes_no_second_click_and_a_refusal_shows_in_that_tile()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (api, _, vm) = await PageAsync(held: held.Task);
        PluginRowViewModel load = Tile(vm, "oadm.load-test");

        Task switching = load.ToggleCommand.ExecuteAsync(null);
        Assert.True(load.IsBusy);
        Assert.True(load.IsEnabled); // the state it goes to
        Assert.False(load.ShowCheck); // the ring in place of the check
        await load.ToggleCommand.ExecuteAsync(null);
        await api.Received(1).SetPluginPackageEnabledAsync("oadm.load-test", Arg.Any<bool>(), Arg.Any<CancellationToken>());

        held.SetResult();
        await switching;
        Assert.Equal((false, true), (Tile(vm, "oadm.load-test").IsBusy, Tile(vm, "oadm.load-test").IsEnabled));

        // VPN Fleet is refused: its tile goes back and says why; no other tile has an error.
        PluginRowViewModel vpn = Tile(vm, "oadm.vpn-fleet");
        await vpn.ToggleCommand.ExecuteAsync(null);
        vpn = Tile(vm, "oadm.vpn-fleet");
        Assert.False(vpn.IsEnabled);
        Assert.False(vpn.IsBusy);
        Assert.Equal($"Could not be turned on: {Refusal}", vpn.ErrorText);
        Assert.Single(vm.Plugins, p => p.HasError);
    }

    [Fact]
    public async Task A_locked_pro_plugin_explains_what_it_needs_instead_of_turning_on()
    {
        var dialogs = Substitute.For<IDialogService>();
        var (api, _, vm) = await PageAsync(dialogs: dialogs, pro: true);
        string? opened = null;
        vm.NavigateRequested += (_, key) => opened = key;
        PluginRowViewModel pro = Tile(vm, "example.pro");
        Assert.True(pro.ProLocked);
        Assert.Null(pro.TileTip); // the tile says why instead
        Assert.Equal(Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro, pro.ProDetail);

        // Cancel: nothing happens.
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await pro.ToggleCommand.ExecuteAsync(null);
        Assert.Null(opened);

        // Open Settings: the page where OADM PRO is; the plugin is never switched.
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await pro.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(Oadm.Sdk.Client.HostPages.Settings, opened);
        await dialogs.Received(2).ConfirmAsync("OADM PRO", "PRO sample needs an OADM PRO license or trial. Start the trial or import a license in Settings.", "Open Settings");
        await api.DidNotReceive().SetPluginPackageEnabledAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.False(Tile(vm, "example.pro").IsEnabled);
    }

    [Fact]
    public async Task Tiles_packing_keeps_one_width_and_gives_a_row_its_tallest_height()
    {
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            static Border Child(double height) => new() { Height = height, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
            var full = new Oadm.Sdk.Client.Controls.FlowColumns { Packing = Oadm.Sdk.Client.Controls.FlowPacking.Tiles, MinColumnWidth = 200, MaxColumns = 12 };
            var tall = Child(150);
            Border[] firstRow = [Child(100), tall, Child(100)];
            foreach (var child in firstRow)
            {
                full.Children.Add(new ContentControl { Content = child });
            }

            full.Children.Add(new ContentControl { Content = Child(80) }); // the second row
            var single = new Oadm.Sdk.Client.Controls.FlowColumns { Packing = Oadm.Sdk.Client.Controls.FlowPacking.Tiles, MinColumnWidth = 200, MaxColumns = 12 };
            single.Children.Add(new ContentControl { Content = Child(100) });
            foreach (var panel in new[] { full, single })
            {
                panel.Measure(new Avalonia.Size(660, double.PositiveInfinity));
                panel.Arrange(new Avalonia.Rect(0, 0, 660, panel.DesiredSize.Height));
            }

            // Three columns fit; one child alone keeps the width of a column, not the whole row (Rows and Columns would stretch it).
            var cells = full.Children.Select(c => c.Bounds).ToList();
            Assert.Single(cells.Select(c => c.Width).Distinct());
            Assert.Equal(cells[0].Width, single.Children[0].Bounds.Width);
            Assert.True(single.Children[0].Bounds.Width < 660 / 2.0);
            // A row is as high as its tallest child; the next row starts below it.
            Assert.Single(cells.Take(3).Select(c => c.Height).Distinct());
            Assert.Equal(cells[1].Height, cells[0].Height);
            Assert.True(cells[3].Y > cells[1].Bottom);
            Assert.Equal(cells[0].X, cells[3].X);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_locked_pro_tile_says_why()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (_, _, vm) = await PageAsync(pro: true);
            vm.SearchText = "security";
            var window = new Window { Width = 1100, Height = 420, Content = new Border { Padding = new Avalonia.Thickness(16, 12, 0, 0), Child = new PluginsPageView { DataContext = vm } } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();

            Button pro = Tiles(window).Single(b => b.DataContext is PluginRowViewModel { Id: "example.pro" });
            Assert.Null(ToolTip.GetTip(pro));
            Assert.Contains(pro.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro);
            Assert.DoesNotContain("on", pro.Classes);
            Save(window, outDir, $"plugins-pro-locked-{appearance}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Space_on_a_focused_tile_turns_it_on()
    {
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, _, vm) = await PageAsync();
            var window = new Window { Width = 1300, Height = 900, Content = new PluginsPageView { DataContext = vm } };
            window.Show();
            Pump();

            Button tile = Tiles(window).Single(b => b.DataContext is PluginRowViewModel { Id: "oadm.hardening-scan" });
            Assert.Equal("Click to turn on", ToolTip.GetTip(tile));
            Assert.True(tile.Focus(NavigationMethod.Tab));
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => Tile(vm, "oadm.hardening-scan").IsEnabled && !Tile(vm, "oadm.hardening-scan").IsBusy, "the tile turned on");
            await api.Received(1).SetPluginPackageEnabledAsync("oadm.hardening-scan", true, Arg.Any<CancellationToken>());
            Pump();
            Assert.Contains(Tiles(window), b => b.DataContext is PluginRowViewModel { Id: "oadm.hardening-scan" } && b.Classes.Contains("on"));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Plugins_page_tiles_in_their_states()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (_, addOns, vm) = await PageAsync(held: held.Task);
            var page = new PluginsPageView { DataContext = vm };
            var window = new Window { Width = 1500, Height = 1000, Content = new Border { Padding = new Avalonia.Thickness(16, 12, 0, 0), Child = page } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();

            // Every plugin as a tile under its group, the count in the header, the Optional plugins card below.
            Assert.Equal(22, Tiles(window).Count);
            List<string> texts = Visible(window);
            Assert.Contains("9 of 22 on", texts);
            Assert.Contains("Maintenance", texts);
            Assert.Contains("Optional plugins", texts);
            Assert.Equal(9, Tiles(window).Count(t => t.Classes.Contains("on")));
            Assert.Equal(9, window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.OadmIcon>().Count(i => i.Classes.Contains("tileCheck") && i.IsEffectivelyVisible));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible);
            Save(window, outDir, $"plugins-tiles-{appearance}.png");

            // Scrolled to the end: the last groups and the Optional plugins card right below them.
            ScrollViewer scroll = page.FindControl<ScrollViewer>("PageScroll")!;
            scroll.ScrollToEnd();
            Pump();
            Save(window, outDir, $"plugins-tiles-end-{appearance}.png");
            scroll.ScrollToHome();

            // A search: the matches marked in names and descriptions.
            vm.SearchText = "sd";
            Pump();
            Assert.Equal(2, Tiles(window).Count);
            Assert.Contains(window.GetVisualDescendants().OfType<HighlightText>(), h => h.Inlines is { Count: > 1 } && h.IsEffectivelyVisible);
            Save(window, outDir, $"plugins-search-sd-{appearance}.png");

            // The Off filter.
            vm.SearchText = string.Empty;
            vm.Filters[2].SelectCommand.Execute(null);
            Pump();
            Assert.Equal(13, Tiles(window).Count);
            Assert.DoesNotContain(Tiles(window), t => t.Classes.Contains("on"));
            Save(window, outDir, $"plugins-filter-off-{appearance}.png");

            // One tile busy (its ring, no check), one refused (the error in the tile).
            vm.Filters[0].SelectCommand.Execute(null);
            await Tile(vm, "oadm.vpn-fleet").ToggleCommand.ExecuteAsync(null);
            Task switching = Tile(vm, "oadm.load-test").ToggleCommand.ExecuteAsync(null);
            Pump();
            Assert.Single(window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.ProgressRing>(), r => r.IsEffectivelyVisible);
            Assert.Contains($"Could not be turned on: {Refusal}", Visible(window));
            Save(window, outDir, $"plugins-busy-and-refused-{appearance}.png");

            held.SetResult();
            await switching;
            Pump();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.ProgressRing>(), r => r.IsEffectivelyVisible);
            Assert.NotNull(addOns);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>
    /// A page over a server that lists <see cref="Today"/> (Text recognition and Vision Center installed as optional
    /// plugins); VPN Fleet is refused, Load test waits for <paramref name="held"/>.
    /// </summary>
    private static async Task<(IOadmApi Api, AddOnsViewModel AddOns, PluginsViewModel Vm)> PageAsync(Task? held = null, IDialogService? dialogs = null, bool pro = false)
    {
        var api = Substitute.For<IOadmApi>();
        var list = new PluginPackageList { ServerId = "server" };
        foreach (var p in Today)
        {
            list.Packages.Add(new PluginPackageInfo { Id = p.Id, DisplayName = p.Name, Group = p.Group, Enabled = p.On, Description = p.Description });
        }

        if (pro)
        {
            list.Packages.Add(new PluginPackageInfo { Id = "example.pro", DisplayName = "PRO sample", Group = "Security", Pro = true, Description = "A plugin that needs OADM PRO." });
        }

        api.ListPluginPackagesAsync(Arg.Any<CancellationToken>()).Returns(_ => list.Clone());
        api.SetPluginPackageEnabledAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            string id = call.ArgAt<string>(0);
            if (id == "oadm.vpn-fleet")
            {
                throw new RpcException(new Status(StatusCode.Unavailable, Refusal));
            }

            if (id == "oadm.load-test" && held is not null)
            {
                await held;
            }

            list.Packages.Single(p => p.Id == id).Enabled = call.ArgAt<bool>(1);
            return list.Clone();
        });
        var addOnList = new AddOnList();
        addOnList.AddOns.Add(new AddOnInfo { Id = "oadm.ocr", DisplayName = "Text recognition", Description = "Reads text in camera pictures.", State = AddOnState.AddOnInstalled, Version = "5.5.0" });
        addOnList.AddOns.Add(new AddOnInfo { Id = "oadm.vision", DisplayName = "Vision Center", Description = "Finds, tracks and counts objects in camera pictures.", State = AddOnState.AddOnInstalled, Version = "5.5.0" });
        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns(addOnList);

        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var addOns = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance, dialogs: dialogs, addOns: addOns);
        await vm.RefreshAsync();
        return (api, addOns, vm);
    }

    private static PluginRowViewModel Tile(PluginsViewModel vm, string id) => vm.Plugins.Single(p => p.Id == id);

    private static List<string> Shown(PluginsViewModel vm) => [.. vm.TileGroups.SelectMany(g => g.Tiles).Select(t => t.Name)];

    private static (int All, int On, int Off, int Optional) Counts(PluginsViewModel vm) =>
        (vm.Filters[0].Count, vm.Filters[1].Count, vm.Filters[2].Count, vm.Filters[3].Count);

    private static List<Button> Tiles(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pluginTile") && b.IsEffectivelyVisible)];

    private static List<string> Visible(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
