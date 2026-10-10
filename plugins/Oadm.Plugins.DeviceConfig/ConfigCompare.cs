using System.Globalization;
using System.Text.Json.Nodes;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>Which imported values the device does not report afterwards (the verify step).</summary>
public static class ConfigCompare
{
    /// <summary>
    /// Paths of the values in <paramref name="expected"/> that differ in <paramref name="actual"/> or are missing there
    /// ("Image.I0.Appearance.Resolution"). Collections match their items by <c>groupId</c> or <c>id</c> when every item has
    /// one, else by position. Values only in <paramref name="actual"/> are fine (a merge keeps them).
    /// </summary>
    public static IReadOnlyList<string> Differences(JsonNode? expected, JsonNode? actual)
    {
        var result = new List<string>();
        Walk(expected, actual, string.Empty, result);
        return result;
    }

    private static void Walk(JsonNode? expected, JsonNode? actual, string path, List<string> result)
    {
        switch (expected)
        {
            case JsonObject obj:
                var other = actual as JsonObject;
                foreach (var (name, value) in obj)
                {
                    Walk(value, other?[name], Join(path, name), result);
                }

                break;
            case JsonArray array:
                WalkArray(array, actual as JsonArray, path, result);
                break;
            default:
                if (!JsonNode.DeepEquals(expected, actual))
                {
                    result.Add(path);
                }

                break;
        }
    }

    private static void WalkArray(JsonArray expected, JsonArray? actual, string path, List<string> result)
    {
        var key = KeyOf(expected);
        if (key is not null && actual is not null)
        {
            var byKey = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var item in actual)
            {
                if (item is JsonObject obj && Id(obj, key) is { } id)
                {
                    byKey.TryAdd(id, item);
                }
            }

            foreach (var item in expected)
            {
                var id = Id((JsonObject)item!, key)!;
                Walk(item, byKey.GetValueOrDefault(id), Join(path, id), result);
            }

            return;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var other = actual is not null && i < actual.Count ? actual[i] : null;
            Walk(expected[i], other, Join(path, i.ToString(CultureInfo.InvariantCulture)), result);
        }
    }

    /// <summary>"groupId" or "id" when every item is an object with that text, else null.</summary>
    private static string? KeyOf(JsonArray array)
    {
        foreach (var key in new[] { "groupId", "id" })
        {
            if (array.Count > 0 && array.All(item => item is JsonObject obj && Id(obj, key) is not null))
            {
                return key;
            }
        }

        return null;
    }

    private static string? Id(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;
}
