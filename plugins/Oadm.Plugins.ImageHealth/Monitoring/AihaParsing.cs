using System.Text.Json;

namespace Oadm.Plugins.ImageHealth.Monitoring;

/// <summary>What one status request said about AXIS Image Health Analytics on a camera.</summary>
public enum AihaAppState
{
    /// <summary>HTTP 404: the app is not on the camera.</summary>
    NotInstalled,

    /// <summary>HTTP 503: the app is installed but not running.</summary>
    Stopped,

    /// <summary>HTTP 200 with the status JSON.</summary>
    Running,
}

/// <summary>
/// Reads the answer of <see cref="ImageHealthPluginInfo.StatusPath"/>. Verified on an AXIS Q3548-LVE (AXIS OS 12.11.118,
/// AIHA 3.2.2): <c>{"block":"normal","blur":"normal","redirect":"normal","status":"none","status_criticity":"info",
/// "under-exposure":"normal","unsuitability":"suitable"}</c>; a detection goes normal -> pending -> detected; the stopped
/// app answers 503, a camera without it 404.
/// </summary>
public static class AihaParsing
{
    /// <summary>
    /// The detections of a status answer by <see cref="AihaDetections"/> key, as <see cref="DetectionStates"/> values;
    /// a missing key is left out. Throws <see cref="JsonException"/> for an answer that is not a JSON object.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The status is not a JSON object.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in AihaDetections.All)
        {
            if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } raw)
            {
                result[key] = DetectionStates.FromRaw(raw);
            }
        }

        return result;
    }
}
