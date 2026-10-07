using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>Entry point for the headless Avalonia session: the real App with Skia rendering.</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>
/// Loads the real views against the fake server, so XAML, bindings and theme resources are exercised.
/// Set OADM_SCREENSHOT_DIR to also write PNGs of the rendered windows.
/// </summary>
public sealed class HeadlessSmokeTests
{
    [Fact]
    public async Task Main_window_pages_and_wizard_render_with_fake_server()
    {
        string dataFolder = Path.Combine(Path.GetTempPath(), "oadm-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

        int deviceCount = await session.Dispatch(async () =>
        {
            var app = (App)Application.Current!;
            MainWindowViewModel vm = app.Services!.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            vm.Start();
            await PumpUntilAsync(() => vm.Devices.FilteredDevices.Count == 12 && vm.Devices.Tasks.Tasks.Count > 0);
            Capture(window, outDir, "client-fake-headless.png");

            // Tasks pane with the Devices column (after Name)
            vm.Devices.Tasks.IsExpanded = true;
            await PumpUntilAsync(() => vm.Devices.Tasks.Tasks.Any(t => t.DevicesText.Length > 0));
            DataGrid tasksGrid = window.GetVisualDescendants().OfType<Oadm.Client.Tasks.TasksGridView>().Single()
                .GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal(["Name", "Devices", "Status", "Start time", "Owner", "Progress"], tasksGrid.Columns.Select(c => c.Header as string ?? "").ToArray());
            Capture(window, outDir, "client-tasks-pane.png");

            foreach (NavItemViewModel item in vm.NavItems.Concat(vm.BottomNavItems).ToList())
            {
                vm.NavigateCommand.Execute(item);
                await PumpUntilAsync(() => true);
                Capture(window, outDir, $"client-page-{item.Key}.png");
            }

            vm.ToggleNavCommand.Execute(null);
            vm.NavigateCommand.Execute(vm.NavItems[0]);
            await PumpUntilAsync(() => true);
            Capture(window, outDir, "client-rail-expanded.png");

            var factory = app.Services!.GetRequiredService<Func<AddDevicesMode, AddDevicesWizardViewModel>>();
            AddDevicesWizardViewModel wizardVm = factory(AddDevicesMode.ZeroConf);
            var wizard = new AddDevicesWizardWindow { DataContext = wizardVm };
            wizard.Show();
            await PumpUntilAsync(() => wizardVm.Discovered.Count >= 8);
            wizardVm.SelectAllCommand.Execute(null);
            await PumpUntilAsync(() => true);
            Assert.Equal(["Select devices", "Set password", "Credentials", "Review"], wizardVm.Steps.Select(s => s.Title).ToArray());
            Capture(wizard, outDir, "client-wizard.png");
            await wizardVm.NextCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => wizardVm.CurrentStep == WizardStep.Password);
            Capture(wizard, outDir, "client-wizard-password.png");
            wizardVm.SkipCommand.Execute(null);
            wizardVm.Password = "secret";
            await wizardVm.NextCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => wizardVm.CurrentStep == WizardStep.Review);
            Capture(wizard, outDir, "client-wizard-review.png");
            await wizardVm.DisposeAsync();
            wizard.Close();

            // Task details with per-device results and the task log (fake "Done with warnings" task).
            TaskRowViewModel warned = vm.Devices.Tasks.Tasks.First(t => t.State == TaskState.DoneWithWarnings);
            var details = new TaskDetailsViewModel(warned, app.Services!.GetRequiredService<DeviceStore>());
            await details.LoadLogAsync(app.Services!.GetRequiredService<IOadmApi>(), CancellationToken.None);
            var detailsWindow = new TaskDetailsWindow { DataContext = details };
            detailsWindow.Show();
            await PumpUntilAsync(() => details.Log.Count == 4);
            Capture(detailsWindow, outDir, "client-task-details.png");
            detailsWindow.Close();
            window.Close();
            return vm.Devices.FilteredDevices.Count;
        }, CancellationToken.None);

        Assert.Equal(12, deviceCount);
        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
    }

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
        while (!condition() && DateTime.UtcNow < end);

        Assert.True(condition(), "UI condition not met in time");
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
