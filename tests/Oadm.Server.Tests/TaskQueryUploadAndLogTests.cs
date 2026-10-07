using System.Security.Cryptography;
using System.Text.Json;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Uploads;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

public sealed class TaskQueryUploadAndLogTests
{
    [Fact]
    public async Task QueryRunsThePluginQueryWithTheDeviceVapixClient()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        Register(host, new SamplePlugin());
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        var reply = await host.Tasks.QueryAsync(new Proto.TaskQueryRequest
        {
            PluginId = SamplePlugin.PluginId,
            DeviceId = device.Id.ToString(),
            Method = "apis",
            PayloadJson = "{}",
        });

        Assert.Equal("""["user-management 1.2","network-settings 1.37"]""", reply.PayloadJson);
    }

    [Fact]
    public async Task QueryErrorsBecomeStatusCodesWithTheMessage()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        Register(host, new SamplePlugin());
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        var incompatible = await Assert.ThrowsAsync<RpcException>(() => Query(host, SamplePlugin.PluginId, device.Id, "needs-v2"));
        Assert.Equal(StatusCode.FailedPrecondition, incompatible.StatusCode);
        Assert.Equal("Device has user-management 1.2, needs 2.0 or later. Nothing was changed.", incompatible.Status.Detail);

        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => Query(host, "nope", device.Id, "apis"))).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => Query(host, SamplePlugin.PluginId, Guid.NewGuid(), "apis"))).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => host.Tasks.QueryAsync(new Proto.TaskQueryRequest { PluginId = SamplePlugin.PluginId, DeviceId = "x", Method = "apis" }).ResponseAsync)).StatusCode);
    }

    [Fact]
    public async Task UploadStoresTheFileAndTheTaskReadsItById()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        Register(host, new SamplePlugin());
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var files = new Proto.FileService.FileServiceClient(host.Channel);
        var content = RandomNumberGenerator.GetBytes(700_000);

        var uploaded = await UploadAsync(files, "AXIS_P3265-V_12_11_77.bin", content);

        Assert.Equal("AXIS_P3265-V_12_11_77.bin", uploaded.Name);
        Assert.Equal(content.Length, uploaded.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), uploaded.Sha256);
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "uploads", uploaded.Id + ".bin")));

        // A task gets the upload through ITaskExecutionContext.Files and reports a warning plus log lines.
        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest
        {
            PluginId = SamplePlugin.PluginId,
            DeviceIds = { device.Id.ToString() },
            PayloadJson = JsonSerializer.Serialize(new { fileId = uploaded.Id }),
        });
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskId);

        Assert.Equal(Proto.TaskState.DoneWithWarnings, task.State);
        Assert.Equal(Proto.TaskState.DoneWithWarnings, task.Devices[0].State);
        Assert.Equal("Same firmware already installed", task.Devices[0].Message);

        var log = await host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = run.TaskId });
        Assert.Equal(
            [$"read {content.Length} bytes, sha {uploaded.Sha256}", "Same firmware already installed"],
            log.Entries.Select(e => e.Message));
        Assert.Equal([Proto.TaskLogLevel.Info, Proto.TaskLogLevel.Warning], log.Entries.Select(e => e.Level));
        Assert.All(log.Entries, e => Assert.Equal(device.Id.ToString(), e.DeviceId));
        Assert.All(log.Entries, e => Assert.NotNull(e.Time));

        await files.DeleteAsync(new Proto.FileIdRequest { FileId = uploaded.Id });
        Assert.Null(await host.Get<IUploadedFiles>().FindAsync(uploaded.Id, CancellationToken.None));
        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => files.DeleteAsync(new Proto.FileIdRequest { FileId = uploaded.Id }).ResponseAsync)).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => files.DeleteAsync(new Proto.FileIdRequest { FileId = "../master.key" }).ResponseAsync)).StatusCode);
    }

    [Fact]
    public async Task UploadsOverTheLimitOrWithoutHeaderAreRejected()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Get<ServerSettingsStore>().SetAsync(SettingKeys.UploadsMaxMegabytes, 1, CancellationToken.None);
        var files = new Proto.FileService.FileServiceClient(host.Channel);

        var tooLarge = await Assert.ThrowsAsync<RpcException>(() => UploadAsync(files, "big.bin", new byte[(1024 * 1024) + 1]));
        Assert.Equal(StatusCode.ResourceExhausted, tooLarge.StatusCode);
        Assert.Contains("1 MB", tooLarge.Status.Detail, StringComparison.Ordinal);

        using (var call = files.Upload())
        {
            await call.RequestStream.WriteAsync(new Proto.UploadChunk { Data = ByteString.CopyFrom(1, 2, 3) });
            await call.RequestStream.CompleteAsync();
            Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync)).StatusCode);
        }

        using (var call = files.Upload())
        {
            await call.RequestStream.WriteAsync(new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = "short.bin", Size = 10 } });
            await call.RequestStream.WriteAsync(new Proto.UploadChunk { Data = ByteString.CopyFrom(1, 2, 3) });
            await call.RequestStream.CompleteAsync();
            var incomplete = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
            Assert.Equal(StatusCode.InvalidArgument, incomplete.StatusCode);
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(host.DataDirectory, "uploads")));
    }

    [Fact]
    public async Task GetLogOfAnUnknownTaskIsNotFound()
    {
        await using var host = await TestServerHost.StartAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = Guid.NewGuid().ToString() }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    private static Task<Proto.TaskQueryReply> Query(TestServerHost host, string pluginId, Guid deviceId, string method) =>
        host.Tasks.QueryAsync(new Proto.TaskQueryRequest { PluginId = pluginId, DeviceId = deviceId.ToString(), Method = method }).ResponseAsync;

    private static async Task<Proto.UploadedFileInfo> UploadAsync(Proto.FileService.FileServiceClient files, string name, byte[] content)
    {
        using var call = files.Upload();
        await call.RequestStream.WriteAsync(new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = name, Size = content.Length } });
        const int Chunk = 256 * 1024;
        for (var offset = 0; offset < content.Length; offset += Chunk)
        {
            await call.RequestStream.WriteAsync(new Proto.UploadChunk { Data = ByteString.CopyFrom(content, offset, Math.Min(Chunk, content.Length - offset)) });
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    private static void Register(TestServerHost host, ITaskPlugin plugin) =>
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null)));

    /// <summary>Query: lists the device APIs or requires an API version. Task: reads an upload, logs and warns.</summary>
    private sealed class SamplePlugin : ITaskPlugin, ITaskPluginQuery
    {
        public const string PluginId = "test.sample";

        public string Id => PluginId;

        public string DisplayName => "Sample";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(IDeviceInfo device) => true;

        public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            var fileId = JsonDocument.Parse(payloadJson!).RootElement.GetProperty("fileId").GetString()!;
            var file = await ctx.Files.FindAsync(fileId, ct) ?? throw new InvalidOperationException("upload missing");
            await using var stream = await ctx.Files.OpenReadAsync(fileId, ct);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            ctx.Log(TaskLogLevel.Info, $"read {file.Size} bytes, sha {sha}");
            ctx.ReportWarning("Same firmware already installed");
        }

        public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct);
            if (method == "needs-v2")
            {
                apis.Require("user-management", "2.0");
            }

            return JsonSerializer.Serialize(apis.Select(a => $"{a.Id} {a.Version}"));
        }
    }
}
