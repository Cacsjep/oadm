using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Plugins;

/// <summary>
/// gRPC PluginService: core plugin pages and their backend calls. Invoke checks the role the plugin requires for the
/// method (<see cref="ICorePlugin.RequiredRole"/>, PERMISSION_DENIED) and writes audited calls to the audit log.
/// ListPackages / SetPackageEnabled: the plugin list of the Settings page (<see cref="PluginActivation"/>).
/// </summary>
public sealed class PluginGrpcService(PluginRegistry registry, CorePluginHost host, AuditLog audit, PluginActivation activation) : Proto.PluginService.PluginServiceBase
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

        await audit.WriteAsync(
            request.Enabled ? AuditActions.PluginTurnedOn : AuditActions.PluginTurnedOff,
            changed.Origin.DisplayName,
            null,
            context.CancellationToken).ConfigureAwait(false);
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
        }));
        return reply;
    }

    public override Task<Proto.CorePluginList> ListCorePlugins(Proto.Empty request, ServerCallContext context)
    {
        var reply = new Proto.CorePluginList();
        reply.Plugins.AddRange(registry.CorePlugins.Select(Mappers.ToProto));
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

    public override async Task<Proto.InvokeReply> Invoke(Proto.InvokeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Method))
        {
            throw GrpcGuard.InvalidArgument("plugin_id and method are required.");
        }

        var access = host.AccessOf(request.PluginId, request.Method);
        if (access is not null && access.RequiredRole == UserRole.Admin && CallerContext.Current is { IsAdmin: false })
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, Auth.AuthInterceptor.AdminOnlyMessage));
        }

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
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Plugin {request.PluginId} failed: {ex.Message}"));
        }
    }
}
