using System.Globalization;

using Oadm.Plugins.ActionRules.Device;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>Where a rule's trigger sits: the start event (actions once per start) or a condition (while active).</summary>
public static class TriggerKinds
{
    /// <summary>The event starts the rule; "once" actions.</summary>
    public const string Start = "start";

    /// <summary>The rule is active while the event is; "while the rule is active" actions need it.</summary>
    public const string Condition = "condition";
}

/// <summary>
/// The parts of a rule a mass edit changes; everything left null stays as each device has it. <see cref="Parameters"/>
/// holds only the checked fields; other parameters keep each device's own value (e.g. a message with a camera's number).
/// </summary>
public sealed record RulePatch
{
    public string? Name { get; init; }

    public bool? Enabled { get; init; }

    /// <summary><see cref="TriggerKinds.Start"/> or <see cref="TriggerKinds.Condition"/>: moves the trigger.</summary>
    public string? TriggerKind { get; init; }

    /// <summary>Seconds the rule waits before it can run again; 0 = none.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>A new action template (e.g. the "while the rule is active" one).</summary>
    public string? Template { get; init; }

    /// <summary>Parameter values to set on every rule (name -> value). Secrets only when entered.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>The page's new event of the rules (topic and source values); the server turns it into <see cref="Event"/>.</summary>
    public NewFilter? NewEvent { get; init; }

    /// <summary>
    /// The event that replaces each rule's trigger (the start event, else its first condition that is not a schedule), as the
    /// server built it from <see cref="NewEvent"/> (with the state "active" of an event that is on or off). Set by the server
    /// only.
    /// </summary>
    public EventFilter? Event { get; init; }

    /// <summary>The schedule of the rules: empty = always (no schedule), else the schedule's id; null = keep.</summary>
    public string? ScheduleId { get; init; }

    /// <summary>The schedule to add to the devices that lack it (OADM's own id <see cref="ScheduleId"/>).</summary>
    public SchedulePatch? NewSchedule { get; init; }

    /// <summary>The recipient whose settings the action takes, by name; each device uses its own one (with its password).</summary>
    public string? RecipientName { get; init; }

    public bool IsEmpty => Name is null && Enabled is null && TriggerKind is null && TimeoutSeconds is null && Template is null
        && (Parameters is null || Parameters.Count == 0) && !ChangesTrigger && RecipientName is null;

    /// <summary>The event or the schedule changes.</summary>
    public bool ChangesTrigger => NewEvent is not null || Event is not null || ScheduleId is not null;

    /// <summary>Only on and off: the action and its passwords are never touched.</summary>
    public bool OnlyEnabled => Enabled is not null && Name is null && TriggerKind is null && TimeoutSeconds is null && Template is null
        && (Parameters is null || Parameters.Count == 0) && !ChangesTrigger && RecipientName is null;

    /// <summary>What is wrong with the patch itself (before any device), or null.</summary>
    public string? Problem()
    {
        if (IsEmpty)
        {
            return "Choose at least one thing to change.";
        }

        if (Name is not null && (Name.Trim().Length == 0 || Name.Length > RulePatchApply.MaxNameLength))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters.");
        }

        if (TriggerKind is not (null or TriggerKinds.Start or TriggerKinds.Condition))
        {
            return "Unknown trigger.";
        }

        if (TimeoutSeconds is < 0 or > RulePatchApply.MaxTimeoutSeconds)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Enter 0 to {RulePatchApply.MaxTimeoutSeconds:N0} seconds.");
        }

        if (Template is not null && !Template.StartsWith("com.axis.action.", StringComparison.Ordinal))
        {
            return "Unknown action.";
        }

        if (ActionNames.IsRefused(Template))
        {
            return ActionNames.RefusedText;
        }

        if (Parameters is not null && Parameters.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Length > RulePatchApply.MaxValueLength))
        {
            return "A value is too long.";
        }

        return null;
    }
}

/// <summary>Turns a device's rule and a <see cref="RulePatch"/> into the rule to write, against that device's templates.</summary>
public static class RulePatchApply
{
    public const int MaxNameLength = 128;
    public const int MaxTimeoutSeconds = 86_400;
    public const int MaxValueLength = 8_192;

    /// <summary>The changed rule; throws <see cref="ActionEngineException"/> ("Nothing was changed") when the device cannot take it.</summary>
    public static DeviceRule Apply(DeviceRule rule, RulePatch patch, DeviceRuleSet device)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(device);
        var result = rule;
        if (patch.Name is { } name)
        {
            result = result with { Name = name.Trim() };
        }

        if (patch.Enabled is { } enabled)
        {
            result = result with { Enabled = enabled };
        }

        if (patch.Event is { } trigger)
        {
            result = ReplaceTrigger(result, trigger);
        }

        if (patch.ScheduleId is { } schedule)
        {
            var conditions = result.Conditions.Where(c => RuleText.ScheduleId(c) is null).ToList();
            if (schedule.Length > 0)
            {
                conditions.Add(RuleText.ScheduleCondition(schedule));
            }

            result = result with { Conditions = conditions };
        }

        if (patch.TriggerKind is { } kind)
        {
            result = MoveTrigger(result, kind);
        }

        if (patch.TimeoutSeconds is { } seconds)
        {
            result = result with { Timeout = RuleText.Timeout(seconds) };
        }

        if (patch.Template is not null || patch.Parameters is { Count: > 0 })
        {
            result = result with { Action = ApplyAction(rule.Action, patch, device) };
        }

        return result;
    }

    /// <summary>The rule with another event: in place of the start event, else of the first condition that is not a schedule.</summary>
    public static DeviceRule ReplaceTrigger(DeviceRule rule, EventFilter trigger)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(trigger);
        if (rule.StartEvent is not null)
        {
            return rule with { StartEvent = trigger };
        }

        var conditions = rule.Conditions.ToList();
        var index = conditions.FindIndex(c => RuleText.ScheduleId(c) is null);
        if (index < 0)
        {
            conditions.Insert(0, trigger);
        }
        else
        {
            conditions[index] = trigger;
        }

        return rule with { Conditions = conditions };
    }

    /// <summary>Moves the trigger between start event and conditions; a schedule condition never becomes the start event.</summary>
    public static DeviceRule MoveTrigger(DeviceRule rule, string kind)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (kind == TriggerKinds.Condition)
        {
            return rule.StartEvent is { } start
                ? rule with { StartEvent = null, Conditions = [start, .. rule.Conditions] }
                : rule;
        }

        if (rule.StartEvent is not null)
        {
            return rule;
        }

        var index = rule.Conditions.ToList().FindIndex(c => RuleText.ScheduleId(c) is null);
        if (index < 0)
        {
            throw new ActionEngineException("The rule has no event that can start it. Nothing was changed.");
        }

        var conditions = rule.Conditions.ToList();
        var trigger = conditions[index];
        conditions.RemoveAt(index);
        return rule with { StartEvent = trigger, Conditions = conditions };
    }

    /// <summary>
    /// The new action: every parameter the (new) template and its recipient have on this device, each from the patch, else
    /// the rule's own value; one without a value is refused.
    /// </summary>
    public static RuleAction ApplyAction(RuleAction action, RulePatch patch, DeviceRuleSet device)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(device);
        var token = patch.Template ?? action.Template;
        var template = device.Template(token)
            ?? throw new ActionEngineException("This device does not offer this action. Nothing was changed.");
        var names = template.Parameters.Concat(device.RecipientParameters(token)).Distinct(StringComparer.Ordinal).ToList();
        // A setting this device's action lacks is left out (AXIS OS 10 has no overlay index): the page offers the settings of
        // every target device, and the server refuses names no device knows before any task starts.
        var values = (patch.Parameters ?? new Dictionary<string, string>()).Where(v => names.Contains(v.Key, StringComparer.Ordinal))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

        var parameters = new List<NameValue>(names.Count);
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value))
            {
                if (value.Length > 0 || !RuleText.OmitWhenEmpty(name))
                {
                    parameters.Add(new NameValue(name, RuleText.KeepLogin(value, action.Get(name))));
                }
            }
            else if (action.Get(name) is { } own)
            {
                parameters.Add(new NameValue(name, own));
            }
            else if (RuleText.OmitWhenEmpty(name))
            {
                // given as the other fingerprint
            }
            else
            {
                throw new ActionEngineException($"Enter a value for \"{name}\". Nothing was changed.");
            }
        }

        return action with { Template = token, Parameters = parameters };
    }

    /// <summary>The checked settings this device's action (or its recipient) does not have: left out, reported as a warning.</summary>
    public static IReadOnlyList<string> LeftOut(string template, RulePatch patch, DeviceRuleSet device)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(device);
        if (patch.Parameters is not { Count: > 0 } values || device.Template(template) is not { } t)
        {
            return [];
        }

        var names = t.Parameters.Concat(device.RecipientParameters(template)).ToHashSet(StringComparer.Ordinal);
        return [.. values.Keys.Where(k => !names.Contains(k)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Whether two actions differ (template or any parameter value; order does not matter).</summary>
    public static bool ActionChanged(RuleAction before, RuleAction after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Template != after.Template || before.Parameters.Count != after.Parameters.Count)
        {
            return true;
        }

        var old = before.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        return after.Parameters.Any(p => !old.TryGetValue(p.Name, out var v) || v != p.Value);
    }

    /// <summary>Whether the trigger (start event, conditions, timeout) differs.</summary>
    public static bool TriggerChanged(DeviceRule before, DeviceRule after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return RuleText.TriggerKey(before.StartEvent, before.Conditions, before.Timeout) != RuleText.TriggerKey(after.StartEvent, after.Conditions, after.Timeout);
    }
}
