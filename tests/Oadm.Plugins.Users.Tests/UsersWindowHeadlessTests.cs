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
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

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

            // Remove mode: no user name field; several rows selected in the grid, the protected root row is refused.
            vm.IsRemove = true;
            Dispatcher.UIThread.RunJobs();
            var grid = window.FindControl<DataGrid>("UsersGrid")!;
            Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
            grid.SelectedItems.Add(vm.ExistingUsers[0]);
            grid.SelectedItems.Add(vm.ExistingUsers[1]);
            grid.SelectedItems.Add(vm.ExistingUsers[3]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["fakeroot", "acs"], vm.UsersToRemove);
            Assert.DoesNotContain(vm.ExistingUsers[0], grid.SelectedItems.Cast<object>());
            Assert.True(vm.CanApply);
            Capture(window, outDir, "plugin-users-remove.png");

            vm.IsChange = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(DataGridSelectionMode.Single, grid.SelectionMode);
            Assert.True(grid.SelectedItems.Count <= 1);

            vm.IsChange = true;
            vm.UserName = "acs";
            vm.ChangeRole = true;
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "plugin-users-change.png");

            // Errors directly below their fields; Apply disabled with the reason as tooltip.
            vm.IsAdd = true;
            vm.UserName = "jo e";
            vm.Password = "pw";
            vm.ConfirmPassword = "px";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("The user name may only contain the letters a-z, A-Z and the digits 0-9.", vm.ErrorOf(nameof(vm.UserName)));
            Assert.Equal("The passwords do not match.", vm.ErrorOf(nameof(vm.ConfirmPassword)));
            Assert.False(vm.CanApply);
            Assert.Equal(vm.ErrorOf(nameof(vm.UserName)), vm.ApplyBlockedReason);
            Capture(window, outDir, "plugin-users-errors.png");
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
