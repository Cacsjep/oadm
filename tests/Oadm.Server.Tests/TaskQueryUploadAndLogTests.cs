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
        var files = new Proto.FileService.FileServiceClient(host.Invoker);
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
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);

        Assert.Equal(Proto.TaskState.DoneWithWarnings, task.State);
        Assert.Equal(Proto.TaskState.DoneWithWarnings, task.Devices[0].State);
        Assert.Equal("Same firmware already installed", task.Devices[0].Message);

        var log = await host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = run.TaskIds[0] });
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
        var files = new Proto.FileService.FileServiceClient(host.Invoker);

        var tooLarge = await Assert.ThrowsAsync<RpcException>(() => UploadAsync(files, "big.bin", new byte[(1024 * 1024) + 1]));
        Assert.Equal(StatusCode.ResourceExhausted, tooLarge.StatusCode);
        Assert.Contains("1 MB", tooLarge.Status.Detail, StringComparison.Ordinal);

        Assert.Equal(StatusCode.InvalidArgument, (await RejectedAsync(files, new Proto.UploadChunk { Data = ByteString.CopyFrom(1, 2, 3) })).StatusCode);

        var incomplete = await RejectedAsync(
            files,
            new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = "short.bin", Size = 10 } },
            new Proto.UploadChunk { Data = ByteString.CopyFrom(1, 2, 3) });
        Assert.Equal(StatusCode.InvalidArgument, incomplete.StatusCode);

        Assert.Empty(Directory.GetFiles(Path.Combine(host.DataDirectory, "uploads")));
    }

    /// <summary>
    /// Sends the chunks and returns the server's rejection. The server may reject while the client still writes (a busy
    /// machine): then the write fails instead of the reply, which is the same rejection.
    /// </summary>
    private static async Task<RpcException> RejectedAsync(Proto.FileService.FileServiceClient files, params Proto.UploadChunk[] chunks)
    {
        using var call = files.Upload();
        try
        {
            foreach (var chunk in chunks)
            {
                await call.RequestStream.WriteAsync(chunk);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException)
        {
            // rejected early; the reply carries the status
        }
        catch (InvalidOperationException)
        {
            // the call already ended; the reply carries the status
        }

        return await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
    }

    [Fact]
    public async Task GetLogOfAnUnknownTaskIsNotFound()
    {
        await using var host = await TestServerHost.StartAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = Guid.NewGuid().ToString() }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task RunCreatesOneTaskPerDeviceWithBatchAndDeviceIds()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        network.Add("10.9.0.2", FakeSerials.Make(2), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        var plugin = new CredentialsPlugin();
        Register(host, plugin);
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest
        {
            PluginId = CredentialsPlugin.PluginId,
            DeviceIds = { a.Id.ToString(), b.Id.ToString() },
            PayloadJson = "\"noop\"",
        });

        Assert.Equal(2, run.TaskIds.Count);
#pragma warning disable CS0612 // the deprecated single id is still the first task
        Assert.Equal(run.TaskIds[0], run.TaskId);
#pragma warning restore CS0612
        var tasks = new List<Proto.TaskInfo>();
        foreach (var id in run.TaskIds)
        {
            tasks.Add(await TestHelpers.WaitForTaskAsync(host, id));
        }

        Assert.Equal([a.Id.ToString(), b.Id.ToString()], tasks.Select(t => t.DeviceId));
        Assert.All(tasks, t => Assert.Equal(t.DeviceId, Assert.Single(t.Devices).DeviceId));
        Assert.Single(tasks.Select(t => t.BatchId).Distinct());
        Assert.True(Guid.TryParse(tasks[0].BatchId, out _));
        Assert.All(tasks, t => Assert.Equal(Proto.TaskState.Done, t.State));
        Assert.Equal(["root", "root"], plugin.SeenUserNames.Order()); // IDeviceInfo.CredentialUserName on the server
    }

    [Fact]
    public async Task TasksCanUpdateOrInvalidateTheStoredCredentials()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        network.Add("10.9.0.2", FakeSerials.Make(2), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        Register(host, new CredentialsPlugin());
        var changed = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var reset = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);

        // The plugin changes the password of the OADM account on the device, then tells the server.
        camera.Password = "N3w-pw";
        var update = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = CredentialsPlugin.PluginId, DeviceIds = { changed.Id.ToString() }, PayloadJson = "\"update\"" });
        var updated = await TestHelpers.WaitForTaskAsync(host, update.TaskIds[0]);
        Assert.Equal(Proto.TaskState.Done, updated.State);
        var stored = await host.Get<Core.Security.CredentialStore>().GetAsync(changed.Id, CancellationToken.None);
        Assert.Equal(("root", "N3w-pw"), (stored!.UserName, stored.Password));

        var invalidate = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = CredentialsPlugin.PluginId, DeviceIds = { reset.Id.ToString() }, PayloadJson = "\"invalidate\"" });
        Assert.Equal(Proto.TaskState.Done, (await TestHelpers.WaitForTaskAsync(host, invalidate.TaskIds[0])).State);
        await TestHelpers.WaitUntilAsync(
            async () => !(await TestHelpers.GetDeviceAsync(host, reset.Id.ToString())).HasCredentials,
            "credentials removed");
        Assert.Null(await host.Get<Core.Security.CredentialStore>().GetAsync(reset.Id, CancellationToken.None));

        var log = await host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = update.TaskIds[0] });
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("N3w-pw", StringComparison.Ordinal));
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

    /// <summary>Payload "update": stores root/N3w-pw and checks the new client works; "invalidate": clears the credentials.</summary>
    private sealed class CredentialsPlugin : ITaskPlugin
    {
        public const string PluginId = "test.credentials";

        public System.Collections.Concurrent.ConcurrentBag<string?> SeenUserNames { get; } = [];

        public string Id => PluginId;

        public string DisplayName => "Credentials";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(IDeviceInfo device) => true;

        public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            SeenUserNames.Add(device.CredentialUserName);
            switch (JsonSerializer.Deserialize<string>(payloadJson!))
            {
                case "update":
                    await ctx.UpdateCredentialsAsync("root", "N3w-pw", ct);
                    await ctx.Vapix.GetBasicDeviceInfoAsync(ct); // the swapped client uses the new password
                    break;
                case "invalidate":
                    ctx.MarkCredentialsInvalid();
                    break;
            }
        }
    }

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
