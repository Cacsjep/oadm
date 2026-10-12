using Oadm.Plugins.EdgeRecordingExport.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>The page: choosing cameras, the range, the search and the download into a folder per camera.</summary>
public sealed class ViewModelTests
{
    private static (Rig Rig, EdgeRecordingViewModel Vm) Page()
    {
        var rig = new Rig();
        return (rig, new EdgeRecordingViewModel(rig.Page));
    }

    [Fact]
    public async Task Search_finds_the_recordings_of_every_chosen_camera_and_sums_them_up()
    {
        var (rig, vm) = Page();
        var a = rig.Add(FakeCamera.WithClips(3, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)), "10.0.0.48");
        var b = rig.Add(FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)), "10.0.0.5", tags: "Building A");
        rig.Add(new FakeCamera { LocalStorage = false }, "10.0.0.7", tags: "Building A");
        rig.Add(new FakeCamera { HasDisk = false }, "10.0.0.8", tags: "Building A");
        var readOnly = FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        readOnly.SdCard = "status=\"OK\" readonly=\"yes\"";
        rig.Add(readOnly, "10.0.0.6", tags: "Building A");
        rig.Add(null, "10.0.0.9", category: DeviceCategory.Speaker, tags: "Building A"); // not a video device: never listed
        vm.AllRecordings = true;
        Assert.False(vm.CanSearch);
        Assert.Equal("Choose cameras first", vm.SearchTip);

        vm.SetChoice(DeviceChoice.Devices([a.Id], ["Building A"]));
        Assert.Equal("Selected cameras: Tag Building A and 10.0.0.48", vm.CamerasText);
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["10.0.0.5", "10.0.0.6", "10.0.0.7", "10.0.0.8", "10.0.0.48"], vm.Rows.Select(r => r.Address)); // address order
        Assert.Equal("5 cameras · 5 recordings · about 9.8 MB · 1 not searched", vm.SummaryText);
        Assert.Equal(("No SD card or network share", false), (vm.Rows[3].StatusText, vm.Rows[3].IsWarning));
        Assert.Equal(("1 recording found", true, "The SD card is read-only"), (vm.Rows[1].StatusText, vm.Rows[1].IsWarning, vm.Rows[1].StatusDetail));
        var row = vm.Rows.Single(r => r.Device.Id == a.Id);
        Assert.Equal(("3 recordings found", true, "3", "about 5.9 MB"), (row.StatusText, row.IsOk, row.RecordingsText, row.SizeText));
        var failed = vm.Rows.Single(r => r.Address == "10.0.0.7");
        Assert.Equal((Recordings.EdgeRecordingTexts.NoStorage, true), (failed.StatusText, failed.IsError));
        Assert.True(vm.CanDownload);
        Assert.Equal(b.Id, vm.Rows[0].Device.Id);
    }

    [Fact]
    public void The_range_is_checked_below_its_fields_and_all_recordings_needs_none()
    {
        var (rig, vm) = Page();
        vm.SetChoice(DeviceChoice.AllDevices);
        vm.StartText = "2026-10-12 08:00";
        vm.StopText = "2026-10-12 07:00";
        Assert.Equal("The stop must be after the start.", vm.ErrorOf(nameof(vm.StopText)));
        vm.StopText = "tomorrow";
        Assert.Equal("Enter the stop as yyyy-mm-dd hh:mm.", vm.ErrorOf(nameof(vm.StopText)));
        Assert.False(vm.CanSearch);
        vm.AllRecordings = true;
        Assert.Null(vm.ErrorOf(nameof(vm.StopText)));
        Assert.True(vm.CanSearch);
        Assert.Equal("Selected cameras: All", vm.CamerasText);
        Assert.Empty(rig.Devices.Items);
    }

    [Fact]
    public async Task Download_writes_a_folder_per_camera_with_the_cameras_file_names_and_skips_them_the_next_time()
    {
        var (rig, vm) = Page();
        var camera = FakeCamera.WithClips(3, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");
        using var folder = new TempFolder();
        vm.PickFolder = () => Task.FromResult<string?>(folder.Path);
        vm.AllRecordings = true;
        vm.SetChoice(DeviceChoice.Devices([device.Id]));
        await vm.SearchCommand.ExecuteAsync(null);

        await vm.DownloadCommand.ExecuteAsync(null);

        var dir = Path.Combine(folder.Path, $"10.0.0.48 P3265-V {device.Serial}");
        Assert.Equal(
            ["20261012_200000_20261012_200010.mp4", "20261012_200100_20261012_200110.mp4", "20261012_200200_20261012_200210.mp4"],
            Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        Assert.All(Directory.GetFiles(dir), f => Assert.Equal(10_000, new FileInfo(f).Length));
        Assert.Equal("3 downloaded", vm.Rows[0].StatusText);
        Assert.StartsWith("Download finished: 3 downloaded · 29.3 KB in ", vm.SummaryText, StringComparison.Ordinal);

        await vm.DownloadCommand.ExecuteAsync(null); // a second run: everything is there
        Assert.Equal("Already there: 3", "Already there: " + vm.Rows[0].Skipped);
        Assert.Equal("3 already there", vm.Rows[0].StatusText);
        Assert.Equal(3, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public async Task A_recording_that_fails_is_counted_in_its_row_and_the_others_continue()
    {
        var (rig, vm) = Page();
        var camera = FakeCamera.WithClips(3, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");
        using var folder = new TempFolder();
        vm.PickFolder = () => Task.FromResult<string?>(folder.Path);
        vm.AllRecordings = true;
        vm.SetChoice(DeviceChoice.Devices([device.Id]));
        await vm.SearchCommand.ExecuteAsync(null);
        camera.Missing.Add(camera.Recordings[1].Id);

        await vm.DownloadCommand.ExecuteAsync(null);

        var row = vm.Rows[0];
        Assert.Equal(("2 downloaded, 1 failed", true), (row.StatusText, row.IsError));
        Assert.Equal(Recordings.EdgeRecordingTexts.NotExported, row.StatusDetail);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Two_recordings_with_the_same_name_get_a_number()
    {
        var (rig, vm) = Page();
        var camera = new FakeCamera();
        camera.Recordings.Add(new FakeRecording("source1", FakeCamera.T0, FakeCamera.T0.AddSeconds(10)));
        camera.Recordings.Add(new FakeRecording("source2", FakeCamera.T0, FakeCamera.T0.AddSeconds(10)));
        var device = rig.Add(camera, "10.0.0.48");
        using var folder = new TempFolder();
        vm.PickFolder = () => Task.FromResult<string?>(folder.Path);
        vm.AllRecordings = true;
        vm.SetChoice(DeviceChoice.Devices([device.Id]));
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["20261012_200000_20261012_200010.mp4", "20261012_200000_20261012_200010_2.mp4"],
            Directory.GetFiles(Directory.GetDirectories(folder.Path).Single()).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Without_the_export_right_the_page_searches_but_does_not_download()
    {
        var (rig, vm) = Page();
        var device = rig.Add(FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)), "10.0.0.48");
        rig.Page.Denied.Add(PermissionKeys.Of(EdgeRecordingInfo.PluginId, EdgeRecordingInfo.ExportKey));
        rig.Page.RaisePermissionsChanged();
        vm.AllRecordings = true;
        vm.SetChoice(DeviceChoice.Devices([device.Id]));
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal((false, "Your role does not allow exporting recordings."), (vm.CanDownload, vm.DownloadTip));
        vm.PickFolder = () => throw new InvalidOperationException("must not ask");
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(0, rig.Page.Downloads);
    }

    [Fact]
    public async Task Stop_ends_the_download_and_keeps_the_finished_files()
    {
        var (rig, vm) = Page();
        var camera = FakeCamera.WithClips(200, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");
        using var folder = new TempFolder();
        vm.AllRecordings = true;
        vm.SetChoice(DeviceChoice.Devices([device.Id]));
        await vm.SearchCommand.ExecuteAsync(null);
        var stopAfter = 0;
        vm.PickFolder = () => Task.FromResult<string?>(folder.Path);
        vm.Rows[0].PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraRowViewModel.StatusText) && vm.Rows[0].StatusText == "Downloading 5 of 200" && stopAfter++ == 0)
            {
                vm.StopCommand.Execute(null);
            }
        };

        await vm.DownloadCommand.ExecuteAsync(null);

        var files = Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories);
        Assert.InRange(files.Length, 4, 5);
        Assert.DoesNotContain(files, f => f.EndsWith(".part", StringComparison.Ordinal));
        Assert.EndsWith("stopped", vm.Rows[0].StatusText, StringComparison.Ordinal);
        Assert.StartsWith("Download stopped", vm.SummaryText, StringComparison.Ordinal);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public void Folder_names_are_safe_on_every_system()
    {
        var device = new TestDevice("fe80::1%eth0", "AXIS Q1656-LE") { Serial = "ACCC8E3344BB" };
        Assert.Equal("fe80--1%eth0 Q1656-LE ACCC8E3344BB", RecordingFiles.CameraFolderName(device));
        Assert.Equal("recording", RecordingFiles.Safe(".."));
        Assert.Equal("a-b", RecordingFiles.Safe("a/b"));
    }

    [Fact]
    public void Sizes_and_times_read_well()
    {
        Assert.Equal(("about 48 GB", "Unknown", "512 bytes"), (EdgeRecordingFormat.AboutSize(48L * 1024 * 1024 * 1024), EdgeRecordingFormat.AboutSize(null), EdgeRecordingFormat.Size(512)));
        var local = EdgeRecordingFormat.ParseLocal("2026-10-12 08:00");
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Local)), local);
        Assert.Null(EdgeRecordingFormat.ParseLocal("12.10.2026"));
    }
}
