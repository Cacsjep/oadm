using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Plugins;

/// <summary>
/// gRPC PluginService: core plugin pages and their backend calls. Invoke checks the permission the plugin requires for
/// the method (<see cref="CorePluginHost.AccessOf"/>, PERMISSION_DENIED) and writes audited calls to the audit log;
/// Download (<see cref="ICorePluginDownloads"/>) checks and audits like Invoke and streams the file uncompressed;
/// Watch and ListCorePlugins follow the page's "show" permission. ListPackages / SetPackageEnabled: the Plugins page
/// (<see cref="PluginActivation"/>).
/// </summary>
public sealed class PluginGrpcService(PluginRegistry registry, CorePluginHost host, AuditLog audit, PluginActivation activation, RoleStore roles) : Proto.PluginService.PluginServiceBase
{
    public override Task<Proto.PluginPackageList> ListPackages(Proto.Empty request, ServerCallContext context) =>
        Task.FromResult(PackageList());

    public override async Task<Proto.PluginPackageList> SetPackageEnabled(Proto.SetPackageEnabledRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw GrpcGuard.InvalidArgument("id is required.");
        }

        PluginPackageState changed;
        try
        {
            changed = await activation.SetEnabledAsync(request.Id, request.Enabled, context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }

        // The change is done: it is audited even when the caller has gone meanwhile.
        await audit.WriteAsync(
            request.Enabled ? AuditActions.PluginTurnedOn : AuditActions.PluginTurnedOff,
            changed.Origin.DisplayName,
            null,
            CancellationToken.None).ConfigureAwait(false);
        return PackageList();
    }

    private Proto.PluginPackageList PackageList()
    {
        var reply = new Proto.PluginPackageList();
        reply.Packages.AddRange(registry.Packages.Select(p => new Proto.PluginPackageInfo
        {
            Id = p.Id,
            DisplayName = p.Origin.DisplayName,
            Version = p.Origin.Version,
            Enabled = p.Enabled,
            EnabledByDefault = p.Origin.EnabledByDefault,
            HasPage = p.HasPage,
            MenuTaskCount = p.MenuTaskCount,
            AlwaysOn = p.Origin.AlwaysOn,
            Description = p.Origin.Description ?? string.Empty,
            Group = p.Group.ToString(),
        }));
        return reply;
    }

    public override Task<Proto.CorePluginList> ListCorePlugins(Proto.Empty request, ServerCallContext context)
    {
        // A page the caller's role may not show is left out (no rail entry); plugins without a page stay (toolbar parts).
        var caller = CallerContext.Current;
        var reply = new Proto.CorePluginList();
        reply.Plugins.AddRange(registry.CorePlugins
            .Where(p => caller is null || host.ShowPermissionOf(p.Id) is not { } show || caller.Has(show))
            .Select(Mappers.ToProto));
        return Task.FromResult(reply);
    }

    public override async Task Watch(Proto.WatchPluginRequest request, IServerStreamWriter<Proto.PluginEvent> responseStream, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw GrpcGuard.InvalidArgument("plugin_id is required.");
        }

        if (!registry.TryGetCorePlugin(request.PluginId, out var registered))
        {
            throw GrpcGuard.NotFound($"Unknown core plugin '{request.PluginId}'.");
        }

        // Live events of a page are what the page shows: they need its "show" permission.
        if (host.ShowPermissionOf(registered.Id) is { } show)
        {
            Auth.AuthInterceptor.Require(show, roles.Catalog);
        }

        try
        {
            await foreach (var item in host.Events.WatchAsync(registered.Id, context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new Proto.PluginEvent
                {
                    PluginId = registered.Id,
                    Topic = item.Topic,
                    PayloadJson = item.PayloadJson ?? string.Empty,
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client stopped watching.
        }
    }

    /// <summary>Bytes per message of <see cref="Download"/> (the client's limit is 32 MB, the gRPC default 4 MB).</summary>
    internal const int DownloadChunkBytes = 256 * 1024;

    public override async Task Download(Proto.InvokeRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Method))
        {
            throw GrpcGuard.InvalidArgument("plugin_id and method are required.");
        }

        var access = Authorize(request);
        PluginDownload download;
        try
        {
            download = await host.OpenDownloadAsync(
                request.PluginId,
                request.Method,
                string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            throw ToRpc(request.PluginId, ex);
        }

        await using (download.ConfigureAwait(false))
        {
            if (access is { Audited: true })
            {
                await audit.WriteAsync(AuditActions.PluginCall, access.PluginName, download.AuditDetail ?? request.Method, context.CancellationToken).ConfigureAwait(false);
            }

            // Video and archives do not compress; the chunks go out as they are read.
            context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
            await responseStream.WriteAsync(new Proto.DownloadChunk { FileName = download.FileName, Length = download.Length ?? -1 }).ConfigureAwait(false);
            var buffer = new byte[DownloadChunkBytes];
            try
            {
                while (true)
                {
                    var read = await ReadChunkAsync(download.Content, buffer, context.CancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await responseStream.WriteAsync(new Proto.DownloadChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !context.CancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "The download stopped: " + ex.Message));
            }
        }
    }

    /// <summary>Fills <paramref name="buffer"/> as far as the stream allows (fewer, larger messages); 0 at the end.</summary>
    private static async Task<int> ReadChunkAsync(Stream content, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await content.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Checks the permission of a page method (Invoke, Download); returns its access rule.</summary>
    private CorePluginMethodAccess? Authorize(Proto.InvokeRequest request)
    {
        var access = host.AccessOf(request.PluginId, request.Method);
        if (access is { AdminOnly: true })
        {
            Auth.AuthInterceptor.RequireAdmin();
        }
        else if (access?.Permission is { } permission)
        {
            Auth.AuthInterceptor.Require(permission, roles.Catalog);
        }

        return access;
    }

    private static RpcException ToRpc(string pluginId, Exception ex) => ex switch
    {
        KeyNotFoundException => GrpcGuard.NotFound(ex.Message),
        InvalidOperationException => GrpcGuard.FailedPrecondition(ex.Message),
        ArgumentException => GrpcGuard.InvalidArgument(ex.Message),
        _ => new RpcException(new Status(StatusCode.Internal, $"Plugin {pluginId} failed: {ex.Message}")),
    };

    public override async Task<Proto.InvokeReply> Invoke(Proto.InvokeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Method))
        {
            throw GrpcGuard.InvalidArgument("plugin_id and method are required.");
        }

        var access = Authorize(request);
        try
        {
            var result = await host.InvokeAsync(
                request.PluginId,
                request.Method,
                string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson,
                context.CancellationToken).ConfigureAwait(false);
            if (access is { Audited: true })
            {
                await audit.WriteAsync(AuditActions.PluginCall, access.PluginName, request.Method, context.CancellationToken).ConfigureAwait(false);
            }

            return new Proto.InvokeReply { PayloadJson = result ?? string.Empty };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            throw ToRpc(request.PluginId, ex);
        }
    }
}
