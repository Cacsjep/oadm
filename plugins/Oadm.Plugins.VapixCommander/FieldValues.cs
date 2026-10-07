using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.VapixCommander;

/// <summary><c>{{name}}</c> placeholders in a request.</summary>
public static partial class Placeholders
{
    [GeneratedRegex("\\{\\{([a-zA-Z][a-zA-Z0-9_]*)\\}\\}", RegexOptions.CultureInvariant)]
    public static partial Regex Pattern();

    /// <summary>Names used in <paramref name="text"/>, in order (with repeats).</summary>
    public static IEnumerable<string> In(string? text) =>
        string.IsNullOrEmpty(text) ? [] : Pattern().Matches(text).Select(m => m.Groups[1].Value);

    /// <summary>Placeholder names used anywhere in the request (path, query keys and values, headers, body strings and keys).</summary>
    public static IEnumerable<string> InRequest(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var texts = new List<string?> { request.Path };
        foreach (var (key, value) in request.Query ?? [])
        {
            texts.Add(key);
            texts.Add(value);
        }

        foreach (var (key, value) in request.Headers ?? [])
        {
            texts.Add(key);
            texts.Add(value);
        }

        CollectBody(request.Body, texts);
        return texts.SelectMany(In);
    }

    /// <summary>The field name when <paramref name="text"/> is exactly one placeholder ("{{level}}"), else null.</summary>
    public static string? Whole(string text)
    {
        var match = Pattern().Match(text);
        return match.Success && match.Index == 0 && match.Length == text.Length ? match.Groups[1].Value : null;
    }

    private static void CollectBody(JsonNode? node, List<string?> texts)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    texts.Add(key);
                    CollectBody(value, texts);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectBody(item, texts);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                texts.Add(value.GetValue<string>());
                break;
        }
    }
}

/// <summary>A checked value of one field: string, long, double or bool (null when an optional field is empty).</summary>
public sealed record FieldValue(CommandField Field, object? Value)
{
    /// <summary>Text for URL, header, form, text and XML positions. Booleans: field trueValue/falseValue, else yes/no for param.cgi, else true/false.</summary>
    public string Text(bool paramCgi) => Value switch
    {
        null => string.Empty,
        bool b when b => Field.TrueValue ?? (paramCgi ? "yes" : "true"),
        bool => Field.FalseValue ?? (paramCgi ? "no" : "false"),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        string s => s,
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Typed JSON for a body value that is exactly the placeholder: numbers and booleans stay unquoted.</summary>
    public JsonNode? Json(bool paramCgi) => Value switch
    {
        null => null,
        bool b when Field.TrueValue is null && Field.FalseValue is null => JsonValue.Create(b),
        bool => JsonValue.Create(Text(paramCgi)),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonValue.Create(Text(paramCgi)),
    };
}

/// <summary>Converts and checks entered values against their field definition.</summary>
public static class FieldValues
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Checks every field: the entered value, else the field default; required fields must have one.
    /// Throws <see cref="CommandValidationException"/> with one message per problem (password values never appear in it).
    /// </summary>
    public static IReadOnlyDictionary<string, FieldValue> Resolve(CommandDefinition command, IReadOnlyDictionary<string, JsonElement>? values)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var field in command.Fields)
        {
            JsonElement? raw = values is not null && values.TryGetValue(field.Name, out var entered) && !IsEmpty(entered)
                ? entered
                : field.Default is { } d && !IsEmpty(d) ? d : null;
            if (raw is null)
            {
                if (field.IsRequired)
                {
                    problems.Add($"{Label(field)} is required.");
                }

                result[field.Name] = new FieldValue(field, null);
                continue;
            }

            if (Convert(field, raw.Value, out var value) is { } error)
            {
                problems.Add($"{Label(field)}: {error}");
                continue;
            }

            result[field.Name] = new FieldValue(field, value);
        }

        if (problems.Count > 0)
        {
            throw new CommandValidationException(problems);
        }

        return result;
    }

    /// <summary>Converts one value; returns the error text (without the value) or null.</summary>
    public static string? Convert(CommandField field, JsonElement raw, out object? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        value = null;
        switch (field.Type)
        {
            case FieldTypes.Integer:
            {
                long number;
                if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var l))
                {
                    number = l;
                }
                else if (raw.ValueKind == JsonValueKind.String && long.TryParse(raw.GetString()!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    number = parsed;
                }
                else
                {
                    return "must be a whole number.";
                }

                if (Range(field, number) is { } rangeError)
                {
                    return rangeError;
                }

                value = number;
                return null;
            }

            case FieldTypes.Number:
            {
                double number;
                if (raw.ValueKind == JsonValueKind.Number)
                {
                    number = raw.GetDouble();
                }
                else if (raw.ValueKind == JsonValueKind.String && double.TryParse(raw.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed))
                {
                    number = parsed;
                }
                else
                {
                    return "must be a number.";
                }

                if (Range(field, number) is { } rangeError)
                {
                    return rangeError;
                }

                value = number;
                return null;
            }

            case FieldTypes.Boolean:
            {
                bool? flag = raw.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => raw.GetString()!.Trim().ToLowerInvariant() switch
                    {
                        "true" or "yes" or "on" or "1" => true,
                        "false" or "no" or "off" or "0" => false,
                        _ => null,
                    },
                    _ => null,
                };
                if (flag is null)
                {
                    return "must be yes or no.";
                }

                value = flag.Value;
                return null;
            }

            case FieldTypes.Enum:
            {
                var text = TextOf(raw);
                var option = field.Options?.FirstOrDefault(o => string.Equals(TextOf(o.Value), text, StringComparison.Ordinal));
                if (option is null)
                {
                    return "is not one of the allowed values.";
                }

                value = option.Value.ValueKind switch
                {
                    JsonValueKind.Number when option.Value.TryGetInt64(out var l) => l,
                    JsonValueKind.Number => option.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => TextOf(option.Value),
                };
                return null;
            }

            default:
            {
                // string and password
                if (raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                {
                    return "must be text.";
                }

                var text = TextOf(raw);
                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    return "contains an invalid character.";
                }

                if (field.Pattern is { } pattern)
                {
                    try
                    {
                        if (!Regex.IsMatch(text, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, PatternTimeout))
                        {
                            return "does not have the expected format.";
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return "could not be checked.";
                    }
                }

                if (field.Min is { } min && text.Length < min)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"needs at least {min} characters.");
                }

                if (field.Max is { } max && text.Length > max)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"allows at most {max} characters.");
                }

                value = text;
                return null;
            }
        }
    }

    /// <summary>Plain text of a JSON scalar (strings unquoted, numbers as written, booleans lower case).</summary>
    public static string TextOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText(),
    };

    private static bool IsEmpty(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (element.ValueKind == JsonValueKind.String && element.GetString()!.Length == 0);

    private static string Label(CommandField field) => string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;

    private static string? Range(CommandField field, double number)
    {
        if (field.Min is { } min && number < min)
        {
            return string.Create(CultureInfo.InvariantCulture, $"must be at least {min}.");
        }

        if (field.Max is { } max && number > max)
        {
            return string.Create(CultureInfo.InvariantCulture, $"must be at most {max}.");
        }

        return null;
    }
}
