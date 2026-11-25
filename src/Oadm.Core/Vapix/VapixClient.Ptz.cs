using System.Globalization;
using System.Text.Json;

namespace Oadm.Core.Vapix;

/// <summary>
/// PTZ API (<c>axis-cgi/com/ptz.cgi</c>) for the live view of PTZ cameras: presets, home, continuous pan and tilt, zoom,
/// centering on a point; a camera that only forwards PTZ to a paired PTZ camera counts as without PTZ. Verified on an AXIS Q6411-LE (AXIS OS 13.0.88): commands answer 204, errors answer 200 with a
/// body "Error:..."; <c>continuouspantiltmove</c> takes <c>timeout=[seconds]</c> and the camera stops by itself after it;
/// both channels (visual, thermal) move the same head and list the same presets.
/// </summary>
public sealed partial class VapixClient
{
    internal const string PtzPath = "axis-cgi/com/ptz.cgi";

    /// <summary>What the PTZ camera offers on <paramref name="camera"/> (1-based channel); <see cref="PtzState.None"/> for a camera without PTZ.</summary>
    public async Task<PtzState> GetPtzAsync(int camera, CancellationToken ct)
    {
        var properties = await ListParametersAsync(["Properties.PTZ.PTZ", "Properties.PTZ.DriverModeList"], ct).ConfigureAwait(false);
        if (!properties.TryGetValue("Properties.PTZ.PTZ", out var ptz) || !string.Equals(ptz, "yes", StringComparison.OrdinalIgnoreCase)
            || VapixParsers.IsRemotePtzOnly(properties.GetValueOrDefault("Properties.PTZ.DriverModeList")))
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

    /// <summary>
    /// The presets of <paramref name="camera"/> with their numbers (<c>query=presetposall&amp;format=json</c>) and the number
    /// of the home preset (<c>PTZ.Preset.P#.HomePosition</c> of the channel's preset group, -1 or missing = none). Not
    /// verified on a camera yet.
    /// </summary>
    public async Task<PtzPresets> GetPtzPresetsAsync(int camera, CancellationToken ct)
    {
        camera = Math.Max(1, camera);
        var presets = VapixParsers.ParsePtzPresetNumbers(await PtzTextAsync("query=presetposall&format=json", ct).ConfigureAwait(false), camera);
        var parameters = await ListParametersAsync(["PTZ.Preset"], ct).ConfigureAwait(false);
        return new PtzPresets(presets, VapixParsers.PtzHomePreset(parameters, camera));
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

/// <summary>One preset of a PTZ channel: its number (<c>presetposno&lt;n&gt;</c>) and name.</summary>
public sealed record PtzPreset(int Number, string Name);

/// <summary>The presets of a PTZ channel and the number of its home preset (null = none known).</summary>
public sealed record PtzPresets(IReadOnlyList<PtzPreset> Presets, int? HomeNumber);

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
    internal static IReadOnlyList<string> ParsePtzPresets(string json, int camera) =>
        [.. ParsePtzPresetNumbers(json, camera).Select(p => p.Name)];

    /// <summary>The presets of one channel with their numbers ("presetposno2=Gate" -> 2, "Gate"; 0 when the number is missing).</summary>
    internal static IReadOnlyList<PtzPreset> ParsePtzPresetNumbers(string json, int camera)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty($"Camera {camera}", out var channel) || channel.ValueKind != JsonValueKind.Object
                || !channel.TryGetProperty("presets", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var presets = new List<PtzPreset>();
            foreach (var entry in list.EnumerateArray())
            {
                var text = entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? "" : "";
                var eq = text.IndexOf('=', StringComparison.Ordinal);
                var name = eq >= 0 ? text[(eq + 1)..] : text;
                if (name.Length == 0)
                {
                    continue;
                }

                var key = eq >= 0 ? text[..eq] : "";
                var number = key.StartsWith("presetposno", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(key.AsSpan("presetposno".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
                presets.Add(new PtzPreset(number, name));
            }

            return presets;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The number of the home preset of a channel from the <c>PTZ.Preset</c> parameters: the group whose <c>ImageSource</c>
    /// is the channel (0-based; without any <c>ImageSource</c> the group P&lt;channel - 1&gt;), its <c>HomePosition</c>;
    /// null for none (-1, 0, missing).
    /// </summary>
    internal static int? PtzHomePreset(IReadOnlyDictionary<string, string> parameters, int camera)
    {
        const string prefix = "PTZ.Preset.P";
        string? group = null;
        var anySource = false;
        foreach (var (key, value) in parameters)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(".ImageSource", StringComparison.Ordinal)
                && !key[prefix.Length..^".ImageSource".Length].Contains('.', StringComparison.Ordinal))
            {
                anySource = true;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var source) && source == camera - 1)
                {
                    group = key[..^".ImageSource".Length];
                    break;
                }
            }
        }

        group ??= anySource ? null : prefix + (camera - 1).ToString(CultureInfo.InvariantCulture);
        return group is not null && parameters.TryGetValue(group + ".HomePosition", out var home)
            && int.TryParse(home, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    /// <summary>
    /// True for a camera whose PTZ only forwards to a paired PTZ camera (AXIS Q6300-E with One-click PTZ control:
    /// <c>DriverModeList=disabled,remote</c>); its own pictures never move. A PTZ camera has <c>none</c> (AXIS Q6154-E).
    /// </summary>
    internal static bool IsRemotePtzOnly(string? driverModeList) =>
        driverModeList is not null
        && driverModeList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("remote", StringComparer.OrdinalIgnoreCase);

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
