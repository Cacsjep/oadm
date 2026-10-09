using System.Diagnostics;
using System.Net.Sockets;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Core.Plugins;
using Oadm.Plugins.DhcpServer.Client;
using Oadm.Plugins.DhcpServer.Status;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>The page view model against the in-process plugin on the in-memory network and the real event hub.</summary>
public sealed class PageViewModelTests : IAsyncLifetime
{
    private readonly FakeDhcpNetwork _network = new();
    private readonly PluginEventHub _hub = new();
    private DhcpServerPlugin _plugin = null!;
    private PluginPageContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _plugin = new DhcpServerPlugin(Options.Test(_network));
        await _plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(DhcpServerPluginInfo.PluginId), _hub.For(DhcpServerPluginInfo.PluginId)), default);
        _ctx = new PluginPageContext(_plugin, _hub);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    [Fact]
    public async Task Load_fills_interfaces_derived_values_and_status()
    {
        using var vm = new DhcpServerViewModel(_ctx);
        await vm.LoadAsync();
        Assert.Equal(["Ethernet - 10.0.0.17/24 (Intel(R) Ethernet Connection I219-LM)", "Wi-Fi - 192.168.1.5/24"], vm.Listen.Items.Select(i => i.Label));
        Assert.Equal("eth-id", vm.Listen.SelectedId);
        Assert.Equal("Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138, domain example.local, lease 24 h", vm.ClientsGetText);
        Assert.False(vm.IsEnabled);
        Assert.Equal("Stopped", vm.StatusText);
        Assert.False(vm.HasErrors); // untouched form
        Assert.Equal("No leases yet.", vm.EmptyText);

        vm.Listen.Selected = vm.Listen.Items[1];
        Assert.Equal("Clients get mask 255.255.255.0, no router, no DNS, lease 24 h", vm.ClientsGetText);
    }

    [Fact]
    public async Task Range_errors_show_under_the_fields_and_block_saving()
    {
        using var vm = new DhcpServerViewModel(_ctx);
        await vm.LoadAsync();
        vm.IsEnabled = true;
        vm.RangeStart = "10.0.1.100";
        vm.RangeEnd = "10.0.0.50";
        Assert.Equal("Must be inside the subnet 10.0.0.0/24.", vm.ErrorOf(nameof(vm.RangeStart)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal("Must be inside the subnet 10.0.0.0/24.", vm.SaveTip);

        vm.RangeStart = "10.0.0.100";
        Assert.Equal("Must be after the start address.", vm.ErrorOf(nameof(vm.RangeEnd)));
        vm.RangeEnd = "10.0.0.100";
        Assert.Equal("Must be after the start address.", vm.ErrorOf(nameof(vm.RangeEnd)));
        vm.RangeEnd = "10.0.0.199";
        Assert.False(vm.HasErrors);
        Assert.True(vm.SaveCommand.CanExecute(null));

        // The same range on Wi-Fi is outside its subnet.
        vm.Listen.Selected = vm.Listen.Items[1];
        Assert.Equal("Must be inside the subnet 192.168.1.0/24.", vm.ErrorOf(nameof(vm.RangeStart)));
    }

    [Fact]
    public async Task Automatic_add_is_saved_with_save_and_loaded_again()
    {
        using var vm = new DhcpServerViewModel(_ctx);
        await vm.LoadAsync();
        Assert.False(vm.AutoAddAxisDevices); // off by default
        vm.IsEnabled = true;
        vm.RangeStart = "10.0.0.100";
        vm.RangeEnd = "10.0.0.199";
        vm.AutoAddAxisDevices = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(_plugin.Service!.Config.AutoAddAxisDevices);
        using var again = new DhcpServerViewModel(_ctx);
        await again.LoadAsync();
        Assert.True(again.AutoAddAxisDevices);
    }

    [Fact]
    public async Task Save_asks_before_enabling_next_to_another_DHCP_server()
    {
        await using var router = new FakeOtherServer(_network);
        using var vm = new DhcpServerViewModel(_ctx);
        await vm.LoadAsync();
        vm.IsEnabled = true;
        vm.RangeStart = "10.0.0.100";
        vm.RangeEnd = "10.0.0.199";

        _ctx.Confirm = (_, _) => false;
        await vm.SaveCommand.ExecuteAsync(null);
        var (title, message) = Assert.Single(_ctx.Confirmations);
        Assert.Equal("Another DHCP server answers", title);
        Assert.Equal("Another DHCP server (10.0.0.1) answers on this network. Running two DHCP servers causes address conflicts. Enable anyway?", message);
        Assert.False(_plugin.Service!.IsRunning);
        Assert.Equal("Stopped", vm.StatusText);

        _ctx.Confirm = (_, _) => true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(_plugin.Service!.IsRunning);
        Assert.True(vm.IsStatusOk); // confirmed with "Enable anyway": the status says it runs
        Assert.StartsWith("Running on ", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leases_arrive_live_with_managed_device_names_and_row_actions_work()
    {
        _ctx.DeviceList.Add(new TestDevice("ACCC8E5F6071", "10.0.0.100", "P3265-V"));
        using var vm = new DhcpServerViewModel(_ctx);
        vm.Activate();
        await Wait.UntilAsync(() => vm.Listen.Items.Count > 0 && _hub.WatcherCount(DhcpServerPluginInfo.PluginId) == 1);
        vm.IsEnabled = true;
        vm.RangeStart = "10.0.0.100";
        vm.RangeEnd = "10.0.0.199";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Running on Ethernet (10.0.0.17/24)", vm.StatusText);
        Assert.True(vm.IsStatusOk);

        await using (var managed = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71")))
        {
            await managed.AcquireAsync("axis-accc8e5f6071");
        }

        await using (var other = new TestClient(_network, Mac.Of("00:40:8C:12:34:56")))
        {
            await other.AcquireAsync("printer");
        }

        await Wait.UntilAsync(() => vm.Rows.Count == 2);
        var cam = vm.Rows.Single(r => r.Mac == "AC:CC:8E:5F:60:71");
        Assert.Equal("P3265-V (managed)", cam.HostOrDevice);
        Assert.Equal("Dynamic", cam.Type);
        Assert.StartsWith("in 2", cam.Expires, StringComparison.Ordinal); // in 23 h / in 24 h
        Assert.Equal("printer", vm.Rows.Single(r => r.Mac == "00:40:8C:12:34:56").HostOrDevice);
        Assert.Equal("2 leases, 0 static", vm.LeaseSummary);

        vm.SearchText = "print";
        Assert.Equal("00:40:8C:12:34:56", Assert.Single(vm.Rows).Mac);
        Assert.Equal("2 leases, 0 static, 1 shown", vm.LeaseSummary);
        vm.SearchText = "";

        await vm.MakeStaticCommand.ExecuteAsync(cam);
        await Wait.UntilAsync(() => vm.Rows.Single(r => r.Mac == "AC:CC:8E:5F:60:71").IsStatic);
        Assert.Equal("-", vm.Rows.Single(r => r.Mac == "AC:CC:8E:5F:60:71").Expires);

        var printer = vm.Rows.Single(r => r.Mac == "00:40:8C:12:34:56");
        await vm.ReleaseCommand.ExecuteAsync(printer);
        Assert.Equal("Release lease", _ctx.Confirmations[^1].Title);
        await Wait.UntilAsync(() => vm.Rows.Count == 1);

        // Device changes re-resolve the names.
        _ctx.DeviceList.Clear();
        _ctx.RaiseDevicesChanged();
        Assert.Equal("axis-accc8e5f6071", vm.Rows[0].HostOrDevice);
        vm.Deactivate();
        await Wait.UntilAsync(() => _hub.WatcherCount(DhcpServerPluginInfo.PluginId) == 0);
    }

    [Fact]
    public async Task Static_lease_dialog_shows_errors_under_the_fields()
    {
        await _plugin.InvokeAsync(DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("B8:A4:4F:63:13:39", "10.0.0.48", "Lobby")), default);
        using var vm = new DhcpServerViewModel(_ctx);
        await vm.LoadAsync();

        var dialog = vm.CreateStaticLeaseDialog();
        Assert.Equal("Static lease", dialog.Title);
        Assert.False(dialog.HasErrors); // untouched
        Assert.False(dialog.SaveCommand.CanExecute(null));

        dialog.Mac = "B8:A4";
        dialog.Address = "10.0.1.5";
        Assert.Equal("Enter a MAC address, e.g. B8:A4:4F:63:13:39.", dialog.ErrorOf(nameof(dialog.Mac)));
        Assert.Equal("Must be inside the subnet 10.0.0.0/24.", dialog.ErrorOf(nameof(dialog.Address)));

        dialog.Mac = "B8:A4:4F:63:13:39";
        Assert.Equal("This device already has a static lease (10.0.0.48).", dialog.ErrorOf(nameof(dialog.Mac)));
        dialog.Mac = "AC:CC:8E:5F:60:71";
        dialog.Address = "10.0.0.48";
        Assert.Equal("Already reserved for B8:A4:4F:63:13:39.", dialog.ErrorOf(nameof(dialog.Address)));
        dialog.Address = "10.0.0.17";
        Assert.Equal("This is the address of the OADM server.", dialog.ErrorOf(nameof(dialog.Address)));

        dialog.Address = "10.0.0.49";
        dialog.Name = "Gate";
        Assert.True(dialog.SaveCommand.CanExecute(null));
        var closed = false;
        dialog.CloseRequested += (_, saved) => closed = saved;
        await dialog.SaveCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal("Gate", _plugin.Service!.Leases.Find(Mac.Of("AC:CC:8E:5F:60:71"))!.Name);

        // Edit keeps the own address free of the collision check.
        await vm.LoadAsync();
        var edit = vm.CreateStaticLeaseDialog(vm.Rows.Single(r => r.Mac == "B8:A4:4F:63:13:39"));
        Assert.Equal("Edit static lease", edit.Title);
        edit.Name = "Lobby west";
        Assert.False(edit.HasErrors);
        await edit.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Lobby west", _plugin.Service.Leases.Find(Mac.Of("B8:A4:4F:63:13:39"))!.Name);
    }

    [Fact]
    public async Task Server_answers_go_under_the_field()
    {
        using var vm = new DhcpServerViewModel(_ctx);
        await vm.LoadAsync();
        var dialog = vm.CreateStaticLeaseDialog();
        dialog.Mac = "AC:CC:8E:5F:60:71";
        dialog.Address = "10.0.0.48";

        // Someone else reserved the address meanwhile: the server's answer shows under the address.
        await _plugin.InvokeAsync(DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("B8:A4:4F:63:13:39", "10.0.0.48", null)), default);
        await dialog.SaveCommand.ExecuteAsync(null);
        Assert.False(dialog.Saved);
        Assert.Equal("Already reserved for B8:A4:4F:63:13:39.", dialog.ErrorOf(nameof(dialog.Address)));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void Five_thousand_leases_load_search_update_and_resolve_device_names_fast()
    {
        var now = DateTime.UtcNow;
        var leases = Enumerable.Range(0, 5_000).Select(i => new LeaseInfo(
            Oadm.Plugins.DhcpServer.Protocol.MacAddress.Format(0xACCC8E000000UL + (ulong)i),
            Oadm.Plugins.DhcpServer.Protocol.Ip4.Format(Ip.Of("10.0.0.0") + (uint)i + 1),
            $"axis-{i}",
            null,
            i % 10 == 0,
            i % 10 == 0 ? LeaseInfo.Reserved : LeaseInfo.Active,
            i % 10 == 0 ? null : now.AddHours(20))).ToList();
        for (var i = 0; i < 5_000; i += 2)
        {
            _ctx.DeviceList.Add(new TestDevice(Oadm.Plugins.DhcpServer.Protocol.MacAddress.Compact(0xACCC8E000000UL + (ulong)i), "x", "P3265-V"));
        }

        using var vm = new DhcpServerViewModel(_ctx);
        var watch = Stopwatch.StartNew();
        vm.ReplaceLeases(leases, 1);
        Assert.Equal(5_000, vm.Rows.Count);
        Assert.Equal("5,000 leases, 500 static", vm.LeaseSummary);
        Assert.Equal("P3265-V (managed)", vm.Rows[0].HostOrDevice);
        Assert.Equal("axis-1", vm.Rows[1].HostOrDevice);

        vm.SearchText = "axis-49";
        Assert.Equal(111, vm.Rows.Count); // axis-49, axis-490..499, axis-4900..4999
        vm.SearchText = "";

        vm.HandleEvent(new PluginEvent(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(new LeasesEvent(2, [leases[1] with { HostName = "renamed" }, leases[0] with { Mac = "00:40:8C:00:00:01", Address = "10.0.200.1" }], [leases[2].Mac]))));
        Assert.Equal(5_000, vm.Rows.Count);
        vm.HandleEvent(new PluginEvent(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(new LeasesEvent(2, [leases[3] with { HostName = "old" }], []))));
        Assert.Equal("axis-3", vm.Rows.Single(r => r.Mac == leases[3].Mac).HostOrDevice); // stale event ignored

        _ctx.DeviceList.Clear();
        _ctx.RaiseDevicesChanged();
        vm.RefreshExpires();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
        Assert.Equal("axis-0", vm.Rows.Single(r => r.Mac == leases[0].Mac).HostOrDevice);
    }
}

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.DhcpServer.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the page offscreen inside the real host page (title, page header, card). OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class HeadlessPageTests
{
    [Fact]
    public async Task Page_dialog_and_popup_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var network = new FakeDhcpNetwork();
        var hub = new PluginEventHub();
        await using var plugin = new DhcpServerPlugin(Options.Test(network));
        await plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(DhcpServerPluginInfo.PluginId), hub.For(DhcpServerPluginInfo.PluginId)), default);
        await plugin.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(Options.Enable()), default);
        await plugin.InvokeAsync(DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("B8:A4:4F:63:13:39", "10.0.0.48", "Lobby")), default);
        await plugin.InvokeAsync(DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("00:40:8C:AA:BB:01", "10.0.0.49", "Gate camera")), default);
        var clients = new[] { ("AC:CC:8E:5F:60:71", "axis-accc8e5f6071"), ("AC:CC:8E:12:34:56", "axis-accc8e123456"), ("00:1B:44:11:3A:B7", "printer-2f"), ("3C:52:82:00:10:20", "laptop-tech") };
        foreach (var (mac, host) in clients)
        {
            await using var client = new TestClient(network, Mac.Of(mac));
            await client.AcquireAsync(host);
        }

        var ctx = new PluginPageContext(plugin, hub);
        ctx.DeviceList.Add(new TestDevice("B8A44F631339", "10.0.0.48", "P3265-V"));
        ctx.DeviceList.Add(new TestDevice("ACCC8E5F6071", "10.0.0.100", "Q1656-LE"));

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var rows = await session.Dispatch(async () =>
        {
            var view = new DhcpServerView();
            using var vm = new DhcpServerViewModel(ctx);
            var window = Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadAsync();
            Pump();
            Capture(window, outDir, "dhcp-server-page.png");

            // "Automatically add Axis devices that get an address", checked (saved with Save like the other settings).
            vm.AutoAddAxisDevices = true;
            Pump();
            var autoAdd = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Automatically add Axis devices that get an address"));
            Assert.True(autoAdd.IsChecked);
            Assert.True(autoAdd.IsEffectivelyVisible);
            Capture(window, outDir, "dhcp-server-auto-add.png");
            vm.AutoAddAxisDevices = false;

            vm.RangeStart = "10.0.1.100";
            vm.RangeEnd = "10.0.0.50";
            vm.HandleEvent(new PluginEvent(DhcpServerMethods.StateTopic, DhcpJson.Serialize(new DhcpState
            {
                Config = new DhcpConfig { Enabled = true, InterfaceId = "eth-id" },
                Status = DhcpStatusTexts.ForBindError(SocketError.AddressAlreadyInUse, 67, "Ethernet", HostOs.Windows),
            })));
            Pump();
            Capture(window, outDir, "dhcp-server-page-errors.png");

            vm.RangeStart = "10.0.0.100";
            vm.RangeEnd = "10.0.0.199";
            vm.HandleEvent(new PluginEvent(DhcpServerMethods.StateTopic, DhcpJson.Serialize(new DhcpState
            {
                Config = new DhcpConfig { Enabled = true, InterfaceId = "eth-id" },
                Status = DhcpStatusTexts.OtherServer(["10.0.0.1"]),
            })));
            Pump();
            Capture(window, outDir, "dhcp-server-page-other-server.png");

            var confirm = new MessageWindow
            {
                Heading = "Another DHCP server answers",
                Message = "Another DHCP server (10.0.0.1) answers on this network. Running two DHCP servers causes address conflicts. Enable anyway?",
                ConfirmText = "Enable anyway",
                CancelText = "Cancel",
            };
            Oadm.Client.App.ApplyCrispText(confirm);
            confirm.Show(window);
            Pump();
            Capture(confirm, outDir, "dhcp-server-other-server-confirm.png");
            confirm.Close();

            var dialogVm = vm.CreateStaticLeaseDialog();
            dialogVm.Mac = "B8:A4:4F";
            dialogVm.Address = "10.0.0.101";
            dialogVm.Name = "Entrance";
            var dialog = new StaticLeaseWindow();
            dialog.Attach(dialogVm);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show(window);
            Pump();
            Capture(dialog, outDir, "dhcp-server-static-lease-dialog.png");
            dialog.Close();

            var count = vm.Rows.Count;
            window.Close();
            return count;
        }, CancellationToken.None);

        // The awaited dispatch may continue on the session's UI thread; Dispose waits for that thread, so dispose elsewhere.
        GC.KeepAlive(session); // not disposed: Avalonia's headless Dispose can throw a NullReferenceException on CI
        Assert.Equal(6, rows);
    }

    private static Window Host(Control view)
    {
        // The real host page (title, ui:PageHeader subtitle and status, card), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(DhcpServerPluginInfo.PluginId, DhcpServerPluginInfo.DisplayName, view),
        };
        var window = new Window { Width = 1280, Height = 860, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window); // same text rendering as the real app windows
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
