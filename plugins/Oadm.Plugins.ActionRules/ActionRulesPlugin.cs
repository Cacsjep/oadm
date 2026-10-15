using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules;

/// <summary>
/// Core plugin "Action Rule Editor": the action rules of many devices in one page, grouped by what they do, and changed,
/// turned on or off, deleted and undone for all of them at once. Off by default (plugin.json). The rules are read only on
/// request (<see cref="DeviceSweep{TRow}"/>: when the page opens and on Refresh, read-only, over SOAP); every change is a
/// task per device (<see cref="RuleTaskPlugin"/>) started only from the page. Rights: "show" (Operator, Viewer: read),
/// "manage" (change, on / off, undo; Operator yes), "delete" (Operator yes).
/// </summary>
public sealed partial class ActionRulesPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly DeviceSweepOptions _options;
    private readonly RuleTaskPlugin _task;
    private readonly ConcurrentDictionary<string, TemplateSet> _templateSets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _deviceGates = new();
    private readonly ConcurrentDictionary<Guid, (int Left, DateTimeOffset Started)> _running = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<Guid>> _runTasks = new();
    private ICorePluginContext? _ctx;
    private DeviceSweep<DeviceRulesRow>? _sweep;

    public ActionRulesPlugin()
        : this(new DeviceSweepOptions { DeviceTimeout = TimeSpan.FromSeconds(20), Parallelism = 16 })
    {
    }

    internal ActionRulesPlugin(DeviceSweepOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _task = new RuleTaskPlugin(this);
        TaskPlugins = [_task];
        Undo = new UndoStore(null, null, options.Time);
    }

    public string Id => ActionRulesInfo.PluginId;

    public string DisplayName => ActionRulesInfo.DisplayName;

    public string? IconKey => ActionRulesInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Automation;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show the page", "See the action rules of the devices.") { ViewerDefault = true },
        new(ActionRulesMethods.ManagePermission, "Change action rules", "Change action rules, turn them on or off and undo changes."),
        new(ActionRulesMethods.DeletePermission, "Delete action rules", "Delete action rules on the devices."),
    ];

    /// <summary>The saved rules of every run (undo).</summary>
    internal UndoStore Undo { get; private set; }

    /// <summary>The table while the plugin runs (tests).</summary>
    internal DeviceSweep<DeviceRulesRow>? Sweep => _sweep;

    public string? RequiredPermission(string method) => method switch
    {
        ActionRulesMethods.Read or ActionRulesMethods.GetState or ActionRulesMethods.GetProgress or ActionRulesMethods.Templates or ActionRulesMethods.UndoRuns
            or ActionRulesMethods.Events => PermissionKeys.Show,
        ActionRulesMethods.Edit or ActionRulesMethods.Undo or ActionRulesMethods.Deploy or ActionRulesMethods.Build or ActionRulesMethods.RecipientEdit
            or ActionRulesMethods.RecipientDeploy or ActionRulesMethods.RecipientNew or ActionRulesMethods.ScheduleEdit or ActionRulesMethods.ScheduleDeploy => ActionRulesMethods.ManagePermission,
        ActionRulesMethods.Delete or ActionRulesMethods.RecipientDelete or ActionRulesMethods.ScheduleDelete => ActionRulesMethods.DeletePermission,
        _ => null,
    };

    /// <summary>Every run is in the audit log.</summary>
    public bool IsAudited(string method) => RequiredPermission(method) is ActionRulesMethods.ManagePermission or ActionRulesMethods.DeletePermission;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        Undo = new UndoStore(ctx.DataDirectory is { } data ? Path.Combine(data, "undo") : null, ctx.Secrets, _options.Time);
        _sweep = new DeviceSweep<DeviceRulesRow>(ctx, _options, _ => true, async (d, _, t) => await ReadAsync(d, t).ConfigureAwait(false), r => r.Address, PublishRows, PublishState);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_sweep is { } sweep)
        {
            _sweep = null;
            await sweep.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var sweep = _sweep ?? throw new InvalidOperationException("The Action Rule Editor plugin is not running.");
        switch (method)
        {
            case ActionRulesMethods.Read:
                sweep.Start(ReadIds(payloadJson));
                return ActionRulesJson.Serialize(State());
            case ActionRulesMethods.GetState:
                return ActionRulesJson.Serialize(State());
            case ActionRulesMethods.GetProgress:
                return ActionRulesJson.Serialize(State() with { Rows = [] });
            case ActionRulesMethods.Templates:
                var ids = ActionRulesJson.Deserialize<TemplatesRequest>(payloadJson).Ids ?? [];
                return ActionRulesJson.Serialize(ids.Select(id => _templateSets.TryGetValue(id, out var set) ? set : null).OfType<TemplateSet>().ToList());
            case ActionRulesMethods.Edit or ActionRulesMethods.Delete:
                return ActionRulesJson.Serialize(await RunAsync(method, ActionRulesJson.Deserialize<RunRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.UndoRuns:
                return ActionRulesJson.Serialize(await Undo.ListAsync(ct).ConfigureAwait(false));
            case ActionRulesMethods.Undo:
                return ActionRulesJson.Serialize(await UndoAsync(ActionRulesJson.Deserialize<UndoRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.Deploy:
                return ActionRulesJson.Serialize(await DeployAsync(ActionRulesJson.Deserialize<DeployRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.Events:
                return ActionRulesJson.Serialize(Events(ActionRulesJson.Deserialize<EventsRequest>(payloadJson)));
            case ActionRulesMethods.Build:
                return ActionRulesJson.Serialize(await BuildAsync(ActionRulesJson.Deserialize<BuildRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.RecipientNew:
                return ActionRulesJson.Serialize(await NewRecipientAsync(ActionRulesJson.Deserialize<NewRecipientRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.RecipientDeploy:
                return ActionRulesJson.Serialize(await DeployRecipientAsync(ActionRulesJson.Deserialize<DeployRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.RecipientEdit or ActionRulesMethods.RecipientDelete:
                return ActionRulesJson.Serialize(await RecipientsAsync(method, ActionRulesJson.Deserialize<RecipientRunRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.ScheduleEdit or ActionRulesMethods.ScheduleDelete:
                return ActionRulesJson.Serialize(await SchedulesAsync(method, ActionRulesJson.Deserialize<ScheduleRunRequest>(payloadJson), ct).ConfigureAwait(false));
            case ActionRulesMethods.ScheduleDeploy:
                return ActionRulesJson.Serialize(await DeployScheduleAsync(ActionRulesJson.Deserialize<ScheduleDeployRequest>(payloadJson), ct).ConfigureAwait(false));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>One change at a time per device: tasks of two quick runs never write the same device together.</summary>
    internal SemaphoreSlim DeviceGate(Guid deviceId) => _deviceGates.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));

    /// <summary>A task of a run ended (whatever its result).</summary>
    internal void TaskEnded(Guid runId)
    {
        while (_running.TryGetValue(runId, out var r))
        {
            if (r.Left <= 1 ? _running.TryRemove(new KeyValuePair<Guid, (int, DateTimeOffset)>(runId, r)) : _running.TryUpdate(runId, (r.Left - 1, r.Started), r))
            {
                if (r.Left <= 1)
                {
                    _runTasks.TryRemove(runId, out _);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Whether tasks of the run may still write. Tasks that were cancelled while queued never report: the task engine says
    /// whether any task of the run is still queued or running; a host that cannot tell counts the run as done after
    /// <see cref="RunningLimit"/>; after a server restart nothing runs.
    /// </summary>
    internal bool IsRunning(Guid runId)
    {
        if (!_running.TryGetValue(runId, out var r) || r.Left <= 0 || _options.Time.GetUtcNow() - r.Started >= RunningLimit)
        {
            return false;
        }

        if (_ctx?.Tasks is { } tasks && _runTasks.TryGetValue(runId, out var ids) && ids.Count > 0
            && ids.All(id => tasks.IsActive(id) == false))
        {
            _running.TryRemove(runId, out _);
            _runTasks.TryRemove(runId, out _);
            return false;
        }

        return true; // also while the tasks are being started (ids not known yet)
    }

    internal static readonly TimeSpan RunningLimit = TimeSpan.FromHours(6);

    /// <summary>Runs whose tasks ended without reporting (cancelled while queued) or that passed the limit are forgotten.</summary>
    private void ForgetOldRuns()
    {
        foreach (var runId in _running.Keys)
        {
            IsRunning(runId); // removes runs whose tasks all ended
            if (_running.TryGetValue(runId, out var r) && _options.Time.GetUtcNow() - r.Started >= RunningLimit)
            {
                _running.TryRemove(runId, out _);
                _runTasks.TryRemove(runId, out _);
            }
        }
    }

    /// <summary>Reads one device again (after a task on it).</summary>
    internal async Task RefreshRowAsync(Guid deviceId)
    {
        if (_sweep is { } sweep)
        {
            try
            {
                await sweep.ReadOneAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The task's own result counts; the table reads it again on Refresh.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_ctx is { } ctx)
                {
                    LogRefreshFailed(ctx.Logger, ex);
                }
            }
        }
    }

    /// <summary>One device: its rules (passwords dropped), or why they could not be read.</summary>
    internal async Task<DeviceRulesRow> ReadAsync(IDeviceInfo device, CancellationToken ct)
    {
        var row = new DeviceRulesRow(device.Id, device.Address, device.Model, device.FirmwareVersion, RowStates.Error, null, false, [], null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), _options.Time.GetUtcNow());
        _deviceEvents.TryRemove(device.Id, out _);
        if (DeviceMessages.Refusal(device.Status, refuseUnreachable: true) is { } refusal)
        {
            return row with { Text = refusal };
        }

        try
        {
            var vapix = await _ctx!.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
            var set = await SoapActionEngine.ReadAsync(vapix, ct).ConfigureAwait(false);
            KeepEvents(device.Id, set.Events);
            var inPlace = await RestActionRules.IsAvailableAsync(vapix, ct).ConfigureAwait(false);
            var templates = TemplateSet.From(set.ActionTemplates, set.RecipientTemplates);
            _templateSets.TryAdd(templates.Id, templates);
            var used = set.Rules.SelectMany(r => r.Conditions.Prepend(r.StartEvent)).OfType<EventFilter>()
                .Select(f => RuleText.Topic(f.Topic)).ToHashSet(StringComparer.Ordinal);
            return row with
            {
                State = RowStates.Ok,
                InPlace = inPlace,
                Rules = [.. set.Rules.Select(RuleView.From)],
                TemplateSetId = templates.Id,
                TopicNames = set.TopicNames.Where(n => used.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal),
                ScheduleNames = set.Schedules.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal),
                Recipients = [.. set.Recipients.Select(RecipientView.From)],
                Schedules = [.. set.Schedules.Select(s => ScheduleView.From(s, set.Rules))],
            };
        }
#pragma warning disable CA1031 // One device's failure must not stop the others; its row says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return row with { Text = _sweep?.Failure(ex) ?? ex.Message };
        }
    }

    /// <summary>Starts one task per device after checking the request; nothing starts on a bad one.</summary>
    private async Task<RunStarted> RunAsync(string method, RunRequest request, CancellationToken ct)
    {
        var action = method == ActionRulesMethods.Delete ? RuleTaskActions.Delete : RuleTaskActions.Edit;
        var targets = Targets(request.Targets);
        if (action == RuleTaskActions.Edit && (request.Patch is null ? "Choose what to change." : request.Patch.Problem() ?? UnknownSetting(request.Patch.Parameters)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var patch = action == RuleTaskActions.Edit ? PreparePatch(request.Patch!, targets.Select(t => t.DeviceId)) : null;
        var description = TaskName(action, patch);
        var payload = new RuleTaskPayload(action, Guid.NewGuid(), description, targets, patch);
        return await StartAsync(payload, [.. targets.Select(t => t.DeviceId)], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts back what a run changed. Refused while its tasks may still write, and only once: the mark is set before the tasks
    /// start (atomically) and taken back when they cannot be started.
    /// </summary>
    private async Task<RunStarted> UndoAsync(UndoRequest request, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("The Action Rule Editor plugin is not running.");
        var run = await Undo.FindAsync(request.RunId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("This change can no longer be undone.", nameof(request));
        if (IsRunning(run.RunId))
        {
            throw new ArgumentException("This change is still running. Undo it when its tasks are finished.", nameof(request));
        }

        var devices = Undo.DevicesOf(run.RunId);
        if (devices.Count == 0)
        {
            throw new ArgumentException("This change wrote nothing that can be undone.", nameof(request));
        }

        if (!await Undo.TryMarkUndoneAsync(run.RunId, true, ct).ConfigureAwait(false))
        {
            throw new ArgumentException("This change was undone already.", nameof(request));
        }

        var payload = new RuleTaskPayload(RuleTaskActions.Undo, Guid.NewGuid(), "Undo action rule change", [], null, run.RunId);
        LogRun(ctx.Logger, RuleTaskActions.Undo, devices.Count);
        try
        {
            var ids = await ctx.Tasks.RunAsync(ActionRulesInfo.TaskPluginId, devices, ActionRulesJson.Serialize(payload), ActionRulesInfo.DisplayName, ct).ConfigureAwait(false);
            return new RunStarted(payload.RunId, ids);
        }
        catch
        {
            await Undo.TryMarkUndoneAsync(run.RunId, false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Copies a rule of one device to the chosen devices (read with its passwords now, kept in the run's memory).</summary>
    private async Task<RunStarted> DeployAsync(DeployRequest request, CancellationToken ct)
    {
        var (vapix, devices) = await DeploySourceAsync(request.SourceDeviceId, request.DeviceIds, ct).ConfigureAwait(false);
        if (request.Patch is { IsEmpty: false } patch && (patch.Problem() ?? UnknownSetting(patch.Parameters)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var set = await SoapActionEngine.ReadAsync(vapix, ct).ConfigureAwait(false);
        var rule = set.Rules.FirstOrDefault(r => r.Id == request.SourceId && RuleView.VersionOf(r) == request.Version)
            ?? throw new ArgumentException(RuleTexts.ChangedSinceRead, nameof(request));
        if (ActionNames.IsRefused(rule.Action.Template) && request.Patch?.Template is null)
        {
            throw new ArgumentException(ActionNames.RefusedText, nameof(request));
        }

        var used = rule.Conditions.Prepend(rule.StartEvent).OfType<EventFilter>().Select(RuleText.ScheduleId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var name = request.Patch?.Name ?? rule.Name;
        var prepared = request.Patch is { } p ? PreparePatch(p, devices) : null;
        var payload = new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), Shorten("Copy action rule " + name), [], prepared)
        {
            Rule = rule,
            Schedules = [.. set.Schedules.Where(s => used.Contains(s.Id))],
        };
        return await StartAsync(payload, devices, ct).ConfigureAwait(false);
    }

    private async Task<RunStarted> DeployRecipientAsync(DeployRequest request, CancellationToken ct)
    {
        var (vapix, devices) = await DeploySourceAsync(request.SourceDeviceId, request.DeviceIds, ct).ConfigureAwait(false);
        var set = await SoapActionEngine.ReadAsync(vapix, ct).ConfigureAwait(false);
        var recipient = set.Recipients.FirstOrDefault(r => r.Id == request.SourceId && RecipientView.VersionOf(r) == request.Version)
            ?? throw new ArgumentException(RuleTexts.RecipientChangedSinceRead, nameof(request));
        var payload = new RuleTaskPayload(RuleTaskActions.RecipientDeploy, Guid.NewGuid(), Shorten("Copy recipient " + (request.RecipientPatch?.Name ?? recipient.Name)), [], null)
        {
            Recipient = recipient,
            RecipientPatch = request.RecipientPatch,
        };
        return await StartAsync(payload, devices, ct).ConfigureAwait(false);
    }

    /// <summary>A new recipient, created on each device by the same task as a copied one.</summary>
    private async Task<RunStarted> NewRecipientAsync(NewRecipientRequest request, CancellationToken ct)
    {
        var devices = DeviceIds(request.DeviceIds);
        if (string.IsNullOrWhiteSpace(request.Template) || !request.Template.StartsWith("com.axis.recipient.", StringComparison.Ordinal) || request.Template.Length > 256)
        {
            throw new ArgumentException("Choose the kind of recipient.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > RulePatchApply.MaxNameLength)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters."), nameof(request));
        }

        var parameters = request.Parameters ?? new Dictionary<string, string>();
        if (parameters.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Length > RulePatchApply.MaxValueLength))
        {
            throw new ArgumentException("A value is too long.", nameof(request));
        }

        if (UnknownSetting(parameters) is { } unknown)
        {
            throw new ArgumentException(unknown, nameof(request));
        }

        var name = request.Name.Trim();
        var payload = new RuleTaskPayload(RuleTaskActions.RecipientDeploy, Guid.NewGuid(), Shorten("Add recipient " + name), [], null)
        {
            Recipient = new DeviceRecipient(string.Empty, name, request.Template, [.. parameters.Select(p => new NameValue(p.Key, p.Value))]),
        };
        return await StartAsync(payload, devices, ct).ConfigureAwait(false);
    }

    private async Task<RunStarted> RecipientsAsync(string method, RecipientRunRequest request, CancellationToken ct)
    {
        var delete = method == ActionRulesMethods.RecipientDelete;
        var targets = Targets(request.Targets);
        if (!delete && request.Patch is null or { IsEmpty: true })
        {
            throw new ArgumentException("Choose what to change.", nameof(request));
        }

        if (request.Patch?.Name is { } name && name.Trim().Length is 0 or > RulePatchApply.MaxNameLength)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters."), nameof(request));
        }

        if (UnknownSetting(request.Patch?.Parameters) is { } unknown)
        {
            throw new ArgumentException(unknown, nameof(request));
        }

        var payload = new RuleTaskPayload(delete ? RuleTaskActions.RecipientDelete : RuleTaskActions.RecipientEdit, Guid.NewGuid(), delete ? "Delete recipients" : "Change recipients", targets, null)
        {
            RecipientPatch = delete ? null : request.Patch,
        };
        return await StartAsync(payload, [.. targets.Select(t => t.DeviceId)], ct).ConfigureAwait(false);
    }

    private async Task<RunStarted> SchedulesAsync(string method, ScheduleRunRequest request, CancellationToken ct)
    {
        var delete = method == ActionRulesMethods.ScheduleDelete;
        var targets = Targets(request.Targets);
        if (!delete && (request.Patch is null or { Name: null, Definition: null } ? "Choose what to change." : ScheduleProblem(request.Patch)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var payload = new RuleTaskPayload(delete ? RuleTaskActions.ScheduleDelete : RuleTaskActions.ScheduleEdit, Guid.NewGuid(), delete ? "Delete schedules" : "Change schedules", targets, null)
        {
            SchedulePatch = delete ? null : request.Patch,
            Rules = delete && request.Rules is { Count: > 0 } rules ? Targets(rules) : null,
        };
        return await StartAsync(payload, [.. targets.Select(t => t.DeviceId)], ct).ConfigureAwait(false);
    }

    /// <summary>A new schedule (OADM's own id from its name, the same on every device) or one copied from a device.</summary>
    private async Task<RunStarted> DeployScheduleAsync(ScheduleDeployRequest request, CancellationToken ct)
    {
        DeviceSchedule schedule;
        if (request.New is { } created)
        {
            if (string.IsNullOrWhiteSpace(created.Name) || created.Definition is null || ScheduleProblem(created) is { } problem)
            {
                throw new ArgumentException(string.IsNullOrWhiteSpace(created.Name) ? "Enter a name." : ScheduleProblem(created) ?? "Enter the schedule.", nameof(request));
            }

            schedule = new DeviceSchedule(ScheduleText.OwnId(created.Name), created.Name.Trim(), ScheduleText.Normalize(created.Definition));
        }
        else
        {
            var source = request.SourceDeviceId ?? throw new ArgumentException("Choose the schedule.", nameof(request));
            var (vapix, _) = await DeploySourceAsync(source, request.DeviceIds, ct).ConfigureAwait(false);
            var set = await SoapActionEngine.ReadAsync(vapix, ct).ConfigureAwait(false);
            var found = set.Schedules.FirstOrDefault(s => s.Id == request.SourceId && ScheduleView.VersionOf(s) == request.SourceVersion)
                ?? throw new ArgumentException(RuleTexts.ScheduleChangedSinceRead, nameof(request));
            schedule = found.IsBuiltIn || found.Id.StartsWith(ScheduleText.OwnIdPrefix, StringComparison.Ordinal)
                ? found
                : found with { Id = ScheduleText.OwnId(found.Name) };
        }

        var devices = DeviceIds(request.DeviceIds);
        var payload = new RuleTaskPayload(RuleTaskActions.ScheduleDeploy, Guid.NewGuid(), Shorten("Add schedule " + schedule.Name), [], null)
        {
            Schedules = [schedule],
        };
        return await StartAsync(payload, devices, ct).ConfigureAwait(false);
    }

    private async Task<(Sdk.Vapix.IVapixClient Vapix, IReadOnlyList<Guid> Devices)> DeploySourceAsync(Guid source, IReadOnlyList<Guid>? deviceIds, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("The Action Rule Editor plugin is not running.");
        var devices = DeviceIds(deviceIds);
        if (await ctx.Devices.FindAsync(source, ct).ConfigureAwait(false) is not { } device)
        {
            throw new ArgumentException(DeviceMessages.Removed, nameof(source));
        }

        if (DeviceMessages.Refusal(device.Status, refuseUnreachable: true) is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(source));
        }

        return (await ctx.Vapix.CreateAsync(source, ct).ConfigureAwait(false), devices);
    }

    /// <summary>The devices a read covers: the page's choice (1 to 30,000), or every device for an older page without one.</summary>
    private static HashSet<Guid>? ReadIds(string? payloadJson)
    {
        var ids = string.IsNullOrWhiteSpace(payloadJson) ? null : ActionRulesJson.Deserialize<ReadRequest>(payloadJson).DeviceIds;
        return ids is null ? null : DeviceIds(ids).ToHashSet();
    }

    private static List<Guid> DeviceIds(IReadOnlyList<Guid>? ids)
    {
        var devices = (ids ?? []).Distinct().ToList();
        return devices.Count is 0 or > ActionRulesInfo.MaxDevicesPerRun
            ? throw new ArgumentException(Oadm.Sdk.Devices.DeviceLimits.ChooseRangeText("devices"), nameof(ids))
            : devices;
    }

    private static List<DeviceTargets> Targets(IReadOnlyList<DeviceTargets>? targets)
    {
        var list = (targets ?? []).Where(t => t.Rules is { Count: > 0 }).GroupBy(t => t.DeviceId)
            .Select(g => new DeviceTargets(g.Key, [.. g.SelectMany(t => t.Rules).DistinctBy(r => r.Id)])).ToList();
        if (list.Count is 0 or > ActionRulesInfo.MaxDevicesPerRun)
        {
            throw new ArgumentException("Choose items on 1 to 30,000 devices.", nameof(targets));
        }

        return list.Any(t => t.Rules.Count > ActionRulesInfo.MaxRulesPerDevice)
            ? throw new ArgumentException("Too many items on one device.", nameof(targets))
            : list;
    }

    private static string? ScheduleProblem(SchedulePatch patch) =>
        patch.Name is { } n && n.Trim().Length is 0 or > ScheduleText.MaxNameLength
            ? string.Create(CultureInfo.InvariantCulture, $"Enter a name of 1 to {ScheduleText.MaxNameLength} characters.")
            : patch.Definition is { } d ? ScheduleText.Problem(ScheduleText.Parse(d)) : null;

    /// <summary>
    /// A setting name no read device knows for any action or recipient (a typo or an old page): refused before any task
    /// starts. Each device leaves out the settings its own action lacks.
    /// </summary>
    private string? UnknownSetting(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0 || _templateSets.IsEmpty)
        {
            return null;
        }

        var known = _templateSets.Values.SelectMany(t => t.Actions.SelectMany(a => a.Parameters).Concat(t.Recipients.SelectMany(r => r.Parameters)))
            .ToHashSet(StringComparer.Ordinal);
        return parameters.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown
            ? $"No device has the setting \"{unknown}\"."
            : null;
    }

    /// <summary>Saves the run for undo and starts one task per device.</summary>
    private async Task<RunStarted> StartAsync(RuleTaskPayload payload, IReadOnlyList<Guid> devices, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("The Action Rule Editor plugin is not running.");
        await Undo.StartRunAsync(new UndoRun(payload.RunId, _options.Time.GetUtcNow(), RunDescription(payload.Description, devices.Count), payload.Action, devices.Count, false), ct).ConfigureAwait(false);
        LogRun(ctx.Logger, payload.Action, devices.Count);
        ForgetOldRuns();
        _running[payload.RunId] = (devices.Count, _options.Time.GetUtcNow());
        IReadOnlyList<Guid> ids;
        try
        {
            ids = await ctx.Tasks.RunAsync(ActionRulesInfo.TaskPluginId, devices, ActionRulesJson.Serialize(payload), ActionRulesInfo.DisplayName, ct).ConfigureAwait(false);
        }
        catch
        {
            _running.TryRemove(payload.RunId, out _); // nothing started: nothing can write
            throw;
        }

        if (_running.ContainsKey(payload.RunId))
        {
            _runTasks[payload.RunId] = ids;
        }

        return new RunStarted(payload.RunId, ids);
    }

    /// <summary>Task names are at most 48 characters.</summary>
    private static string Shorten(string name) => name.Length > 48 ? name[..47] + "…" : name;

    /// <summary>The task name ("Turn off action rules", "Change action rules", "Delete action rules").</summary>
    public static string TaskName(string action, RulePatch? patch) => action switch
    {
        RuleTaskActions.Delete => "Delete action rules",
        RuleTaskActions.Undo => "Undo action rule change",
        _ when patch is { OnlyEnabled: true } => patch.Enabled == true ? "Turn on action rules" : "Turn off action rules",
        _ => "Change action rules",
    };

    private static string RunDescription(string name, int devices) =>
        name + (devices == 1 ? " on 1 device" : string.Create(CultureInfo.InvariantCulture, $" on {devices:N0} devices"));

    private RulesState State()
    {
        if (_sweep is not { } sweep)
        {
            return new RulesState(false, 0, 0, null, []);
        }

        var now = sweep.Snapshot();
        return new RulesState(now.Running, now.Done, now.Total, now.FinishedUtc, sweep.Sorted(now.Rows));
    }

    private void PublishRows(IReadOnlyList<DeviceRulesRow> rows, IReadOnlyList<Guid> removed)
    {
        if (_ctx?.Events is not { } events)
        {
            return;
        }

        foreach (var chunk in rows.Chunk(ActionRulesInfo.MaxRowsPerEvent))
        {
            events.Publish(ActionRulesMethods.RowsTopic, ActionRulesJson.Serialize(new RowsEvent(chunk, [])));
        }

        if (removed.Count > 0)
        {
            events.Publish(ActionRulesMethods.RowsTopic, ActionRulesJson.Serialize(new RowsEvent([], removed)));
        }
    }

    private void PublishState()
    {
        if (_sweep is { } sweep && _ctx?.Events is { } events)
        {
            var now = sweep.Snapshot();
            events.Publish(ActionRulesMethods.StateTopic, ActionRulesJson.Serialize(new RulesState(now.Running, now.Done, now.Total, now.FinishedUtc, [])));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Action Rule Editor: {Action} started on {Count} devices")]
    private static partial void LogRun(ILogger logger, string action, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Action Rule Editor: reading a device after its task failed")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex);
}
