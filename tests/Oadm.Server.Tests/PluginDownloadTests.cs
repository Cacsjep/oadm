using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// PluginService.Download (<see cref="ICorePluginDownloads"/>): a file of a core plugin passed on in chunks, with the
/// permission check and the audit of Invoke, the plugin's errors as status codes, and the download disposed when the
/// client is done or goes away.
/// </summary>
public sealed class PluginDownloadTests
{
    private const string PluginId = "oadm.test-downloads";

    [Fact]
    public async Task AFileArrivesWholeInChunksWithItsNameAndTheAuditEntry()
    {
        var plugin = new DownloadPlugin();
        await using var host = await StartAsync(plugin);
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));

        var (header, data) = await ReadAllAsync(op, "file", "{\"size\":1000000}");

        Assert.Equal(("recording.mp4", 1_000_000L), (header.FileName, header.Length));
        Assert.Equal(DownloadPlugin.Bytes(1_000_000), data);
        Assert.True(plugin.Disposed.Task.IsCompleted);
        var entry = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.PluginCall);
        Assert.Equal(("operator1", "Test downloads", "Exported 1000000 bytes"), (entry.UserName, entry.Target, entry.Detail));
    }

    [Fact]
    public async Task AnUnknownLengthIsMinusOneAndAnEmptyFileIsOnlyTheHeader()
    {
        await using var host = await StartAsync(new DownloadPlugin());
        var (header, data) = await ReadAllAsync(host.Plugins, "file", "{\"size\":0,\"unknownLength\":true}");
        Assert.Equal(-1, header.Length);
        Assert.Empty(data);
    }

    [Fact]
    public async Task ARoleWithoutThePermissionIsRefusedBeforeThePluginOpensAnything()
    {
        var plugin = new DownloadPlugin();
        await using var host = await StartAsync(plugin);
        var viewer = new Proto.PluginService.PluginServiceClient(await host.InvokerForRoleAsync("viewer1", BuiltInRoles.ViewerId));

        var ex = await Assert.ThrowsAsync<RpcException>(() => ReadAllAsync(viewer, "file", "{\"size\":10}"));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal(0, plugin.Opened);
    }

    [Fact]
    public async Task ThePluginsErrorsBecomeStatusCodes()
    {
        await using var host = await StartAsync(new DownloadPlugin());
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => ReadAllAsync(host.Plugins, "bad", null))).StatusCode);
        var refused = await Assert.ThrowsAsync<RpcException>(() => ReadAllAsync(host.Plugins, "refuse", null));
        Assert.Equal((StatusCode.FailedPrecondition, "The camera did not send the recording."), (refused.StatusCode, refused.Status.Detail));
        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => ReadAllAsync(host.Plugins, "file", null, "oadm.unknown"))).StatusCode);
    }

    [Fact]
    public async Task APluginWithoutDownloadsAnswersUnimplemented()
    {
        await using var host = await StartAsync(new DownloadPlugin(), withoutDownloads: true);
        var ex = await Assert.ThrowsAsync<RpcException>(() => ReadAllAsync(host.Plugins, "file", null, "oadm.no-downloads"));
        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task AClientThatStopsReadingEndsTheDownloadOnTheServer()
    {
        var plugin = new DownloadPlugin();
        await using var host = await StartAsync(plugin);
        using (var cts = new CancellationTokenSource())
        {
            using var call = host.Plugins.Download(new Proto.InvokeRequest { PluginId = PluginId, Method = "endless" }, cancellationToken: cts.Token);
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.True(await call.ResponseStream.MoveNext());
            await cts.CancelAsync();
        }

        await plugin.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<TestServerHost> StartAsync(DownloadPlugin plugin, bool withoutDownloads = false)
    {
        var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(PluginId, "0.1.0", null)));
        if (withoutDownloads)
        {
            Assert.True(registry.RegisterCorePlugin(new PlainPlugin(), new PluginOrigin("oadm.no-downloads", "0.1.0", null)));
        }

        await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return host;
    }

    private static async Task<(Proto.DownloadChunk Header, byte[] Data)> ReadAllAsync(Proto.PluginService.PluginServiceClient client, string method, string? payload, string pluginId = PluginId)
    {
        using var call = client.Download(new Proto.InvokeRequest { PluginId = pluginId, Method = method, PayloadJson = payload ?? string.Empty });
        Assert.True(await call.ResponseStream.MoveNext());
        var header = call.ResponseStream.Current;
        using var data = new MemoryStream();
        while (await call.ResponseStream.MoveNext())
        {
            Assert.True(call.ResponseStream.Current.Data.Length <= 256 * 1024);
            call.ResponseStream.Current.Data.WriteTo(data);
        }

        return (header, data.ToArray());
    }

    /// <summary>"file" sends {size} bytes, "endless" never ends, "refuse" and "bad" throw like a camera problem and a bad request.</summary>
    private sealed class DownloadPlugin : ICorePlugin, ICorePluginDownloads
    {
        public int Opened;

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => PluginId;

        public string DisplayName => "Test downloads";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("export", "Export", "Download files.")];

        public string? RequiredPermission(string method) => method == "file" ? "export" : PermissionKeys.Show;

        public bool IsAudited(string method) => method == "file";

        public static byte[] Bytes(int size) => [.. Enumerable.Range(0, size).Select(i => (byte)(i * 7))];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct)
        {
            Interlocked.Increment(ref Opened);
            var owner = new DisposeSignal(Disposed);
            switch (method)
            {
                case "file":
                    var request = System.Text.Json.JsonDocument.Parse(payloadJson ?? "{}").RootElement;
                    var size = request.GetProperty("size").GetInt32();
                    var unknown = request.TryGetProperty("unknownLength", out var u) && u.GetBoolean();
                    return Task.FromResult(new PluginDownload(new MemoryStream(Bytes(size)), "recording.mp4", unknown ? null : size, owner)
                    {
                        AuditDetail = $"Exported {size} bytes",
                    });
                case "endless":
                    return Task.FromResult(new PluginDownload(new EndlessStream(), "endless.bin", null, owner));
                case "refuse":
                    throw new InvalidOperationException("The camera did not send the recording.");
                default:
                    throw new ArgumentException($"Unknown download '{method}'.");
            }
        }
    }

    private sealed class PlainPlugin : ICorePlugin
    {
        public string Id => "oadm.no-downloads";

        public string DisplayName => "No downloads";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class DisposeSignal(TaskCompletionSource done) : IDisposable
    {
        public void Dispose() => done.TrySetResult();
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            buffer.Span.Fill(1);
            return buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
