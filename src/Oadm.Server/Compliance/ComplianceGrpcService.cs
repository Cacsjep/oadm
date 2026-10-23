using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Compliance;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Events;
using Oadm.Server.Auth;
using Oadm.Server.Common;
using Oadm.Server.Events;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Compliance;

/// <summary>
/// gRPC ComplianceService: the check types, the checks (changes audited), their results and Check now. Every method works
/// on the caller's view (docs/spec/device-scope.md): users of a device group see only the checks of their group and their
/// results; Check now and the device of ListChoices come from the view.
/// </summary>
public sealed class ComplianceGrpcService(ComplianceService compliance, DeviceViewFactory views, AuditLog audit, IHostApplicationLifetime lifetime)
    : Proto.ComplianceService.ComplianceServiceBase
{
    /// <summary>Results per message of the snapshot.</summary>
    internal const int BatchSize = 5_000;

    private ComplianceEngine Engine => compliance.Engine;

    public override async Task<Proto.ComplianceCheckList> SetEnabled(Proto.SetComplianceEnabledRequest request, ServerCallContext context)
    {
        var was = Engine.Enabled;
        await compliance.SetEnabledAsync(request.Enabled, context.CancellationToken).ConfigureAwait(false);
        if (was != request.Enabled)
        {
            await audit.WriteAsync(request.Enabled ? AuditActions.ComplianceTurnedOn : AuditActions.ComplianceTurnedOff, "Compliance", null, context.CancellationToken).ConfigureAwait(false);
        }

        return ToProto(compliance.ListChecks(await views.ForAsync(context).ConfigureAwait(false)));
    }

    public override Task<Proto.ComplianceCatalog> GetCatalog(Proto.Empty request, ServerCallContext context)
    {
        var catalog = new Proto.ComplianceCatalog();
        foreach (var type in Engine.Catalog.Types)
        {
            var entry = new Proto.ComplianceTypeEntry
            {
                Id = type.Id,
                DisplayName = type.Type.DisplayName,
                Group = EventGrpcService.Safe(() => type.Type.Group) ?? "General",
                Description = EventGrpcService.Safe(() => type.Type.Description) ?? string.Empty,
                IconKey = type.Type.IconKey ?? string.Empty,
                DefaultSeverity = (Proto.ComplianceSeverity)(int)EventGrpcService.Safe(() => type.Type.DefaultSeverity),
                ReadsDevices = !EventGrpcService.Safe(() => type.Type.UsesCachedDataOnly),
                Available = type.Available,
                SourceName = type.SourceName,
            };
            entry.Fields.AddRange((EventGrpcService.Safe(() => type.Type.Fields) ?? []).Select(EventGrpcService.ToProto));
            catalog.Entries.Add(entry);
        }

        return Task.FromResult(catalog);
    }

    public override async Task<Proto.ComplianceCheckList> ListChecks(Proto.Empty request, ServerCallContext context) =>
        ToProto(compliance.ListChecks(await views.ForAsync(context).ConfigureAwait(false)));

    /// <summary>The list now and after every change; a burst sends the newest list once (at most every 500 ms).</summary>
    public override async Task WatchChecks(Proto.Empty request, IServerStreamWriter<Proto.ComplianceCheckList> responseStream, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        Engine.ChecksChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(ToProto(compliance.ListChecks(view)), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(ToProto(compliance.ListChecks(view)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            Engine.ChecksChanged -= OnChanged;
        }
    }

    public override async Task<Proto.ComplianceCheckDetail> GetCheck(Proto.ComplianceCheckId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "check id");
        var state = Find(await views.ForAsync(context).ConfigureAwait(false), id);
        return new Proto.ComplianceCheckDetail { Summary = ToProto(state), SettingsJson = state.Check.Settings.ToJsonString() };
    }

    public override async Task<Proto.ComplianceCheckSummary> SaveCheck(Proto.ComplianceCheckDetail request, ServerCallContext context)
    {
        var summary = request.Summary ?? throw GrpcGuard.InvalidArgument("The check is missing.");
        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(string.IsNullOrEmpty(request.SettingsJson) ? "{}" : request.SettingsJson) as JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            throw GrpcGuard.InvalidArgument("The settings are not valid: " + ex.Message);
        }

        var check = new ComplianceCheck
        {
            Id = string.IsNullOrEmpty(summary.Id) ? Guid.Empty : GrpcGuard.ParseId(summary.Id, "check id"),
            Name = summary.Name,
            TypeId = summary.TypeId,
            Settings = settings,
            Devices = FromProto(summary.Devices),
            Severity = (ComplianceSeverity)(int)summary.Severity,
            IntervalMinutes = summary.IntervalMinutes,
            Enabled = summary.Enabled,
            Version = summary.Version,
        };
        var caller = AuthInterceptor.CallerOf(context);
        var view = await views.ForAsync(context).ConfigureAwait(false);
        try
        {
            var (stored, created) = await compliance.SaveAsync(check, view, Mapping.OwnerMapping.FromProto(summary.Owner), caller.IsAdmin, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(created ? AuditActions.ComplianceCheckCreated : AuditActions.ComplianceCheckChanged, stored.Name, null, context.CancellationToken).ConfigureAwait(false);
            return ToProto(State(view, stored.Id));
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (ComplianceValidationException ex) when (ex.Message.StartsWith("At most", StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (ComplianceValidationException ex) when (ex.Message.StartsWith("Another user", StringComparison.Ordinal))
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
        catch (ComplianceValidationException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    public override async Task<Proto.Empty> DeleteCheck(Proto.ComplianceCheckId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "check id");
        try
        {
            var name = await compliance.DeleteAsync(id, await views.ForAsync(context).ConfigureAwait(false), context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.ComplianceCheckDeleted, name, null, context.CancellationToken).ConfigureAwait(false);
            return new Proto.Empty();
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (ComplianceValidationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    public override async Task<Proto.ComplianceCheckSummary> SetCheckEnabled(Proto.SetComplianceCheckEnabledRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "check id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        try
        {
            var stored = await compliance.SetEnabledAsync(id, request.Enabled, view, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(request.Enabled ? AuditActions.ComplianceCheckTurnedOn : AuditActions.ComplianceCheckTurnedOff, stored.Name, null, context.CancellationToken).ConfigureAwait(false);
            return ToProto(State(view, stored.Id));
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (ComplianceValidationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    public override async Task<Proto.ActionChoiceList> ListChoices(Proto.ComplianceChoicesRequest request, ServerCallContext context)
    {
        Guid? deviceId = string.IsNullOrEmpty(request.DeviceId) ? null : GrpcGuard.ParseId(request.DeviceId, "device id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var devices = await view.GetAsync(deviceId is { } id ? [id] : [], context.CancellationToken).ConfigureAwait(false); // outside: NOT_FOUND
        try
        {
            var choices = await compliance.ListChoicesAsync(request.TypeId, request.FieldKey, devices, context.CancellationToken).ConfigureAwait(false);
            var reply = new Proto.ActionChoiceList();
            reply.Choices.AddRange(choices.Select(c => new Proto.ActionChoiceEntry { Value = c.Value, Label = c.Label }));
            return reply;
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or Oadm.Core.Vapix.VapixException or TimeoutException)
        {
            throw GrpcGuard.FailedPrecondition("The device could not be read: " + ex.Message);
        }
    }

    /// <summary>Check now on the caller's view: empty lists = every check the caller sees / every device of the view.</summary>
    public override async Task<Proto.Empty> CheckNow(Proto.ComplianceCheckNowRequest request, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var devices = await view.GetAsync(GrpcGuard.ParseIds(request.DeviceIds, "device id"), context.CancellationToken).ConfigureAwait(false);
        try
        {
            await compliance.CheckNowAsync(GrpcGuard.ParseIds(request.CheckIds, "check id"), devices, context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }

        return new Proto.Empty();
    }

    public override async Task WatchResults(Proto.Empty request, IServerStreamWriter<Proto.ComplianceResultBatch> responseStream, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        using var watch = Engine.Watch();
        try
        {
            // Users of a group: only the results of their group's checks (those run only on the group's devices).
            IReadOnlyList<ComplianceResultModel> snapshot = watch.Snapshot;
            var shown = new HashSet<Guid>(); // checks this stream sent: their removals follow even after the check is gone
            if (!view.HasAllDevices)
            {
                shown.UnionWith(compliance.ListChecks(view).Select(c => c.Check.Id));
                snapshot = [.. snapshot.Where(r => shown.Contains(r.CheckId))];
            }

            for (var i = 0; i < snapshot.Count; i += BatchSize)
            {
                var batch = new Proto.ComplianceResultBatch();
                batch.Results.AddRange(snapshot.Skip(i).Take(BatchSize).Select(ToProto));
                batch.SnapshotEnd = i + BatchSize >= snapshot.Count;
                await responseStream.WriteAsync(batch, ct).ConfigureAwait(false);
            }

            if (snapshot.Count == 0)
            {
                await responseStream.WriteAsync(new Proto.ComplianceResultBatch { SnapshotEnd = true }, ct).ConfigureAwait(false);
            }

            while (await watch.Changes.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new Proto.ComplianceResultBatch();
                while (batch.Results.Count < BatchSize && watch.Changes.TryRead(out var change))
                {
                    if (view.HasAllDevices || (change.Removed && shown.Contains(change.CheckId)) || compliance.SeesResultsOf(view, change.CheckId))
                    {
                        shown.Add(change.CheckId);
                        batch.Results.Add(ToProto(change));
                    }
                }

                if (batch.Results.Count > 0)
                {
                    await responseStream.WriteAsync(batch, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    private OwnerScopes Owners => compliance.Owners;

    private ComplianceCheckState State(DeviceView view, Guid id) => Find(view, id);

    private ComplianceCheckState Find(DeviceView view, Guid id)
    {
        try
        {
            return compliance.Find(view, id);
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
    }

    private static DeviceSelector FromProto(Proto.ComplianceDevices? devices)
    {
        if (devices is null)
        {
            return new DeviceSelector { Mode = DeviceSelectorMode.Devices };
        }

        return devices.All
            ? new DeviceSelector { Mode = DeviceSelectorMode.All }
            : new DeviceSelector
            {
                Mode = DeviceSelectorMode.Devices,
                DeviceIds = [.. GrpcGuard.ParseIds(devices.DeviceIds, "device id").Distinct()],
                Tags = [.. devices.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
            };
    }

    private Proto.ComplianceCheckList ToProto(IEnumerable<ComplianceCheckState> states)
    {
        var list = new Proto.ComplianceCheckList { Enabled = Engine.Enabled };
        list.Checks.AddRange(states.Select(ToProto));
        return list;
    }

    private Proto.ComplianceCheckSummary ToProto(ComplianceCheckState state)
    {
        var proto = ToProto(state, Owners);
        return proto;
    }

    internal static Proto.ComplianceCheckSummary ToProto(ComplianceCheckState state, OwnerScopes? owners)
    {
        var check = state.Check;
        var devices = new Proto.ComplianceDevices { All = check.Devices.Mode == DeviceSelectorMode.All };
        if (!devices.All)
        {
            devices.DeviceIds.AddRange(check.Devices.DeviceIds.Select(id => id.ToString()));
            devices.Tags.AddRange(check.Devices.Tags);
        }

        return new Proto.ComplianceCheckSummary
        {
            Id = check.Id.ToString(),
            Name = check.Name,
            TypeId = check.TypeId,
            TypeName = state.TypeName,
            Summary = state.Summary,
            Severity = (Proto.ComplianceSeverity)(int)check.Severity,
            IntervalMinutes = check.IntervalMinutes,
            Enabled = check.Enabled,
            BuiltIn = check.BuiltIn,
            Version = check.Version,
            Devices = devices,
            Passed = state.Passed,
            Failed = state.Failed,
            NotChecked = state.NotChecked,
            NotApplicable = state.NotApplicable,
            CouldNotCheck = state.CouldNotCheck,
            Problem = state.Problem ?? string.Empty,
            ReadsDevices = state.ReadsDevices,
            Owner = Mapping.OwnerMapping.ToProto(check.Owner, owners),
        };
    }

    private static Proto.ComplianceResultEntry ToProto(ComplianceResultModel result) => new()
    {
        CheckId = result.CheckId.ToString(),
        DeviceId = result.DeviceId.ToString(),
        Outcome = (Proto.ComplianceOutcome)(int)result.Outcome,
        Detail = result.Detail,
        CheckedUnixMs = result.CheckedUtc == default ? 0 : new DateTimeOffset(DateTime.SpecifyKind(result.CheckedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        SinceUnixMs = result.SinceUtc == default ? 0 : new DateTimeOffset(DateTime.SpecifyKind(result.SinceUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        Removed = result.Removed,
        Checking = result.Checking,
    };
}
