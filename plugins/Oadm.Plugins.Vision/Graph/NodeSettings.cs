using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Graph;

/// <summary>Reads the JSON values of node settings (camelCase, as stored): numbers, texts, class lists and shapes.</summary>
public static class NodeSettings
{
    /// <summary>The value as a number; null when it is none (or not finite).</summary>
    public static double? Number(JsonNode? value)
    {
        if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        double d;
        if (v.TryGetValue(out double dd))
        {
            d = dd;
        }
        else if (v.TryGetValue(out int i))
        {
            d = i;
        }
        else if (v.TryGetValue(out long l))
        {
            d = l;
        }
        else if (v.TryGetValue(out float f))
        {
            d = f;
        }
        else if (v.TryGetValue(out decimal m))
        {
            d = (double)m;
        }
        else if (!double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
        {
            return null;
        }

        return double.IsFinite(d) ? d : null;
    }

    /// <summary>The value as a text; null when it is none.</summary>
    public static string? Text(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue(out string? s) ? s : null;

    /// <summary>The value as true or false; null when it is neither.</summary>
    public static bool? Bool(JsonNode? value) =>
        value is JsonValue v ? v.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    /// <summary>A list of non-empty texts; null when the value is no such list.</summary>
    public static IReadOnlyList<string>? Texts(JsonNode? value)
    {
        if (value is not JsonArray array)
        {
            return null;
        }

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (Text(item) is not { Length: > 0 } s)
            {
                return null;
            }

            list.Add(s);
        }

        return list;
    }

    /// <summary>A list of points <c>{"x": 0.1, "y": 0.2}</c>; null when the value is no such list.</summary>
    public static IReadOnlyList<RelativePoint>? Points(JsonNode? value)
    {
        if (value is not JsonArray array)
        {
            return null;
        }

        var list = new List<RelativePoint>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject o || Number(o["x"]) is not { } x || Number(o["y"]) is not { } y)
            {
                return null;
            }

            list.Add(new RelativePoint(x, y));
        }

        return list;
    }

    /// <summary>A time of day "HH:mm"; null when the value is none.</summary>
    public static TimeOnly? TimeOfDay(JsonNode? value) =>
        Text(value) is { } s && TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>The JSON of a list of points, as the page stores a shape.</summary>
    public static JsonArray ToJson(IEnumerable<RelativePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var array = new JsonArray();
        foreach (var p in points)
        {
            array.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
        }

        return array;
    }
}
