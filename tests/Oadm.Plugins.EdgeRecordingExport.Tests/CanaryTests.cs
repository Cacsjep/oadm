using System.Text.Json;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

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
        var scopes = new OneGroup(devices, [device.Id]);
        var plugin = new EdgeRecordingExportPlugin(new EdgeRecordingOptions { AccessCheckInterval = TimeSpan.Zero });
        await plugin.StartAsync(new FakeContext(devices, cameras, scopes), CancellationToken.None);
        var call = new GroupCall(devices, cameras, scopes);
        var recording = camera.Recordings[0];

        var download = await plugin.OpenDownloadAsync(call, EdgeRecordingMethods.Export,
            JsonSerializer.Serialize(new ExportRequest(device.Id, recording.Id, recording.DiskId, recording.Start, recording.Stop, "mp4"), EdgeRecordingInfo.Json), CancellationToken.None);
        await using (download)
        {
            var buffer = new byte[100];
            Assert.True(await download.Content.ReadAsync(buffer) > 0);

            scopes.Members.Clear(); // a tag change moves the camera out of the group
            await Assert.ThrowsAsync<DevicesNotAvailableException>(async () => await download.Content.ReadAtLeastAsync(buffer, 1));
        }
    }

    /// <summary>One device group whose members a test changes.</summary>
    private sealed class OneGroup(FakeDevices devices, IEnumerable<Guid> members) : IDeviceScopes, IDeviceRepository
    {
        public HashSet<Guid> Members { get; } = [.. members];

        public event EventHandler<string>? Changed
        {
            add { }
            remove { }
        }

        public bool Exists(string scopeKey) => true;

        public bool Contains(string scopeKey, IDeviceInfo device) => Members.Contains(device.Id);

        public IDeviceRepository Devices(string scopeKey) => this;

        public string? NameOf(string scopeKey) => "Building B";

        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => [.. (await devices.ListAsync(ct)).Where(d => Members.Contains(d.Id))];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Members.Contains(id) ? await devices.FindAsync(id, ct) : null;
    }

    /// <summary>A user of the group, as the host passes it: the group's devices at the time of the call.</summary>
    private sealed class GroupCall(FakeDevices devices, FakeCameras cameras, OneGroup group) : ICallContext
    {
        private readonly HashSet<Guid> _atCall = [.. group.Members];

        public string UserName => "anna";

        public string SessionId => "anna-session";

        public string DeviceScopeKey => "building-b";

        public bool HasAllDevices => false;

        public IDeviceRepository Devices => devices;

        public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => cameras;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();

        public IDeviceEventStreams? EventStreams => null;

        public bool Has(string permission) => true;

        public bool Contains(IDeviceInfo device) => _atCall.Contains(device.Id);
    }
}
