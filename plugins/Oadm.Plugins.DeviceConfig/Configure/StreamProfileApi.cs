using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// Stream profiles (<c>streamprofile.cgi</c>, JSON <c>list</c>, <c>create</c>, <c>update</c>; verified read-only on an AXIS
/// P3265-V, AXIS OS 12.11: name, description, parameters, <c>maxProfiles</c> 26).
/// </summary>
public static class StreamProfileApi
{
    public const string Path = "/axis-cgi/streamprofile.cgi";

    public sealed record ProfileList(IReadOnlyList<ConfigStreamProfile> Profiles, int? MaxProfiles);

    public static async Task<ProfileList> ListAsync(IVapixClient vapix, CancellationToken ct)
    {
        var answer = await CallAsync(vapix, "list", new JsonObject { ["streamProfileName"] = new JsonArray() }, ct).ConfigureAwait(false);
        return Parse(answer);
    }

    public static ProfileList Parse(JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var data = answer["data"] as JsonObject;
        var profiles = (data?["streamProfile"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new ConfigStreamProfile(Text(p["name"]) ?? string.Empty, Text(p["description"]), Text(p["parameters"]) ?? string.Empty))
            .Where(p => p.Name.Length > 0)
            .ToList();
        int? max = data?["maxProfiles"] is JsonValue value && value.TryGetValue<int>(out var n) ? n : null;
        return new ProfileList(profiles, max);
    }

    /// <summary>Creates the profile, or updates the one with the same name.</summary>
    public static Task CreateAsync(IVapixClient vapix, ConfigStreamProfile profile, CancellationToken ct) =>
        CallAsync(vapix, "create", Profiles(profile), ct);

    public static Task UpdateAsync(IVapixClient vapix, ConfigStreamProfile profile, CancellationToken ct) =>
        CallAsync(vapix, "update", Profiles(profile), ct);

    /// <summary>Removes a profile by name (the hardware write test cleans up with it).</summary>
    public static Task RemoveAsync(IVapixClient vapix, string name, CancellationToken ct) =>
        CallAsync(vapix, "remove", new JsonObject { ["streamProfileName"] = new JsonArray(new JsonObject { ["name"] = name }) }, ct);

    private static JsonObject Profiles(ConfigStreamProfile profile) => new()
    {
        ["streamProfile"] = new JsonArray(new JsonObject
        {
            ["name"] = profile.Name,
            ["description"] = profile.Description ?? string.Empty,
            ["parameters"] = profile.Parameters,
        }),
    };

    private static async Task<JsonObject> CallAsync(IVapixClient vapix, string method, JsonObject parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var json = new JsonObject { ["apiVersion"] = "1.0", ["method"] = method, ["params"] = parameters }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? answer = null;
        try
        {
            answer = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
        }

        if (answer?["error"] is JsonObject error)
        {
            throw new ConfigDeviceException("The device refused: " + (Text(error["message"]) ?? "error " + error["code"]?.ToJsonString()));
        }

        if (!response.IsSuccessStatusCode || answer is null)
        {
            throw new ConfigDeviceException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? "The device sent an unreadable answer.");
        }

        return answer;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>The device refused one parameter of an update: its name (without "root.") and the device's text.</summary>
public sealed class ParamRefusedException(string message, string? parameter) : ConfigDeviceException(message)
{
    /// <summary>The refused parameter ("Image.I0.Appearance.Compression"), when the device named it.</summary>
    public string? Parameter { get; } = parameter;
}

/// <summary>Writes parameters with <c>param.cgi?action=update</c> (arguments in the POST body).</summary>
public static class ParamWriter
{
    /// <summary>A large update (a whole camera: about 900 parameters) can take the device a while.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sets the parameters in one request, encoded like the other param.cgi writers of OADM (<c>%20</c>, never <c>+</c>).
    /// Throws <see cref="ParamRefusedException"/> with the device's error line (and the parameter it names), in which every
    /// value of <paramref name="secrets"/> (passwords) is replaced by "***".
    /// </summary>
    public static async Task UpdateAsync(IVapixClient vapix, IReadOnlyList<NameValue> parameters, IReadOnlyCollection<string> secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count == 0)
        {
            return;
        }

        var form = new StringBuilder("action=update");
        foreach (var parameter in parameters)
        {
            form.Append('&').Append(Uri.EscapeDataString(parameter.Name)).Append('=').Append(Uri.EscapeDataString(parameter.Value));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ParamDefinitionsReader.ParamPath)
        {
            Content = new StringContent(form.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Options.Set(VapixRequestOptions.Timeout, Timeout);
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (!response.IsSuccessStatusCode)
        {
            throw new ConfigDeviceException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? $"HTTP {(int)response.StatusCode}");
        }

        // "OK", else "# Error: Error setting 'root.X' to 'y'!" (Axis devices answer errors with HTTP 200).
        if (!body.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            var line = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase));
            var text = (line ?? body).TrimStart('#', ' ');
            if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                text = text["Error:".Length..].TrimStart();
            }

            throw new ParamRefusedException("The device refused: " + Scrub(text, secrets), RefusedName(text));
        }
    }

    /// <summary>"Error setting 'root.Image.I0.Appearance.Compression' to '200'!" -> "Image.I0.Appearance.Compression".</summary>
    public static string? RefusedName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf("'root.", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf('\'', start + 6);
        return end > start + 6 ? text[(start + 6)..end] : null;
    }

    /// <summary>The text with every secret value replaced by "***".</summary>
    public static string Scrub(string text, IReadOnlyCollection<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        foreach (var secret in secrets.Where(v => v.Length > 0).OrderByDescending(v => v.Length))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
