using System.Net;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Oadm.Client;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Plugins.VapixCommander.Client;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>Headless session with the real client App, so the page renders with the host theme (OadmTheme.axaml).</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the Commander page offscreen inside the host's core plugin page view. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
public sealed class CommanderViewHeadlessTests
{
    [Fact]
    public async Task Commander_page_renders_rollout_set_try_error_and_raw_request()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-commander-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        try
        {
            var page = await session.Dispatch(PageFixture.CreateAsync, CancellationToken.None);
            await session.Dispatch(async () =>
            {
                var vm = page.Vm;
                var more = Enumerable.Range(49, 3).Select(i => new FakeDevice { Address = "10.0.0." + i, Serial = "B8A44F6313" + i }).ToList();
                page.Server.DeviceList.All.AddRange(more);
                page.Client.DeviceList.AddRange(more);
                page.Client.RaiseDevicesChanged();
                vm.Targets.Single(t => t.Address == "10.0.0.49").IsSelected = true;
                vm.Targets.Single(t => t.Address == "10.0.0.61").IsSelected = true;
                vm.Add(page.Item("common.brand.read"));
                vm.Add(page.Item("common.basicdeviceinfo.read"));
                vm.Add(page.Item("common.daynight.shiftlevel")).Fields.Single(f => f.Name == "level").Text = "65";
                await vm.RefreshCompatibilityAsync();

                var view = new CommanderView { DataContext = vm };
                var host = new CorePluginPageView { DataContext = new CorePluginPageViewModel(VapixCommanderPlugin.PluginId, "VAPIX Commander", view, hasOwnCards: true) };
                var window = new Window { Width = 1600, Height = 940, Content = new Border { Padding = new Thickness(16), Child = host } };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.True(view.Bounds.Width > 1000);
                Capture(window, outDir, "plugin-vapix-commander-rollout.png");

                // Try on one device with a device error (param.cgi "# Error" behind HTTP 400).
                page.Server.VapixFactory.For(page.Camera.Id).Handler = _ =>
                    FakeVapix.Text("# Error: Error setting 'root.ImageSource.I0.DayNight.ShiftLevel' to '65'!", HttpStatusCode.BadRequest);
                page.Client.ConfirmAnswers.Enqueue(true);
                await vm.TryCommand.ExecuteAsync(null);
                Assert.False(vm.TryResult!.Success);
                Dispatcher.UIThread.RunJobs();
                Capture(window, outDir, "plugin-vapix-commander-try-error.png");

                vm.CloseTryResultCommand.Execute(null);
                vm.ShowRawCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                vm.Raw.MakeField(vm.Raw.QueryRows[1]);
                Dispatcher.UIThread.RunJobs();
                Capture(window, outDir, "plugin-vapix-commander-raw.png");
                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            // The awaited dispatch may continue on the session's UI thread; Dispose waits for that thread, so dispose elsewhere.
            await Task.Run(session.Dispose);
        }

        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
