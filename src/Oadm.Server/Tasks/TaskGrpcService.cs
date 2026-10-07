using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tasks;

/// <summary>gRPC TaskService over the <see cref="TaskEngine"/> and the <see cref="PluginRegistry"/>.</summary>
public sealed partial class TaskGrpcService(
    TaskEngine engine,
    PluginRegistry registry,
    DeviceRepository devices,
    IHostApplicationLifetime lifetime,
    ILogger<TaskGrpcService> logger) : Proto.TaskService.TaskServiceBase
{
    /// <summary>Built-in tasks that are not offered in the context menu.</summary>
    public static readonly IReadOnlySet<string> HiddenPluginIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AddDevicesTaskPlugin.PluginId };

    /// <summary>Context-menu entries incl. core-plugin contributions, with the devices each one can run on.</summary>
    public override async Task<Proto.TaskPluginList> ListTaskPlugins(Proto.Empty request, ServerCallContext context)
    {
        var all = await devices.ListDevicesAsync(context.CancellationToken).ConfigureAwait(false);
        var reply = new Proto.TaskPluginList();
        foreach (var plugin in registry.TaskPlugins.Where(p => !HiddenPluginIds.Contains(p.Id)))
        {
            try
            {
                reply.Plugins.Add(Mappers.ToProto(plugin, all.Where(d => SafeCanRun(plugin, d)).Select(d => d.Id)));
            }
#pragma warning disable CA1031 // A plugin with a throwing property must not break the menu.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPluginInfoFailed(ex, plugin.Id);
            }
        }

        return reply;
    }

    public override async Task<Proto.RunTaskReply> Run(Proto.RunTaskRequest request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        try
        {
            var id = await engine.RunAsync(
                request.PluginId,
                ids,
                string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson,
                GrpcGuard.Owner(context, request.Owner),
                context.CancellationToken).ConfigureAwait(false);
            return new Proto.RunTaskReply { TaskId = id.ToString() };
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    public override async Task<Proto.TaskList> List(Proto.Empty request, ServerCallContext context)
    {
        var reply = new Proto.TaskList();
        reply.Tasks.AddRange((await engine.ListAsync(context.CancellationToken).ConfigureAwait(false)).Select(Mappers.ToProto));
        return reply;
    }

    /// <summary>Snapshot (one ADDED per task), then live changes.</summary>
    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.TaskChanged> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        try
        {
            await foreach (var change in engine.WatchAsync(includeSnapshot: true, ct).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(Mappers.ToProto(change), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    public override async Task<Proto.Empty> Cancel(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.TaskId, "task id");
        if (!engine.Cancel(id) && await engine.GetAsync(id, context.CancellationToken).ConfigureAwait(false) is null)
        {
            throw GrpcGuard.NotFound($"Task {id} not found.");
        }

        return new Proto.Empty();
    }

    public override async Task<Proto.Empty> Delete(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.TaskId, "task id");
        if (!await engine.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false))
        {
            throw GrpcGuard.NotFound($"Task {id} not found.");
        }

        return new Proto.Empty();
    }

    /// <summary>Clears the task history: requests cancellation of every active task first, then deletes all tasks.</summary>
    public override async Task<Proto.DeleteAllReply> DeleteAll(Proto.Empty request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var tasks = await engine.ListAsync(ct).ConfigureAwait(false);

        // Cancel all at once so they stop in parallel instead of one after the other.
        foreach (var task in tasks.Where(t => !t.State.IsTerminal()))
        {
            engine.Cancel(task.Id);
        }

        var deleted = 0;
        foreach (var task in tasks)
        {
            // DeleteAsync waits for a cancelled task to stop and publishes Removed.
            if (await engine.DeleteAsync(task.Id, ct).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        LogDeletedAll(deleted);
        return new Proto.DeleteAllReply { Deleted = deleted };
    }

    private bool SafeCanRun(RegisteredTaskPlugin plugin, Device device)
    {
        try
        {
            return plugin.Plugin.CanRun(device);
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCanRunFailed(ex, plugin.Id, device.Id);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId} could not be described")]
    private partial void LogPluginInfoFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted all tasks ({Count})")]
    private partial void LogDeletedAll(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: CanRun threw for device {DeviceId}")]
    private partial void LogCanRunFailed(Exception ex, string pluginId, Guid deviceId);
}
