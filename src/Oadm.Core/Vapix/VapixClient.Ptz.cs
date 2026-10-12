using System.Globalization;
using System.Text.Json;

namespace Oadm.Core.Vapix;

/// <summary>
/// PTZ API (<c>axis-cgi/com/ptz.cgi</c>) for the live view of PTZ cameras: presets, home, continuous pan and tilt, zoom,
/// centering on a point. Verified on an AXIS Q6411-LE (AXIS OS 13.0.88): commands answer 204, errors answer 200 with a
/// body "Error:..."; <c>continuouspantiltmove</c> takes <c>timeout=[seconds]</c> and the camera stops by itself after it;
/// both channels (visual, thermal) move the same head and list the same presets.
/// </summary>
public sealed partial class VapixClient
{
    internal const string PtzPath = "axis-cgi/com/ptz.cgi";

    /// <summary>What the PTZ camera offers on <paramref name="camera"/> (1-based channel); <see cref="PtzState.None"/> for a camera without PTZ.</summary>
    public async Task<PtzState> GetPtzAsync(int camera, CancellationToken ct)
    {
        var properties = await ListParametersAsync(["Properties.PTZ.PTZ"], ct).ConfigureAwait(false);
        if (!properties.TryGetValue("Properties.PTZ.PTZ", out var ptz) || !string.Equals(ptz, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return PtzState.None;
        }

        camera = Math.Max(1, camera);
        var info = await PtzTextAsync($"info=1&camera={camera}", ct).ConfigureAwait(false);
        var commands = VapixParsers.ParsePtzCommands(info);
        IReadOnlyList<string> presets = [];
        if (commands.Contains("gotoserverpresetname"))
        {
            presets = VapixParsers.ParsePtzPresets(await PtzTextAsync("query=presetposall&format=json", ct).ConfigureAwait(false), camera);
        }

        int? zoom = null;
        if (commands.Contains("zoom"))
        {
            var position = VapixParsers.ParseKeyValues(await PtzTextAsync($"query=position&camera={camera}", ct).ConfigureAwait(false));
            zoom = position.TryGetValue("zoom", out var z) && int.TryParse(z, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        return new PtzState
        {
            IsSupported = true,
            Camera = camera,
            CanMove = commands.Contains("continuouspantiltmove"),
            CanZoom = commands.Contains("rzoom") && commands.Contains("zoom"),
            CanCenter = commands.Contains("center"),
            CanGoHome = commands.Contains("move"),
            Presets = presets,
            Zoom = zoom ?? 1,
        };
    }

    /// <summary>Sends one PTZ command (arguments without <c>camera</c>); throws <see cref="PtzException"/> for the camera's "Error:" answer.</summary>
    public async Task SendPtzAsync(int camera, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(arguments);
        _ = await PtzTextAsync($"camera={Math.Max(1, camera)}&{arguments}", ct).ConfigureAwait(false);
    }

    private async Task<string> PtzTextAsync(string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PtzPath + "?" + query);
        var text = await SendForStringAsync(request, ct).ConfigureAwait(false);
        if (VapixParsers.PtzError(text) is { } error)
        {
            throw new PtzException(error);
        }

        return text;
    }
}

/// <summary>What a PTZ camera offers in the live view.</summary>
public sealed record PtzState
{
    public static PtzState None { get; } = new();

    public bool IsSupported { get; init; }

    /// <summary>The 1-based channel the values belong to.</summary>
    public int Camera { get; init; } = 1;

    public bool CanMove { get; init; }

    public bool CanZoom { get; init; }

    public bool CanCenter { get; init; }

    public bool CanGoHome { get; init; }

    /// <summary>Preset names of the channel, in the camera's order (the home preset included).</summary>
    public IReadOnlyList<string> Presets { get; init; } = [];

    /// <summary>Optical zoom position 1..9999.</summary>
    public int Zoom { get; init; } = 1;
}

/// <summary>The PTZ camera refused a command ("Error: ..." answer).</summary>
public sealed class PtzException(string message) : VapixException(message);

public static partial class VapixParsers
{
    /// <summary>The command names of <c>ptz.cgi?info=1</c> ("center=[x],[y]" -> "center"; indented lines are arguments).</summary>
    internal static HashSet<string> ParsePtzCommands(string info)
    {
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('{'))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                commands.Add(line[..eq].Trim());
            }
        }

        return commands;
    }

    /// <summary>Preset names of one channel from <c>query=presetposall&amp;format=json</c> ({"Camera 1": {"presets": ["presetposno1=Home"]}}).</summary>
    internal static IReadOnlyList<string> ParsePtzPresets(string json, int camera)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty($"Camera {camera}", out var channel) || !channel.TryGetProperty("presets", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. list.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Select(s => s.IndexOf('=', StringComparison.Ordinal) is var i and >= 0 ? s[(i + 1)..] : s)
                .Where(s => s.Length > 0)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The text of a ptz.cgi "Error:" answer ("goto preset: No such preset position found"), or null.</summary>
    internal static string? PtzError(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var message = trimmed["Error".Length..].TrimStart(':', ' ', '\r', '\n').Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return message.Length == 0 ? "error" : message;
    }

    /// <summary>"key=value" lines (ptz.cgi queries).</summary>
    internal static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                values[line[..eq]] = line[(eq + 1)..];
            }
        }

        return values;
    }
}
