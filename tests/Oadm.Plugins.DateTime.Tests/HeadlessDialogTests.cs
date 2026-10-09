using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client;
using Oadm.Client.Infrastructure;
using Oadm.Plugins.DateAndTime.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DateAndTime.Tests;

/// <summary>Headless Avalonia session with the real OADM client App, so the dialog renders with the host theme.</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the dialog offscreen (never on the real desktop). Set OADM_SCREENSHOT_DIR to also write PNGs.</summary>
public sealed class HeadlessDialogTests
{
    [Fact]
    public async Task Dialog_renders_with_host_theme()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-datetime-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

        var (singleOk, multiErrors) = await session.Dispatch(async () =>
        {
            var now = new DateTimeOffset(2026, 10, 7, 18, 30, 0, TimeSpan.FromHours(2));

            // One device (10.0.0.48 as recorded): device time, a new time zone, NTP with two servers.
            var single = new DateTimeDialogViewModel([new FakeDevice(Guid.NewGuid())], () => now);
            single.ApplyCurrent(ViewModelTests.Current10048);
            single.SelectedZone = single.Zones.Single(z => z.Id == "Europe/Vienna");
            single.IsNtp = true;
            single.NtpServersText = "10.0.0.17, pool.ntp.org";
            var window = new DateTimeWindow { DataContext = single };
            window.Show();
            await PumpAsync();
            Capture(window, outDir, "datetime-dialog-single.png");
            var ok = single.CanApply;
            window.Close();

            // 5000 devices, manual date and time with field errors under the inputs, 10 % without the Time API.
            var devices = Enumerable.Range(0, 5000)
                .Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.{i / 62500}.{i / 250 % 250}.{i % 250 + 1}")
                {
                    Apis = i % 10 == 0 ? Fixture.LegacyOnly : Fixture.Modern,
                })
                .ToList();
            var multi = new DateTimeDialogViewModel(devices, () => now);
            multi.ApplyCurrent(ViewModelTests.Current10048);
            multi.IsManual = true;
            multi.ManualDate = "07.10.2026";
            multi.ManualTime = "25:00";
            var window2 = new DateTimeWindow { DataContext = multi };
            window2.Show();
            await PumpAsync();
            Capture(window2, outDir, "datetime-dialog-multi-errors.png");
            var errors = multi.HasErrors;
            window2.Close();

            // Server time with NTS-capable devices, still reading the device (loading chip).
            var loading = new DateTimeDialogViewModel([.. devices.Take(3)], () => now) { IsServerTime = true };
            loading.ServerTimeZone = "Europe/Vienna";
            var window3 = new DateTimeWindow { DataContext = loading };
            window3.Show();
            await PumpAsync();
            Capture(window3, outDir, "datetime-dialog-server-time.png");
            window3.Close();

            // NTS KE servers on one device, read error below Device time.
            var nts = new DateTimeDialogViewModel([new FakeDevice(Guid.NewGuid())], () => now);
            nts.ShowLoadError("Unauthorized - HTTP 401 (check the credentials)");
            nts.IsNtp = true;
            nts.UseNts = true;
            nts.NtpServersText = "nts.netnod.se, nts.example..com";
            nts.ApplyCommand.Execute(null); // OK tried: the missing time zone shows below the drop-down too
            var window4 = new DateTimeWindow { DataContext = nts };
            window4.Show();
            await PumpAsync();
            Assert.Equal("Select a time zone.", nts.ErrorOf(nameof(nts.SelectedZone)));
            Assert.NotNull(nts.ErrorOf(nameof(nts.NtpServersText)));
            Capture(window4, outDir, "datetime-dialog-nts-error.png");
            window4.Close();

            // The time zone drop-down: type a city to jump to it; the open list creates only the visible rows.
            var search = new DateTimeDialogViewModel([new FakeDevice(Guid.NewGuid())], () => now);
            var window5 = new DateTimeWindow { DataContext = search };
            window5.Show();
            await PumpAsync();
            var zones = window5.GetVisualDescendants().OfType<ComboBox>().Single();
            zones.Focus();
            window5.KeyTextInput("Tokyo");
            await PumpAsync();
            Assert.Equal("Asia/Tokyo", search.SelectedZone?.Id);
            zones.IsDropDownOpen = true;
            await PumpAsync();
            var realized = zones.GetRealizedContainers().Count();
            Assert.InRange(realized, 1, 100);
            zones.IsDropDownOpen = false;
            window5.Close();
            return (ok, errors);
        }, CancellationToken.None);

        Assert.True(singleOk);
        Assert.True(multiErrors);
        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
