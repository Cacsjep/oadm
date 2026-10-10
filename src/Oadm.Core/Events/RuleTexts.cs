using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>One-line summaries of a rule for the Rules tab: conditions, actions, state, problems.</summary>
public static class RuleTexts
{
    /// <summary>"Blur detected · and Device unreachable (same device) · Mon-Fri 08:00-18:00".</summary>
    public static string Conditions(RuleDefinition rule, EventCatalog catalog, Func<Guid, string?> deviceName)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        var parts = new List<string>();
        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var condition = rule.Conditions[i];
            if (condition.Kind == RuleConditionKind.Schedule)
            {
                parts.Add(condition.Schedule is null ? "Schedule" : Schedule(condition.Schedule));
                continue;
            }

            var type = catalog.FindEventType(condition.EventTypeId);
            var name = type?.Info.DisplayName ?? condition.EventTypeId ?? "?";
            var scope = type is { DeviceScope: false } ? string.Empty : Selector(condition.Devices, deviceName, sameDevice: "same device");
            parts.Add((i == 0 ? name : "and " + Lower(name)) + (scope.Length == 0 ? string.Empty : $" ({scope})"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"Set output: Pulse output 1 for 5 s on 10.0.0.70 · Run VAPIX command: Restart".</summary>
    public static string Actions(RuleDefinition rule, EventCatalog catalog, Func<Guid, string?> deviceName)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        return string.Join(" · ", rule.Actions.Select(a => Action(a, catalog, deviceName)));
    }

    private static string Action(RuleAction action, EventCatalog catalog, Func<Guid, string?> deviceName)
    {
        var catalogAction = catalog.FindAction(action.ActionId);
        if (catalogAction is null)
        {
            return action.ActionId;
        }

        string text;
        try
        {
            using var settings = JsonDocument.Parse(action.Settings.ToJsonString());
            text = catalogAction.Plugin.Describe(settings.RootElement);
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            text = catalogAction.Plugin.DisplayName;
        }

        if (!catalogAction.Plugin.NeedsDevice || action.Targets.Mode == DeviceSelectorMode.EventDevice)
        {
            return text;
        }

        return text + " on " + Selector(action.Targets, deviceName, sameDevice: "the event's device");
    }

    /// <summary>"all devices", "10.0.0.70", "3 devices", "tag Building A", "tags A, B".</summary>
    public static string Selector(DeviceSelector selector, Func<Guid, string?> deviceName, string sameDevice)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(deviceName);
        return selector.Mode switch
        {
            DeviceSelectorMode.All => "all devices",
            DeviceSelectorMode.EventDevice => sameDevice,
            DeviceSelectorMode.Tags => (selector.Tags.Count == 1 ? "tag " : "tags ") + string.Join(", ", selector.Tags),
            _ when selector.DeviceIds.Count == 1 => deviceName(selector.DeviceIds[0]) ?? "1 device",
            _ => string.Create(CultureInfo.InvariantCulture, $"{selector.DeviceIds.Count} devices"),
        };
    }

    /// <summary>"Mon-Fri 08:00-18:00", "Every day 22:00-06:00", "Mon, Wed 08:00-12:00".</summary>
    public static string Schedule(RuleSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Days(schedule.Days) + " " + schedule.From + "-" + schedule.To;
    }

    public static string Days(IReadOnlyCollection<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        DayOfWeek[] week = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        var set = days.ToHashSet();
        if (set.Count == 7)
        {
            return "Every day";
        }

        if (set.SetEquals(week[..5]))
        {
            return "Mon-Fri";
        }

        if (set.SetEquals(week[5..]))
        {
            return "Sat-Sun";
        }

        return string.Join(", ", week.Where(set.Contains).Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)));
    }

    /// <summary>"On for 2 devices", "On", "Off", "Waiting for Image Health Dashboard", "Turned off".</summary>
    public static string State(RuleDefinition rule, bool stateful, RuleRuntimeStatus? status)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.Enabled)
        {
            return "Turned off";
        }

        if (status?.WaitingFor is { } waiting)
        {
            return "Waiting for " + waiting;
        }

        if (!stateful)
        {
            return "Ready";
        }

        return status?.ActiveCount switch
        {
            null or 0 => "Off",
            1 when rule.Conditions.Count > 0 => "On",
            var n => string.Create(CultureInfo.InvariantCulture, $"On for {n} devices"),
        };
    }

    /// <summary>Why a rule does not run as set up (an event or action of a plugin that is off), else null.</summary>
    public static string? Problem(RuleDefinition rule, EventCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var condition in rule.Conditions.Where(c => c.Kind == RuleConditionKind.Event))
        {
            var type = catalog.FindEventType(condition.EventTypeId);
            if (type is null)
            {
                return "Not available: an event of this rule is not installed";
            }

            if (!type.Available)
            {
                return $"Not available: {type.SourceName} is turned off";
            }
        }

        foreach (var action in rule.Actions)
        {
            var catalogAction = catalog.FindAction(action.ActionId);
            if (catalogAction is null)
            {
                return "Not available: an action of this rule is not installed";
            }

            if (!catalogAction.Available)
            {
                return catalogAction.UnavailableText;
            }
        }

        return null;
    }

    private static string Lower(string name) => name.Length > 1 && char.IsUpper(name[0]) && !char.IsUpper(name[1])
        ? char.ToLowerInvariant(name[0]) + name[1..]
        : name;
}
