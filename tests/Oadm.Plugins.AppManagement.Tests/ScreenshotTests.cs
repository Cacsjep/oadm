using Oadm.Plugins.AppManagement.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>Renders the page offscreen like the client shell; OADM_SCREENSHOT_DIR writes PNGs (dark, or light with OADM_TEST_APPEARANCE=Light).</summary>
public sealed class ScreenshotTests
{
    [Fact]
    public async Task The_page_renders_with_selected_devices()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(() =>
        {
            var ctx = new FakePageContext();
            var vm = new ApplicationsViewModel(ctx, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));
            var view = new ApplicationsView();
            var window = Headless.Host(view, 1900); // a full HD screen, like the user's
            window.Show();
            view.DataContext = vm; // after attaching: no calls, deterministic content
            vm.DeviceChoice.Set(DeviceChoice.AllDevices);
            vm.ApplyRows(Rows());
            vm.ApplyState(new ReadState { Reading = false, Done = 12, Total = 12, Devices = 12, FinishedUtc = new DateTimeOffset(2026, 10, 28, 11, 58, 0, TimeSpan.Zero) });
            Headless.Pump();
            var devices = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.DataGrid>(view, "DevicesGrid")!;
            devices.SelectedItems.Add(vm.Devices[1]);
            devices.SelectedItems.Add(vm.Devices[2]);
            Headless.Pump();
            Assert.NotEmpty(vm.SelectedApps);
            Assert.True(vm.GroupApps);
            Headless.Capture(window, $"applications-{suffix}.png");

            // A task's end reads the device again: both grids keep their selection.
            var apps = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.DataGrid>(view, "AppsGrid")!;
            apps.SelectedItems.Add(vm.SelectedApps[0]);
            Headless.Pump();
            var selectedApp = (vm.SelectedApps[0].DeviceId, vm.SelectedApps[0].AppKey);
            vm.ApplyRows([vm.Devices[1].Device with { Activity = AppActivities.Stopping }]);
            Headless.Pump();
            Assert.Equal(2, devices.SelectedItems.Count);
            Assert.Equal(2, vm.SelectedDeviceIds.Count);
            var app = Assert.Single(apps.SelectedItems.OfType<AppRow>());
            Assert.Equal(selectedApp, (app.DeviceId, app.AppKey));
            Assert.Single(vm.SelectedRows);
            window.Close();
            vm.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_log_and_settings_windows_render()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = new FakePageContext();
            ctx.Answers[AppManagementMethods.GetLog] = _ => AppManagementJson.Serialize(new LogReply { Lines = Oadm.Plugins.Acap.AppLogReader.Parse(LogAndSettingsTests.Log).Lines });
            var log = new AppLogViewModel(ctx, Guid.NewGuid(), "demo", "AXIS Perimeter Defender", "10.0.0.48");
            await log.RefreshAsync();
            var logWindow = new AppLogWindow { DataContext = log, Width = 1000, Height = 520 };
            Oadm.Client.App.ApplyCrispText(logWindow);
            logWindow.Show();
            Headless.Pump();
            Headless.Capture(logWindow, $"page-stage2-log-{suffix}.png");
            Headless.Capture(logWindow, $"followup-log-level-icons-{suffix}.png");
            logWindow.Close();
            log.Dispose();

            var settings = new AppSettingsViewModel(ctx, "demo", "AXIS Perimeter Defender", [], "4 devices");
            settings.Apply(new SettingsReply
            {
                ValuesShown = true,
                Devices =
                [
                    .. new[] { "fast", "fast", "exact", "exact" }.Select((mode, i) => new DeviceSettings
                    {
                        DeviceId = Guid.NewGuid(),
                        Address = "10.0.0.4" + i,
                        Settings = [.. Oadm.Plugins.Acap.AppSettings.Parse(LogAndSettingsTests.Typed(i == 3 ? "70" : "40", i == 0 ? "exact" : mode), "demo").Where(s => !s.Hidden)],
                    }),
                    new DeviceSettings { DeviceId = Guid.NewGuid(), Address = "10.0.0.50", Error = "Timeout after 15 s" },
                ],
            });
            settings.Rows.Single(r => r.Name == "Server").Value = new string('x', 70); // longer than the device takes: the error below the field
            var settingsWindow = new AppSettingsWindow { DataContext = settings };
            Oadm.Client.App.ApplyCrispText(settingsWindow);
            settingsWindow.Show();
            Headless.Pump();
            Headless.Capture(settingsWindow, $"page-stage2-settings-{suffix}.png");
            settingsWindow.Close();

            // An application without settings of its own, on one device, with its own page and Web View on.
            ctx.PluginsOn.Add(AppSettingsViewModel.WebViewId);
            var device = Guid.NewGuid();
            var none = new AppSettingsViewModel(ctx, "ax_msf", "Missing Features", [device], "10.0.0.48", "/local/ax_msf/redirect.html");
            none.Apply(new SettingsReply { ValuesShown = true, Devices = [new DeviceSettings { DeviceId = device, Address = "10.0.0.48" }] });
            Assert.True(none.CanOpenAppPage);
            var noneWindow = new AppSettingsWindow { DataContext = none };
            Oadm.Client.App.ApplyCrispText(noneWindow);
            noneWindow.Show();
            Headless.Pump();
            Headless.Capture(noneWindow, $"followup-settings-app-page-{suffix}.png");
            noneWindow.Close();

            var several = new AppSettingsViewModel(ctx, "ax_msf", "Missing Features", [Guid.NewGuid(), Guid.NewGuid()], "2 devices", "/local/ax_msf/redirect.html");
            several.Apply(new SettingsReply { ValuesShown = true, Devices = [new DeviceSettings { DeviceId = Guid.NewGuid(), Address = "10.0.0.48" }] });
            Assert.True(several.ShowOneDeviceHint);
            var severalWindow = new AppSettingsWindow { DataContext = several };
            Oadm.Client.App.ApplyCrispText(severalWindow);
            severalWindow.Show();
            Headless.Pump();
            Headless.Capture(severalWindow, $"followup-settings-several-{suffix}.png");
            severalWindow.Close();
            return true; // a value: Dispatch waits for the whole lambda
        }, CancellationToken.None);
    }

    private static List<DeviceApps> Rows()
    {
        var rows = new List<DeviceApps>();
        for (var i = 0; i < 9; i++)
        {
            rows.Add(Samples.Device("10.0.0." + (40 + i),
                Samples.App("objectanalytics", "AXIS Object Analytics", i < 6 ? "1.30.4" : "1.26.2", bundled: true),
                Samples.App("platereader", "Parking Plate Reader", i < 7 ? "2.10.3" : "2.9.1", i == 2 ? "Stopped" : "Running", i == 4 ? "Missing" : "Valid", i == 5 ? "2026-10-31" : "2027-09-18", vendor: "Acme Parking"),
                Samples.App(i % 2 == 0 ? "eva" : "facever", i % 2 == 0 ? "Eva - Event Virtualizer" : "Face Verifier - Beta", "0.9.0", "Stopped", vendor: "Acs"),
                Samples.App("perimeter", "Perimeter Guard", "3.4.1", i % 3 == 0 ? "Idle" : "Running", "Valid", "2027-03-01", vendor: "Acme Security")) with
            {
                Activity = i == 3 ? AppActivities.Stopping : null,
                TaskResults = i == 2 ? [new AppTaskResult { Application = "platereader", Failed = true, Message = "The application did not start. Nothing was changed." }]
                    : i == 6 ? [new AppTaskResult { Application = "perimeter", Message = "The settings were changed, 1 could not be read back." }]
                    : [],
            });
        }

        rows.Add(Samples.Device("10.0.0.49"));
        rows.Add(Samples.Failed("10.0.0.50", "Timeout after 15 s"));
        rows.Add(Samples.Failed("10.0.0.51", AppManagementInfo.NotChecked("Credentials required"), notChecked: true));
        return rows;
    }
}
