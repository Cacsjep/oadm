using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules;

/// <summary>Ids, names and limits shared by the server part, the page, the fake mode and the tests.</summary>
public static class ActionRulesInfo
{
    public const string PluginId = "oadm.action-rules";

    public const string DisplayName = "Action Rule Editor";

    public const string IconKey = "workflow";

    /// <summary>The task the page starts (not in the device menus).</summary>
    public const string TaskPluginId = "oadm.action-rules.task";

    /// <summary>Rows per pushed event; more changed rows go out in several events.</summary>
    public const int MaxRowsPerEvent = 200;

    /// <summary>Devices per run.</summary>
    public const int MaxDevicesPerRun = Oadm.Sdk.Devices.DeviceLimits.MaxDevices;

    /// <summary>Rules per device and run.</summary>
    public const int MaxRulesPerDevice = 256;

    /// <summary>How long a run can be undone.</summary>
    public static readonly TimeSpan UndoKeep = TimeSpan.FromDays(30);
}

/// <summary>Page backend methods (<c>InvokeAsync</c>), the rights they need and the live event topics.</summary>
public static class ActionRulesMethods
{
    /// <summary><see cref="ReadRequest"/> -> <see cref="RulesState"/>: reads the rules of the chosen devices once; rows and progress follow as events.</summary>
    public const string Read = "read";

    /// <summary>{} -> <see cref="RulesState"/>: the current rows, without asking any device.</summary>
    public const string GetState = "getState";

    /// <summary>{} -> <see cref="RulesState"/> without rows: the progress of a read (the page polls it; rows come as events).</summary>
    public const string GetProgress = "getProgress";

    /// <summary><see cref="TemplatesRequest"/> -> the template sets by id.</summary>
    public const string Templates = "templates";

    /// <summary><see cref="RunRequest"/> with a patch -> <see cref="RunStarted"/>. Right "manage".</summary>
    public const string Edit = "edit";

    /// <summary><see cref="RunRequest"/> -> <see cref="RunStarted"/>: deletes the rules. Right "delete".</summary>
    public const string Delete = "delete";

    /// <summary>{} -> <see cref="UndoRun"/> list, newest first.</summary>
    public const string UndoRuns = "undoRuns";

    /// <summary><see cref="UndoRequest"/> -> <see cref="RunStarted"/>: puts the rules of a run back. Right "manage".</summary>
    public const string Undo = "undo";

    /// <summary><see cref="DeployRequest"/> -> <see cref="RunStarted"/>: copies a rule to other devices. Right "manage".</summary>
    public const string Deploy = "deploy";

    /// <summary><see cref="RecipientRunRequest"/> -> <see cref="RunStarted"/>. Right "manage".</summary>
    public const string RecipientEdit = "recipientEdit";

    /// <summary><see cref="DeployRequest"/> (a recipient) -> <see cref="RunStarted"/>. Right "manage".</summary>
    public const string RecipientDeploy = "recipientDeploy";

    /// <summary><see cref="RecipientRunRequest"/> -> <see cref="RunStarted"/>. Right "delete".</summary>
    public const string RecipientDelete = "recipientDelete";

    /// <summary><see cref="NewRecipientRequest"/> -> <see cref="RunStarted"/>: a new recipient on the devices. Right "manage".</summary>
    public const string RecipientNew = "recipientNew";

    /// <summary><see cref="ScheduleRunRequest"/> -> <see cref="RunStarted"/>. Right "manage".</summary>
    public const string ScheduleEdit = "scheduleEdit";

    /// <summary><see cref="ScheduleDeployRequest"/> -> <see cref="RunStarted"/>: a new or copied schedule. Right "manage".</summary>
    public const string ScheduleDeploy = "scheduleDeploy";

    /// <summary><see cref="ScheduleRunRequest"/> -> <see cref="RunStarted"/>. Right "delete".</summary>
    public const string ScheduleDelete = "scheduleDelete";

    /// <summary><see cref="EventsRequest"/> -> <see cref="EventsReply"/>: the events the devices offered when they were read.</summary>
    public const string Events = "events";

    /// <summary><see cref="BuildRequest"/> -> <see cref="RunStarted"/>: creates a new rule on the devices. Right "manage".</summary>
    public const string Build = "build";

    public const string RowsTopic = "rows";

    public const string StateTopic = "state";

    /// <summary>Right "Change action rules" (Operator yes, Viewer no).</summary>
    public const string ManagePermission = "manage";

    /// <summary>Right "Delete action rules" (Operator yes, Viewer no).</summary>
    public const string DeletePermission = "delete";
}

/// <summary>What a task does on its device.</summary>
public static class RuleTaskActions
{
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Undo = "undo";
    public const string Deploy = "deploy";
    public const string RecipientEdit = "recipientEdit";
    public const string RecipientDeploy = "recipientDeploy";
    public const string RecipientDelete = "recipientDelete";
    public const string ScheduleEdit = "scheduleEdit";
    public const string ScheduleDeploy = "scheduleDeploy";
    public const string ScheduleDelete = "scheduleDelete";

    public static bool IsKnown(string? action) =>
        action is Edit or Delete or Undo or Deploy or RecipientEdit or RecipientDeploy or RecipientDelete or ScheduleEdit or ScheduleDeploy or ScheduleDelete;

    /// <summary>The actions that create something on devices that may not have it yet (no ids per device).</summary>
    public static bool IsDeploy(string? action) => action is Deploy or RecipientDeploy or ScheduleDeploy;
}

/// <summary>States of a device row.</summary>
public static class RowStates
{
    public const string Ok = "ok";
    public const string Error = "error";
}

/// <summary>
/// A rule as the page sees it: every value but passwords (empty, <see cref="StoredSecrets"/> names those with a value).
/// <see cref="Version"/> changes whenever anything of the rule changes; a task only writes a rule still at that version.
/// </summary>
public sealed record RuleView(
    string Id,
    string Name,
    bool Enabled,
    EventFilter? StartEvent,
    IReadOnlyList<EventFilter> Conditions,
    string? Timeout,
    string Template,
    IReadOnlyList<NameValue> Parameters,
    IReadOnlyList<string> StoredSecrets,
    string Version)
{
    public string? Get(string name) => Parameters.FirstOrDefault(p => p.Name == name)?.Value;

    /// <summary>The page's view of a device rule; passwords are dropped here, also a login's inside an address.</summary>
    public static RuleView From(DeviceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var parameters = rule.Action.Parameters.Select(p => RuleText.IsSecret(p.Name) ? p with { Value = string.Empty } : p with { Value = RuleText.HideLogin(p.Value) }).ToList();
        var secrets = rule.Action.Parameters.Where(p => RuleText.IsSecret(p.Name) && p.Value.Length > 0).Select(p => p.Name).ToList();
        var view = new RuleView(rule.Id, rule.Name, rule.Enabled, rule.StartEvent, rule.Conditions, rule.Timeout, rule.Action.Template, parameters, secrets, string.Empty);
        return view with { Version = VersionOf(rule) };
    }

    /// <summary>The keyed version of the whole rule (<see cref="RuleContent.Version"/>); a client cannot learn passwords from it.</summary>
    public static string VersionOf(DeviceRule rule) => RuleContent.Version(RuleContent.Of(rule));
}

/// <summary>A recipient preset as the page sees it: passwords empty, <see cref="StoredSecrets"/> names those with a value.</summary>
public sealed record RecipientView(string Id, string Name, string Template, IReadOnlyList<NameValue> Parameters, IReadOnlyList<string> StoredSecrets, string Version)
{
    public string? Get(string name) => Parameters.FirstOrDefault(p => p.Name == name)?.Value;

    public static RecipientView From(DeviceRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        return new RecipientView(
            recipient.Id,
            recipient.Name,
            recipient.Template,
            [.. recipient.Parameters.Select(p => RuleText.IsSecret(p.Name) ? p with { Value = string.Empty } : p with { Value = RuleText.HideLogin(p.Value) })],
            [.. recipient.Parameters.Where(p => RuleText.IsSecret(p.Name) && p.Value.Length > 0).Select(p => p.Name)],
            VersionOf(recipient));
    }

    public static string VersionOf(DeviceRecipient recipient) => RuleContent.Version(RuleContent.Of(recipient));
}

/// <summary>A schedule as the page sees it, with how many rules of its device use it.</summary>
public sealed record ScheduleView(string Id, string Name, string Definition, bool IsBuiltIn, int UsedBy, string Version)
{
    public static ScheduleView From(DeviceSchedule schedule, IEnumerable<DeviceRule> rules)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(rules);
        return new ScheduleView(schedule.Id, schedule.Name, schedule.Definition, schedule.IsBuiltIn, rules.Count(r => UsesSchedule(r, schedule.Id)), VersionOf(schedule));
    }

    public static string VersionOf(DeviceSchedule schedule) => RuleContent.Version(RuleContent.Of(schedule));

    /// <summary>Whether a rule names the schedule in its start event or conditions.</summary>
    public static bool UsesSchedule(DeviceRule rule, string scheduleId)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Conditions.Prepend(rule.StartEvent).OfType<EventFilter>().Any(f => RuleText.ScheduleId(f) == scheduleId);
    }
}

/// <summary>The action templates of a device; devices with the same set share one id.</summary>
public sealed record TemplateSet(string Id, IReadOnlyList<ActionTemplate> Actions, IReadOnlyList<RecipientTemplate> Recipients)
{
    public static TemplateSet From(IReadOnlyList<ActionTemplate> actions, IReadOnlyList<RecipientTemplate> recipients)
    {
        var text = new StringBuilder();
        foreach (var a in actions.OrderBy(a => a.Token, StringComparer.Ordinal))
        {
            text.Append(a.Token).Append('>').Append(a.RecipientTemplate).Append(':').AppendJoin(',', a.Parameters).Append('\n');
        }

        foreach (var r in recipients.OrderBy(r => r.Token, StringComparer.Ordinal))
        {
            text.Append(r.Token).Append(':').AppendJoin(',', r.Parameters).Append('\n');
        }

        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 8);
        return new TemplateSet(id, actions, recipients);
    }

    public ActionTemplate? Action(string token) => Actions.FirstOrDefault(a => a.Token == token);

    /// <summary>Every parameter name of the template and its recipient.</summary>
    public IReadOnlyList<string> ParameterNames(string token)
    {
        var action = Action(token);
        if (action is null)
        {
            return [];
        }

        var recipient = action.RecipientTemplate is { } r ? Recipients.FirstOrDefault(x => x.Token == r)?.Parameters ?? [] : [];
        return [.. action.Parameters.Concat(recipient).Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>One device of the page: its rules, or why they could not be read.</summary>
public sealed record DeviceRulesRow(
    Guid DeviceId,
    string Address,
    string? Model,
    string? Firmware,
    string State,
    string? Text,
    bool InPlace,
    IReadOnlyList<RuleView> Rules,
    string? TemplateSetId,
    IReadOnlyDictionary<string, string> TopicNames,
    IReadOnlyDictionary<string, string> ScheduleNames,
    DateTimeOffset ReadUtc)
{
    /// <summary>The device's recipient presets.</summary>
    public IReadOnlyList<RecipientView> Recipients { get; init; } = [];

    /// <summary>The device's schedules.</summary>
    public IReadOnlyList<ScheduleView> Schedules { get; init; } = [];
}

/// <summary>Progress and rows of the page.</summary>
public sealed record RulesState(bool Reading, int Done, int Total, DateTimeOffset? FinishedUtc, IReadOnlyList<DeviceRulesRow> Rows);

public sealed record RowsEvent(IReadOnlyList<DeviceRulesRow> Rows, IReadOnlyList<Guid> Removed);

/// <summary>Request of <see cref="ActionRulesMethods.Read"/>: the devices of the page's choice (null: every device, older pages).</summary>
public sealed record ReadRequest(IReadOnlyList<Guid>? DeviceIds);

public sealed record TemplatesRequest(IReadOnlyList<string> Ids);

/// <summary>The rules a run changes on one device, each at the version the page read.</summary>
public sealed record DeviceTargets(Guid DeviceId, IReadOnlyList<RuleRef> Rules);

public sealed record RuleRef(string Id, string Version);

/// <summary>What the page asks for: the rules per device and, for an edit, the patch.</summary>
public sealed record RunRequest(IReadOnlyList<DeviceTargets> Targets, RulePatch? Patch);

public sealed record UndoRequest(Guid RunId);

public sealed record RunStarted(Guid RunId, IReadOnlyList<Guid> TaskIds);

/// <summary>Copies a rule or a recipient of one device to other devices; the patch applies to the copies.</summary>
public sealed record DeployRequest(Guid SourceDeviceId, string SourceId, string Version, IReadOnlyList<Guid> DeviceIds, RulePatch? Patch = null, RecipientPatch? RecipientPatch = null);

/// <summary>Changes or deletes recipients (ids per device in <see cref="DeviceTargets"/>).</summary>
public sealed record RecipientRunRequest(IReadOnlyList<DeviceTargets> Targets, RecipientPatch? Patch);

/// <summary>
/// Changes or deletes schedules (ids per device). <see cref="Rules"/>: the rules that use them, as the page read them,
/// deleted together with them; a schedule that other rules use stays.
/// </summary>
public sealed record ScheduleRunRequest(IReadOnlyList<DeviceTargets> Targets, SchedulePatch? Patch, IReadOnlyList<DeviceTargets>? Rules = null);

/// <summary>A new schedule (<see cref="New"/>) or one copied from a device, put on the chosen devices.</summary>
public sealed record ScheduleDeployRequest(IReadOnlyList<Guid> DeviceIds, SchedulePatch? New = null, Guid? SourceDeviceId = null, string? SourceId = null, string? SourceVersion = null);

/// <summary>
/// The parts of recipients a change sets; null parts stay. <see cref="UpdateRules"/>: the rules that send to the old
/// settings get the changed ones too.
/// </summary>
public sealed record RecipientPatch(string? Name, IReadOnlyDictionary<string, string>? Parameters, bool UpdateRules = true)
{
    public bool IsEmpty => Name is null && (Parameters is null || Parameters.Count == 0);
}

/// <summary>The parts of a schedule a change sets.</summary>
public sealed record SchedulePatch(string? Name, string? Definition);

/// <summary>The payload of every task of a run (memory only: a copied rule or recipient carries its passwords).</summary>
public sealed record RuleTaskPayload(string Action, Guid RunId, string Description, IReadOnlyList<DeviceTargets> Targets, RulePatch? Patch, Guid? UndoOf = null)
{
    /// <summary>The rule to copy (deploy), with its passwords.</summary>
    public DeviceRule? Rule { get; init; }

    /// <summary>The schedules the copied rule uses (deploy), or the schedule to copy.</summary>
    public IReadOnlyList<DeviceSchedule>? Schedules { get; init; }

    /// <summary>The recipient to copy, with its passwords.</summary>
    public DeviceRecipient? Recipient { get; init; }

    public RecipientPatch? RecipientPatch { get; init; }

    public SchedulePatch? SchedulePatch { get; init; }

    /// <summary>Schedule delete: the rules that use the schedules, deleted with them (each at the version the page read).</summary>
    public IReadOnlyList<DeviceTargets>? Rules { get; init; }
}

public sealed record EventsRequest(IReadOnlyList<Guid> DeviceIds);

/// <summary>An event and on how many of the asked devices it is offered.</summary>
public sealed record OfferedEvent(DeviceEvent Event, int Devices);

/// <summary>The events of the asked devices (merged by topic); <see cref="Unknown"/> devices have no event list (not read).</summary>
public sealed record EventsReply(IReadOnlyList<OfferedEvent> Events, int Devices, int Unknown);

/// <summary>An event of a new rule: its topic and the chosen values of its items ("port" = "1", "active" = "1").</summary>
public sealed record NewFilter(string Topic, IReadOnlyList<NameValue> Values);

/// <summary>
/// A rule from the rule builder. <see cref="WhileActive"/>: the event is a condition (the rule is active while it is),
/// else the start event. <see cref="Conditions"/>: further events that must be active; <see cref="ScheduleId"/>: only while
/// this schedule is active. <see cref="Parameters"/>: every setting of the action; with <see cref="RecipientName"/> the
/// recipient's settings come from each device's recipient of that name.
/// </summary>
public sealed record NewRule(
    string Name,
    bool Enabled,
    NewFilter Event,
    bool WhileActive,
    IReadOnlyList<NewFilter> Conditions,
    string? ScheduleId,
    int TimeoutSeconds,
    string Template,
    IReadOnlyDictionary<string, string> Parameters)
{
    public string? RecipientName { get; init; }

    /// <summary>A schedule to create where missing (OADM's own id), when <see cref="ScheduleId"/> is not a built-in one.</summary>
    public SchedulePatch? NewSchedule { get; init; }
}

public sealed record BuildRequest(IReadOnlyList<Guid> DeviceIds, NewRule Rule);

/// <summary>A new recipient of the kind <see cref="Template"/> (<c>com.axis.recipient.ftp</c>) with every setting.</summary>
public sealed record NewRecipientRequest(IReadOnlyList<Guid> DeviceIds, string Template, string Name, IReadOnlyDictionary<string, string> Parameters);

/// <summary>A run that can be undone.</summary>
public sealed record UndoRun(Guid RunId, DateTimeOffset CreatedUtc, string Description, string Action, int Devices, bool Undone);

/// <summary>The page's JSON (camelCase, nulls left out).</summary>
public static class ActionRulesJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("The request is empty.", nameof(json));
}
