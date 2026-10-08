using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tasks;

/// <summary>gRPC TaskService over the <see cref="TaskEngine"/> and the <see cref="PluginRegistry"/>.</summary>
public sealed partial class TaskGrpcService(
    TaskEngine engine,
    TaskPluginRunnableCache runnable,
    TaskPluginQueries queries,
    PluginRegistry registry,
    AuditLog audit,
    IHostApplicationLifetime lifetime,
    ILogger<TaskGrpcService> logger) : Proto.TaskService.TaskServiceBase
{
    /// <summary>The task's log, oldest first (live while the task runs).</summary>
    public override async Task<Proto.TaskLog> GetLog(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.TaskId, "task id");
        var log = await engine.GetLogAsync(id, context.CancellationToken).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound($"Task {id} not found.");
        var reply = new Proto.TaskLog();
        reply.Entries.AddRange(log.Select(Mappers.ToProto));
        return reply;
    }

    /// <summary>Read-only plugin query for a task dialog. Errors carry the message for the user.</summary>
    public override async Task<Proto.TaskQueryReply> Query(Proto.TaskQueryRequest request, ServerCallContext context)
    {
        var deviceId = GrpcGuard.ParseId(request.DeviceId, "device id");
        try
        {
            var result = await queries.QueryAsync(
                request.PluginId,
                deviceId,
                request.Method,
                string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson,
                context.CancellationToken).ConfigureAwait(false);
            return new Proto.TaskQueryReply { PayloadJson = result ?? string.Empty };
        }
        catch (TaskQueryException ex)
        {
            throw new RpcException(new Status(
                ex.Error switch
                {
                    TaskQueryError.NotFound => StatusCode.NotFound,
                    TaskQueryError.NotSupported => StatusCode.Unimplemented,
                    TaskQueryError.InvalidArgument => StatusCode.InvalidArgument,
                    TaskQueryError.FailedPrecondition => StatusCode.FailedPrecondition,
                    TaskQueryError.Unavailable => StatusCode.Unavailable,
                    TaskQueryError.Timeout => StatusCode.DeadlineExceeded,
                    _ => StatusCode.Internal,
                },
                ex.Message));
        }
    }

    /// <summary>
    /// Context-menu entries incl. core-plugin contributions, with the devices each one can run on.
    /// The runnable sets come from the <see cref="TaskPluginRunnableCache"/>; with <c>compact</c> each
    /// plugin carries the shorter of the runnable or the not-runnable ids.
    /// </summary>
    public override async Task<Proto.TaskPluginList> ListTaskPlugins(Proto.ListTaskPluginsRequest request, ServerCallContext context)
    {
        var reply = new Proto.TaskPluginList();
        foreach (var entry in await runnable.GetAsync(context.CancellationToken).ConfigureAwait(false))
        {
            try
            {
                reply.Plugins.Add(Mappers.ToProto(entry, request.Compact));
            }
#pragma warning disable CA1031 // A plugin with a throwing property must not break the menu.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPluginInfoFailed(ex, entry.Plugin.Id);
            }
        }

        return reply;
    }

    public override async Task<Proto.RunTaskReply> Run(Proto.RunTaskRequest request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        try
        {
            var taskIds = await engine.RunAsync(
                request.PluginId,
                ids,
                string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson,
                GrpcGuard.Owner(context, request.Owner),
                context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(
                AuditActions.TaskRun,
                registry.TryGetTaskPlugin(request.PluginId, out var plugin) ? plugin.DisplayName : request.PluginId,
                taskIds.Count == 1 ? "1 device" : $"{taskIds.Count} devices",
                context.CancellationToken).ConfigureAwait(false);
            var reply = new Proto.RunTaskReply();
            reply.TaskIds.AddRange(taskIds.Select(t => t.ToString()));
#pragma warning disable CS0612 // task_id is deprecated but still filled for older clients.
            reply.TaskId = reply.TaskIds.Count > 0 ? reply.TaskIds[0] : string.Empty;
#pragma warning restore CS0612
            return reply;
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    /// <summary>Newest first; a page with <c>limit</c> (store paging), every task without.</summary>
    public override async Task<Proto.TaskList> List(Proto.ListTasksRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var reply = new Proto.TaskList();
        if (request.Limit > 0)
        {
            var page = await engine.ListPageAsync(Math.Max(0, request.Offset), request.Limit, ct).ConfigureAwait(false);
            reply.Tasks.AddRange(page.Tasks.Select(Mappers.ToProto));
            reply.TotalCount = page.TotalCount;
            return reply;
        }

        var all = await engine.ListAsync(ct).ConfigureAwait(false);
        reply.Tasks.AddRange(all.Skip(Math.Max(0, request.Offset)).Select(Mappers.ToProto));
        reply.TotalCount = all.Count;
        return reply;
    }

    /// <summary>
    /// Snapshot (one ADDED per task: every active task plus the newest <c>snapshot_limit</c>, 0 = all),
    /// SNAPSHOT_END when asked for, then live changes.
    /// </summary>
    public override async Task Watch(Proto.WatchTasksRequest request, IServerStreamWriter<Proto.TaskChanged> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        try
        {
            var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(
                request.SnapshotLimit > 0 ? request.SnapshotLimit : null, ct).ConfigureAwait(false);
            using (subscription)
            {
                foreach (var record in snapshot)
                {
                    await responseStream.WriteAsync(Mappers.ToProto(new TaskChange(TaskChangeKind.Added, record)), ct).ConfigureAwait(false);
                }

                if (request.SnapshotEndMarker)
                {
                    await responseStream.WriteAsync(new Proto.TaskChanged { Kind = Proto.TaskChanged.Types.Kind.SnapshotEnd }, ct).ConfigureAwait(false);
                }

                await foreach (var change in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(Mappers.ToProto(change), ct).ConfigureAwait(false);
                }
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

    /// <summary>Clears the task history: cancels every active task, waits for them, then one bulk delete.</summary>
    public override async Task<Proto.DeleteAllReply> DeleteAll(Proto.Empty request, ServerCallContext context)
    {
        var deleted = await engine.DeleteAllAsync(context.CancellationToken).ConfigureAwait(false);
        LogDeletedAll(deleted);
        await audit.WriteAsync(AuditActions.TasksDeletedAll, "Task history", deleted == 1 ? "1 task" : $"{deleted} tasks", context.CancellationToken).ConfigureAwait(false);
        return new Proto.DeleteAllReply { Deleted = deleted };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId} could not be described")]
    private partial void LogPluginInfoFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted all tasks ({Count})")]
    private partial void LogDeletedAll(int count);
}
