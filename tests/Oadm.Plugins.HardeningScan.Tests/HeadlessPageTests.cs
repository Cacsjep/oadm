using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.HardeningScan.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.HardeningScan.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.HardeningScan.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the page offscreen like the client shell (page title above the host card). OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class HeadlessPageTests
{
    [Fact]
    public async Task Page_renders_the_basic_and_the_extended_columns()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var (plugin, devices) = await ScannedPluginAsync();
        await using var _ = plugin;
        var ctx = new PageContext(plugin);
        ctx.DeviceList.AddRange(devices);
        ctx.Selection.AddRange(devices.Take(3));
        var settings = new HardeningClientSettingsStore(Path.Combine(Path.GetTempPath(), "oadm-hardening-tests", Guid.NewGuid().ToString("N"), "client.json"));
        // A detail height dragged on a tall screen must not push the result grid over the toolbar on a smaller window.
        settings.Save(new HardeningClientSettings { DetailHeight = 778 });
        var state = await plugin.InvokeAsync(HardeningMethods.GetState, null, CancellationToken.None);

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var result = await session.Dispatch(() =>
        {
            var view = new HardeningScanView();
            using var vm = new HardeningScanViewModel(ctx, settings);
            var window = Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            vm.ApplyState(HardeningJson.Deserialize<HardeningState>(state));
            vm.SelectionChanged();
            Pump();
            vm.SelectedRow = vm.Rows[1];
            Pump();
            var basicColumns = view.CheckColumns.Count;
            var chips = view.GetVisualDescendants().OfType<StatusChip>().Count(c => c.IsVisible);
            Capture(window, outDir, "hardening-scan-basic.png");

            // Inside the main window at the minimum width: 1800 x 900 with the rail.
            window.Width = 1800 - 220;
            window.Height = 900 - 32;
            Pump();
            Capture(window, outDir, "hardening-scan-1800x900.png");
            window.Width = 1280;
            window.Height = 860;
            Pump();

            vm.Level = ScanLevel.Extended;
            Pump();
            view.ScrollToColumn(view.CheckColumns.Count - 1); // the Extended columns are right of the Basic ones
            Pump();
            var extendedColumns = view.CheckColumns.Count;
            Capture(window, outDir, "hardening-scan-extended.png");
            window.Close();
            return (basicColumns, extendedColumns, chips);
        }, CancellationToken.None);

        await Task.Run(session.Dispose);
        Assert.Equal(15, result.basicColumns);
        Assert.Equal(26, result.extendedColumns);
        Assert.True(result.chips > 50, $"status chips rendered: {result.chips}");
    }

    /// <summary>The real plugin after an Extended scan of fake cameras with different answers (recorded, hardened, refused).</summary>
    private static async Task<(HardeningScanPlugin Plugin, List<IDeviceInfo> Devices)> ScannedPluginAsync()
    {
        var repository = new FakeDevices();
        var vapix = new FakeVapixFactory();
        var hardened = new FakeCamera();
        hardened.Answers["config/rest/firewall/v1"] = """{"status":"success","data":{"activated":true,"conf":{"rules":{"activeDefaultPolicy":"DROP","activeRules":[{"ruleType":"ALLOW"}]}}}}""";
        hardened.Answers["config/rest/user-management/v2"] = """{"status":"success","data":{"settings":{"passphraseComplexity":{"policy":"length"}}}}""";
        hardened.Answers["axis-cgi/param.cgi"] = Fixtures.Read("paramcgi-hardening.txt")
            .Replace("root.Network.SSH.Enabled=yes", "root.Network.SSH.Enabled=no", StringComparison.Ordinal)
            .Replace("root.Network.Bonjour.Enabled=yes", "root.Network.Bonjour.Enabled=no", StringComparison.Ordinal)
            .Replace("root.Network.ZeroConf.Enabled=yes", "root.Network.ZeroConf.Enabled=no", StringComparison.Ordinal)
            .Replace("root.System.WebInterfaceDisabled=no", "root.System.WebInterfaceDisabled=yes", StringComparison.Ordinal);
        var weak = new FakeCamera();
        weak.Answers["config/rest/snmp/v1"] = """{"status":"success","data":{"enabled":true,"snmpV1":{"enabled":true},"snmpV2":{"enabled":true},"snmpV3":{"enabled":false}}}""";
        var models = new[] { "P3265-V", "M3106-L Mk II", "Q6135-LE", "P1468-LE", "C1310-E", "A1610-B", "M4317-PLVE", "Q1656-LE" };
        var devices = new List<IDeviceInfo>();
        for (var i = 0; i < 16; i++)
        {
            var device = new TestDevice
            {
                Address = $"10.0.0.{40 + i}",
                Serial = $"B8A44F6313{i:X2}",
                Model = models[i % models.Length],
                Category = models[i % models.Length].StartsWith('C') ? DeviceCategory.Speaker : models[i % models.Length].StartsWith('A') ? DeviceCategory.DoorController : DeviceCategory.Camera,
                Status = i == 6 ? DeviceStatus.CredentialsRequired : i == 11 ? DeviceStatus.Unreachable : DeviceStatus.Ok,
                DhcpEnabled = i % 3 == 0,
                Dot1xEnabled = i % 4 == 0,
                CertTrustName = i % 2 == 0 ? "Trusted" : "SelfSigned",
                CertNotAfterUtc = DateTime.UtcNow.AddDays(300),
            };
            devices.Add(device);
            repository.Items.Add(device);
            vapix.Add(device.Id, i % 3 == 1 ? hardened : i % 5 == 2 ? weak : new FakeCamera());
        }

        var plugin = new HardeningScanPlugin();
        await plugin.StartAsync(new TestCoreContext(repository, vapix, new MemorySettings(), null), CancellationToken.None);
        await plugin.InvokeAsync(HardeningMethods.StartScan, HardeningJson.Serialize(new StartScanRequest { Level = ScanLevel.Extended }), CancellationToken.None);
        await plugin.Service!.WaitAsync();
        return (plugin, devices);
    }

    private static Window Host(Control view)
    {
        // The real host page (title, ui:PageHeader subtitle, card), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(HardeningScanPluginInfo.PluginId, HardeningScanPluginInfo.DisplayName, view),
        };
        var window = new Window { Width = 1280, Height = 860, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
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
