using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules;

/// <summary>
/// The rule builder: the events the devices offered when the page read them, and new rules put on the chosen devices by the
/// same task as a copied rule (schedules first, the action fitted to each device, an identical rule left as it is).
/// </summary>
public sealed partial class ActionRulesPlugin
{
    /// <summary>Most conditions a new rule can have besides its event and schedule.</summary>
    public const int MaxConditions = 5;

    // Devices with the same firmware and apps offer the same events: one list per content, shared by them.
    private readonly ConcurrentDictionary<string, IReadOnlyList<DeviceEvent>> _eventLists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _deviceEvents = new();

    /// <summary>Keeps the event list a device sent with its rules (schedule events are chosen as schedules, not events).</summary>
    private void KeepEvents(Guid deviceId, System.Xml.Linq.XDocument? events)
    {
        IReadOnlyList<DeviceEvent> list;
        try
        {
            list = events is null ? [] : [.. EventDeclarations.Parse(events).Where(e => !IsScheduleTopic(e.Topic))];
        }
        catch (InvalidOperationException)
        {
            list = [];
        }

        if (list.Count == 0)
        {
            _deviceEvents.TryRemove(deviceId, out _);
            return;
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActionRulesJson.Serialize(list))), 0, 12);
        _eventLists.TryAdd(key, list);
        _deviceEvents[deviceId] = key;
    }

    /// <summary>Shared event lists and template sets in memory (tests).</summary>
    internal (int EventLists, int TemplateSets) CacheCounts => (_eventLists.Count, _templateSets.Count);

    /// <summary>
    /// After a sweep: forgets the events of devices no table has a row of, then every event list and template set no device or
    /// row refers to. Skipped while a sweep still reads (its rows are not all there yet).
    /// </summary>
    internal void PruneCaches()
    {
        var sweeps = _sweeps?.All ?? [];
        if (sweeps.Any(s => s.IsRunning))
        {
            return; // the last sweep to finish prunes
        }

        var rows = sweeps.SelectMany(s => s.Snapshot().Rows).ToList();
        var devices = rows.Select(r => r.DeviceId).ToHashSet();
        foreach (var device in _deviceEvents.Keys)
        {
            if (!devices.Contains(device))
            {
                _deviceEvents.TryRemove(device, out _);
            }
        }

        var lists = _deviceEvents.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var key in _eventLists.Keys)
        {
            if (!lists.Contains(key))
            {
                _eventLists.TryRemove(key, out _);
            }
        }

        var templates = rows.Select(r => r.TemplateSetId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var id in _templateSets.Keys)
        {
            if (!templates.Contains(id))
            {
                _templateSets.TryRemove(id, out _);
            }
        }
    }

    private static bool IsScheduleTopic(string topic) => RuleText.Topic(topic).StartsWith("tns1:UserAlarm/tnsaxis:Recurring", StringComparison.Ordinal);

    /// <summary>The events of the devices merged by topic, with the values every device lists for each item.</summary>
    internal EventsReply Events(EventsRequest request)
    {
        var devices = (request.DeviceIds ?? []).Distinct().ToList();
        // Devices share their lists: each distinct list is merged once and counts for every device that has it.
        var lists = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = 0;
        foreach (var device in devices)
        {
            if (_deviceEvents.TryGetValue(device, out var key) && _eventLists.ContainsKey(key))
            {
                lists[key] = lists.GetValueOrDefault(key) + 1;
            }
            else
            {
                unknown++;
            }
        }

        var merged = new Dictionary<string, (DeviceEvent Event, int Devices, Dictionary<string, Dictionary<string, EventValue>> Values)>(StringComparer.Ordinal);
        foreach (var (key, count) in lists)
        {
            foreach (var e in _eventLists[key])
            {
                if (!merged.TryGetValue(e.Topic, out var entry))
                {
                    entry = (e, 0, new Dictionary<string, Dictionary<string, EventValue>>(StringComparer.Ordinal));
                }

                foreach (var item in e.Sources.Concat(e.Data))
                {
                    if (!entry.Values.TryGetValue(item.Name, out var values))
                    {
                        entry.Values[item.Name] = values = new Dictionary<string, EventValue>(StringComparer.Ordinal);
                    }

                    foreach (var v in item.Values)
                    {
                        values.TryAdd(v.Value, v);
                    }
                }

                merged[e.Topic] = (entry.Event, entry.Devices + count, entry.Values);
            }
        }

        var events = merged.Values
            .Select(m => new OfferedEvent(m.Event with
            {
                Sources = [.. m.Event.Sources.Select(i => Merge(i, m.Values))],
                Data = [.. m.Event.Data.Select(i => Merge(i, m.Values))],
            }, m.Devices))
            .OrderBy(o => o.Event.Group, StringComparer.CurrentCulture)
            .ThenBy(o => o.Event.Title, StringComparer.CurrentCulture)
            .ToList();
        return new EventsReply(events, devices.Count, unknown);
    }

    private static EventItem Merge(EventItem item, Dictionary<string, Dictionary<string, EventValue>> values) =>
        item with
        {
            Values = [.. values[item.Name].Values
                .OrderBy(v => int.TryParse(v.Value, out var n) ? n : int.MaxValue)
                .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)],
        };

    /// <summary>A new rule from the rule builder, checked whole before any task starts.</summary>
    private async Task<RunStarted> BuildAsync(ICallContext caller, BuildRequest request, CancellationToken ct)
    {
        var devices = DeviceIds(request.DeviceIds);
        await CallDevices.RequireAsync(caller, devices, ct).ConfigureAwait(false);
        var rule = Normalize(request.Rule ?? throw new ArgumentException("Describe the rule.", nameof(request)));
        if (BuildProblem(rule) is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        // The devices refuse a filter on an event that is on or off without its state ("property state key's value not
        // specified"): the state "active" is added from the event list, and only such events can be conditions.
        var known = KnownEvents(devices);
        foreach (var condition in (rule.WhileActive ? [rule.Event] : Array.Empty<NewFilter>()).Concat(rule.Conditions))
        {
            if (known.TryGetValue(RuleText.Topic(condition.Topic), out var e) && !e.Stateful)
            {
                throw new ArgumentException($"\"{e.Title}\" is not on or off, so it cannot be a condition.", nameof(request));
            }
        }

        var main = RuleText.Filter(rule.Event.Topic, WithState(rule.Event, known));
        var conditions = rule.Conditions.Select(c => RuleText.Filter(c.Topic, WithState(c, known))).ToList();
        if (rule.WhileActive)
        {
            conditions.Insert(0, main);
        }

        if (rule.ScheduleId is { } scheduleId)
        {
            conditions.Add(RuleText.ScheduleCondition(scheduleId));
        }

        List<DeviceSchedule> schedules = [];
        if (rule.NewSchedule is { Name: { } scheduleName, Definition: { } definition })
        {
            schedules.Add(new DeviceSchedule(rule.ScheduleId!, scheduleName.Trim(), ScheduleText.Normalize(definition)));
        }

        var device = new DeviceRule(string.Empty, rule.Name.Trim(), rule.Enabled, rule.WhileActive ? null : main, conditions, null, new RuleAction(rule.Template, []));
        var patch = new RulePatch
        {
            TimeoutSeconds = rule.WhileActive || rule.TimeoutSeconds == 0 ? null : rule.TimeoutSeconds,
            Parameters = rule.Parameters,
            RecipientName = string.IsNullOrWhiteSpace(rule.RecipientName) ? null : rule.RecipientName,
        };
        var payload = new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), Shorten("Create action rule " + rule.Name.Trim()), [], patch)
        {
            Rule = device,
            Schedules = schedules,
        };
        return await StartAsync(caller, payload, devices, ct).ConfigureAwait(false);
    }

    /// <summary>The events the devices offered, by topic.</summary>
    private Dictionary<string, DeviceEvent> KnownEvents(IEnumerable<Guid> devices) =>
        devices.Select(d => _deviceEvents.TryGetValue(d, out var key) ? key : null).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(k => _eventLists.TryGetValue(k, out var list) ? list : null)
            .OfType<IReadOnlyList<DeviceEvent>>().SelectMany(l => l).DistinctBy(e => e.Topic).ToDictionary(e => e.Topic, StringComparer.Ordinal);

    /// <summary>
    /// Checks the event, schedule and recipient parts of an edit or copy and builds the event filter for the devices (the
    /// state "active" added like for a new rule). An <see cref="RulePatch.Event"/> sent by the page is never used.
    /// </summary>
    internal RulePatch PreparePatch(RulePatch patch, IEnumerable<Guid> devices)
    {
        ArgumentNullException.ThrowIfNull(patch);
        EventFilter? trigger = null;
        if (patch.NewEvent is { } chosen)
        {
            chosen = chosen with { Values = chosen.Values ?? [] };
            if (FilterProblem(chosen) is { } problem)
            {
                throw new ArgumentException(problem, nameof(patch));
            }

            trigger = RuleText.Filter(chosen.Topic, WithState(chosen, KnownEvents(devices)));
        }

        if (patch.ScheduleId is not null && ScheduleChoiceProblem(patch.ScheduleId.Length == 0 ? null : patch.ScheduleId, patch.NewSchedule) is { } schedule)
        {
            throw new ArgumentException(schedule, nameof(patch));
        }

        if (patch.ScheduleId is null && patch.NewSchedule is not null)
        {
            throw new ArgumentException("Choose the schedule.", nameof(patch));
        }

        if (patch.RecipientName is { } recipient && recipient.Trim().Length is 0 or > 256)
        {
            throw new ArgumentException("Choose the recipient.", nameof(patch));
        }

        return patch with { NewEvent = null, Event = trigger };
    }

    /// <summary>The filter's values with the state "active" of an event that is on or off.</summary>
    private static IReadOnlyList<NameValue> WithState(NewFilter filter, Dictionary<string, DeviceEvent> known)
    {
        if (!known.TryGetValue(RuleText.Topic(filter.Topic), out var e) || !e.Stateful || e.StateItem is not { } state || filter.Values.Any(v => v.Name == state))
        {
            return filter.Values;
        }

        var listed = e.Data.Concat(e.Sources).FirstOrDefault(i => i.Name == state)?.Values.Select(v => v.Value).ToList() ?? [];
        var active = listed.Contains("1") || !listed.Contains("true", StringComparer.OrdinalIgnoreCase) ? "1" : listed.First(v => v.Equals("true", StringComparison.OrdinalIgnoreCase));
        return [.. filter.Values, new NameValue(state, active)];
    }

    /// <summary>Missing lists of a request read as empty ones.</summary>
    private static NewRule Normalize(NewRule rule) => rule with
    {
        Event = rule.Event is null ? null! : rule.Event with { Values = rule.Event.Values ?? [] },
        Conditions = [.. (rule.Conditions ?? []).Select(c => c is null ? null! : c with { Values = c.Values ?? [] })],
        Parameters = rule.Parameters ?? new Dictionary<string, string>(),
    };

    /// <summary>What is wrong with a new rule, or null.</summary>
    internal string? BuildProblem(NewRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > RulePatchApply.MaxNameLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters.");
        }

        if (rule.Event is null || FilterProblem(rule.Event) is not null)
        {
            return rule.Event is null ? "Choose the event." : FilterProblem(rule.Event);
        }

        if ((rule.Conditions ?? []).Count > MaxConditions)
        {
            return string.Create(CultureInfo.InvariantCulture, $"A rule can have at most {MaxConditions} conditions.");
        }

        if ((rule.Conditions ?? []).Select(FilterProblem).FirstOrDefault(p => p is not null) is { } condition)
        {
            return condition;
        }

        if (rule.TimeoutSeconds is < 0 or > RulePatchApply.MaxTimeoutSeconds)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Enter 0 to {RulePatchApply.MaxTimeoutSeconds:N0} seconds.");
        }

        if (string.IsNullOrWhiteSpace(rule.Template) || rule.Template.Length > 256)
        {
            return "Choose the action.";
        }

        var template = _templateSets.Values.Select(s => s.Action(rule.Template)).FirstOrDefault(a => a is not null);
        if (!_templateSets.IsEmpty && template is null)
        {
            return "No device offers this action.";
        }

        if (ActionNames.IsRefused(rule.Template))
        {
            return ActionNames.RefusedText;
        }

        if (template is { IsWhileActive: true } && !rule.WhileActive)
        {
            return "Actions that run while the rule is active need \"While the event is active\".";
        }

        if (rule.Parameters is null || rule.Parameters.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Length > RulePatchApply.MaxValueLength))
        {
            return "A setting of the action is too long.";
        }

        if (UnknownSetting(rule.Parameters) is { } unknown)
        {
            return unknown;
        }

        if (rule.RecipientName is { Length: > 256 })
        {
            return "Choose the recipient.";
        }

        return ScheduleChoiceProblem(rule.ScheduleId, rule.NewSchedule);
    }

    /// <summary>A schedule id (null = always) and the schedule to create where missing: built-in ids or OADM's own ones.</summary>
    private static string? ScheduleChoiceProblem(string? id, SchedulePatch? created)
    {
        if (id is null)
        {
            return created is null ? null : "Choose the schedule.";
        }

        if (!RuleText.IsFilterText(id))
        {
            return "Choose the schedule.";
        }

        if (created is null)
        {
            // Without a definition only a schedule every device has: a built-in one.
            return new DeviceSchedule(id, string.Empty, string.Empty).IsBuiltIn ? null : "Choose the schedule.";
        }

        if (string.IsNullOrWhiteSpace(created.Name) || created.Definition is null)
        {
            return "Enter the schedule.";
        }

        if (ScheduleProblem(created) is { } problem)
        {
            return problem;
        }

        // OADM's own id (also one renamed since, or "-2" when the name's id was taken): created where missing.
        return id.StartsWith(ScheduleText.OwnIdPrefix, StringComparison.Ordinal) ? null : "Choose the schedule.";
    }

    private static string? FilterProblem(NewFilter filter)
    {
        if (filter is null || string.IsNullOrWhiteSpace(filter.Topic) || filter.Topic.Length > 512 || filter.Topic.Any(c => c is '"' or '<' or '>' or '&' || char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "Choose the event.";
        }

        if (IsScheduleTopic(filter.Topic))
        {
            return "Choose a schedule as the schedule, not as an event.";
        }

        var values = filter.Values ?? [];
        return values.Count > 8 || values.Any(v => !RuleText.IsFilterText(v.Name) || !RuleText.IsFilterText(v.Value))
            || values.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != values.Count
            ? "A value of the event cannot be used."
            : null;
    }
}
