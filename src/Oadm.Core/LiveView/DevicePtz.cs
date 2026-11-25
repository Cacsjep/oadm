using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView;

/// <summary>What a PTZ command of the live view does.</summary>
public enum PtzCommandKind
{
    /// <summary>Continuous pan and tilt at X, Y (-100..100 each); the camera stops by itself after <see cref="DevicePtz.MoveTimeoutSeconds"/>.</summary>
    Move,

    Stop,

    /// <summary>Absolute zoom <see cref="PtzCommand.Value"/> (1..9999).</summary>
    Zoom,

    /// <summary>Relative zoom by <see cref="PtzCommand.Value"/> steps (-9999..9999; mouse wheel).</summary>
    ZoomBy,

    /// <summary>The point X, Y of the picture (fractions 0..1) becomes the center.</summary>
    Center,

    Home,

    /// <summary>Go to the preset <see cref="PtzCommand.Preset"/>.</summary>
    Preset,

    /// <summary>
    /// The current position becomes the preset <see cref="PtzCommand.Preset"/>, which the camera must list; the home preset
    /// stays home (<see cref="PtzCommand.Home"/>).
    /// </summary>
    SavePreset,
}

/// <summary>One PTZ command of the live view; <paramref name="Home"/>: the saved preset is the camera's home position.</summary>
public sealed record PtzCommand(PtzCommandKind Kind, double X = 0, double Y = 0, int Value = 0, string? Preset = null, bool Home = false)
{
    /// <summary>The refusal of a save to a preset the camera does not list.</summary>
    public const string NoSuchPreset = "The camera has no preset with this name.";

    /// <summary>The ptz.cgi arguments (without camera); values clamped, the preset name escaped.</summary>
    public string Arguments()
    {
        static string I(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        return Kind switch
        {
            PtzCommandKind.Move when Math.Round(X) == 0 && Math.Round(Y) == 0 => "continuouspantiltmove=0,0",
            PtzCommandKind.Move => $"continuouspantiltmove={I(Math.Clamp(X, -100, 100))},{I(Math.Clamp(Y, -100, 100))}&timeout={DevicePtz.MoveTimeoutSeconds}",
            PtzCommandKind.Stop => "continuouspantiltmove=0,0",
            PtzCommandKind.Zoom => $"zoom={I(Math.Clamp(Value, 1, 9999))}",
            PtzCommandKind.ZoomBy => $"rzoom={I(Math.Clamp(Value, -9999, 9999))}",
            PtzCommandKind.Center => $"center={I(Math.Clamp(X, 0, 1) * DevicePtz.CenterScale)},{I(Math.Clamp(Y, 0, 1) * DevicePtz.CenterScale)}"
                + $"&imagewidth={DevicePtz.CenterScale}&imageheight={DevicePtz.CenterScale}",
            PtzCommandKind.Home => "move=home",
            PtzCommandKind.Preset when IsPresetName(Preset) => "gotoserverpresetname=" + Uri.EscapeDataString(Preset),
            PtzCommandKind.SavePreset when IsPresetName(Preset) => "setserverpresetname=" + Uri.EscapeDataString(Preset) + (Home ? "&home=yes" : ""),
            PtzCommandKind.Preset or PtzCommandKind.SavePreset => throw new ArgumentException("Choose a preset.", nameof(Preset)),
            _ => throw new ArgumentException("Unknown PTZ command.", nameof(Kind)),
        };
    }

    private static bool IsPresetName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.Any(char.IsControl);
}

/// <summary>PTZ of managed devices for the live view. Tests replace it with fakes.</summary>
public interface IDevicePtz
{
    /// <summary>What the PTZ camera offers on the channel; <see cref="PtzState.None"/> for a camera without PTZ.</summary>
    Task<PtzState> GetAsync(Guid deviceId, int camera, CancellationToken ct);

    /// <summary>Sends one command; throws <see cref="PtzException"/> when the camera refuses, <see cref="ArgumentException"/> for a bad value.</summary>
    Task SendAsync(Guid deviceId, int camera, PtzCommand command, CancellationToken ct);
}

/// <summary>The PTZ API over the device's authenticated VAPIX client.</summary>
public sealed class DevicePtz(VapixClientFactory clients) : IDevicePtz
{
    /// <summary>A continuous move stops by itself after this time unless the next one follows (the client repeats while dragging).</summary>
    public const int MoveTimeoutSeconds = 1;

    /// <summary>"Picture size" of center commands: points are sent in thousandths of the picture.</summary>
    public const int CenterScale = 1000;

    public async Task<PtzState> GetAsync(Guid deviceId, int camera, CancellationToken ct)
    {
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        return await client.GetPtzAsync(camera, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(Guid deviceId, int camera, PtzCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var arguments = command.Arguments();
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        if (command.Kind == PtzCommandKind.SavePreset)
        {
            await SavePresetAsync(client, camera, command, ct).ConfigureAwait(false);
            return;
        }

        await client.SendPtzAsync(camera, arguments, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Device safety of a save: the channel's presets are read first and only a name the camera lists (exactly) is written,
    /// so a save never adds a preset; the home preset gets <c>home=yes</c> so it stays the home position.
    /// </summary>
    internal static async Task SavePresetAsync(VapixClient client, int camera, PtzCommand command, CancellationToken ct)
    {
        var presets = await client.GetPtzPresetsAsync(camera, ct).ConfigureAwait(false);
        var preset = presets.Presets.FirstOrDefault(p => string.Equals(p.Name, command.Preset, StringComparison.Ordinal))
            ?? throw new ArgumentException(PtzCommand.NoSuchPreset); // the message only: it is the user's text
        var arguments = (command with { Preset = preset.Name, Home = preset.Number == presets.HomeNumber }).Arguments();
        await client.SendPtzAsync(camera, arguments, ct).ConfigureAwait(false);
    }
}
