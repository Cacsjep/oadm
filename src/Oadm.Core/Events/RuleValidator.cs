using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>A rule definition the server refuses; the message is for the user.</summary>
public sealed class RuleValidationException : Exception
{
    public RuleValidationException()
        : base("The rule is not valid.")
    {
    }

    public RuleValidationException(string message)
        : base(message)
    {
    }

    public RuleValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The server's checks of a rule before it is saved (the dialog checks the same first). Simple mode: the first condition
/// is the trigger (an event); further conditions are stateful events or schedules and must all hold.
/// </summary>
public static class RuleValidator
{
    /// <summary>The first problem, or null when the rule can be saved.</summary>
    public static string? FirstProblem(RuleDefinition rule, EventCatalog catalog, Func<Guid, IReadOnlyCollection<string>>? storedSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        var name = rule.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        if (name.Length > RuleDefinition.MaxNameLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The name is longer than {RuleDefinition.MaxNameLength} characters.");
        }

        if (rule.Conditions.Count == 0 || rule.Conditions[0].Kind != RuleConditionKind.Event)
        {
            return "The rule needs an event as its first condition.";
        }

        if (rule.Conditions.Count > RuleDefinition.MaxConditions)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {RuleDefinition.MaxConditions} conditions.");
        }

        var trigger = catalog.FindEventType(rule.Conditions[0].EventTypeId);
        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var problem = CheckCondition(rule.Conditions[i], i, trigger, catalog);
            if (problem is not null)
            {
                return problem;
            }
        }

        if (rule.Actions.Count == 0)
        {
            return "Add at least one action.";
        }

        if (rule.Actions.Count > RuleDefinition.MaxActions)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {RuleDefinition.MaxActions} actions.");
        }

        if (rule.Actions.Select(a => a.Key).Distinct().Count() != rule.Actions.Count)
        {
            return "Two actions have the same key.";
        }

        var stateful = IsStateful(rule, catalog);
        foreach (var action in rule.Actions)
        {
            var problem = CheckAction(action, trigger!, stateful, catalog, storedSecrets?.Invoke(action.Key) ?? []);
            if (problem is not null)
            {
                return problem;
            }
        }

        if (rule.CooldownSeconds is < 0 or > 3600)
        {
            return "The dead time must be 0 to 3600 seconds.";
        }

        if (rule.MaxRunsPerMinute is < 1 or > 1000)
        {
            return "The flood limit must be 1 to 1000 runs per minute.";
        }

        return null;
    }

    /// <summary>Throws <see cref="RuleValidationException"/> with <see cref="FirstProblem"/>.</summary>
    public static void Validate(RuleDefinition rule, EventCatalog catalog, Func<Guid, IReadOnlyCollection<string>>? storedSecrets = null)
    {
        if (FirstProblem(rule, catalog, storedSecrets) is { } problem)
        {
            throw new RuleValidationException(problem);
        }
    }

    /// <summary>A rule whose events are all stateful is stateful (on / off); with a one-shot trigger it fires.</summary>
    public static bool IsStateful(RuleDefinition rule, EventCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        return rule.Conditions.Count > 0 && catalog.FindEventType(rule.Conditions[0].EventTypeId)?.Stateful == true;
    }

    private static string? CheckCondition(RuleCondition condition, int index, CatalogEventType? trigger, EventCatalog catalog)
    {
        var position = index == 0 ? "The trigger" : string.Create(CultureInfo.InvariantCulture, $"Condition {index + 1}");
        if (condition.Kind == RuleConditionKind.Schedule)
        {
            if (index == 0)
            {
                return "The rule needs an event as its first condition.";
            }

            return CheckSchedule(condition.Schedule, position);
        }

        var type = catalog.FindEventType(condition.EventTypeId);
        if (type is null)
        {
            return $"{position}: choose an event.";
        }

        if (index > 0 && !type.Stateful)
        {
            return $"{position}: only the first condition can be an event that happens once.";
        }

        foreach (var key in condition.Data.Keys)
        {
            if (!type.Info.DataFields.Any(f => string.Equals(f.Name, key, StringComparison.Ordinal)))
            {
                return $"{position}: {type.Info.DisplayName} has no value \"{key}\".";
            }
        }

        if (!type.DeviceScope)
        {
            return null;
        }

        return condition.Devices.Mode switch
        {
            DeviceSelectorMode.EventDevice when index == 0 => $"{position}: choose which devices.",
            DeviceSelectorMode.EventDevice when trigger is not { DeviceScope: true } => $"{position}: \"the same device\" needs a device event as the first condition.",
            DeviceSelectorMode.Devices or DeviceSelectorMode.Tags when condition.Devices.IsEmptyChoice => $"{position}: choose at least one device or tag.",
            _ => null,
        };
    }

    private static string? CheckSchedule(RuleSchedule? schedule, string position)
    {
        if (schedule is null || schedule.Days.Count == 0)
        {
            return $"{position}: choose at least one day.";
        }

        if (!RuleTimes.TryParse(schedule.From, out _) || !RuleTimes.TryParse(schedule.To, out _))
        {
            return $"{position}: enter the times as HH:mm, e.g. 08:00.";
        }

        return null;
    }

    private static string? CheckAction(RuleAction action, CatalogEventType trigger, bool stateful, EventCatalog catalog, IReadOnlyCollection<string> storedSecrets)
    {
        var catalogAction = catalog.FindAction(action.ActionId);
        if (catalogAction is null)
        {
            return "Choose an action.";
        }

        var plugin = catalogAction.Plugin;
        var label = plugin.DisplayName;
        if (plugin.NeedsDevice)
        {
            var targetProblem = action.Targets.Mode switch
            {
                DeviceSelectorMode.EventDevice when !trigger.DeviceScope => "the trigger is not about a device; choose the devices.",
                DeviceSelectorMode.Devices or DeviceSelectorMode.Tags when action.Targets.IsEmptyChoice => "choose at least one device or tag.",
                _ => null,
            };
            if (targetProblem is not null)
            {
                return $"{label}: {targetProblem}";
            }
        }

        IReadOnlyList<ActionField> fields;
        try
        {
            fields = plugin.Fields;
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return $"{label}: the action cannot be checked.";
        }

        var errors = ActionSettings.Check(fields, action.Settings, stateful, storedSecrets);
        if (errors.Count > 0)
        {
            var (key, message) = errors.First();
            return $"{label}: {fields.First(f => f.Key == key).Label}: {message}";
        }

        try
        {
            using var settings = JsonDocument.Parse(action.Settings.ToJsonString());
            var pluginErrors = plugin.Validate(settings.RootElement, stateful);
            if (pluginErrors.Count > 0)
            {
                var (key, message) = pluginErrors.First();
                var field = fields.FirstOrDefault(f => f.Key == key);
                return field is null ? $"{label}: {message}" : $"{label}: {field.Label}: {message}";
            }
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"{label}: {ex.Message}";
        }

        return null;
    }
}

/// <summary>"HH:mm" times of schedules.</summary>
public static class RuleTimes
{
    public static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>Whether <paramref name="local"/> (server local time) is inside the schedule; to at or before from = over midnight.</summary>
    public static bool IsInside(RuleSchedule schedule, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!TryParse(schedule.From, out var from) || !TryParse(schedule.To, out var to))
        {
            return false;
        }

        var now = TimeOnly.FromDateTime(local);
        if (from < to)
        {
            return schedule.Days.Contains(local.DayOfWeek) && now >= from && now < to;
        }

        // Over midnight (or the whole day when from == to): the part after midnight belongs to the day before.
        if (now >= from)
        {
            return schedule.Days.Contains(local.DayOfWeek);
        }

        return now < to && schedule.Days.Contains(local.AddDays(-1).DayOfWeek);
    }
}
