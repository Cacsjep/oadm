using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Oadm.Client;
using Oadm.Client.Infrastructure;
using Oadm.Plugins.Users.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Users.Tests;

/// <summary>Headless session with the real client App, so the dialog renders with the host theme (OadmTheme.axaml).</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the Users dialog offscreen. Set OADM_SCREENSHOT_DIR to also write PNGs.</summary>
public sealed class UsersWindowHeadlessTests
{
    [Fact]
    public async Task Users_dialog_renders_with_host_theme()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-users-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

        await session.Dispatch(() =>
        {
            IReadOnlyList<IDeviceInfo> devices = [new FakeDevice(), new FakeDevice { Address = "10.0.0.49" }, new FakeDevice { Address = "10.0.0.50" }];
            var vm = new UsersDialogViewModel(devices);
            vm.Apply(UsersDialogViewModelTests.RecordedResult());
            vm.UserName = "joe";
            vm.Password = vm.ConfirmPassword = "pw";
            vm.SelectedRole = vm.Roles[1];
            vm.Ptz = true;
            var window = new UsersWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Bounds.Height > 0);
            Capture(window, outDir, "plugin-users-add.png");

            vm.IsRemove = true;
            vm.UserName = "root";
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "plugin-users-remove.png");

            vm.IsChange = true;
            vm.UserName = "acs";
            vm.ChangeRole = true;
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "plugin-users-change.png");
            window.Close();
            return Task.CompletedTask;
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
