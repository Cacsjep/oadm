using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Oadm.Plugins.EdgeRecordingExport.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>
/// The page rendered headless in the host's page frame (screenshots: OADM_SCREENSHOT_DIR).
/// </summary>
public sealed class HeadlessTests
{
    [Fact]
    public async Task The_page_shows_what_the_search_found_and_the_progress_of_a_download()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        Assert.True(await session.Dispatch(async () =>
        {
            await SearchResultsAsync();
            await DownloadProgressAsync();
            return true;
        }, CancellationToken.None));
    }

    private static async Task SearchResultsAsync()
    {
        var rig = new Rig();
        var a = rig.Add(FakeCamera.WithClips(240, TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(30)), "10.0.0.48", tags: "Building A");
        rig.Add(FakeCamera.WithClips(12, TimeSpan.FromHours(2), TimeSpan.FromHours(1)), "10.0.0.22", tags: "Building A");
        rig.Add(new FakeCamera { LocalStorage = false }, "10.0.0.21", tags: "Building A");
        rig.Add(new FakeCamera(), "10.0.0.23", DeviceStatus.CredentialsRequired, tags: "Building A");
        rig.Add(new FakeCamera(), "10.0.0.31", tags: "Building A");
        using var vm = new EdgeRecordingViewModel(rig.Page);
        var view = new EdgeRecordingView { DataContext = vm };
        var window = Headless.Host(view);
        window.Show();
        Headless.Pump();
        Headless.Capture(window, "edge-recording-export-empty.png");

        vm.SetChoice(DeviceChoice.Devices([], ["Building A"]));
        vm.StartDate = new DateTime(2026, 10, 12);
        vm.StopDate = new DateTime(2026, 10, 13);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(await Headless.UntilAsync(() => vm.IsIdle && vm.Rows.Count == 5));
        Headless.Pump();
        Headless.Capture(window, "edge-recording-export.png");

        var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
        Assert.Equal(5, grid.ItemsSource!.Cast<object>().Count());
        Assert.Contains("Tag Building A", vm.CamerasText, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 12)), view.GetVisualDescendants().OfType<DatePicker>().Single(p => p.Name == "StartDatePicker").SelectedDate);
        Assert.Equal(TimeSpan.Zero, view.GetVisualDescendants().OfType<TimePicker>().Single(p => p.Name == "StopTimePicker").SelectedTime);
        var download = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DownloadButton");
        Assert.True(download.IsEnabled);
        Assert.EndsWith(" recordings found", vm.Rows.Single(r => r.Device.Id == a.Id).StatusText, StringComparison.Ordinal);

        rig.Page.Denied.Add(PermissionKeys.Of(EdgeRecordingInfo.PluginId, EdgeRecordingInfo.ExportKey));
        rig.Page.RaisePermissionsChanged();
        Headless.Pump();
        Assert.False(download.IsEnabled);
        Assert.Equal("Your role does not allow exporting recordings.", ToolTip.GetTip(download));
        window.Close();
        Headless.Pump();
    }

    private static async Task DownloadProgressAsync()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(400, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        camera.ExportBytesPerSecond = 200_000;
        var device = rig.Add(camera, "10.0.0.48");
        using var folder = new TempFolder();
        using var vm = new EdgeRecordingViewModel(rig.Page) { AllRecordings = true };
        var view = new EdgeRecordingView { DataContext = vm };
        vm.PickFolder = () => Task.FromResult<string?>(folder.Path);
        var window = Headless.Host(view);
        window.Show();
        vm.SetChoice(DeviceChoice.Devices([device.Id]));
        await vm.SearchCommand.ExecuteAsync(null);

        var run = vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(await Headless.UntilAsync(() => vm.Rows[0].Downloaded >= 3));
        var stop = view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "StopButton");
        Assert.True(stop.IsEffectivelyVisible);
        Assert.StartsWith("Downloading recording ", vm.ProgressText, StringComparison.Ordinal);
        Headless.Capture(window, "edge-recording-export-download.png");
        vm.StopCommand.Execute(null);
        var watch = Stopwatch.StartNew();
        while (!run.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            Headless.Pump();
            await Task.Delay(10);
        }

        await run;
        Headless.Pump();
        Assert.False(stop.IsEffectivelyVisible);
        Assert.StartsWith("Download stopped", vm.SummaryText, StringComparison.Ordinal);
        window.Close();
        Headless.Pump();
    }
}
