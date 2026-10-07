using System.Security.Cryptography;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class TaskDialogAndLogTests
{
    [Fact]
    public async Task DialogContextQueriesTheServerForItsOwnPlugin()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        (string Plugin, string Device, string Method, string? Payload)? seen = null;
        api.QueryHandler = (plugin, device, method, payload) =>
        {
            seen = (plugin, device, method, payload);
            return """{"users":["root"]}""";
        };
        var ctx = new TaskDialogContext(api, "oadm.users");
        var deviceId = Guid.NewGuid();

        var result = await ctx.QueryAsync(deviceId, "listUsers", "{}", CancellationToken.None);

        Assert.Equal("""{"users":["root"]}""", result);
        Assert.Equal(("oadm.users", deviceId.ToString(), "listUsers", (string?)"{}"), seen);
    }

    [Fact]
    public async Task APluginWithoutQueriesSurfacesAsUnimplemented()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var ctx = new TaskDialogContext(api, "oadm.restart");

        var ex = await Assert.ThrowsAsync<RpcException>(() => ctx.QueryAsync(Guid.NewGuid(), "x", null, CancellationToken.None));

        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task DialogContextUploadsWithProgress()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var ctx = new TaskDialogContext(api, "oadm.firmware");
        string path = Path.Combine(Path.GetTempPath(), "oadm-upload-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] content = RandomNumberGenerator.GetBytes((GrpcOadmApi.UploadChunkSize * 2) + 100);
        await File.WriteAllBytesAsync(path, content);
        var reports = new List<double>();
        try
        {
            var file = await ctx.UploadAsync(path, new SyncProgress(reports.Add), CancellationToken.None);

            Assert.Equal(Path.GetFileName(path), file.Name);
            Assert.Equal(content.Length, file.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), file.Sha256);
            Assert.True(api.Uploads.ContainsKey(file.Id));
            Assert.Equal(0, reports[0]);
            Assert.Equal(1, reports[^1]);
            Assert.True(reports.Count >= 4, "progress per 256 KB chunk");
            Assert.Equal(reports.Order(), reports);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DoneWithWarningsIsAWarnChip()
    {
        var row = new TaskRowViewModel(new TaskInfo { Id = "t1", Name = "Users", State = TaskState.DoneWithWarnings, Progress = 100 }, new DeviceStore());

        Assert.Equal("Done with warnings", row.StateText);
        Assert.Equal(PillKind.Warning, row.StateKind);
        Assert.True(row.IsStateWarning);
        Assert.False(row.IsStateOk);
        Assert.False(row.IsActive);
    }

    [Fact]
    public async Task DetailsLoadTheTaskLogWithDeviceAddresses()
    {
        using var api = new FakeOadmApi();
        var devices = new DeviceStore();
        devices.Reset(await api.ListDevicesAsync(CancellationToken.None));
        TaskInfo warned = (await api.ListTasksAsync(CancellationToken.None)).Single(t => t.State == TaskState.DoneWithWarnings);
        var details = new TaskDetailsViewModel(new TaskRowViewModel(warned, devices), devices);

        await details.LoadLogAsync(api, CancellationToken.None);

        Assert.Equal(4, details.Log.Count);
        Assert.Equal("4 log entries", details.LogStatus);
        TaskLogRow first = details.Log[0];
        Assert.Equal("Info", first.LevelText);
        Assert.Equal("", first.Device); // task-level entry
        TaskLogRow warning = details.Log.Single(r => r.IsWarning);
        Assert.Equal("Warning", warning.LevelText);
        Assert.Equal(devices.Find(warned.Devices[1].DeviceId)!.DisplayAddress, warning.Device);
        Assert.Contains(details.Rows, r => r.IsStateWarning && r.StateText == "Done with warnings");
    }

    [Fact]
    public async Task DetailsShowWhenTheLogCannotBeLoaded()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var details = new TaskDetailsViewModel(new TaskRowViewModel(new TaskInfo { Id = "gone", Name = "x" }, new DeviceStore()), new DeviceStore());

        await details.LoadLogAsync(api, CancellationToken.None);

        Assert.Empty(details.Log);
        Assert.StartsWith("The log could not be loaded", details.LogStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceRowsCarryTheApiList()
    {
        var device = TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "M3106");
        device.Apis.Add(new DeviceApi { Id = "user-management", Version = "1.2", Name = "User Management", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "fwmgr", Version = "1.10" });

        var row = new DeviceRowViewModel(device);

        Assert.Equal([new Oadm.Sdk.Vapix.DeviceApi("user-management", "1.2", "User Management", "official"), new("fwmgr", "1.10")], row.Apis);
        Assert.True(Oadm.Sdk.Vapix.DeviceApiExtensions.Supports(row.Apis, "fwmgr", "1.4"));
    }

    [Fact]
    public void ClientPluginsAreAlsoLoadedFromTheRepositoryArtifacts()
    {
        string? development = ClientPluginLoader.DevelopmentRoot(AppContext.BaseDirectory);

        Assert.NotNull(development);
        Assert.True(File.Exists(Path.Combine(development, "..", "..", "Oadm.sln")));
        Assert.EndsWith(Path.Combine("artifacts", "plugins"), development, StringComparison.Ordinal);
        Assert.Contains(development, ClientPluginLoader.DefaultRoots(new Infrastructure.AppOptions()));
        Assert.Null(ClientPluginLoader.DevelopmentRoot(Path.GetPathRoot(Path.GetTempPath())!));
    }

    /// <summary>Progress&lt;T&gt; posts to the sync context; tests need the reports in order and immediately.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
