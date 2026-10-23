using System.Text.Json;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Sdk.Devices;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): search and export reach only cameras of the user's view.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task No_method_reaches_a_camera_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        var plugin = new EdgeRecordingExportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(EdgeRecordingMethods.Search, """{"deviceId":"{device}"}""")
            .Download(EdgeRecordingMethods.Export, """{"deviceId":"{device}","recordingId":"20261023_101500_1A2B_B8A44F631339","diskId":"SD_DISK"}""")
            .RunAsync();
    }

    [Fact]
    public async Task An_export_ends_when_the_camera_leaves_the_users_group()
    {
        var devices = new FakeDevices();
        var cameras = new FakeCameras();
        var camera = FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = new TestDevice("10.0.0.48", "AXIS P3265-V", DeviceStatus.Ok, DeviceCategory.Camera, []);
        devices.Items.Add(device);
        cameras.Cameras[device.Id] = camera;
        var scopes = new Testing.FakeDeviceScopes(() => devices.Items);
        var key = scopes.Add("Building B", [device.Id]);
        var plugin = new EdgeRecordingExportPlugin(new EdgeRecordingOptions { AccessCheckInterval = TimeSpan.Zero });
        await plugin.StartAsync(new FakeContext(devices, cameras, scopes), CancellationToken.None);
        var call = Testing.FakeCallContext.OverGroup([device.Id], devices, cameras, userName: "anna", groupId: key);
        var recording = camera.Recordings[0];

        var download = await plugin.OpenDownloadAsync(call, EdgeRecordingMethods.Export,
            JsonSerializer.Serialize(new ExportRequest(device.Id, recording.Id, recording.DiskId, recording.Start, recording.Stop, "mp4"), EdgeRecordingInfo.Json), CancellationToken.None);
        await using (download)
        {
            var buffer = new byte[100];
            Assert.True(await download.Content.ReadAsync(buffer) > 0);

            scopes.Add("Building B", [], key); // a tag change moves the camera out of the group
            await Assert.ThrowsAsync<DevicesNotAvailableException>(async () => await download.Content.ReadAtLeastAsync(buffer, 1));
        }
    }
}
