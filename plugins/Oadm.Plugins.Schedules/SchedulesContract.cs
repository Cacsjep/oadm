using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Schedules;

/// <summary>Ids and names shared by the server part, the client part and the tests (docs/spec/schedules.md).</summary>
public static class SchedulesInfo
{
    public const string PluginId = "oadm.schedules";
    public const string DisplayName = "Schedules";
    public const string IconKey = "calendarClock";

    /// <summary>The task "Run &lt;schedule&gt;" (one per device and run; hidden from the menus, only Schedules starts it).</summary>
    public const string TaskPluginId = "oadm.schedules.run";

    /// <summary>The event of the rules: one per run (docs/spec/schedules.md "Event for rules").</summary>
    public const string RunEndedEvent = "oadm.schedules.run-ended";

    /// <summary>At most this many schedules per server.</summary>
    public const int MaxSchedules = 200;

    /// <summary>At most this many actions per schedule.</summary>
    public const int MaxActions = 10;

    /// <summary>The longest schedule name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Run rows kept per schedule.</summary>
    public const int KeptRuns = 50;

    /// <summary>Failed devices kept per run.</summary>
    public const int MaxFailuresPerRun = 1_000;
}

/// <summary>Backend methods (<c>InvokeAsync</c>) of the client part and live event topics.</summary>
public static class SchedulesMethods
{
    /// <summary>() -> <see cref="SchedulesReply"/>: the schedules the caller may see, with next and last run.</summary>
    public const string List = "list";

    /// <summary><see cref="IdRequest"/> -> <see cref="ScheduleReply"/>.</summary>
    public const string Get = "get";

    /// <summary><see cref="ScheduleRequest"/> -> <see cref="ScheduleCheck"/>: the wizard's check, the same as on save (never writes).</summary>
    public const string Check = "check";

    /// <summary>() -> the action kinds (<see cref="ActionKindInfo"/>) with their default settings, for the wizard's step Then.</summary>
    public const string ActionKinds = "actionKinds";

    /// <summary><see cref="ScheduleRequest"/> -> <see cref="ScheduleReply"/>: refused with the first error; the caller becomes the owner.</summary>
    public const string Save = "save";

    /// <summary><see cref="IdRequest"/> -> <see cref="SchedulesReply"/>. A running run of it runs to its end.</summary>
    public const string Delete = "delete";

    /// <summary><see cref="TurnOnRequest"/> -> <see cref="ScheduleSummary"/>: turns a schedule on or off.</summary>
    public const string TurnOn = "turnOn";

    /// <summary><see cref="IdRequest"/> -> <see cref="ScheduleRun"/>: a run now, as the owner; refused while one runs.</summary>
    public const string RunNow = "runNow";

    /// <summary><see cref="IdRequest"/> (the schedule) -> <see cref="RunsReply"/>: its runs, newest first (the last 50).</summary>
    public const string Runs = "runs";

    /// <summary><see cref="RunRequest"/> -> <see cref="RunFailuresReply"/>: the failed devices of one run (at most 1,000).</summary>
    public const string RunFailures = "runFailures";

    /// <summary>
    /// <see cref="DeleteRunsRequest"/> -> <see cref="RunsReply"/>: deletes run rows of one schedule (the chosen ones, or with
    /// <see cref="DeleteRunsRequest.All"/> every one); a run that still runs is never deleted. Needs "manage".
    /// </summary>
    public const string DeleteRuns = "deleteRuns";

    /// <summary>
    /// () -> PKI's HTTPS and IEEE 802.1X certificate policies (<see cref="Sdk.Certificates.IssuingPolicyInfo"/>), empty when PKI
    /// does not run. The same method and reply as Provisioning's, for the node editor.
    /// </summary>
    public const string Policies = "policies";

    /// <summary>
    /// () -> the VAPIX Commander commands the action VAPIX commands can run (<see cref="ActionCommand"/>, the JSON of
    /// Provisioning's <c>ProfileCommand</c>): no dangerous ones, none with a password or a file.
    /// </summary>
    public const string VapixCommands = "vapixCommands";

    /// <summary>
    /// <see cref="FileRequest"/> -> <see cref="Provisioning.Engine.StoredFile"/>: a file the caller uploaded (an AXIS OS file, a
    /// configuration file), kept for an action. The same method and reply as Provisioning's.
    /// </summary>
    public const string StoreFile = "storeFile";

    /// <summary>
    /// <see cref="IdRequest"/> (a kept file's id) -> <see cref="ConfigFileReply"/>: the text of a configuration file (<c>.cfg</c>,
    /// at most 1 MB, passwords and secrets masked) for the file preview of the action Apply configuration.
    /// </summary>
    public const string ReadConfigFile = "readConfigFile";

    /// <summary>Event: <see cref="ChangedEvent"/> after a change of a schedule or a run (to users with the "show" right).</summary>
    public const string ChangedTopic = "changed";

    /// <summary>The methods that only read.</summary>
    public static bool IsRead(string method) =>
        method is List or Get or Check or ActionKinds or Runs or RunFailures or Policies or VapixCommands or ReadConfigFile;
}

/// <summary>How often a schedule runs.</summary>
public enum Recurrence
{
    /// <summary>Once, on <see cref="ScheduleWhen.Date"/>.</summary>
    Once = 0,

    Daily = 1,

    /// <summary>On the days of <see cref="ScheduleWhen.Days"/>.</summary>
    Weekly = 2,

    /// <summary>On <see cref="ScheduleWhen.DayOfMonth"/> (1 to 28).</summary>
    Monthly = 3,
}

/// <summary>
/// When a schedule runs, in server time: the recurrence, the time and, optionally, the window end (no device task starts after
/// it; queued ones are cancelled).
/// </summary>
public sealed record ScheduleWhen
{
    public Recurrence Recurrence { get; init; } = Recurrence.Weekly;

    /// <summary>The day of a <see cref="Recurrence.Once"/> run.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>The start time.</summary>
    public TimeOnly Time { get; init; } = new(23, 0);

    /// <summary>The days of a <see cref="Recurrence.Weekly"/> run (at least one).</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>The day of a <see cref="Recurrence.Monthly"/> run (1 to 28).</summary>
    public int? DayOfMonth { get; init; }

    /// <summary>Start no new devices after this time (the next time it comes after the start); null: no window.</summary>
    public TimeOnly? WindowEnd { get; init; }
}

/// <summary>What the device choice of a schedule means (the shared device picker without the device of the event).</summary>
public enum ScheduleDevicesKind
{
    /// <summary>The devices of <see cref="ScheduleDevices.DeviceIds"/> and every device with one of <see cref="ScheduleDevices.Tags"/>.</summary>
    Chosen = 0,

    /// <summary>Every device of the owner's view, also devices added later.</summary>
    All = 1,
}

/// <summary>The devices of a schedule, worked out at the start of every run within the owner's view.</summary>
public sealed record ScheduleDevices
{
    public ScheduleDevicesKind Kind { get; init; }

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A schedule: devices, a time and a straight list of actions (Provisioning's nodes).</summary>
public sealed record Schedule
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>Turned on: it runs at its times.</summary>
    public bool On { get; init; } = true;

    /// <summary>Roll out in stages: 1 device, 2 %, 5 %, 10 %, 20 %, the rest; one failure stops the run.</summary>
    public bool Stages { get; init; }

    public ScheduleDevices Devices { get; init; } = new();

    public ScheduleWhen When { get; init; } = new();

    /// <summary>The actions in order (node kinds of <see cref="ActionKinds"/> only).</summary>
    public IReadOnlyList<FlowNode> Actions { get; init; } = [];

    /// <summary>The user who last saved it (set by the server; the runs run as this user).</summary>
    public string? Owner { get; init; }

    /// <summary>When it was last saved (set by the server).</summary>
    public DateTimeOffset? Saved { get; init; }
}

/// <summary>The node kinds a schedule may use (docs/spec/schedules.md "Actions").</summary>
public static class ActionKinds
{
    public static IReadOnlyList<NodeKind> All { get; } =
    [
        NodeKind.UpgradeOnline,
        NodeKind.UpgradeFiles,
        NodeKind.Configuration,
        NodeKind.Restart,
        NodeKind.Certificate,
        NodeKind.Dot1x,
        NodeKind.VapixCommands,
    ];

    public static bool IsAction(NodeKind kind) => All.Contains(kind);
}

/// <summary>A kind of action for the wizard's step Then: its name, the step name of its tasks and its default settings.</summary>
public sealed record ActionKindInfo(NodeKind Kind, string Name, string StepName, JsonElement DefaultSettings, TimeSpan StartingEstimate);

/// <summary>How a run ended (or that it runs).</summary>
public enum RunResult
{
    Running = 0,

    Done = 1,

    DoneWithWarnings = 2,

    /// <summary>Without stages: at least one device failed. Also: nothing ran (the owner lost a right; <see cref="ScheduleRun.Reason"/>).</summary>
    Failed = 3,

    /// <summary>Roll out in stages stopped at a failure.</summary>
    Stopped = 4,

    /// <summary>The server was off at the time and the window had closed.</summary>
    Missed = 5,

    /// <summary>The previous run still ran.</summary>
    Skipped = 6,
}

/// <summary>One run of a schedule with its counts.</summary>
public sealed record ScheduleRun
{
    public Guid Id { get; init; }

    public Guid ScheduleId { get; init; }

    /// <summary>The time it was due (Missed, Skipped) or started.</summary>
    public DateTimeOffset Started { get; init; }

    public DateTimeOffset? Ended { get; init; }

    public RunResult Result { get; init; }

    /// <summary>Why nothing ran or why it stopped ("The owner no longer has the right Upgrade AXIS OS.", "Stopped at stage 3: 1 failed").</summary>
    public string? Reason { get; init; }

    /// <summary>The user who pressed Run now; null for a run at its time.</summary>
    public string? StartedBy { get; init; }

    /// <summary>The devices of the run.</summary>
    public int Devices { get; init; }

    public int Done { get; init; }

    public int Warnings { get; init; }

    public int Failed { get; init; }

    /// <summary>Devices that were not Ok when their turn came, by their status text ("Offline": 12).</summary>
    public IReadOnlyDictionary<string, int> NotReachable { get; init; } = new Dictionary<string, int>();

    /// <summary>Devices that never got a task (stopped, window end, cancelled).</summary>
    public int NotStarted { get; init; }

    /// <summary>With stages: the stage running or reached (1-based) and how many there are.</summary>
    public int? Stage { get; init; }

    public int? StageCount { get; init; }

    /// <summary>
    /// While it runs: what it does now, "Stage 2 of 4: Install the certificate" (with stages) or "Install the certificate" (the
    /// step most running devices are on); null once it ended or before a device started.
    /// </summary>
    public string? CurrentStep { get; init; }

    /// <summary>While it runs: the devices whose task ended (done, warning, failed, not reachable, not started) of <see cref="Devices"/>, 0 to 100; null once it ended.</summary>
    public int? Progress { get; init; }
}

/// <summary>One failed device of a run.</summary>
public sealed record RunFailure(Guid DeviceId, string Device, string Step, string Message);

/// <summary>A schedule in the page's list: no device ids (the count only), the next and the last run.</summary>
public sealed record ScheduleSummary
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool On { get; init; }

    public bool Stages { get; init; }

    public ScheduleDevicesKind DevicesKind { get; init; }

    /// <summary>The chosen devices (ids not sent).</summary>
    public int DeviceCount { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public ScheduleWhen When { get; init; } = new();

    /// <summary>The action kinds in order.</summary>
    public IReadOnlyList<NodeKind> Actions { get; init; } = [];

    public string? Owner { get; init; }

    /// <summary>The next start in server time; null when it will not run again (off, a past Once).</summary>
    public DateTimeOffset? NextRun { get; init; }

    public ScheduleRun? LastRun { get; init; }

    /// <summary>Why it will not run as it should, until fixed ("Sign in to MyAxis.", "The owner no longer has ..."); null: fine.</summary>
    public string? Warning { get; init; }
}

public sealed record SchedulesReply(IReadOnlyList<ScheduleSummary> Schedules);

/// <summary>A schedule with its check.</summary>
public sealed record ScheduleReply(Schedule Schedule, ScheduleCheck Check);

/// <summary>
/// A schedule to check or save. <paramref name="DevicesUnchanged"/> (the wizard, after the devices were checked once): Check
/// skips resolving and checking the devices and takes <paramref name="KnownDeviceCount"/> for the stages and the estimate. Save
/// ignores both and always resolves the devices.
/// </summary>
public sealed record ScheduleRequest(Schedule Schedule, bool DevicesUnchanged = false, int? KnownDeviceCount = null);

public sealed record IdRequest(Guid Id);

public sealed record TurnOnRequest(Guid Id, bool On);

public sealed record RunRequest(Guid ScheduleId, Guid RunId);

/// <summary>Run rows of one schedule to delete: <paramref name="RunIds"/>, or every ended one with <paramref name="All"/>.</summary>
public sealed record DeleteRunsRequest(Guid ScheduleId, IReadOnlyList<Guid> RunIds, bool All = false);

public sealed record RunsReply(IReadOnlyList<ScheduleRun> Runs);

public sealed record RunFailuresReply(IReadOnlyList<RunFailure> Failures);

/// <summary>A schedule or its runs changed: the client reads the list (and the selected schedule's runs) again.</summary>
public sealed record ChangedEvent(Guid? ScheduleId);

/// <summary>A file of the caller's uploads to keep for an action (the request of Provisioning's <c>storeFile</c>).</summary>
public sealed record FileRequest(string UploadId);

/// <summary>A kept configuration file: its name and its text (secrets masked).</summary>
public sealed record ConfigFileReply(string Name, string Content);

/// <summary>A command of the VAPIX Commander library for the action VAPIX commands: its id, name, category and fields.</summary>
public sealed record ActionCommand(string Id, string Name, string Category, string? Description, IReadOnlyList<VapixCommander.CommandField> Fields);

/// <summary>
/// The check of a schedule (the wizard's steps, the same on save): one error per part, the actions' errors by node id, the
/// warnings and the Overview's facts. Errors are texts for directly below their field.
/// </summary>
public sealed record ScheduleCheck
{
    /// <summary>Step Schedule.</summary>
    public string? NameError { get; init; }

    /// <summary>Step Devices ("Choose at least one device.").</summary>
    public string? DevicesError { get; init; }

    /// <summary>Step When: the date, days, day of month or window.</summary>
    public string? WhenError { get; init; }

    /// <summary>Step Then: the list itself ("Add at least one action.", the caller lacks an action's right).</summary>
    public string? ActionsError { get; init; }

    /// <summary>The one error of each action's settings, by node id.</summary>
    public IReadOnlyDictionary<string, string> ActionErrors { get; init; } = new Dictionary<string, string>();

    /// <summary>A warning of an action's settings (it still works), by node id.</summary>
    public IReadOnlyDictionary<string, string> ActionWarnings { get; init; } = new Dictionary<string, string>();

    /// <summary>Below When: a run takes longer than the time between two runs (Save stays possible).</summary>
    public string? WhenWarning { get; init; }

    /// <summary>The devices the choice gives now within the caller's view.</summary>
    public int DeviceCount { get; init; }

    /// <summary>The stage sizes (with Roll out in stages; empty without).</summary>
    public IReadOnlyList<int> StageSizes { get; init; } = [];

    /// <summary>About how long a run takes; null without devices or actions.</summary>
    public TimeSpan? Estimate { get; init; }

    /// <summary>The next start in server time; null when none.</summary>
    public DateTimeOffset? NextRun { get; init; }

    public bool IsValid => NameError is null && DevicesError is null && WhenError is null && ActionsError is null && ActionErrors.Count == 0;
}

/// <summary>The JSON options of the plugin's settings, page calls and events.</summary>
public static class SchedulesJson
{
    public static readonly JsonSerializerOptions Options = Sdk.Plugins.PluginJson.CreateOptions(Sdk.Plugins.PluginJsonEnums.CamelCase);
}
