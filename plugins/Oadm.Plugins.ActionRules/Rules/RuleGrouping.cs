using System.Globalization;

using Oadm.Plugins.ActionRules.Device;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>How the page groups the rules of all devices.</summary>
public enum RuleGroupBy
{
    /// <summary>Same trigger (event, filter, conditions, schedule, timeout) and same action.</summary>
    WhatItDoes,

    /// <summary>Same name (case-insensitive, trimmed).</summary>
    Name,

    /// <summary>Same action.</summary>
    Action,

    /// <summary>Same trigger.</summary>
    Trigger,
}

/// <summary>One rule of one device.</summary>
public sealed record RuleMember(DeviceRulesRow Device, RuleView Rule);

/// <summary>A parameter across the rules of a group: its value when all share one, else how many values there are.</summary>
public sealed record ParameterSummary(string Name, string? Value, int Values, bool IsSecret);

/// <summary>
/// Rules of many devices that belong together, with what the page shows: the trigger and action in words (taken from the
/// first rule; <see cref="SameTrigger"/> and <see cref="SameAction"/> say whether every rule shares them), the names, how
/// many are on, and every parameter of a shared action with its value or its count of values.
/// </summary>
public sealed record RuleGroup(
    string Key,
    IReadOnlyList<RuleMember> Members,
    string TriggerText,
    string ActionText,
    string NamesText,
    bool SameTrigger,
    bool SameAction,
    string? Template,
    int OnCount,
    IReadOnlyList<ParameterSummary> Parameters)
{
    public int DeviceCount => Members.Select(m => m.Device.DeviceId).Distinct().Count();

    public int RuleCount => Members.Count;

    /// <summary>"On", "Off" or "3 of 5 on".</summary>
    public string OnText => OnCount == RuleCount ? "On" : OnCount == 0 ? "Off"
        : string.Create(CultureInfo.CurrentCulture, $"{OnCount:N0} of {RuleCount:N0} on");

    /// <summary>"host 192.168.1.5 · port 33 · message: 487 values".</summary>
    public string ValuesText => string.Join(" · ", Parameters.Where(p => !p.IsSecret).Select(p =>
        p.Value is not null ? p.Name + " " + (p.Value.Length == 0 ? "(empty)" : p.Value)
        : string.Create(CultureInfo.CurrentCulture, $"{p.Name}: {p.Values:N0} values")));
}

/// <summary>Builds the groups of the page in O(rules).</summary>
public static class RuleGrouping
{
    public static IReadOnlyList<RuleGroup> Build(IEnumerable<DeviceRulesRow> rows, RuleGroupBy by)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new Dictionary<string, List<RuleMember>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var rule in row.Rules)
            {
                var key = KeyOf(rule, by);
                if (!groups.TryGetValue(key, out var members))
                {
                    groups[key] = members = [];
                }

                members.Add(new RuleMember(row, rule));
            }
        }

        return [.. groups.Select(g => Summarize(g.Key, g.Value))
            .OrderByDescending(g => g.DeviceCount)
            .ThenBy(g => g.NamesText, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The group key of a rule.</summary>
    public static string KeyOf(RuleView rule, RuleGroupBy by)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return by switch
        {
            RuleGroupBy.Name => "n:" + rule.Name.Trim().ToUpperInvariant(),
            RuleGroupBy.Action => "a:" + rule.Template,
            RuleGroupBy.Trigger => "t:" + TriggerKey(rule),
            _ => "w:" + TriggerKey(rule) + "|" + rule.Template,
        };
    }

    public static string TriggerKey(RuleView rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return RuleText.TriggerKey(rule.StartEvent, rule.Conditions, rule.Timeout);
    }

    /// <summary>"When Virtual input (port 20)", "While Virtual input (port 20) and Schedule: Office Hours", plus the wait.</summary>
    public static string TriggerText(RuleView rule, DeviceRulesRow device)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(device);
        string Describe(EventFilter f) => RuleText.Describe(f, device.TopicNames, device.ScheduleNames);
        var conditions = string.Join(" and ", rule.Conditions.Select(Describe));
        var text = rule.StartEvent is { } start
            ? "When " + Describe(start) + (conditions.Length > 0 ? ", if " + conditions : string.Empty)
            : conditions.Length > 0 ? "While " + conditions : "No event";
        return rule.Timeout is { } timeout ? text + ", wait " + RuleText.TimeoutText(timeout) : text;
    }

    private static RuleGroup Summarize(string key, List<RuleMember> members)
    {
        var first = members[0];
        var triggerKey = TriggerKey(first.Rule);
        var sameTrigger = members.All(m => TriggerKey(m.Rule) == triggerKey && (m.Rule.StartEvent is null) == (first.Rule.StartEvent is null));
        var sameAction = members.All(m => m.Rule.Template == first.Rule.Template);
        // The most common name first: "test and 2 other names".
        var names = members.GroupBy(m => m.Rule.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Key).ToList();
        var namesText = names.Count switch
        {
            1 => names[0],
            2 => names[0] + " and 1 other name",
            _ => string.Create(CultureInfo.CurrentCulture, $"{names[0]} and {names.Count - 1:N0} other names"),
        };
        var parameters = sameAction ? Parameters(members) : [];
        return new RuleGroup(
            key,
            members,
            TriggerText(first.Rule, first.Device) + (sameTrigger ? string.Empty : string.Create(CultureInfo.CurrentCulture, $" (and {members.Select(m => TriggerKey(m.Rule)).Distinct(StringComparer.Ordinal).Count() - 1:N0} other events)")),
            sameAction ? ActionNames.Of(first.Rule.Template) : string.Create(CultureInfo.CurrentCulture, $"{members.Select(m => m.Rule.Template).Distinct(StringComparer.Ordinal).Count():N0} different actions"),
            namesText,
            sameTrigger,
            sameAction,
            sameAction ? first.Rule.Template : null,
            members.Count(m => m.Rule.Enabled),
            parameters);
    }

    private static IReadOnlyList<ParameterSummary> Parameters(List<RuleMember> members)
    {
        var order = new List<string>();
        var values = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var p in member.Rule.Parameters)
            {
                if (!values.TryGetValue(p.Name, out var set))
                {
                    values[p.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    order.Add(p.Name);
                }

                set.Add(RuleText.IsSecret(p.Name) ? (member.Rule.StoredSecrets.Contains(p.Name) ? "set" : string.Empty) : p.Value);
            }
        }

        return [.. order.Select(name => new ParameterSummary(
            name,
            values[name].Count == 1 && !RuleText.IsSecret(name) ? values[name].First() : null,
            values[name].Count,
            RuleText.IsSecret(name)))];
    }
}
