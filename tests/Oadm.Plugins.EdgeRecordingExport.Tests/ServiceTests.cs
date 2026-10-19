using System.Text.Json;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>The server part against fake cameras: search, size estimate, export, refusals and the page's rights.</summary>
public sealed class ServiceTests
{
    private static async Task<SearchResult> SearchAsync(Rig rig, Guid deviceId, DateTimeOffset? start = null, DateTimeOffset? stop = null) =>
        JsonSerializer.Deserialize<SearchResult>(
            (await rig.Plugin.InvokeAsync(EdgeRecordingMethods.Search, JsonSerializer.Serialize(new SearchRequest(deviceId, start, stop), EdgeRecordingInfo.Json), CancellationToken.None))!,
            EdgeRecordingInfo.Json)!;

    [Fact]
    public async Task A_search_lists_the_recordings_in_the_range_cut_to_it_with_their_exact_size()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(6, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");

        var result = await SearchAsync(rig, device.Id, FakeCamera.T0.AddSeconds(65), FakeCamera.T0.AddMinutes(3).AddSeconds(5));

        Assert.Null(result.Problem);
        Assert.Equal("mp4", result.Format);
        Assert.Equal(3, result.Recordings.Count); // the clips at 1, 2 and 3 minutes
        Assert.Equal(FakeCamera.T0.AddSeconds(65), result.Recordings[0].Start); // cut by the camera
        Assert.Equal(FakeCamera.T0.AddMinutes(3).AddSeconds(5), result.Recordings[2].Stop);
        Assert.Equal((5 + 10 + 5) * 200L * 1024 / 1024 * 1024, result.EstimatedBytes); // every recording estimated
        Assert.All(camera.Requests, r => Assert.DoesNotContain("remove", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Many_recordings_come_in_pages_and_the_size_is_extrapolated_from_samples()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(2500, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");

        var result = await SearchAsync(rig, device.Id);

        Assert.Equal(2500, result.Recordings.Count);
        Assert.Equal(result.Recordings.OrderBy(r => r.Start).Select(r => r.Id), result.Recordings.Select(r => r.Id));
        Assert.Equal(3, camera.Requests.Count(r => r.StartsWith(EdgeStorageRequests.ListPath, StringComparison.Ordinal)));
        Assert.Equal(EdgeRecordingInfo.SizeSamples, camera.Requests.Count(r => r.StartsWith(EdgeStorageRequests.PropertiesPath, StringComparison.Ordinal)));
        var exact = 2500 * 10 * 200L * 1024;
        Assert.InRange(result.EstimatedBytes!.Value, exact * 0.99, exact * 1.01);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Without_an_estimate_the_size_is_unknown_and_without_mp4_the_export_is_matroska()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(2, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        camera.EstimateBytesPerSecond = -1;
        camera.OffersMp4 = false;
        var result = await SearchAsync(rig, rig.Add(camera, "10.0.0.48").Id);
        Assert.Equal((2, null, "matroska"), (result.Recordings.Count, result.EstimatedBytes, result.Format));
    }

    [Fact]
    public async Task Nothing_found_says_whether_an_SD_card_or_network_share_is_in_use()
    {
        var rig = new Rig();
        var withCard = new FakeCamera();
        var withoutCard = new FakeCamera { HasDisk = false };
        var a = await SearchAsync(rig, rig.Add(withCard, "10.0.0.1").Id);
        var b = await SearchAsync(rig, rig.Add(withoutCard, "10.0.0.2").Id);
        Assert.Equal((0, null, null), (a.Recordings.Count, a.DiskProblem, a.Problem));
        Assert.Equal((0, "No SD card or network share", null), (b.Recordings.Count, b.DiskProblem, b.Problem));
        Assert.DoesNotContain(withCard.Requests, r => r.StartsWith(EdgeStorageRequests.PropertiesPath, StringComparison.Ordinal));

        // A card problem is reported next to the recordings that were found.
        var failing = FakeCamera.WithClips(2, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        failing.SdCard = "status=\"failed\"";
        var c = await SearchAsync(rig, rig.Add(failing, "10.0.0.3").Id);
        Assert.Equal((2, "The SD card failed"), (c.Recordings.Count, c.DiskProblem));
    }

    [Fact]
    public async Task Cameras_that_cannot_be_asked_or_cannot_export_say_why_without_listing()
    {
        var rig = new Rig();
        var noStorage = new FakeCamera { LocalStorage = false };
        var noExport = new FakeCamera { ExportRecording = false };
        var credentials = new FakeCamera();
        var atSite = new FakeCamera();
        var cases = new (TestDevice Device, FakeCamera? Camera, string Problem)[]
        {
            (rig.Add(atSite, "10.0.0.6", DeviceStatus.SiteOffline), atSite, "Site offline"),
            (rig.Add(noStorage, "10.0.0.1"), noStorage, EdgeRecordingTexts.NoStorage),
            (rig.Add(noExport, "10.0.0.2"), noExport, EdgeRecordingTexts.NoExport),
            (rig.Add(credentials, "10.0.0.3", DeviceStatus.CredentialsRequired), credentials, DeviceMessages.CredentialsRequired),
            (rig.Add(null, "10.0.0.4", category: DeviceCategory.Speaker), null, EdgeRecordingTexts.NotVideo),
            (rig.Add(null, "10.0.0.5"), null, "Unreachable - "),
        };

        foreach (var (device, camera, problem) in cases)
        {
            var result = await SearchAsync(rig, device.Id);
            Assert.StartsWith(problem, result.Problem, StringComparison.Ordinal);
            Assert.Empty(result.Recordings);
            Assert.DoesNotContain(camera?.Requests ?? [], r => r.StartsWith(EdgeStorageRequests.ListPath, StringComparison.Ordinal));
        }

        Assert.Empty(credentials.Requests); // refused statuses are not contacted
        Assert.Empty(atSite.Requests);
        Assert.Equal(DeviceMessages.Removed, (await SearchAsync(rig, Guid.NewGuid())).Problem);
        await Assert.ThrowsAsync<ArgumentException>(() => SearchAsync(rig, cases[0].Device.Id, FakeCamera.T0, FakeCamera.T0));
    }

    [Fact]
    public async Task An_export_streams_the_recording_with_the_cameras_name_and_an_audit_line()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");
        var recording = (await SearchAsync(rig, device.Id)).Recordings[0];

        var download = await rig.Plugin.OpenDownloadAsync(EdgeRecordingMethods.Export,
            JsonSerializer.Serialize(new ExportRequest(device.Id, recording.Id, recording.DiskId, recording.Start, recording.Stop, "mp4"), EdgeRecordingInfo.Json), CancellationToken.None);
        await using (download)
        {
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            Assert.Equal(("20261012_200000_20261012_200010.mp4", 10_000L, 10_000L), (download.FileName, download.Length, copy.Length));
            Assert.Equal("Exported recording: 10.0.0.48, 2026-10-12 18:00:00 to 2026-10-12 18:00:10 UTC", download.AuditDetail);
        }

        Assert.Contains(camera.Requests, r => r.StartsWith(EdgeStorageRequests.ExportPath, StringComparison.Ordinal) && r.Contains("exportformat=mp4", StringComparison.Ordinal)
            && r.Contains("starttime=2026-10-12T18%3A00%3A00.000000Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_recording_deleted_meanwhile_and_a_refused_camera_are_refused_in_plain_words()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        camera.Missing.Add(camera.Recordings[0].Id);
        var device = rig.Add(camera, "10.0.0.48");
        var changed = rig.Add(new FakeCamera(), "10.0.0.49", DeviceStatus.CertificateChanged);

        string Request(Guid id) => JsonSerializer.Serialize(new ExportRequest(id, camera.Recordings[0].Id, "SD_DISK", null, null, "mp4"), EdgeRecordingInfo.Json);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Plugin.OpenDownloadAsync(EdgeRecordingMethods.Export, Request(device.Id), CancellationToken.None));
        Assert.Equal(EdgeRecordingTexts.NotExported, missing.Message);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Plugin.OpenDownloadAsync(EdgeRecordingMethods.Export, Request(changed.Id), CancellationToken.None));
        Assert.Equal(DeviceMessages.CertificateChanged, refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Plugin.OpenDownloadAsync("other", Request(device.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Plugin.OpenDownloadAsync(EdgeRecordingMethods.Export, "{", CancellationToken.None));
    }

    [Fact]
    public void Viewers_search_operators_export_and_every_export_is_audited()
    {
        var plugin = new EdgeRecordingExportPlugin();
        Assert.Equal(CorePluginGroup.Maintenance, plugin.Group);
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(EdgeRecordingMethods.Search));
        Assert.Equal(EdgeRecordingInfo.ExportKey, plugin.RequiredPermission(EdgeRecordingMethods.Export));
        var export = Assert.Single(plugin.Permissions, p => p.Key == EdgeRecordingInfo.ExportKey);
        Assert.Equal((true, false), (export.OperatorDefault, export.ViewerDefault));
        Assert.True(Assert.Single(plugin.Permissions, p => p.Key == PermissionKeys.Show).ViewerDefault);
        Assert.True(plugin.IsAudited(EdgeRecordingMethods.Export));
        Assert.False(plugin.IsAudited(EdgeRecordingMethods.Search));
    }
}
