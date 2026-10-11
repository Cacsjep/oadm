using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Sdk.Actions;

/// <summary>
/// The host's checks of declarative action settings (<see cref="ActionField"/>), shared by the server (before a rule is
/// saved) and the rule dialog (errors below the fields): required values, number ranges, choices, lengths.
/// </summary>
public static class ActionSettings
{
    /// <summary>Whether <paramref name="field"/> is shown with these settings (<see cref="ActionField.VisibleWhenKey"/>, off-edge fields only on stateful rules).</summary>
    public static bool IsVisible(ActionField field, JsonObject settings, bool statefulRule)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(settings);
        if (field.OffEdge && !statefulRule)
        {
            return false;
        }

        if (field.VisibleWhenKey is not { Length: > 0 } key)
        {
            return true;
        }

        var current = Text(settings, key) ?? string.Empty;
        return (field.VisibleWhenValue ?? string.Empty).Split('|').Any(v => string.Equals(current, v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Most rows of a <see cref="ActionFieldType.KeyValues"/> setting.</summary>
    public const int MaxPairs = 50;

    /// <summary>The rows of a <see cref="ActionFieldType.KeyValues"/> value (a JSON array of {"name", "value"}), without empty names.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Pairs(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var row in value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : string.Empty;
            var text = row.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            if (name.Length > 0)
            {
                pairs.Add(new(name, text));
            }
        }

        return pairs;
    }

    /// <summary>A setting as text: strings as they are, numbers and booleans in invariant JSON form, null when missing.</summary>
    public static string? Text(JsonObject settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    /// <summary>
    /// Errors per field key of the visible fields. <paramref name="storedSecrets"/>: password fields that have a value on
    /// the server (an empty field keeps it, so it counts as filled).
    /// </summary>
    public static Dictionary<string, string> Check(
        IReadOnlyList<ActionField> fields, JsonObject settings, bool statefulRule, IReadOnlyCollection<string>? storedSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!IsVisible(field, settings, statefulRule))
            {
                continue;
            }

            var value = Text(settings, field.Key)?.Trim() ?? string.Empty;
            if (value.Length == 0)
            {
                var stored = field.Type == ActionFieldType.Password && storedSecrets?.Contains(field.Key) == true;
                if (field.Required && !stored && field.Type != ActionFieldType.Bool)
                {
                    errors[field.Key] = field.Type is ActionFieldType.Choice or ActionFieldType.DynamicChoice
                        ? $"Choose {field.Label.ToLowerInvariant()}."
                        : $"Enter {field.Label.ToLowerInvariant()}.";
                }

                continue;
            }

            var problem = field.Type switch
            {
                ActionFieldType.KeyValues => CheckPairs(settings[field.Key], field),
                ActionFieldType.Number => CheckNumber(field, value),
                ActionFieldType.Bool => value is "true" or "false" ? null : "Must be on or off.",
                ActionFieldType.Choice => field.Choices.Any(c => string.Equals(c.Value, value, StringComparison.Ordinal)) ? null : "Choose one of the list.",
                _ => value.Length > field.MaxLength
                    ? string.Create(CultureInfo.InvariantCulture, $"At most {field.MaxLength:N0} characters.")
                    : null,
            };
            if (problem is not null)
            {
                errors[field.Key] = problem;
            }
        }

        return errors;
    }

    private static string? CheckPairs(JsonNode? node, ActionField field)
    {
        if (node is not JsonArray rows)
        {
            return "Must be a list of names and values.";
        }

        if (rows.Count > MaxPairs)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {MaxPairs} rows.");
        }

        foreach (var row in rows)
        {
            var name = (row as JsonObject)?["name"]?.GetValueKind() == JsonValueKind.String ? row!["name"]!.GetValue<string>().Trim() : string.Empty;
            var value = (row as JsonObject)?["value"]?.GetValueKind() == JsonValueKind.String ? row!["value"]!.GetValue<string>() : string.Empty;
            if (name.Length == 0 && value.Length > 0)
            {
                return "Every row with a value needs a name.";
            }

            if (name.Any(char.IsControl) || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
            {
                return "Names and values must be on one line.";
            }
        }

        return rows.ToJsonString().Length > field.MaxLength
            ? string.Create(CultureInfo.InvariantCulture, $"At most {field.MaxLength:N0} characters.")
            : null;
    }

    private static string? CheckNumber(ActionField field, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return "Enter a whole number.";
        }

        return number < field.Min || number > field.Max
            ? string.Create(CultureInfo.InvariantCulture, $"Must be {field.Min} to {field.Max}.")
            : null;
    }

    /// <summary>A new action's settings: every field's <see cref="ActionField.Default"/>.</summary>
    public static JsonObject Defaults(IReadOnlyList<ActionField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var settings = new JsonObject();
        foreach (var field in fields.Where(f => f.Default is not null))
        {
            settings[field.Key] = field.Type switch
            {
                ActionFieldType.Number when int.TryParse(field.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                ActionFieldType.Bool => JsonValue.Create(string.Equals(field.Default, "true", StringComparison.OrdinalIgnoreCase)),
                _ => JsonValue.Create(field.Default),
            };
        }

        return settings;
    }
}
