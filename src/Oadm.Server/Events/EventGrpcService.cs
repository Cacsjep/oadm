using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Server.Auth;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Events;

/// <summary>
/// gRPC EventService: the catalog of events and actions, the rules (Admin to change, audited), their live state and the
/// event log (<see cref="EventRulesService"/>, <see cref="EventLog"/>). Every method works on the caller's view
/// (docs/spec/device-scope.md): users of a device group see the events of their devices, sites and checks, only the rules
/// of their group and the log lines of those rules and of their devices; Test uses only a device of the view.
/// </summary>
public sealed class EventGrpcService(
    EventRulesService rules,
    ActionRunner actions,
    EventLog log,
    AuditLog audit,
    DeviceViewFactory views,
    IHostApplicationLifetime lifetime) : Proto.EventService.EventServiceBase
{
    public override async Task<Proto.EventCatalog> GetCatalog(Proto.Empty request, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var catalog = new Proto.EventCatalog();
        foreach (var type in await rules.EventTypesAsync(view, context.CancellationToken).ConfigureAwait(false))
        {
            var entry = new Proto.EventTypeEntry
            {
                Id = type.Id,
                DisplayName = type.Info.DisplayName,
                Group = type.Info.Group,
                Stateful = type.Stateful,
                DeviceScope = type.DeviceScope,
                Description = type.Info.Description ?? string.Empty,
                Available = type.Available,
                SourceName = type.SourceName,
            };
            entry.Fields.AddRange(type.Info.DataFields.Select(f => new Proto.EventFieldInfo { Name = f.Name, DisplayName = f.DisplayName, Values = { f.Values ?? [] }, NoFilter = !f.Filter }));
            catalog.EventTypes.Add(entry);
        }

        foreach (var action in rules.Catalog.Actions)
        {
            catalog.Actions.Add(ToProto(action));
        }

        return catalog;
    }

    public override async Task<Proto.RuleList> ListRules(Proto.Empty request, ServerCallContext context) =>
        ToProto(await rules.ListAsync(await views.ForAsync(context).ConfigureAwait(false), context.CancellationToken).ConfigureAwait(false));

    /// <summary>The list now and after every change; a burst sends the newest list once (at most every 500 ms).</summary>
    public override async Task WatchRules(Proto.Empty request, IServerStreamWriter<Proto.RuleList> responseStream, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        rules.Engine.StatusChanged += OnChanged;
        rules.Changed += OnChanged;
        try
        {
            await responseStream.WriteAsync(ToProto(await rules.ListAsync(view, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(ToProto(await rules.ListAsync(view, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            rules.Engine.StatusChanged -= OnChanged;
            rules.Changed -= OnChanged;
        }
    }

    public override async Task<Proto.RuleDetail> GetRule(Proto.RuleId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "rule id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var rule = await rules.GetAsync(id, view, context.CancellationToken).ConfigureAwait(false) ?? throw GrpcGuard.NotFound(EventRulesService.NoLongerExists);
        return ToProto(rule.Definition, rule.Summary);
    }

    public override async Task<Proto.RuleDetail> SaveRule(Proto.SaveRuleRequest request, ServerCallContext context)
    {
        RuleDefinition definition;
        try
        {
            definition = RuleDefinition.FromJson(request.DefinitionJson);
        }
        catch (JsonException)
        {
            throw GrpcGuard.InvalidArgument("The rule could not be read.");
        }

        var ct = context.CancellationToken;
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var owner = OwnerMapping.FromProto(request.Owner);
        try
        {
            var (saved, summary, created) = await rules.SaveAsync(definition, view, owner, AuthInterceptor.CallerOf(context).IsAdmin, ct).ConfigureAwait(false);
            await audit.WriteAsync(created ? AuditActions.RuleCreated : AuditActions.RuleChanged, saved.Name, summary.ConditionsText + " -> " + summary.ActionsText, ct).ConfigureAwait(false);
            return ToProto(saved, summary);
        }
        catch (RuleValidationException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
    }

    public override async Task<Proto.Empty> DeleteRule(Proto.RuleId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "rule id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var removed = await rules.DeleteAsync(id, view, context.CancellationToken).ConfigureAwait(false) ?? throw GrpcGuard.NotFound(EventRulesService.NoLongerExists);
        await audit.WriteAsync(AuditActions.RuleDeleted, removed.Definition.Name, null, context.CancellationToken).ConfigureAwait(false);
        return new Proto.Empty();
    }

    public override async Task<Proto.RuleSummary> SetRuleEnabled(Proto.SetRuleEnabledRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "rule id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        RuleSummaryModel summary;
        try
        {
            summary = await rules.SetEnabledAsync(id, request.Enabled, view, context.CancellationToken).ConfigureAwait(false)
                ?? throw GrpcGuard.NotFound(EventRulesService.NoLongerExists);
        }
        catch (RuleValidationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }

        await audit.WriteAsync(request.Enabled ? AuditActions.RuleTurnedOn : AuditActions.RuleTurnedOff, summary.Name, null, context.CancellationToken).ConfigureAwait(false);
        return ToProto(summary);
    }

    public override async Task<Proto.RuleDetail> DuplicateRule(Proto.RuleId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "rule id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        try
        {
            var copy = await rules.DuplicateAsync(id, view, context.CancellationToken).ConfigureAwait(false) ?? throw GrpcGuard.NotFound(EventRulesService.NoLongerExists);
            await audit.WriteAsync(AuditActions.RuleCreated, copy.Definition.Name, "copy", context.CancellationToken).ConfigureAwait(false);
            return ToProto(copy.Definition, copy.Summary);
        }
        catch (RuleValidationException ex) when (ex.Message.StartsWith("At most", StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (RuleValidationException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
    }

    public override async Task<Proto.ActionChoiceList> ListActionChoices(Proto.ActionChoicesRequest request, ServerCallContext context)
    {
        try
        {
            var view = await views.ForAsync(context).ConfigureAwait(false);
            var choices = await actions.ListChoicesAsync(request.ActionId, request.FieldKey, view, context.CancellationToken).ConfigureAwait(false);
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
    }

    public override async Task<Proto.EventLogPage> ListLog(Proto.ListEventLogRequest request, ServerCallContext context)
    {
        Guid? ruleId = string.IsNullOrEmpty(request.RuleId) ? null : GrpcGuard.ParseId(request.RuleId, "rule id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var scope = await LogScopeAsync(view, context.CancellationToken).ConfigureAwait(false);
        var (lines, total) = await log.ListAsync(request.Limit, request.Offset, ruleId, scope, context.CancellationToken).ConfigureAwait(false);
        var page = new Proto.EventLogPage { TotalCount = total };
        page.Entries.AddRange(lines.Select(ToProto));
        return page;
    }

    public override async Task<Proto.ClearEventLogReply> ClearLog(Proto.Empty request, ServerCallContext context)
    {
        var deleted = await log.ClearAsync(context.CancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.EventLogCleared, "Event log", string.Create(CultureInfo.InvariantCulture, $"{deleted} entries"), context.CancellationToken).ConfigureAwait(false);
        return new Proto.ClearEventLogReply { Deleted = deleted };
    }

    public override async Task<Proto.TestActionReply> TestAction(Proto.TestActionRequest request, ServerCallContext context)
    {
        System.Text.Json.Nodes.JsonObject settings;
        try
        {
            settings = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(request.SettingsJson) ? "{}" : request.SettingsJson) as System.Text.Json.Nodes.JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The settings are not valid: " + ex.Message));
        }

        Guid? ruleId = string.IsNullOrEmpty(request.RuleId) ? null : GrpcGuard.ParseId(request.RuleId, "rule id");
        Guid? actionKey = string.IsNullOrEmpty(request.ActionKey) ? null : GrpcGuard.ParseId(request.ActionKey, "action key");
        Guid? deviceId = string.IsNullOrEmpty(request.DeviceId) ? null : GrpcGuard.ParseId(request.DeviceId, "device id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var device = await view.GetAsync(deviceId is { } id ? [id] : [], context.CancellationToken).ConfigureAwait(false); // outside: NOT_FOUND
        if (ruleId is { } rule && await rules.GetAsync(rule, view, context.CancellationToken).ConfigureAwait(false) is null)
        {
            ruleId = null; // a rule the caller does not see (or a new one): no stored passwords from it
            actionKey = null;
        }

        var (ok, message) = await actions.TestAsync(
            request.ActionId, settings, request.RuleName, ruleId, actionKey, string.IsNullOrEmpty(request.EventTypeId) ? null : request.EventTypeId,
            device, OwnerScopes.OwnerOf(view), context.CancellationToken).ConfigureAwait(false);
        var name = rules.Catalog.FindAction(request.ActionId)?.Plugin.DisplayName ?? request.ActionId;
        await audit.WriteAsync(AuditActions.EventActionTested, name, (ok ? "Sent" : "Failed") + (string.IsNullOrWhiteSpace(request.RuleName) ? string.Empty : " · " + request.RuleName), context.CancellationToken).ConfigureAwait(false);
        return new Proto.TestActionReply { Ok = ok, Message = message };
    }

    public override async Task WatchLog(Proto.Empty request, IServerStreamWriter<Proto.EventLogPage> responseStream, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var rulesChanged = 1; // the rules of the group are read again after a rule changed
        void OnRulesChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref rulesChanged, 1);
        rules.Changed += OnRulesChanged;
        IReadOnlySet<Guid> ruleIds = new HashSet<Guid>();
        try
        {
            await foreach (var batch in log.WatchAsync(ct).ConfigureAwait(false))
            {
                IReadOnlyList<EventLogLine> lines = batch;
                if (!view.HasAllDevices)
                {
                    if (Interlocked.Exchange(ref rulesChanged, 0) == 1)
                    {
                        ruleIds = await rules.VisibleIdsAsync(view, ct).ConfigureAwait(false);
                    }

                    var scope = new EventLogScope(await view.IdsAsync(ct).ConfigureAwait(false), ruleIds);
                    lines = [.. batch.Where(scope.Covers)];
                    if (lines.Count == 0)
                    {
                        continue;
                    }
                }

                var page = new Proto.EventLogPage { TotalCount = lines.Count };
                page.Entries.AddRange(lines.Select(ToProto));
                await responseStream.WriteAsync(page, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            rules.Changed -= OnRulesChanged;
        }
    }

    /// <summary>The lines a user of a group sees (null: every line, users with all devices).</summary>
    private async Task<EventLogScope?> LogScopeAsync(DeviceView view, CancellationToken ct) =>
        view.HasAllDevices ? null : new EventLogScope(await view.IdsAsync(ct).ConfigureAwait(false), await rules.VisibleIdsAsync(view, ct).ConfigureAwait(false));

    internal static Proto.ActionEntry ToProto(CatalogAction action)
    {
        var plugin = action.Plugin;
        var entry = new Proto.ActionEntry
        {
            Id = action.Id,
            DisplayName = plugin.DisplayName,
            Group = Safe(() => plugin.Group) ?? "General",
            Description = Safe(() => plugin.Description) ?? string.Empty,
            IconKey = plugin.IconKey ?? string.Empty,
            NeedsDevice = plugin.NeedsDevice,
            SupportsTurnOff = Safe(() => plugin.SupportsTurnOff),
            UsesPlaceholders = Safe(() => plugin.UsesPlaceholders),
            Available = action.Available,
            SourceName = action.SourceName,
        };
        entry.Fields.AddRange((Safe(() => plugin.Fields) ?? []).Select(ToProto));
        return entry;
    }

    /// <summary>A setting of an action or a compliance check as the client draws it.</summary>
    internal static Proto.ActionFieldEntry ToProto(ActionField field)
    {
        var proto = new Proto.ActionFieldEntry
        {
            Key = field.Key,
            Label = field.Label,
            Kind = (Proto.ActionFieldKind)(int)field.Type,
            Required = field.Required,
            Hint = field.Hint ?? string.Empty,
            Min = field.Min,
            Max = field.Max,
            MaxLength = field.MaxLength,
            OffEdge = field.OffEdge,
            VisibleWhenKey = field.VisibleWhenKey ?? string.Empty,
            VisibleWhenValue = field.VisibleWhenValue ?? string.Empty,
            Language = field.Language ?? string.Empty,
            LanguageKey = field.LanguageKey ?? string.Empty,
            ChoicesFromDevice = field.ChoicesFromDevice,
        };
        if (field.Default is not null)
        {
            proto.DefaultValue = field.Default;
        }

        proto.Choices.AddRange(field.Choices.Select(c => new Proto.ActionChoiceEntry { Value = c.Value, Label = c.Label }));
        proto.Accept.AddRange(field.Accept);
        return proto;
    }

    internal static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return default;
        }
    }

    private Proto.RuleList ToProto(IEnumerable<RuleSummaryModel> summaries)
    {
        var list = new Proto.RuleList();
        list.Rules.AddRange(summaries.Select(ToProto));
        return list;
    }

    private Proto.RuleSummary ToProto(RuleSummaryModel s)
    {
        var proto = new Proto.RuleSummary
        {
            Id = s.Id.ToString(),
            Name = s.Name,
            Enabled = s.Enabled,
            ConditionsText = s.ConditionsText,
            ActionsText = s.ActionsText,
            Stateful = s.Stateful,
            ActiveCount = s.ActiveCount,
            StateText = s.StateText,
            Problem = s.Problem ?? string.Empty,
            Owner = OwnerMapping.ToProto(s.Owner, rules.Owners.Scopes),
        };
        if (s.LastFiredUtc is { } fired)
        {
            proto.LastFired = Timestamp.FromDateTime(DateTime.SpecifyKind(fired, DateTimeKind.Utc));
        }

        return proto;
    }

    private Proto.RuleDetail ToProto(RuleDefinition definition, RuleSummaryModel summary) => new()
    {
        Summary = ToProto(summary),
        DefinitionJson = definition.ToJson(),
    };

    private static Proto.EventLogEntry ToProto(EventLogLine line) => new()
    {
        Id = line.Id,
        Time = Timestamp.FromDateTime(DateTime.SpecifyKind(line.TimeUtc, DateTimeKind.Utc)),
        Kind = (Proto.EventLogKind)(int)line.Kind,
        Result = (Proto.EventLogResult)(int)line.Result,
        What = line.What,
        DeviceId = line.DeviceId?.ToString() ?? string.Empty,
        Device = line.Device,
        RuleId = line.RuleId?.ToString() ?? string.Empty,
        Rule = line.Rule,
        Message = line.Message,
    };
}
