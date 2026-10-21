using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.Plugins;
using Oadm.Client.Settings;
using Oadm.Client.Shell;

namespace Oadm.Client.Tests;

/// <summary>The Plugins page: states from the server, turning a plugin on and off, plugins that are always on.</summary>
public sealed class PluginListTests
{
    [Fact]
    public async Task The_hardening_scan_is_off_by_default_and_turning_it_on_brings_its_page_back()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
        await store.LoadAsync(CancellationToken.None);

        var hardening = vm.Plugins.Single(p => p.Id == FakeOadmApi.HardeningScanPluginId);
        Assert.False(hardening.IsEnabled);
        Assert.True(hardening.OffByDefault);
        Assert.Equal("Checks devices against the AXIS OS hardening guide.", hardening.Description);
        Assert.True(hardening.CanToggle);
        Assert.DoesNotContain(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.HardeningScanPluginId);
        Assert.False(store.IsEnabled(FakeOadmApi.HardeningScanPluginId));
        Assert.True(store.IsEnabled("unknown.package")); // older server: everything is on

        var changed = 0;
        store.Changed += (_, _) => changed++;
        hardening.IsEnabled = true;
        await WaitAsync(() => changed == 1);
        Assert.Contains(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.HardeningScanPluginId);

        // Snapshot report off: its page leaves the rail. The Metadata Monitor is off by default too.
        Assert.False(vm.Plugins.Single(p => p.Id == FakeOadmApi.MetadataMonitorPluginId).IsEnabled);
        vm.Plugins.Single(p => p.Id == FakeOadmApi.SnapshotReportPluginId).IsEnabled = false;
        await WaitAsync(() => changed == 2);
        Assert.DoesNotContain(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.SnapshotReportPluginId);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Core_plugins_are_always_on_and_not_listed()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
        await store.LoadAsync(CancellationToken.None);

        Assert.DoesNotContain(vm.Plugins, p => p.Id is FakeOadmApi.RestartPluginId or FakeOadmApi.PkiPluginId or FakeOadmApi.SystemReportPluginId);
        Assert.All(vm.Plugins, p => Assert.True(p.CanToggle));
        Assert.True(store.IsEnabled(FakeOadmApi.RestartPluginId));

        // The server refuses to turn them off.
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() =>
            api.SetPluginPackageEnabledAsync(FakeOadmApi.RestartPluginId, false, CancellationToken.None));
        Assert.Equal("Restart is always on.", refused.Status.Detail);
        Assert.Contains(await api.ListTaskPluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.RestartPluginId);
    }

    [Fact]
    public async Task The_plugins_are_grouped_like_the_navigation_rail()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
        await store.LoadAsync(CancellationToken.None);

        // Rail order, only groups with plugins (Reporting keeps Snapshot report; System report is always on).
        Assert.Equal(["Servers", "Automation", "Security", "Monitoring", "Reporting", "Maintenance", "Utilities", "Extensions"], vm.Groups.Select(g => g.ToString()));
        Assert.Equal(
            ["DHCP server", "NTP server", "Device triggers", "VAPIX Commander", "Hardening scan", "Dashboards", "I/O Monitor", "Image Health Dashboard", "Live View", "Metadata Monitor", "Snapshot report", "Edge Recording Export", "Edge storage", "Web View", "Site reports"],
            vm.Plugins.Select(p => p.Name));
        Assert.Equal("Extensions", PluginGroupKey.Parse("NoSuchGroup").ToString()); // like the rail
    }

    [Fact]
    public async Task Each_plugin_says_whether_it_works_at_sites_and_which_ports_it_listens_on()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
        await store.LoadAsync(CancellationToken.None);

        var ntp = vm.Plugins.Single(p => p.Id == "oadm.ntp-server");
        Assert.Equal(("Works at sites", "UDP 123", true), (ntp.SiteText, ntp.SitePortsText, ntp.HasSitePorts));
        var dhcp = vm.Plugins.Single(p => p.Id == "oadm.dhcp-server");
        Assert.Equal(("Only this server's network", false), (dhcp.SiteText, dhcp.HasSitePorts));

        // An older server sends nothing: the column stays empty.
        Assert.Equal(string.Empty, PluginRowViewModel.SiteTextOf(new Contracts.V1.PluginPackageInfo()));
    }

    [Fact]
    public async Task A_refused_change_is_undone_and_explained()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
        await store.LoadAsync(CancellationToken.None);
        var row = vm.Plugins.Single(p => p.Id == FakeOadmApi.SnapshotReportPluginId);

        api.SetOnline(false);
        row.IsEnabled = false;
        await WaitAsync(() => vm.HasError);

        Assert.True(row.IsEnabled);
        Assert.StartsWith("Snapshot report could not be turned off: ", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugins_page_shows_the_plugin_list()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
            var devices = new Oadm.Client.Devices.DeviceStore();
            devices.Sites.Apply(await api.ListSitesAsync(CancellationToken.None)); // fake mode has sites: the Sites column shows
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance, devices: devices);
            await store.LoadAsync(CancellationToken.None);

            var window = new Window { Width = 1500, Height = 760, Content = new PluginsPageView { DataContext = plugins } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Plugins", texts);
            Assert.Contains("Hardening scan", texts);
            Assert.Contains("Shows the events a camera sends, live.", texts);
            Assert.DoesNotContain("PKI", texts);
            Assert.Contains("Servers", texts); // group headers like the navigation rail
            Assert.Contains("Monitoring", texts);
            Assert.Contains("Sites", texts); // the column next to License
            Assert.Contains("Only this server's network", texts);
            Assert.Contains("UDP 123", texts);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-plugins-page.png"), PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_commercial_plugin_shows_its_license_and_takes_a_new_one()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
            await store.LoadAsync(CancellationToken.None);

            Assert.Equal(FakeOadmApi.FakeServerId, plugins.ServerId);
            Assert.True(plugins.ShowServerId); // a plugin needs a license
            Assert.True(plugins.HasLicensedPlugins);
            var row = plugins.Plugins.Single(p => p.Id == FakeOadmApi.SampleLicensedPluginId);
            Assert.True(row.NeedsLicense);
            Assert.Equal(("Trial", true, false), (row.LicenseText, row.LicenseWarning, row.HasLicense));
            Assert.StartsWith("Trial until ", row.LicenseDetail, StringComparison.Ordinal);
            Assert.Equal("Enter license", row.EnterLicenseText);
            Assert.False(plugins.Plugins.Single(p => p.Id == FakeOadmApi.HardeningScanPluginId).NeedsLicense);
            Assert.Equal(Oadm.Sdk.Licensing.LicenseStatus.Trial, store.LicenseOf(FakeOadmApi.SampleLicensedPluginId)!.Status);
            Assert.Equal(Oadm.Sdk.Licensing.LicenseStatus.NotRequired, store.LicenseOf(FakeOadmApi.HardeningScanPluginId)!.Status);

            var window = new Window { Width = 1300, Height = 1150, Content = new PluginsPageView { DataContext = plugins } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(FakeOadmApi.FakeServerId, texts);
            Assert.Contains("Trial", texts);
            // No sites known to this page: the Sites column stays hidden, like every Site column.
            Assert.DoesNotContain("Works at sites", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? ""));

            // The dialog: a license for another server is refused below the field, the right one is stored.
            var license = new PluginLicenseViewModel(store, row.Id, row.Name);
            var dialog = new PluginLicenseWindow();
            dialog.Attach(license);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            Assert.False(license.SaveCommand.CanExecute(null)); // nothing pasted yet
            license.LicenseText = "not a key";
            Assert.Equal("This is not an OADM license key.", license.ErrorOf(nameof(PluginLicenseViewModel.LicenseText)));
            license.LicenseText = Sign(FakeOadmApi.FakeServerId);
            Assert.Equal("ACME GmbH, until 2099-12-31, reports", license.LicenseHint); // what the key says, before the check
            license.LicenseText = Sign("11111111-2222-3333-4444-555555555555");
            await license.SaveCommand.ExecuteAsync(null);
            Assert.Equal("The license is for another server (11111111-2222-3333-4444-555555555555).", license.ErrorOf(nameof(PluginLicenseViewModel.LicenseText)));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Save(dialog, outDir, "client-plugin-license-refused.png");

            var closed = false;
            license.CloseRequested += (_, ok) => closed = ok;
            license.LicenseText = Sign(FakeOadmApi.FakeServerId);
            await license.SaveCommand.ExecuteAsync(null);
            Assert.True(closed);
            dialog.Close();

            row = plugins.Plugins.Single(p => p.Id == FakeOadmApi.SampleLicensedPluginId);
            Assert.Equal(("Licensed", true, true, "Change license"), (row.LicenseText, row.LicenseOk, row.HasLicense, row.EnterLicenseText));
            Assert.Equal("Licensed to ACME GmbH until 2099-12-31.", row.LicenseDetail);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Save(window, outDir, "client-plugins-licenses.png");

            // Remove: back to the trial.
            await api.RemovePluginLicenseAsync(row.Id, CancellationToken.None);
            await store.LoadAsync(CancellationToken.None);
            Assert.Equal("Trial", plugins.Plugins.Single(p => p.Id == FakeOadmApi.SampleLicensedPluginId).LicenseText);

            // The dialog as it opens, for the docs.
            var fresh = new PluginLicenseWindow();
            fresh.Attach(new PluginLicenseViewModel(store, row.Id, row.Name) { LicenseText = Sign(FakeOadmApi.FakeServerId) });
            Oadm.Client.App.ApplyCrispText(fresh);
            fresh.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Save(fresh, outDir, "client-plugin-license.png");
            fresh.Close();
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);

        static string Sign(string server) => Oadm.Sdk.Licensing.LicenseFile.Sign(
            new Oadm.Sdk.Licensing.LicenseData(FakeOadmApi.SampleLicensedPluginId, server, "ACME GmbH", new DateOnly(2026, 10, 17), new DateOnly(2099, 12, 31),
                ["reports"], new Dictionary<string, long> { ["sites"] = 3 }),
            FakeOadmApi.SampleLicenseKeys.PrivateKeyPem);
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out.");
            await Task.Delay(10);
        }
    }
}
