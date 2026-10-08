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
using Oadm.Sdk.Client.Controls;

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
/// The one headless Avalonia session of the test run (AppBuilder.Setup may only run once per
/// process). Runs the real App against the fake server.
/// </summary>
public static class HeadlessSession
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "oadm-headless-" + Guid.NewGuid().ToString("N"));

    public static string DataFolder => Folder;

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
    {
        App.Options = new AppOptions { UseFake = true, DataFolder = Folder };
        return HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
    });

    public static HeadlessUnitTestSession Shared => Session.Value;
}

/// <summary>
/// Loads the real views against the fake server, so XAML, bindings and theme resources are exercised.
/// Set OADM_SCREENSHOT_DIR to also write PNGs of the rendered windows.
/// </summary>
public sealed class HeadlessSmokeTests
{
    [Fact]
    public async Task Main_window_pages_and_add_page_render_with_fake_server()
    {
        string dataFolder = HeadlessSession.DataFolder;
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;

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
            await PumpUntilAsync(() => vm.Devices.Tasks.Tasks.Any(t => t.DeviceText.Length > 0));
            DataGrid tasksGrid = window.GetVisualDescendants().OfType<Oadm.Client.Tasks.TasksGridView>().Single()
                .GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal(["Name", "Device", "Status", "Current step", "Start time", "Owner", "Progress"], tasksGrid.Columns.Select(c => c.Header as string ?? "").ToArray());
            await PumpUntilAsync(() => vm.Devices.Tasks.Tasks.Any(t => t.CurrentStepText.Contains("Upload firmware", StringComparison.Ordinal)));
            Capture(window, outDir, "client-tasks-pane.png");

            // Exact task names (ITaskPlugin.GetTaskName) and the final "Completed" step of finished tasks.
            string[] names = ["Add user joe", "Remove users guest, temp", "Set static IP 10.0.0.60", "Upgrade AXIS Object Analytics to 1.4.2", "Read brand parameters +2 more"];
            await PumpUntilAsync(() => names.All(n => vm.Devices.Tasks.Tasks.Any(t => t.Name == n)));
            Assert.All(names, n => Assert.EndsWith("Completed", vm.Devices.Tasks.Tasks.First(t => t.Name == n).CurrentStepText, StringComparison.Ordinal));
            var namedWindow = new Window { Width = 1440, Height = 900, Content = new Oadm.Client.Tasks.TasksGridView { DataContext = vm.Devices.Tasks } };
            namedWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Capture(namedWindow, outDir, "client-tasks-pane-names.png");
            namedWindow.Close();

            // Live view of the first camera: the fake server replays recorded H.265, decoded by FFmpeg.
            LiveView.LiveViewViewModel live = vm.Devices.LiveView;
            Devices.DeviceRowViewModel camera = vm.Devices.FilteredDevices.First(LiveView.LiveViewSupport.IsSupported);
            live.ToggleCommand.Execute(camera);
            if (LiveView.FfmpegRuntime.Status.IsAvailable)
            {
                await PumpUntilAsync(() => live.Image is not null && live.State == LiveView.LiveViewState.Live
                    && live.DetailText.Contains("fps", StringComparison.Ordinal) && live.HasSourceChoice);
                Assert.StartsWith("H.265", live.DetailText, StringComparison.Ordinal);
                Capture(window, outDir, "client-liveview.png");

                // The P3265-V has two view areas (like 10.0.0.48): switch to the second one.
                live.SelectSourceCommand.Execute(live.Sources[1]);
                await PumpUntilAsync(() => live.Camera == 2 && live.Image is not null && live.State == LiveView.LiveViewState.Live);
                Capture(window, outDir, "client-liveview-source2.png");
            }
            else
            {
                await PumpUntilAsync(() => live.State == LiveView.LiveViewState.Error);
                Capture(window, outDir, "client-liveview.png");
            }

            live.ToggleCommand.Execute(camera);
            Assert.False(live.IsOpen);
            await PumpUntilAsync(() => true);

            // Fake mode is the administrator: every host page is in the bottom group of the rail.
            Assert.Equal(["users", "credentials", "logs", "settings", "about"], vm.BottomNavItems.Select(n => n.Key).ToArray());
            var pageTitles = new Dictionary<string, string>
            {
                ["users"] = "Who can log in to this server and what they may do.",
                ["credentials"] = "Passwords OADM tries when it adds devices.",
                ["settings"] = "Stored on the OADM server and shared by every client.",
                ["about"] = "Version and licenses.",
            };
            foreach (NavItemViewModel item in vm.NavItems.Concat(vm.BottomNavItems).ToList())
            {
                vm.NavigateCommand.Execute(item);
                await PumpUntilAsync(() => true);
                if (pageTitles.TryGetValue(item.Key, out string? text))
                {
                    List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
                    Assert.Contains(item.Title, texts);
                    Assert.Contains(text, texts);
                }

                Capture(window, outDir, $"client-page-{item.Key.Replace(':', '-')}.png");
            }

            vm.IsNavExpanded = true;
            vm.NavigateCommand.Execute(vm.NavItems[0]);
            await PumpUntilAsync(() => true);
            Capture(window, outDir, "client-rail-expanded.png");
            vm.ToggleNavCommand.Execute(null);
            await PumpUntilAsync(() => true);
            Capture(window, outDir, "client-rail-collapsed.png");

            // The Devices page toolbar: toolbar plugins with a separator between groups.
            StackPanel toolbarPanel = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ToolbarPanel");
            Assert.Equal(["Scan", "Scan IP range", "Add manually", "Import devices", "Remove", "Export devices", "Restart", "AXIS OS - Release Notes"],
                toolbarPanel.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.ToolbarButton>().Select(b => b.Text ?? "").ToArray());
            Capture(window, outDir, "client-toolbar.png");

            // At the minimum window width with the rail expanded the toolbar still fits on one line.
            window.Width = 1280;
            vm.IsNavExpanded = true;
            await PumpUntilAsync(() => true);
            Capture(window, outDir, "client-toolbar-1280.png");
            Assert.True(toolbarPanel.DesiredSize.Width <= toolbarPanel.Bounds.Width + 0.5,
                $"toolbar needs {toolbarPanel.DesiredSize.Width} px, has {toolbarPanel.Bounds.Width} px at 1280 px with the rail expanded");
            window.Width = 1440;
            vm.IsNavExpanded = false;
            await PumpUntilAsync(() => true);

            // The add page in its three modes, with mixed automatic login results.
            var factory = app.Services!.GetRequiredService<Func<AddDevicesMode, AddDevicesViewModel>>();
            AddDevicesViewModel scanVm = factory(AddDevicesMode.Scan);
            var scanWindow = new AddDevicesWindow { DataContext = scanVm, Width = 1040 };
            scanWindow.Show();
            await PumpUntilAsync(() => scanVm.Rows.Count == 11 && scanVm.Rows.All(r => r.AuthState != AuthState.Pending));
            scanVm.SelectAllAuthenticatedCommand.Execute(null);
            await PumpUntilAsync(() => true);
            Capture(scanWindow, outDir, "client-add-scan.png");

            // Compact columns at 1040 px: Login (star) starts right after Model, Action is auto-sized after it.
            DataGrid list = scanWindow.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal([40, 36, 140, 150, 160], list.Columns.Take(5).Select(c => c.ActualWidth).ToArray());
            Assert.True(list.Columns[6].ActualWidth < 200, "Action column is auto-sized");
            Button stop = scanWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopScanButton");
            Assert.True(stop.IsEffectivelyVisible);

            scanVm.FocusedRow = scanVm.Rows.First(r => r.ShowLogIn);
            scanVm.EditorPassword = "secret";
            scanVm.EditorUserName = "";
            await PumpUntilAsync(() => scanVm.IsLoginEditorOpen && scanVm.ErrorOf(nameof(AddDevicesViewModel.EditorUserName)) is not null);
            Capture(scanWindow, outDir, "client-add-login.png");

            // The error sits below its field: the user name box grows, its label stays level with the box.
            FormField userField = scanWindow.GetVisualDescendants().OfType<FormField>().First(f => f.Label == "User name" && f.IsEffectivelyVisible);
            Assert.True(userField.HasError);
            TextBox userBox = (TextBox)userField.Input!;
            TextBlock userLabel = userField.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("fieldLabel"));
            double labelCenter = userLabel.TranslatePoint(new Point(0, userLabel.Bounds.Height / 2), scanWindow)!.Value.Y;
            double boxTop = userBox.TranslatePoint(default, scanWindow)!.Value.Y;
            Assert.InRange(labelCenter - boxTop, 14, 19); // centered on the 32 px box, not on box + error
            Button retry = scanWindow.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Retry" && b.IsEffectivelyVisible);
            Assert.False(retry.IsEffectivelyEnabled);
            Assert.Equal("Enter a user name.", ToolTip.GetTip(retry));
            scanVm.EditorUserName = "root";
            Oadm.Sdk.Client.Controls.PasswordBox loginPassword = scanWindow.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.PasswordBox>().First(p => p.IsEffectivelyVisible);
            Assert.True(loginPassword.RevealButton.IsEffectivelyVisible);
            loginPassword.RevealButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await PumpUntilAsync(() => loginPassword.RevealPassword);
            Capture(scanWindow, outDir, "client-add-login-revealed.png");
            scanVm.CancelEditorCommand.Execute(null);

            // Stop, then a scan that ends on its own (short fake time limit): "Scan finished" with Scan again.
            await scanVm.StopScanCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => !scanVm.IsScanning);
            Assert.Equal("Scan stopped, 8 devices found, 3 already added", scanVm.ScanStatusText);
            Capture(scanWindow, outDir, "client-add-scan-stopped.png");
            if (app.Services!.GetRequiredService<IOadmApi>() is FakeOadmApi fakeApi)
            {
                fakeApi.ZeroConfDuration = TimeSpan.FromMilliseconds(500);
                await scanVm.ScanAgainCommand.ExecuteAsync(null);
                await PumpUntilAsync(() => !scanVm.IsScanning);
                fakeApi.ZeroConfDuration = null;
                Assert.Equal("Scan finished, 8 devices found, 3 already added", scanVm.ScanStatusText);
                Button again = scanWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ScanAgainButton");
                Assert.True(again.IsEffectivelyVisible);
                Assert.False(stop.IsEffectivelyVisible);
                Capture(scanWindow, outDir, "client-add-scan-finished.png");
            }

            scanVm.OpenEditorCommand.Execute(scanVm.Rows.First(r => r.PassphrasePolicy == "complex"));
            scanVm.NewPassword = "short";
            scanVm.ConfirmPassword = "short";
            await PumpUntilAsync(() => scanVm.IsPasswordEditorOpen && scanVm.ErrorOf(nameof(AddDevicesViewModel.NewPassword)) is not null);
            Capture(scanWindow, outDir, "client-add-password.png");
            await scanVm.DisposeAsync();
            scanWindow.Close();

            AddDevicesViewModel rangeVm = factory(AddDevicesMode.IpRange);
            var rangeWindow = new AddDevicesWindow { DataContext = rangeVm };
            rangeWindow.Show();
            rangeVm.RangeFrom = "10.0.1.1";
            rangeVm.RangeTo = "10.0.0.254";
            await PumpUntilAsync(() => rangeVm.ErrorOf(nameof(AddDevicesViewModel.RangeTo)) is not null);
            Capture(rangeWindow, outDir, "client-add-range-error.png");
            rangeVm.RangeTo = "10.0.1.254";
            await rangeVm.StartRangeCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => !rangeVm.IsScanning && rangeVm.Rows.Count == 7 && rangeVm.Rows.All(r => r.AuthState != AuthState.Pending));
            Capture(rangeWindow, outDir, "client-add-range.png");
            await rangeVm.DisposeAsync();
            rangeWindow.Close();

            AddDevicesViewModel manualVm = factory(AddDevicesMode.Manual);
            var manualWindow = new AddDevicesWindow { DataContext = manualVm };
            manualWindow.Show();
            foreach (string address in new[] { "camera7.example.com:8443", "10.0.0.93", "10.0.0.90" })
            {
                manualVm.ManualAddress = address;
                await manualVm.ProbeAddressCommand.ExecuteAsync(null);
            }

            await PumpUntilAsync(() => manualVm.Rows.Count == 3 && manualVm.Rows.All(r => r.AuthState != AuthState.Pending) && !manualVm.IsScanning);
            manualVm.ManualAddress = "10.0.0.199";
            await manualVm.ProbeAddressCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => manualVm.ErrorOf(nameof(AddDevicesViewModel.ManualAddress)) is not null);
            manualVm.SelectAllAuthenticatedCommand.Execute(null);
            await PumpUntilAsync(() => true);
            Capture(manualWindow, outDir, "client-add-manual.png");
            await manualVm.DisposeAsync();
            manualWindow.Close();

            // Import devices: every line of the file is a row; credentials of a line are tried first for its device.
            AddDevicesViewModel importVm = factory(AddDevicesMode.Import);
            importVm.SetImport(DeviceImportFile.Parse("site-a.csv", string.Join('\n',
                "Address,User name,Password",
                "10.0.0.93,admin,right-Pass1",
                "10.0.0.97,,",
                "camera7.example.com:8443,,",
                "10.0.0.90,,",
                "10.0.0.199,,",
                "10.0.0.97,,",
                "not an address,,")));
            var importWindow = new AddDevicesWindow { DataContext = importVm, Width = 1040 };
            importWindow.Show();
            await PumpUntilAsync(() => importVm.ImportCompletion is { IsCompleted: true } && !importVm.IsScanning && importVm.Rows.All(r => r.IsImportPlaceholder || r.AuthState != AuthState.Pending));
            importVm.SelectAllAuthenticatedCommand.Execute(null);
            await PumpUntilAsync(() => true);
            Capture(importWindow, outDir, "client-add-import.png");
            Assert.Equal("Import finished, 4 devices found", importVm.ScanStatusText);
            await importVm.DisposeAsync();
            importWindow.Close();

            // Task details with per-device results and the task log (fake "Done with warnings" task).
            TaskRowViewModel warned = vm.Devices.Tasks.Tasks.First(t => t.State == TaskState.DoneWithWarnings);
            var details = new TaskDetailsViewModel(warned, app.Services!.GetRequiredService<DeviceStore>());
            await details.LoadLogAsync(app.Services!.GetRequiredService<IOadmApi>(), CancellationToken.None);
            var detailsWindow = new TaskDetailsWindow { DataContext = details };
            detailsWindow.Show();
            await PumpUntilAsync(() => details.Log.Count == 3);
            Capture(detailsWindow, outDir, "client-task-details.png");
            detailsWindow.Close();

            // Task details of a running multi-step task: the step list follows the task live.
            TaskRowViewModel upgrade = vm.Devices.Tasks.Tasks.First(t => t.PluginId == "oadm.firmware");
            var stepDetails = new TaskDetailsViewModel(upgrade, app.Services!.GetRequiredService<DeviceStore>());
            await stepDetails.LoadLogAsync(app.Services!.GetRequiredService<IOadmApi>(), CancellationToken.None);
            var stepsWindow = new TaskDetailsWindow { DataContext = stepDetails };
            stepsWindow.Show();
            await PumpUntilAsync(() => stepDetails.Steps.Count == 10 && stepDetails.Steps.Any(s => s.IsRunning));
            Capture(stepsWindow, outDir, "client-task-details-steps.png");
            stepsWindow.Close();
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
