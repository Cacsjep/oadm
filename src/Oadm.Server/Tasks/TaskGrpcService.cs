using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Server.Auth;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tasks;

/// <summary>
/// gRPC TaskService over the <see cref="TaskEngine"/> and the <see cref="PluginRegistry"/>. Every method works on the
/// caller's <see cref="DeviceView"/> (docs/spec/device-scope.md, Tasks): a user with all devices gets the unfiltered
/// answers; a user with a device group sees, runs and changes only the tasks of devices in the view, and a task outside it
/// is answered like an unknown task.
/// </summary>
public sealed partial class TaskGrpcService(
    TaskEngine engine,
    TaskPluginRunnableCache runnable,
    TaskPluginQueries queries,
    PluginRegistry registry,
    DeviceViewFactory views,
    AuditLog audit,
    RoleStore roles,
    IHostApplicationLifetime lifetime,
    ILogger<TaskGrpcService> logger) : Proto.TaskService.TaskServiceBase
{
    /// <summary>The task's log, oldest first (live while the task runs).</summary>
    public override async Task<Proto.TaskLog> GetLog(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.TaskId, "task id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var log = await engine.GetLogAsync(view, id, context.CancellationToken).ConfigureAwait(false)
            ?? throw TaskNotFound(id);
        var reply = new Proto.TaskLog();
        reply.Entries.AddRange(log.Select(Mappers.ToProto));
        return reply;
    }

    /// <summary>Read-only plugin query for a task dialog, on a device of the caller's view. Errors carry the message for the user.</summary>
    public override async Task<Proto.TaskQueryReply> Query(Proto.TaskQueryRequest request, ServerCallContext context)
    {
        var deviceId = GrpcGuard.ParseId(request.DeviceId, "device id");
        var caller = AuthInterceptor.CallerOf(context); // fail closed: never "no caller = any device"

        // The dialog's reads: any permission of the task (a role that may run some of its choices opens the dialog). A plugin
        // the lookup does not find answers at once (never queried unchecked when it is turned on meanwhile).
        if (!registry.TryGetTaskPlugin(request.PluginId, out var plugin))
        {
            throw GrpcGuard.NotFound(UnknownTaskPlugin(request.PluginId));
        }

        if (!caller.Permissions.HasAny(PermissionCatalog.TaskKeys(plugin.Plugin)))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(PermissionCatalog.TaskKeys(plugin.Plugin)[0], roles.Catalog)));
        }

        var view = await views.ForAsync(context).ConfigureAwait(false);
        _ = await view.GetAsync([deviceId], context.CancellationToken).ConfigureAwait(false); // outside or unknown: NOT_FOUND
        try
        {
            var result = await queries.QueryAsync(
                new PluginCaller(caller, view),
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
    /// The runnable sets come from the <see cref="TaskPluginRunnableCache"/>, intersected with the caller's view before they
    /// are encoded; with <c>compact</c> each plugin carries the shorter of the runnable or the not-runnable ids. A task the
    /// caller's role cannot run (none of its permissions) is left out: the client shows no entry for it.
    /// </summary>
    public override async Task<Proto.TaskPluginList> ListTaskPlugins(Proto.ListTaskPluginsRequest request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context); // fail closed: never "no caller = every plugin"
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var reply = new Proto.TaskPluginList();
        foreach (var entry in await runnable.GetAsync(view, context.CancellationToken).ConfigureAwait(false))
        {
            if (!caller.Permissions.HasAny(PermissionCatalog.TaskKeys(entry.Plugin.Plugin)))
            {
                continue;
            }

            if (!caller.HasAllDevices && !entry.Plugin.Origin.SupportsCallContext)
            {
                continue; // a package built before SDK 0.4.0: only for users with all devices (Run refuses it too)
            }

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

    private static string UnknownTaskPlugin(string pluginId) => TaskRunPermissions.UnknownPlugin(pluginId);

    /// <summary>One task per device of the caller's view; any device outside it (or unknown) refuses the whole run.</summary>
    public override async Task<Proto.RunTaskReply> Run(Proto.RunTaskRequest request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        var payload = string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson;
        var caller = AuthInterceptor.CallerOf(context);

        // Every permission this run needs (from its payload), before any task is created. A plugin the lookup does not find
        // answers at once: one turned on meanwhile never runs without this check.
        if (!registry.TryGetTaskPlugin(request.PluginId, out var task))
        {
            throw GrpcGuard.NotFound(UnknownTaskPlugin(request.PluginId));
        }

        // The same check as call.Tasks of a plugin's call: PERMISSION_DENIED with what the role lacks (the interceptor's answer
        // to DeviceAccessRefusedException).
        TaskRunPermissions.Require(task.Plugin, payload, caller.Permissions, () => roles.Catalog);

        var view = await views.ForAsync(context).ConfigureAwait(false);
        var devices = await view.GetAsync(ids, context.CancellationToken).ConfigureAwait(false);
        try
        {
            var taskIds = await engine.RunAsync(request.PluginId, devices, payload, TaskCaller.Of(caller), context.CancellationToken).ConfigureAwait(false);
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

    /// <summary>Newest first; a page with <c>limit</c> (store paging), every task without. Only the tasks of the caller's view.</summary>
    public override async Task<Proto.TaskList> List(Proto.ListTasksRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var reply = new Proto.TaskList();
        if (request.Limit > 0)
        {
            var page = await engine.ListPageAsync(view, Math.Max(0, request.Offset), request.Limit, ct).ConfigureAwait(false);
            reply.Tasks.AddRange(page.Tasks.Select(Mappers.ToProto));
            reply.TotalCount = page.TotalCount;
            return reply;
        }

        var all = await engine.ListAsync(view, ct).ConfigureAwait(false);
        reply.Tasks.AddRange(all.Skip(Math.Max(0, request.Offset)).Select(Mappers.ToProto));
        reply.TotalCount = all.Count;
        return reply;
    }

    /// <summary>
    /// Snapshot (one ADDED per task: every active task plus the newest <c>snapshot_limit</c>, 0 = all, of the caller's view;
    /// the limit counts after the filter), SNAPSHOT_END when asked for, then the live changes of the view.
    /// </summary>
    public override async Task Watch(Proto.WatchTasksRequest request, IServerStreamWriter<Proto.TaskChanged> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        try
        {
            var view = await views.ForAsync(context).ConfigureAwait(false);
            var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(
                view, request.SnapshotLimit > 0 ? request.SnapshotLimit : null, ct).ConfigureAwait(false);
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

                await foreach (var change in subscription.ReadAllAsync(ct).ConfigureAwait(false))
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
        var view = await views.ForAsync(context).ConfigureAwait(false);
        if (!await engine.CancelAsync(view, id, context.CancellationToken).ConfigureAwait(false))
        {
            throw TaskNotFound(id);
        }

        return new Proto.Empty();
    }

    public override async Task<Proto.Empty> Delete(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.TaskId, "task id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        if (!await engine.DeleteAsync(view, id, context.CancellationToken).ConfigureAwait(false))
        {
            throw TaskNotFound(id);
        }

        return new Proto.Empty();
    }

    /// <summary>
    /// Clears the task history: cancels every active task, waits for them, then one bulk delete. Users with all devices only
    /// (<c>AccessRule.AllDevices</c>, checked here again): it deletes the history of every group and of removed devices.
    /// </summary>
    public override async Task<Proto.DeleteAllReply> DeleteAll(Proto.Empty request, ServerCallContext context)
    {
        AuthInterceptor.RequireAllDevices(AuthInterceptor.CallerOf(context));
        var deleted = await engine.DeleteAllAsync(context.CancellationToken).ConfigureAwait(false);
        LogDeletedAll(deleted);
        await audit.WriteAsync(AuditActions.TasksDeletedAll, "Task history", deleted == 1 ? "1 task" : $"{deleted} tasks", context.CancellationToken).ConfigureAwait(false);
        return new Proto.DeleteAllReply { Deleted = deleted };
    }

    /// <summary>The answer for an unknown task and for a task outside the caller's view (the same).</summary>
    internal static RpcException TaskNotFound(Guid id) => GrpcGuard.NotFound($"Task {id} not found.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId} could not be described")]
    private partial void LogPluginInfoFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted all tasks ({Count})")]
    private partial void LogDeletedAll(int count);
}
