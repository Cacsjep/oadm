using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The server's reason for a status (status_detail) in the device list: the Status column's tooltip.</summary>
public sealed class DeviceStatusDetailTests
{
    [Fact]
    public async Task A_device_that_offers_only_Basic_over_HTTP_says_so_in_the_status_column()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            using var fixture = new DevicesFixture(api);
            var devices = (await api.ListDevicesAsync(CancellationToken.None)).Select(d => d.Clone()).ToList();
            var basicOnly = devices[0];
            basicOnly.Status = DeviceStatus.CredentialsRequired;
            basicOnly.StatusDetail = Oadm.Sdk.Devices.DeviceMessages.BasicOnlyOverHttp;
            fixture.SeedDevices([.. devices]);

            var row = fixture.Store.Find(basicOnly.Id)!;
            Assert.Equal("Basic login only", row.StatusText);
            Assert.Equal(Oadm.Sdk.Devices.DeviceMessages.BasicOnlyOverHttp, row.StatusDetail);
            Assert.All(fixture.Store.Devices.Where(r => r.Id != basicOnly.Id), r => Assert.Null(r.StatusDetail));

            var window = new Window { Width = 1500, Height = 600, Content = new DevicesView { DataContext = fixture.Devices } };
            window.Show();
            Pump();
            var cell = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => Equals(ToolTip.GetTip(t), Oadm.Sdk.Devices.DeviceMessages.BasicOnlyOverHttp));
            Assert.Equal("Basic login only", cell.Text);
            ToolTip.SetIsOpen(cell, true);
            Pump();
            Capture(window, outDir, "devices-status-basic-only.png");
            ToolTip.SetIsOpen(cell, false);

            // The next check says nothing more: the tooltip goes.
            var ok = basicOnly.Clone();
            ok.Status = DeviceStatus.Ok;
            ok.StatusDetail = "";
            fixture.SeedDevices([.. devices.Select(d => d.Id == ok.Id ? ok : d)]);
            Assert.Null(fixture.Store.Find(ok.Id)!.StatusDetail);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
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
