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
    ILogger<TaskGrpcService> logger,
    Devices.DeviceFactsSource facts,
    TaskResultFiles? results = null) : Proto.TaskService.TaskServiceBase
{
    /// <summary>
    /// The result file of a task for the caller: the task in the caller's view, started by the caller (or the caller is an
    /// administrator), with a file. Every other case answers NOT_FOUND alike.
    /// </summary>
    private async Task<TaskResultFileInfo> ResultFileOfAsync(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.TaskId, "task id");
        var caller = AuthInterceptor.CallerOf(context);
        var view = await views.ForAsync(context).ConfigureAwait(false);
        if (results?.Find(id) is not { } file || (file.OwnerId != caller.UserId && !caller.IsAdmin)
            || await engine.GetLogAsync(view, id, context.CancellationToken).ConfigureAwait(false) is null)
        {
            throw GrpcGuard.NotFound(NoResultFile);
        }

        return file;
    }

    public const string NoResultFile = "This task has no result file (it is kept for a day).";

    public override async Task<Proto.TaskResultFile> GetResultFile(Proto.TaskIdRequest request, ServerCallContext context)
    {
        var file = await ResultFileOfAsync(request, context).ConfigureAwait(false);
        return new Proto.TaskResultFile
        {
            TaskId = file.TaskId.ToString(),
            FileName = file.FileName,
            ContentType = file.ContentType,
            Size = file.Size,
            Saved = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(file.SavedUtc),
        };
    }

    public override async Task DownloadResult(Proto.TaskIdRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        var file = await ResultFileOfAsync(request, context).ConfigureAwait(false);
        var content = results!.Open(file.TaskId) ?? throw GrpcGuard.NotFound(NoResultFile);
        await using (content.ConfigureAwait(false))
        {
            await audit.WriteAsync(AuditActions.FileDownloaded, file.FileName,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{file.Size:N0} bytes, the result of a task"), CancellationToken.None).ConfigureAwait(false);
            context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
            await responseStream.WriteAsync(new Proto.DownloadChunk { FileName = file.FileName, Length = file.Size }).ConfigureAwait(false);
            var buffer = new byte[Plugins.PluginGrpcService.DownloadChunkBytes];
            int read;
            while ((read = await content.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
            {
                await responseStream.WriteAsync(new Proto.DownloadChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }).ConfigureAwait(false);
            }
        }
    }

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
        var reply = new Proto.TaskPluginList();
        reply.Plugins.AddRange(await TaskPluginsAsync(request, context).ConfigureAwait(false));
        return reply;
    }

    /// <summary>
    /// The entries of <see cref="ListTaskPlugins"/>, one message per plugin: the whole list can pass the client's message
    /// limit at 100,000 devices, one plugin's ids never do.
    /// </summary>
    public override async Task StreamTaskPlugins(
        Proto.ListTaskPluginsRequest request, IServerStreamWriter<Proto.TaskPluginInfo> responseStream, ServerCallContext context)
    {
        foreach (var plugin in await TaskPluginsAsync(request, context).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(plugin, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<Proto.TaskPluginInfo>> TaskPluginsAsync(Proto.ListTaskPluginsRequest request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context); // fail closed: never "no caller = every plugin"
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var reply = new List<Proto.TaskPluginInfo>();
        IReadOnlySet<Guid>? filtered = request.Filter is { } filter
            ? (await Devices.DeviceSearch.MatchesAsync(view, filter, facts.Now(), withApis: false, context.CancellationToken).ConfigureAwait(false)).Select(d => d.Id).ToHashSet()
            : null;
        foreach (var entry in await runnable.GetAsync(view, context.CancellationToken).ConfigureAwait(false))
        {
            if (!entry.InMenus && !request.WithPayloadSchema)
            {
                continue; // a page task that agents may start: never an entry of the client's menus
            }

            var keys = entry.InMenus ? PermissionCatalog.TaskKeys(entry.Plugin.Plugin) : TaskRunPermissions.PageRunKeys(registry, entry.Plugin, null) ?? [];
            if (!caller.Permissions.HasAny(keys))
            {
                continue;
            }

            if (!caller.HasAllDevices && !entry.Plugin.Origin.SupportsCallContext)
            {
                continue; // a package built before SDK 0.4.0: only for users with all devices (Run refuses it too)
            }

            try
            {
                var info = filtered is null ? Mappers.ToProto(entry, request.Compact) : Counts(TaskPluginRunnableCache.Within(entry, filtered));
                if (request.WithPayloadSchema && TaskPayloadSchemas.Of(entry.Plugin.Plugin) is { } schema)
                {
                    info.PayloadSchemaJson = schema.ToJsonString();
                }
                else if (!entry.InMenus)
                {
                    continue; // a page task without a schema stays on its page
                }

                reply.Add(info);
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

    /// <summary>A plugin's entry with counts only (the filter of the tools): no ids leave the server.</summary>
    private static Proto.TaskPluginInfo Counts(TaskPluginRunnable entry)
    {
        var info = Mappers.ToProto(entry, compact: true);
        info.RunnableDeviceIds.Clear();
        info.NotRunnableDeviceIds.Clear();
        info.RunnableOnAllExcept = false;
        info.NotRunnableGroups.Clear();
        info.RunnableCount = entry.Runnable.Count;
        info.NotRunnableGroups.AddRange(entry.Reasons.Select(r => new Proto.NotRunnableGroup { Reason = Sdk.Plugins.TaskSupportReasons.General(r.Reason), Count = r.DeviceIds.Count }));
        return info;
    }

    /// <summary>The trailer of a refused payload that names its field (like a refused recipient).</summary>
    public const string FieldTrailer = "oadm-field";

    /// <summary>The text when a plugin's payload check fails itself (fail closed: no task starts).</summary>
    public const string PayloadUnreadable = "The task settings cannot be checked. Nothing was changed.";

    /// <summary>The problems of a payload by the plugin's own rules; a check that throws counts as one problem.</summary>
    internal IReadOnlyList<Sdk.Tasks.PayloadError> PayloadErrors(Sdk.Plugins.ITaskPlugin plugin, string? payload, Sdk.Plugins.ICallContext call)
    {
        try
        {
            return plugin.ValidatePayload(payload, call) ?? [];
        }
#pragma warning disable CA1031 // A plugin's failing check refuses the run, it never breaks the server.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPayloadCheckFailed(ex, plugin.Id);
            return [new Sdk.Tasks.PayloadError(string.Empty, PayloadUnreadable)];
        }
    }

    /// <summary>The most secrets one run takes (the secret channel's slots of one preview).</summary>
    public const int MaxSecrets = 8;

    /// <summary>The secrets a run of the payload needs (<see cref="Sdk.Plugins.ITaskPlugin.SecretsOf"/>); a plugin's failing answer refuses the plan.</summary>
    internal IReadOnlyList<Sdk.Tasks.TaskSecret> SecretsOf(Sdk.Plugins.ITaskPlugin plugin, string? payload)
    {
        IReadOnlyList<Sdk.Tasks.TaskSecret> secrets;
        try
        {
            secrets = plugin.SecretsOf(payload) ?? [];
        }
#pragma warning disable CA1031 // A plugin's failing answer refuses the plan, it never breaks the server.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPayloadCheckFailed(ex, plugin.Id);
            throw GrpcGuard.InvalidArgument(PayloadUnreadable);
        }

        if (secrets.Count > MaxSecrets
            || secrets.Any(s => !SecretName().IsMatch(s.Name ?? string.Empty) || string.IsNullOrWhiteSpace(s.Label) || s.Label.Length > 64 || string.IsNullOrWhiteSpace(s.Field))
            || secrets.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != secrets.Count)
        {
            throw GrpcGuard.InvalidArgument(PayloadUnreadable); // fail closed: the run could not get its secrets
        }

        return secrets;
    }

    /// <summary>True when <paramref name="field"/> is <paramref name="secret"/> or inside it ("password", "settings[2].value").</summary>
    internal static bool IsField(string? field, string secret) =>
        field is not null && (string.Equals(field, secret, StringComparison.Ordinal)
            || (field.StartsWith(secret, StringComparison.Ordinal) && field.Length > secret.Length && field[secret.Length] is '.' or '['));

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SecretName();

    /// <summary>
    /// What a Run of the payload would do on the filter's devices, nothing written (docs/spec/grpc.md): the checks of Run, the
    /// payload's field errors (then nothing else), else the devices to run in address order, the skipped ones by reason, the
    /// task name and the warnings (<see cref="Sdk.Plugins.ITaskPlugin.PlanAsync(string?, IReadOnlyList{Sdk.Devices.IDeviceInfo}, Sdk.Plugins.IUploadedFiles, CancellationToken)"/>, cached data only).
    /// </summary>
    public override async Task<Proto.TaskPlanReply> Plan(Proto.PlanTaskRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var payload = string.IsNullOrEmpty(request.PayloadJson) ? null : request.PayloadJson;
        var caller = AuthInterceptor.CallerOf(context);
        if (!registry.TryGetTaskPlugin(request.PluginId, out var task))
        {
            throw GrpcGuard.NotFound(UnknownTaskPlugin(request.PluginId));
        }

        // A page task plans here only when it names the page permissions a run needs (ITaskPlugin.PagePermissions).
        TaskRunPermissions.RequireOutsidePage(registry, task, payload, caller.Permissions, () => roles.Catalog);
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var reply = new Proto.TaskPlanReply();
        var errors = PayloadErrors(task.Plugin, payload, engine.CallFor(view, TaskCaller.Of(caller)));
        if (request.CheckOnly)
        {
            reply.Errors.AddRange(errors.Select(e => new Proto.PayloadFieldError { Field = e.Field, Message = e.Message }));
            return reply;
        }

        // The secrets the person enters later (ITaskPlugin.SecretsOf): their fields are empty now, so their errors wait for
        // the check of the entered value and for Run.
        var secrets = SecretsOf(task.Plugin, payload);
        reply.Secrets.AddRange(secrets.Select(s => new Proto.TaskSecretSlot { Name = s.Name, Label = s.Label, Field = s.Field }));
        errors = [.. errors.Where(e => !secrets.Any(s => IsField(e.Field, s.Field)))];
        if (errors.Count > 0)
        {
            reply.Errors.AddRange(errors.Select(e => new Proto.PayloadFieldError { Field = e.Field, Message = e.Message }));
            return reply;
        }

        var devices = await Devices.DeviceSearch.MatchesAsync(view, request.Filter ?? new Proto.DeviceFilter(), facts.Now(), withApis: true, ct).ConfigureAwait(false);
        Sdk.Tasks.TaskPlan plan;
        try
        {
            plan = await task.Plugin.PlanAsync(payload, devices, engine.FilesOf(TaskCaller.Of(caller)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPayloadCheckFailed(ex, task.Plugin.Id);
            plan = Sdk.Tasks.TaskPlan.ByCanRun(task.Plugin, devices);
        }

        // Problems only the plan can find (an upload it read, a choice the user must make): answered like payload errors.
        if (plan.Errors is { Count: > 0 } planErrors)
        {
            reply.Errors.AddRange(planErrors.Select(e => new Proto.PayloadFieldError { Field = e.Field, Message = e.Message }));
            return reply;
        }

        var known = devices.Select(d => d.Id).ToHashSet();
        var planned = plan.Devices.Where(p => known.Contains(p.DeviceId)).GroupBy(p => p.DeviceId).ToDictionary(g => g.Key, g => g.First());
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            // A device the plan leaves out is skipped too: only what the plugin said yes to runs.
            var entry = planned.GetValueOrDefault(device.Id) ?? new Sdk.Tasks.TaskPlanDevice(device.Id, Sdk.Plugins.TaskSupportReasons.Default);
            if (entry.SkipReason is null)
            {
                reply.RunDeviceIds.Add(device.Id.ToString());
            }
            else
            {
                var reason = Sdk.Plugins.TaskSupportReasons.General(entry.SkipReason.Trim());
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
            }
        }

        reply.TaskName = engine.TaskNameFor(task, payload);
        reply.DeviceCount = devices.Count;
        reply.Skipped.AddRange(skipped.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new Proto.NotRunnableGroup { Reason = p.Key, Count = p.Value }));
        reply.Warnings.AddRange(plan.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)));
        reply.Files.AddRange(plan.Files.Select(f => new Proto.PlanFile { Name = f.Name, Sha256 = f.Sha256 }));
        reply.Details.AddRange((plan.Details ?? []).Where(d => !string.IsNullOrWhiteSpace(d)));
        return reply;
    }

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
        // to DeviceAccessRefusedException). A task only its page starts has no run key: the page's methods carry the rights,
        // so Run refuses it for every caller unless the task names the page permissions one run needs
        // (ITaskPlugin.PagePermissions, the agents' task tools); then the role needs exactly those.
        TaskRunPermissions.RequireOutsidePage(registry, task, payload, caller.Permissions, () => roles.Catalog);

        var view = await views.ForAsync(context).ConfigureAwait(false);
        var devices = await view.GetAsync(ids, context.CancellationToken).ConfigureAwait(false);

        // The plugin's own check of the payload (the dialog's rules, ITaskPlugin.ValidatePayload): an invalid one starts no task.
        if (PayloadErrors(task.Plugin, payload, engine.CallFor(view, TaskCaller.Of(caller))) is [var first, ..])
        {
            var refused = new Metadata { { FieldTrailer, first.Field } };
            throw new RpcException(new Status(StatusCode.InvalidArgument, first.Message), refused);
        }

        try
        {
            // One audit entry "Started task" for the run ("3 devices"), its outcome once every task of the run has ended.
            var taskAudit = new Sdk.Tasks.TaskAudit(task.DisplayName);
            var taskIds = await engine.RunAsync(request.PluginId, devices, payload, TaskCaller.Of(caller), context.CancellationToken, taskAudit).ConfigureAwait(false);
            var reply = new Proto.RunTaskReply();
            reply.TaskIds.AddRange(taskIds.Select(t => t.ToString()));
            if (taskIds.Count > 0 && await engine.GetAsync(taskIds[0], context.CancellationToken).ConfigureAwait(false) is { } started)
            {
                reply.BatchId = started.BatchId.ToString();
            }

#pragma warning disable CS0612 // task_id is deprecated but still filled for older clients.
            reply.TaskId = reply.TaskIds.Count > 0 ? reply.TaskIds[0] : string.Empty;
#pragma warning restore CS0612
            return reply;
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (TasksHeldException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: its payload could not be checked or planned")]
    private partial void LogPayloadCheckFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId} could not be described")]
    private partial void LogPluginInfoFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted all tasks ({Count})")]
    private partial void LogDeletedAll(int count);
}
