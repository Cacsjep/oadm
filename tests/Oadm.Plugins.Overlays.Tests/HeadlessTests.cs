using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client;
using Oadm.Client.Infrastructure;
using Oadm.Plugins.Overlays.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>Headless session with the real client App, so the dialog renders with the host theme (OadmTheme.axaml).</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>
/// Renders the Text overlays dialog offscreen. Set OADM_SCREENSHOT_DIR to also write PNGs; OADM_TEST_APPEARANCE=Light renders the
/// light appearance (the colours load once per process, so each appearance is its own test run).
/// </summary>
public sealed class HeadlessTests
{
    [Fact]
    public async Task The_dialog_renders_with_the_host_theme_in_every_state()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-overlays-headless-" + Guid.NewGuid().ToString("N"));
        var light = Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light";
        var suffix = light ? "-light" : "-dark";
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder, Appearance = light ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

        await session.Dispatch(async () =>
        {
            IReadOnlyList<IDeviceInfo> devices = [Fixtures.Camera(), Fixtures.Camera("10.0.0.49"), Fixtures.Camera("10.0.0.50")];
            var vm = ViewModelTests.Dialog(devices);
            await vm.LoadAsync(CancellationToken.None);
            var window = new OverlaysWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Bounds.Height > 0);
            Assert.Null(vm.Rows[0].ErrorOf(nameof(OverlayRowViewModel.Text))); // nothing red on an untouched form
            Capture(window, outDir, "overlays-empty" + suffix + ".png");

            // Two overlays: date and time bottom left, the camera name top right.
            vm.Rows[0].Text = "%F %X";
            vm.AddOverlayCommand.Execute(null);
            vm.Rows[1].Text = "#n";
            vm.Rows[1].SelectedPosition = vm.Rows[1].Positions.Single(p => p.Position == OverlayPosition.TopRight);
            vm.Rows[1].SelectedBackgroundColor = OverlayRowViewModel.Colors.Single(c => c.Color == OverlayColor.SemiTransparent);
            vm.Rows[1].SizeText = "32";
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.CanApply);
            Assert.Single(Buttons(window), b => b.Classes.Contains("primary"));
            Capture(window, outDir, "overlays-two" + suffix + ".png");

            // The modifiers list of the first overlay.
            var more = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Insert a value");
            more.Flyout!.ShowAt(more);
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "overlays-modifiers" + suffix + ".png");
            more.Flyout.Hide();
            Dispatcher.UIThread.RunJobs();

            // An error directly below its field; Apply disabled with the reason as tooltip.
            vm.Rows[1].SizeText = "500";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(OverlayRules.SizeRange(16, 200), vm.Rows[1].ErrorOf(nameof(OverlayRowViewModel.SizeText)));
            Assert.False(vm.CanApply);
            Assert.Equal(OverlayRules.SizeRange(16, 200), vm.ApplyBlockedReason);
            Capture(window, outDir, "overlays-error" + suffix + ".png");
            vm.Rows[1].SizeText = "32";

            // Replace: the note says what goes.
            vm.IsReplace = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(OverlaysTaskPlugin.ReplaceNote, vm.ModeNote);
            Capture(window, outDir, "overlays-replace" + suffix + ".png");

            // Remove matching: the text only, the red button.
            vm.IsRemove = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Single(Buttons(window), b => b.Classes.Contains("danger") && !b.Classes.Contains("toolbar"));
            Assert.DoesNotContain(Buttons(window), b => b.Classes.Contains("primary"));
            Capture(window, outDir, "overlays-remove" + suffix + ".png");

            // Remove all: no list, the red button works without one.
            vm.IsRemoveAll = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.CanApply);
            Assert.Single(Buttons(window), b => b.Classes.Contains("danger") && !b.Classes.Contains("toolbar"));
            Assert.DoesNotContain(Buttons(window), b => b.Content is IconLabel { Text: "Read from a camera" or "Add overlay" });
            Capture(window, outDir, "overlays-removeall" + suffix + ".png");
            window.Close();

            // Many cameras, some without text overlays: the summary counts them.
            IReadOnlyList<IDeviceInfo> many = [.. Enumerable.Range(0, 250).Select(i => (IDeviceInfo)(i % 36 == 5
                ? new Oadm.Sdk.Testing.FakeDevice { Address = $"10.0.1.{i}", Apis = [new Oadm.Sdk.Vapix.DeviceApi("param-cgi", "1.0")] }
                : Fixtures.Camera($"10.0.1.{i}")))];
            var big = ViewModelTests.Dialog(many);
            await big.LoadAsync(CancellationToken.None);
            big.Rows[0].Text = "%F %X";
            big.AddOverlayCommand.Execute(null);
            big.Rows[1].Text = "#n";
            big.Rows[1].SelectedPosition = big.Rows[1].Positions.Single(p => p.Position == OverlayPosition.TopRight);
            var bigWindow = new OverlaysWindow { DataContext = big };
            bigWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("250 devices: 243 compatible, 7 not supported", big.DevicesText);
            Capture(bigWindow, outDir, "overlays-many" + suffix + ".png");
            bigWindow.Close();
            return true;
        }, CancellationToken.None);

        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
    }

    private static IEnumerable<Button> Buttons(Window window) => window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);

    private static void Capture(Window window, string? outDir, string name)
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
