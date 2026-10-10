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

        // Metadata Monitor off: its page leaves the rail.
        vm.Plugins.Single(p => p.Id == FakeOadmApi.MetadataMonitorPluginId).IsEnabled = false;
        await WaitAsync(() => changed == 2);
        Assert.DoesNotContain(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.MetadataMonitorPluginId);
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
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
            await store.LoadAsync(CancellationToken.None);

            var window = new Window { Width = 1100, Height = 420, Content = new PluginsPageView { DataContext = plugins } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Plugins", texts);
            Assert.Contains("Hardening scan", texts);
            Assert.Contains("Shows the events a camera sends, live.", texts);
            Assert.DoesNotContain("PKI", texts);

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

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out.");
            await Task.Delay(10);
        }
    }
}
