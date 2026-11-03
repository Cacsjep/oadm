using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Client.Plugins.AddOns;
using Oadm.Client.Settings;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// The Optional plugins card on the Plugins page, rendered headless through its states: not installed, installing, installed and
/// removed (screenshots with OADM_SCREENSHOT_DIR; OADM_TEST_APPEARANCE=Light for the light ones).
/// </summary>
public sealed class AddOnsCardTests
{
    [Fact]
    public async Task The_add_ons_card_shows_install_progress_installed_and_removed()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var gate = new SemaphoreSlim(0);
            api.AddOnInstallGate = (_, _) => gate.WaitAsync();
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
            var addOns = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance, dialogs);
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance, dialogs: dialogs, addOns: addOns);
            await plugins.RefreshAsync();

            var window = new Window { Width = 1300, Height = 900, Content = new PluginsPageView { DataContext = plugins } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();

            // Not installed: Install, the size and the line where optional plugins come from.
            Assert.Contains("Optional plugins", Visible(window));
            NeverSaysAddOn(window);
            Assert.Contains("Text recognition", Visible(window));
            Assert.Contains("V0.0.0-fake · 38 MB", Visible(window));
            Assert.Contains(AddOnTexts.Source, Visible(window));
            Assert.Contains("Install", Visible(window));
            Save(window, outDir, $"addons-not-installed-{appearance}.png");

            // Installing: the step and its percent.
            AddOnRowViewModel row = Assert.Single(addOns.AddOns);
            Task install = addOns.InstallCommand.ExecuteAsync(row);
            gate.Release(2);
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => row.ProgressText == "Download 42 %", "the download shows its percent");
            Pump();
            Assert.Contains("Download 42 %", Visible(window));
            Assert.DoesNotContain("Install", Visible(window));
            Save(window, outDir, $"addons-installing-{appearance}.png");

            // Installed: the chip and Remove; the package is in the plugin list (Vision, off).
            gate.Release(3);
            await install;
            Pump();
            Assert.Contains(AddOnTexts.Installed, Visible(window));
            Assert.Contains("Remove", Visible(window));
            Assert.Contains(plugins.Plugins, p => p.Id == FakeOadmApi.TextRecognitionAddOnId && !p.IsEnabled);
            Assert.Contains("Vision", Visible(window));
            NeverSaysAddOn(window);
            Save(window, outDir, $"addons-installed-{appearance}.png");

            // Removed: the files go at the next restart of the server.
            await addOns.RemoveCommand.ExecuteAsync(row);
            Pump();
            Assert.Contains(AddOnTexts.Removed, Visible(window));
            Assert.DoesNotContain("Remove", Visible(window));
            NeverSaysAddOn(window);
            Save(window, outDir, $"addons-removed-{appearance}.png");

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>
    /// The card follows right below the plugin list on a large screen too (not at the bottom of the window), and on a
    /// small one the list scrolls so the card stays in view.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1300, 640)]
    public async Task The_add_ons_card_sits_right_below_the_plugin_list(int width, int height)
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
            var addOns = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance);
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance, addOns: addOns);
            await plugins.RefreshAsync();

            var page = new PluginsPageView { DataContext = plugins };
            var window = new Window { Width = width, Height = height, Content = page };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();

            Border list = page.FindControl<Border>("PluginCard")!;
            AddOnsCardView card = page.FindControl<AddOnsCardView>("AddOnsCard")!;
            Assert.True(card.IsEffectivelyVisible);
            Point listBottom = list.TranslatePoint(new Point(0, list.Bounds.Height), window)!.Value;
            Point cardTop = card.TranslatePoint(default, window)!.Value;
            Point cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), window)!.Value;
            Assert.True(cardTop.Y >= listBottom.Y, "the card is below the list");
            Assert.True(cardTop.Y - listBottom.Y < 40, $"the card follows right below the list, not {cardTop.Y - listBottom.Y:0} px further down");
            Assert.True(cardBottom.Y <= window.ClientSize.Height + 0.5, "the card stays in the window, the list scrolls");

            // The list stays a virtualized grid: on a small window it creates only the rows that fit and scrolls.
            if (height < 1000)
            {
                DataGrid grid = page.FindControl<DataGrid>("PluginGrid")!;
                int realized = grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);
                Assert.InRange(realized, 1, plugins.Plugins.Count - 1);
            }

            Save(window, outDir, $"plugins-page-{width}x{height}-{appearance}.png");

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_server_without_add_ons_shows_no_card()
    {
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = Substitute.For<IOadmApi>();
            api.ListPluginPackagesAsync(Arg.Any<CancellationToken>()).Returns(new PluginPackageList());
            api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ =>
                throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unimplemented, "")));
            var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
            var plugins = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance,
                addOns: new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance));
            await plugins.RefreshAsync();
            var window = new Window { Width = 1000, Height = 600, Content = new PluginsPageView { DataContext = plugins } };
            window.Show();
            Pump();

            Assert.DoesNotContain("Optional plugins", Visible(window));

            // Without a view model (older composition) the card stays hidden too.
            window.Content = new PluginsPageView { DataContext = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance) };
            Pump();
            Assert.DoesNotContain("Optional plugins", Visible(window));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>Users see "optional plugins", never "add-on" (docs/spec/add-ons.md).</summary>
    private static void NeverSaysAddOn(Window window) =>
        Assert.DoesNotContain(Visible(window), t => t.Contains("add-on", StringComparison.OrdinalIgnoreCase));

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
