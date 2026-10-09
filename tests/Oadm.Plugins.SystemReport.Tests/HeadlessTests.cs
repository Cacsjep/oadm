using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.SystemReport.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.SystemReport.Tests;

/// <summary>Minimal host: dark theme variant plus the real OADM theme (styles, colors, icons), like the client app.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.SystemReport.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>The toolbar button and the progress dialog, rendered offscreen. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
public sealed class HeadlessTests
{
    [Fact]
    public async Task The_button_follows_the_selection_and_the_dialog_renders_every_state()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        await session.Dispatch(() =>
        {
            // Toolbar button: disabled without a selection, enabled with one.
            var ctx = new ClientTests.FakeToolbarContext();
            var button = (ToolbarButton)new SystemReportToolbarPlugin().CreateControl(ctx);
            Assert.Equal(("System report", "file", false, false), (button.Text, button.IconKey, button.IsIconOnly, button.IsEnabled));
            Assert.Equal(SystemReportToolbarPlugin.Tooltip, ToolTip.GetTip(button));
            ctx.Selected.Add(new FakeDevice());
            ctx.RaiseSelectionChanged();
            Assert.True(button.IsEnabled);

            // The dialog in the middle of a job: done, downloading, waiting and failed devices.
            string[] models = ["AXIS P3265-V", "AXIS Q6135-LE", "AXIS M3106-L Mk II", "AXIS C1310-E", "AXIS A1610", "AXIS P1465-LE"];
            var devices = Enumerable.Range(0, 6).Select(i => (IDeviceInfo)new FakeDevice
            {
                Address = $"10.0.0.{48 + i}",
                Serial = $"B8A44F6313{30 + i}",
                Model = models[i],
            }).ToList();
            var vm = new SystemReportViewModel((_, _, _) => Task.FromResult<string?>(null), devices);
            vm.Apply(new JobStatus
            {
                Total = 6,
                Finished = 3,
                Failed = 1,
                Devices =
                [
                    new() { DeviceId = devices[0].Id, State = DeviceReportStates.Done, Size = 315_870 },
                    new() { DeviceId = devices[1].Id, State = DeviceReportStates.Done, Size = 402_113 },
                    new() { DeviceId = devices[2].Id, State = DeviceReportStates.Downloading },
                    new() { DeviceId = devices[3].Id, State = DeviceReportStates.Failed, Error = "Forbidden - HTTP 403 (administrator rights are required)" },
                    new() { DeviceId = devices[4].Id, State = DeviceReportStates.Downloading },
                ],
            });
            var window = new SystemReportWindow();
            window.Attach(vm);
            window.Show();
            Pump();

            Assert.Equal("Downloading system reports 4 of 6", vm.ProgressText);
            var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal(["Address", "MAC address", "Model", "Status"], grid.Columns.Select(c => c.Header as string ?? "").ToArray());
            var chips = window.GetVisualDescendants().OfType<StatusChip>().Where(c => c.IsEffectivelyVisible).Select(c => c.Text).ToList();
            Assert.Contains("Failed", chips);
            Assert.Contains("Downloading", chips);
            Assert.Contains("Waiting", chips);
            Capture(window, outDir, "system-report-dialog.png");
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
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
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
