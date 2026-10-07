using Grpc.Core;

using Oadm.Core.Plugins;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Plugins;

/// <summary>gRPC PluginService: core plugin pages and their backend calls.</summary>
public sealed class PluginGrpcService(PluginRegistry registry, CorePluginHost host) : Proto.PluginService.PluginServiceBase
{
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

        try
        {
            var result = await host.InvokeAsync(
                request.PluginId,
                request.Method,
                string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson,
                context.CancellationToken).ConfigureAwait(false);
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
