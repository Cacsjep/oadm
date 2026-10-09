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

/// <summary>The Settings page's plugin list: states from the server, turning a plugin on and off, operators read-only.</summary>
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
        Assert.Equal("Page", hardening.Adds);
        Assert.Equal("Page, 9 context menu entries", vm.Plugins.Single(p => p.Id == FakeOadmApi.PkiPluginId).Adds);
        Assert.DoesNotContain(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.HardeningScanPluginId);
        Assert.False(store.IsEnabled(FakeOadmApi.HardeningScanPluginId));
        Assert.True(store.IsEnabled("unknown.package")); // older server: everything is on

        var changed = 0;
        store.Changed += (_, _) => changed++;
        hardening.IsEnabled = true;
        await WaitAsync(() => changed == 1);
        Assert.Contains(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.HardeningScanPluginId);

        // Restart off: its menu entry leaves the task list.
        vm.Plugins.Single(p => p.Id == FakeOadmApi.RestartPluginId).IsEnabled = false;
        await WaitAsync(() => changed == 2);
        Assert.DoesNotContain(await api.ListTaskPluginsAsync(CancellationToken.None), p => p.Id == FakeOadmApi.RestartPluginId);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task A_refused_change_is_undone_and_explained()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
        await store.LoadAsync(CancellationToken.None);
        var row = vm.Plugins.Single(p => p.Id == FakeOadmApi.PkiPluginId);

        api.SetOnline(false);
        row.IsEnabled = false;
        await WaitAsync(() => vm.HasError);

        Assert.True(row.IsEnabled);
        Assert.StartsWith("PKI could not be turned off: ", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_page_shows_the_plugin_list()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance);
            var settings = new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance, plugins: plugins);
            await settings.LoadAsync();
            await store.LoadAsync(CancellationToken.None);

            var window = new Window { Width = 1100, Height = 1500, Content = new SettingsView { DataContext = settings } };
            window.Show();
            window.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Plugins", texts);
            Assert.Contains("Hardening scan", texts);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-settings-plugins.png"), PngBitmapEncoderOptions.Default);
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
