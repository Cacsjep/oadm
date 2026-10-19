using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Compliance;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Events;
using Oadm.Server.Common;
using Oadm.Server.Events;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Compliance;

/// <summary>gRPC ComplianceService: the check types, the checks (changes audited), their results and Check now.</summary>
public sealed class ComplianceGrpcService(ComplianceService compliance, AuditLog audit, IHostApplicationLifetime lifetime)
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

        return ToProto(Engine.ListChecks());
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

    public override Task<Proto.ComplianceCheckList> ListChecks(Proto.Empty request, ServerCallContext context) =>
        Task.FromResult(ToProto(Engine.ListChecks()));

    /// <summary>The list now and after every change; a burst sends the newest list once (at most every 500 ms).</summary>
    public override async Task WatchChecks(Proto.Empty request, IServerStreamWriter<Proto.ComplianceCheckList> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        Engine.ChecksChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(ToProto(Engine.ListChecks()), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(ToProto(Engine.ListChecks()), ct).ConfigureAwait(false);
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

    public override Task<Proto.ComplianceCheckDetail> GetCheck(Proto.ComplianceCheckId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "check id");
        var state = Engine.ListChecks().FirstOrDefault(c => c.Check.Id == id) ?? throw GrpcGuard.NotFound("The check no longer exists.");
        return Task.FromResult(new Proto.ComplianceCheckDetail { Summary = ToProto(state), SettingsJson = state.Check.Settings.ToJsonString() });
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
        try
        {
            var (stored, created) = await compliance.SaveAsync(check, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(created ? AuditActions.ComplianceCheckCreated : AuditActions.ComplianceCheckChanged, stored.Name, null, context.CancellationToken).ConfigureAwait(false);
            return ToProto(State(stored.Id));
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
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
            var name = await compliance.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false);
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
        try
        {
            var stored = await compliance.SetEnabledAsync(id, request.Enabled, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(request.Enabled ? AuditActions.ComplianceCheckTurnedOn : AuditActions.ComplianceCheckTurnedOff, stored.Name, null, context.CancellationToken).ConfigureAwait(false);
            return ToProto(State(stored.Id));
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
        try
        {
            var choices = await compliance.ListChoicesAsync(request.TypeId, request.FieldKey, deviceId, context.CancellationToken).ConfigureAwait(false);
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

    public override Task<Proto.Empty> CheckNow(Proto.ComplianceCheckNowRequest request, ServerCallContext context)
    {
        Engine.CheckNow(GrpcGuard.ParseIds(request.CheckIds, "check id"), GrpcGuard.ParseIds(request.DeviceIds, "device id"));
        return Task.FromResult(new Proto.Empty());
    }

    public override async Task WatchResults(Proto.Empty request, IServerStreamWriter<Proto.ComplianceResultBatch> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        using var watch = Engine.Watch();
        try
        {
            for (var i = 0; i < watch.Snapshot.Count; i += BatchSize)
            {
                var batch = new Proto.ComplianceResultBatch();
                batch.Results.AddRange(watch.Snapshot.Skip(i).Take(BatchSize).Select(ToProto));
                batch.SnapshotEnd = i + BatchSize >= watch.Snapshot.Count;
                await responseStream.WriteAsync(batch, ct).ConfigureAwait(false);
            }

            if (watch.Snapshot.Count == 0)
            {
                await responseStream.WriteAsync(new Proto.ComplianceResultBatch { SnapshotEnd = true }, ct).ConfigureAwait(false);
            }

            while (await watch.Changes.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new Proto.ComplianceResultBatch();
                while (batch.Results.Count < BatchSize && watch.Changes.TryRead(out var change))
                {
                    batch.Results.Add(ToProto(change));
                }

                await responseStream.WriteAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    private ComplianceCheckState State(Guid id) =>
        Engine.ListChecks().FirstOrDefault(c => c.Check.Id == id) ?? throw GrpcGuard.NotFound("The check no longer exists.");

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

    internal static Proto.ComplianceCheckSummary ToProto(ComplianceCheckState state)
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
