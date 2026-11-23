using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Tools;

/// <summary>
/// The reads and changes of the Schedules page as tools (docs/spec/schedules.md "MCP", docs/spec/mcp.md "Plugins"): the
/// schedules, one schedule with its estimate and next runs, its runs with their failed devices, the actions with their settings;
/// saving, deleting, turning on and off, Run now and keeping a file for an action, each a preview with the page's checks and a
/// run through the plugin's own code. The rights of the page's methods.
/// </summary>
internal static class SchedulesTools
{
    /// <summary>Failed devices of one run in an answer at most (<c>more</c> says whether there are more).</summary>
    public const int MaxFailures = 50;

    public static PluginPageTools<SchedulesPlugin> Set { get; } = new PluginPageTools<SchedulesPlugin>()
        .Read<SearchArgs, ListAnswer>(
            new("list", "Schedules", "The schedules the caller may see, by name: devices, when, actions, next run, last run with its counts, owner and why one will not run.") { Paged = true },
            SchedulesMethods.List,
            async (plugin, caller, input, ct) => ListAnswer.Of((await plugin.ListAsync(caller, ct).ConfigureAwait(false)).Schedules, input.Args.Search, input.Page))
        .Read<IdArgs, ScheduleAnswer>(
            new("get", "Schedule", "One schedule with its devices, times, actions (settings as save takes them), the errors of each part, the estimate and the next five runs."),
            SchedulesMethods.Get,
            async (plugin, caller, input, ct) =>
            {
                var reply = await plugin.GetAsync(caller, input.Args.Id, ct).ConfigureAwait(false);
                var next = reply.Schedule.On ? ScheduleTimes.Next(reply.Schedule.When, plugin.RunningEngine.Time.GetUtcNow(), plugin.RunningEngine.Zone, 5) : [];
                return ScheduleAnswer.Of(reply, next);
            })
        .Read<RunsArgs, RunsAnswer>(
            new("runs", "Schedule runs", "The runs of a schedule, newest first (the last 50 are kept), with result and counts; with run_id also that run's failed devices (device, step, message).") { Paged = true },
            SchedulesMethods.Runs,
            async (plugin, caller, input, ct) =>
            {
                var runs = await plugin.RunsOfAsync(caller, input.Args.ScheduleId, ct).ConfigureAwait(false);
                IReadOnlyList<RunFailure>? failures = null;
                if (input.Args.RunId is { } runId)
                {
                    failures = (await plugin.FailuresAsync(caller, new RunRequest(input.Args.ScheduleId, runId), ct).ConfigureAwait(false)).Failures;
                }

                return RunsAnswer.Of(runs, failures, input.Page);
            })
        .Read<ActionsArgs, ActionsAnswer>(
            new("actions", "Schedule actions", "The actions a schedule can run with their step names, default settings and estimates, PKI's policies, and VAPIX Commander commands matching command_search with their fields."),
            SchedulesMethods.ActionKinds,
            async (plugin, _, input, ct) => ActionsAnswer.Of(
                plugin.ActionKindInfos(),
                await plugin.PoliciesAsync(ct).ConfigureAwait(false) ?? [],
                input.Args.CommandSearch is { Length: > 0 } search ? plugin.VapixCommands() : [],
                input.Args.CommandSearch))
        .Change<SaveArgs, SavePlan, SavedAnswer>(
            new("save", "Save schedule", "Adds a schedule, or changes the one with id: name, devices, when and the actions in order. The caller becomes its owner: its runs run with the caller's rights and devices."),
            SchedulesMethods.Save,
            async (plugin, caller, input, ct) =>
            {
                var (schedule, check) = await plugin.CheckSaveAsync(caller, input.Args.Schedule(), ct).ConfigureAwait(false);
                return new SavePlan(schedule.Name, check.DeviceCount, check.NextRun, check.Estimate is { } e ? Rollout.Duration(e) : null, check.StageSizes, check.WhenWarning);
            },
            async (plugin, caller, input, _, ct) =>
            {
                var reply = await plugin.SaveAsync(caller, input.Args.Schedule(), ct).ConfigureAwait(false);
                return new SavedAnswer(reply.Schedule.Id, reply.Check.NextRun);
            })
        .Change<IdArgs, NamePlan, DoneAnswer>(
            new("delete", "Delete schedule", "Deletes a schedule with its runs. A run that goes on runs to its end.") { Destructive = true },
            SchedulesMethods.Delete,
            async (plugin, caller, input, ct) => new NamePlan((await plugin.VisibleAsync(caller, input.Args.Id, ct).ConfigureAwait(false)).Schedule.Name),
            async (plugin, caller, input, _, ct) =>
            {
                await plugin.DeleteAsync(caller, input.Args.Id, ct).ConfigureAwait(false);
                return new DoneAnswer("Deleted.");
            })
        .Change<IdArgs, NamePlan, DoneAnswer>(
            new("turn_on", "Turn schedule on", "Turns a schedule on: it runs at its next time (times that passed while it was off do not run)."),
            SchedulesMethods.TurnOn,
            async (plugin, caller, input, ct) =>
            {
                SchedulesPlugin.RequireUser(caller);
                return new NamePlan((await plugin.VisibleAsync(caller, input.Args.Id, ct).ConfigureAwait(false)).Schedule.Name);
            },
            async (plugin, caller, input, _, ct) =>
            {
                await plugin.TurnOnAsync(caller, new TurnOnRequest(input.Args.Id, true), ct).ConfigureAwait(false);
                return new DoneAnswer("Turned on.");
            })
        .Change<IdArgs, NamePlan, DoneAnswer>(
            new("turn_off", "Turn schedule off", "Turns a schedule off: it no longer runs at its times. A run that goes on runs to its end."),
            SchedulesMethods.TurnOn,
            async (plugin, caller, input, ct) => new NamePlan((await plugin.VisibleAsync(caller, input.Args.Id, ct).ConfigureAwait(false)).Schedule.Name),
            async (plugin, caller, input, _, ct) =>
            {
                await plugin.TurnOnAsync(caller, new TurnOnRequest(input.Args.Id, false), ct).ConfigureAwait(false);
                return new DoneAnswer("Turned off.");
            })
        .Change<IdArgs, NamePlan, RunAnswer>(
            new("run_now", "Run schedule now", "Starts a run of the schedule at once, as its owner; refused while a run of it goes on. Follow it with runs."),
            SchedulesMethods.RunNow,
            async (plugin, caller, input, ct) =>
            {
                SchedulesPlugin.RequireUser(caller);
                var stored = await plugin.VisibleAsync(caller, input.Args.Id, ct).ConfigureAwait(false);
                return plugin.RunningEngine.IsRunning(input.Args.Id)
                    ? throw new InvalidOperationException(ScheduleEngine.AlreadyRunning)
                    : new NamePlan(stored.Schedule.Name);
            },
            async (plugin, caller, input, _, ct) =>
            {
                var run = await plugin.RunNowAsync(caller, input.Args.Id, ct).ConfigureAwait(false);
                return new RunAnswer(run.Id, run.Started);
            })
        .Change<FileArgs, FilePlan, FileAnswer>(
            new("file_store", "Keep a schedule file", "Keeps a file the caller uploaded (an AXIS OS file, a configuration file .cfg of Create configuration file or ADM) for an action; the answer is its file_id for the action's settings."),
            SchedulesMethods.StoreFile,
            async (_, caller, input, ct) =>
                await caller.Uploads.FindAsync(input.Args.UploadId?.Trim() ?? string.Empty, ct).ConfigureAwait(false) is { } upload
                    ? new FilePlan(upload.Name)
                    : throw new PluginToolInputException("upload_id", "No uploaded file has this id. Upload it again."),
            async (plugin, caller, input, _, ct) =>
            {
                var file = await plugin.StoreFileAsync(caller, input.Args.UploadId.Trim(), ct).ConfigureAwait(false);
                return new FileAnswer(file.Id, file.Name, file.Size);
            });

    /// <summary>The settings of a tool (JSON text, snake_case) as the page writes them (camelCase); the values of a VAPIX command keep their names.</summary>
    public static JsonElement? Settings(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(settings);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Rename(document.RootElement, toSnake: false)
                : throw new PluginToolInputException("actions.settings", "Give the settings as a JSON object.");
        }
        catch (JsonException)
        {
            throw new PluginToolInputException("actions.settings", "The settings are no valid JSON.");
        }
    }

    /// <summary>Stored settings (camelCase) as the tools show them: snake_case JSON text.</summary>
    public static string? SettingsText(JsonElement? settings) => settings is { } element ? JsonSerializer.Serialize(Rename(element, toSnake: true)) : null;

    private static JsonElement Rename(JsonElement element, bool toSnake)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var name = toSnake ? Snake(property.Name) : Camel(property.Name);
                    map[name] = name == "values" ? property.Value : Rename(property.Value, toSnake);
                }

                return JsonSerializer.SerializeToElement(map);
            case JsonValueKind.Array:
                return JsonSerializer.SerializeToElement(element.EnumerateArray().Select(e => Rename(e, toSnake)).ToList());
            default:
                return element;
        }
    }

    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string Camel(string name)
    {
        var parts = name.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

internal sealed record SearchArgs([property: Description("Only schedules whose name contains this text."), MaxLength(100)] string? Search = null);

internal sealed record IdArgs([property: Description("The schedule id.")] Guid Id);

internal sealed record RunsArgs(
    [property: Description("The schedule id.")] Guid ScheduleId,
    [property: Description("A run of it: its failed devices come too.")] Guid? RunId = null);

internal sealed record ActionsArgs(
    [property: Description("Text in the name, id or category of VAPIX Commander commands to list with their fields (at most 20); leave out for none."), MaxLength(100)] string? CommandSearch = null);

internal sealed record FileArgs([property: Description("The id of a file the caller uploaded."), MaxLength(64)] string UploadId);

internal sealed record DevicesArg(
    [property: Description("Every device of the owner's view, also devices added later.")] bool All = false,
    [property: Description("Chosen devices by OADM device id."), MaxLength(DevicesArg.MaxIds)] IReadOnlyList<Guid>? DeviceIds = null,
    [property: Description("Every device with one of these tags."), MaxLength(100)] IReadOnlyList<string>? Tags = null)
{
    public const int MaxIds = 10_000;
}

internal sealed record WhenArg(
    [property: Description("once, daily, weekly or monthly.")] Recurrence Recurrence,
    [property: Description("The start time in the server's local time, HH:mm (\"23:00\")."), MaxLength(5)] string Time,
    [property: Description("once: the date, yyyy-MM-dd."), MaxLength(10)] string? Date = null,
    [property: Description("weekly: the days (monday ... sunday)."), MaxLength(7)] IReadOnlyList<DayOfWeek>? Days = null,
    [property: Description("monthly: the day, 1 to 28."), Range(1, 28)] int? DayOfMonth = null,
    [property: Description("Start no new device after this time, HH:mm; leave out for no window."), MaxLength(5)] string? WindowEnd = null)
{
    public ScheduleWhen ToWhen() => new()
    {
        Recurrence = Recurrence,
        Time = TimeOf(Time, "when.time") ?? new TimeOnly(23, 0),
        Date = Date is { Length: > 0 } date
            ? DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new PluginToolInputException("when.date", "Enter the date as yyyy-MM-dd.")
            : null,
        Days = Days ?? [],
        DayOfMonth = DayOfMonth,
        WindowEnd = TimeOf(WindowEnd, "when.window_end"),
    };

    private static TimeOnly? TimeOf(string? text, string field) =>
        string.IsNullOrWhiteSpace(text) ? null
        : TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t
        : throw new PluginToolInputException(field, "Enter the time as HH:mm.");
}

internal sealed record ActionArg(
    [property: Description("The action's id in the schedule: letters, digits and hyphens."), MaxLength(32)] string Id,
    [property: Description("upgrade_online, upgrade_files, configuration, restart, certificate, dot1x or vapix_commands.")] NodeKind Kind,
    [property: Description(ActionArg.SettingsHelp), MaxLength(ActionArg.MaxSettingsLength)] string? Settings = null,
    [property: Description("Go on with the next action after an error.")] bool ContinueOnError = true)
{
    public const int MaxSettingsLength = 20_000;

    public const string SettingsHelp =
        "The action's settings as the text of a JSON object (leave out for the defaults). "
        + "upgrade_online: target (ownTrack, latestLts, latestActive), only_if_older_than (e.g. 12.6.95). "
        + "upgrade_files: files [{file_id, name}] (one AXIS OS file per model; never a downgrade). "
        + "configuration: file_id (a .cfg), parameters, action_rules, stream_profiles. restart: none. "
        + "certificate: mode (policy, est), policy_id. dot1x: policy_id. "
        + "vapix_commands: commands [{command_id, values}] of the VAPIX Commander library.";
}

internal sealed record SaveArgs(
    [property: Description("The name, unique."), MaxLength(SchedulesInfo.MaxNameLength)] string Name,
    [property: Description("The devices of every run.")] DevicesArg Devices,
    [property: Description("When it runs.")] WhenArg When,
    [property: Description("The actions in order, 1 to 10."), MaxLength(SchedulesInfo.MaxActions)] IReadOnlyList<ActionArg> Actions,
    [property: Description("The schedule id to change; leave out for a new one.")] Guid? Id = null,
    [property: Description("Turned on: it runs at its times.")] bool On = true,
    [property: Description("Roll out in stages: 1 device, 2 %, 5 %, 10 %, 20 %, the rest; one failure stops the run.")] bool Stages = false)
{
    public Schedule Schedule() => new()
    {
        Id = Id ?? Guid.Empty,
        Name = Name,
        On = On,
        Stages = Stages,
        Devices = new ScheduleDevices
        {
            Kind = Devices?.All == true ? ScheduleDevicesKind.All : ScheduleDevicesKind.Chosen,
            DeviceIds = Devices?.DeviceIds ?? [],
            Tags = Devices?.Tags ?? [],
        },
        When = (When ?? throw new PluginToolInputException("when", "Choose when it runs.")).ToWhen(),
        Actions = [.. (Actions ?? []).Select(a => new FlowNode { Id = a.Id, Kind = a.Kind, ContinueOnError = a.ContinueOnError, Settings = SchedulesTools.Settings(a.Settings) })],
    };
}

internal sealed record SavePlan(
    [property: Description("The schedule name.")] string Name,
    [property: Description("The devices the choice gives now within the caller's view.")] int Devices,
    [property: Description("The next start in server time.")] DateTimeOffset? NextRun,
    [property: Description("About how long a run takes.")] string? Estimated,
    [property: Description("The stage sizes with Roll out in stages.")] IReadOnlyList<int> Stages,
    [property: Description("A run takes longer than the time between two runs (a start during a run is skipped).")] string? Warning) : PluginToolPlan(1, "schedule");

internal sealed record NamePlan([property: Description("The schedule name.")] string Name) : PluginToolPlan(1, "schedule");

internal sealed record FilePlan([property: Description("The file name.")] string Name) : PluginToolPlan(1, "file");

internal sealed record SavedAnswer([property: Description("The schedule id.")] Guid Id, [property: Description("The next start.")] DateTimeOffset? NextRun);

internal sealed record DoneAnswer([property: Description("What happened.")] string Message);

internal sealed record RunAnswer([property: Description("The run id (see runs).")] Guid RunId, DateTimeOffset Started);

internal sealed record FileAnswer([property: Description("The file_id to put into the action's settings.")] Guid FileId, string Name, long Size);

internal sealed record RunRow(
    Guid Id,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    [property: Description("running, done, done_with_warnings, failed, stopped, missed or skipped.")] RunResult Result,
    string? Reason,
    [property: Description("Who pressed Run now; null for a run at its time.")] string? StartedBy,
    int Devices,
    int Done,
    int Warnings,
    int Failed,
    [property: Description("Devices not reachable when their turn came, by status.")] IReadOnlyDictionary<string, int> NotReachable,
    int NotStarted,
    int? Stage,
    int? StageCount)
{
    public static RunRow Of(ScheduleRun r) =>
        new(r.Id, r.Started, r.Ended, r.Result, r.Reason, r.StartedBy, r.Devices, r.Done, r.Warnings, r.Failed, r.NotReachable, r.NotStarted, r.Stage, r.StageCount);
}

internal sealed record FailureRow(Guid DeviceId, string Device, string Step, string Message);

internal sealed record RunsAnswer(
    int Total,
    string? Next,
    IReadOnlyList<RunRow> Runs,
    [property: Description("The failed devices of run_id (at most 50 here).")] IReadOnlyList<FailureRow>? Failures,
    [property: Description("The run has more failed devices than listed.")] bool More)
{
    public static RunsAnswer Of(IReadOnlyList<ScheduleRun> runs, IReadOnlyList<RunFailure>? failures, PluginToolPage page)
    {
        var keyed = runs.Select((r, i) => (Key: i.ToString("D3", CultureInfo.InvariantCulture), Run: r)).Where(r => page.Includes(r.Key)).ToList();
        var (rows, more) = page.Take(keyed.Select(k => RunRow.Of(k.Run)), SchedulesTools.Set.Json);
        return new RunsAnswer(
            runs.Count,
            more ? keyed[rows.Count - 1].Key : null,
            rows,
            failures?.Take(SchedulesTools.MaxFailures).Select(f => new FailureRow(f.DeviceId, f.Device, f.Step, f.Message)).ToList(),
            failures is { Count: > SchedulesTools.MaxFailures });
    }
}

internal sealed record ScheduleRowAnswer(
    Guid Id,
    string Name,
    bool On,
    bool Stages,
    [property: Description("All devices, or the chosen device count and tags.")] string Devices,
    string When,
    [property: Description("The actions in order.")] IReadOnlyList<NodeKind> Actions,
    DateTimeOffset? NextRun,
    RunRow? LastRun,
    string? Owner,
    [property: Description("Why it will not run as it should.")] string? Warning);

internal sealed record ListAnswer(int Total, string? Next, IReadOnlyList<ScheduleRowAnswer> Schedules)
{
    public static ListAnswer Of(IReadOnlyList<ScheduleSummary> all, string? search, PluginToolPage page)
    {
        var matching = all.Where(s => string.IsNullOrWhiteSpace(search) || s.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(s => (Key: s.Name.ToUpperInvariant() + "|" + s.Id.ToString("N"), Summary: s))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();
        var after = matching.Where(m => page.Includes(m.Key)).ToList();
        var (rows, more) = page.Take(after.Select(m => Row(m.Summary)), SchedulesTools.Set.Json);
        return new ListAnswer(matching.Count, more ? after[rows.Count - 1].Key : null, rows);
    }

    private static ScheduleRowAnswer Row(ScheduleSummary s) => new(
        s.Id,
        s.Name,
        s.On,
        s.Stages,
        s.DevicesKind == ScheduleDevicesKind.All ? "All devices"
            : string.Join(", ", s.Tags.Select(t => "Tag " + t).Concat(s.DeviceCount > 0 ? [s.DeviceCount == 1 ? "1 device" : $"{s.DeviceCount} devices"] : [])),
        ScheduleTimes.Describe(s.When),
        s.Actions,
        s.NextRun,
        s.LastRun is { } run ? RunRow.Of(run) : null,
        s.Owner,
        s.Warning);
}

internal sealed record ActionRow(string Id, NodeKind Kind, bool ContinueOnError, [property: Description("The settings as save takes them.")] string? Settings, string? Error, string? Warning);

internal sealed record ScheduleAnswer(
    Guid Id,
    string Name,
    bool On,
    bool Stages,
    bool AllDevices,
    IReadOnlyList<Guid> DeviceIds,
    IReadOnlyList<string> Tags,
    string When,
    IReadOnlyList<ActionRow> Actions,
    string? Owner,
    [property: Description("The errors of the name, devices, times and action list; empty when it can run.")] IReadOnlyList<string> Errors,
    [property: Description("The devices the choice gives now within the caller's view.")] int DeviceCount,
    IReadOnlyList<int> StageSizes,
    [property: Description("About how long a run takes.")] string? Estimated,
    string? WhenWarning,
    [property: Description("The next five starts in server time.")] IReadOnlyList<DateTimeOffset> NextRuns)
{
    public static ScheduleAnswer Of(ScheduleReply reply, IReadOnlyList<DateTimeOffset> next)
    {
        var s = reply.Schedule;
        var c = reply.Check;
        return new ScheduleAnswer(
            s.Id,
            s.Name,
            s.On,
            s.Stages,
            s.Devices.Kind == ScheduleDevicesKind.All,
            s.Devices.DeviceIds ?? [],
            s.Devices.Tags ?? [],
            ScheduleTimes.Describe(s.When),
            [.. (s.Actions ?? []).Select(a => new ActionRow(a.Id, a.Kind, a.ContinueOnError, SchedulesTools.SettingsText(a.Settings), c.ActionErrors.GetValueOrDefault(a.Id), c.ActionWarnings.GetValueOrDefault(a.Id)))],
            s.Owner,
            [.. new[] { c.NameError, c.DevicesError, c.WhenError, c.ActionsError }.OfType<string>()],
            c.DeviceCount,
            c.StageSizes,
            c.Estimate is { } e ? Rollout.Duration(e) : null,
            c.WhenWarning,
            next);
    }
}

internal sealed record ActionKindRow(NodeKind Kind, string Name, string StepName, [property: Description("The default settings as save takes them.")] string? Settings, [property: Description("Minutes per device before a run measured it.")] double Minutes);

internal sealed record PolicyRow(Guid Id, string Name, string Use);

internal sealed record CommandRow(
    [property: Description("The command_id of a vapix_commands action.")] string Id,
    string Name,
    string Category,
    [property: Description("The fields: name, label, type, required.")] IReadOnlyList<CommandFieldRow> Fields);

internal sealed record CommandFieldRow(string Name, string Label, string Type, bool Required);

internal sealed record ActionsAnswer(IReadOnlyList<ActionKindRow> Actions, IReadOnlyList<PolicyRow> Policies, IReadOnlyList<CommandRow> Commands)
{
    public const int MaxCommands = 20;

    public static ActionsAnswer Of(IReadOnlyList<ActionKindInfo> kinds, IReadOnlyList<Sdk.Certificates.IssuingPolicyInfo> policies, IReadOnlyList<ActionCommand> commands, string? search) => new(
        [.. kinds.Select(k => new ActionKindRow(k.Kind, k.Name, k.StepName, SchedulesTools.SettingsText(k.DefaultSettings), k.StartingEstimate.TotalMinutes))],
        [.. policies.Select(p => new PolicyRow(p.Id, p.Name, p.Use == Sdk.Certificates.IssuingPolicyUse.Dot1x ? "802.1X" : "HTTPS"))],
        [.. commands
            .Where(c => search is not null && (c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Id.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxCommands)
            .Select(c => new CommandRow(c.Id, c.Name, c.Category, [.. c.Fields.Select(f => new CommandFieldRow(f.Name, f.Label, f.Type, f.IsRequired && f.Default is null))]))]);
}
