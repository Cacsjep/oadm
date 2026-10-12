using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView;

/// <summary>One change of a camera's optics; exactly one of the members is set.</summary>
public sealed record OpticsChange
{
    public double? Magnification { get; init; }

    public double? FocusPosition { get; init; }

    /// <summary>A new autofocus area; autofocus runs in it right after.</summary>
    public OpticsWindow? FocusWindow { get; init; }

    public bool Autofocus { get; init; }

    /// <summary>Focus back to its default position, then autofocus.</summary>
    public bool ResetFocus { get; init; }

    /// <summary>"Zoom 2.10", "Focus 53", "Autofocus area", "Autofocus", "Reset focus" (audit log, logs).</summary>
    public string Describe() =>
        Magnification is { } z ? $"Zoom {z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"
        : FocusPosition is { } f ? $"Focus {Math.Round(f * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : FocusWindow is not null ? "Autofocus area"
        : ResetFocus ? "Reset focus"
        : "Autofocus";
}

/// <summary>Zoom and focus of managed devices (live view). Tests replace it with fakes.</summary>
public interface IDeviceOptics
{
    /// <summary>What the lens can do and where it is; <see cref="OpticsState.None"/> for a device without zoom or focus.</summary>
    Task<OpticsState> GetAsync(Guid deviceId, int sensor, CancellationToken ct);

    /// <summary>
    /// Applies one change and returns the state read again. Values out of range are clamped first (zoom 1..maximum, focus
    /// 0..1, the area inside the picture). Throws <see cref="InvalidOperationException"/> for a device without the
    /// optics the change needs, <see cref="OpticsException"/> when the device refuses.
    /// </summary>
    Task<OpticsState> SetAsync(Guid deviceId, int sensor, OpticsChange change, CancellationToken ct);
}

/// <summary>The Optics Control API over the device's authenticated VAPIX client.</summary>
public sealed class AxisDeviceOptics(VapixClientFactory clients) : IDeviceOptics
{
    public async Task<OpticsState> GetAsync(Guid deviceId, int sensor, CancellationToken ct)
    {
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        return await client.GetOpticsAsync(sensor, ct).ConfigureAwait(false);
    }

    public async Task<OpticsState> SetAsync(Guid deviceId, int sensor, OpticsChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        var state = await client.GetOpticsAsync(sensor, ct).ConfigureAwait(false);
        await ApplyAsync(client, state, change, ct).ConfigureAwait(false);
        return await client.GetOpticsAsync(sensor, ct).ConfigureAwait(false);
    }

    /// <summary>Checks the change against <paramref name="state"/> (fresh capabilities) and sends it.</summary>
    internal static async Task ApplyAsync(VapixClient client, OpticsState state, OpticsChange change, CancellationToken ct)
    {
        const string NoFocus = "This camera has no remote focus.";
        var id = state.OpticsId;
        if (change.Magnification is { } zoom)
        {
            if (!state.HasZoom)
            {
                throw new InvalidOperationException("This camera has no remote zoom.");
            }

            await client.SetMagnificationAsync(id, Math.Clamp(zoom, 1, state.MaxMagnification), ct).ConfigureAwait(false);
        }
        else if (!state.HasFocus)
        {
            throw new InvalidOperationException(NoFocus);
        }
        else if (change.FocusPosition is { } focus)
        {
            await client.SetFocusAsync(id, Math.Clamp(focus, 0, 1), ct).ConfigureAwait(false);
        }
        else if (change.FocusWindow is { } window)
        {
            await client.SetFocusWindowAsync(id, window.Clamped(), ct).ConfigureAwait(false);
            await client.PerformAutofocusAsync(id, ct).ConfigureAwait(false);
        }
        else if (change.ResetFocus)
        {
            await client.ResetFocusAsync(id, ct).ConfigureAwait(false);
            await client.PerformAutofocusAsync(id, ct).ConfigureAwait(false);
        }
        else if (change.Autofocus)
        {
            await client.PerformAutofocusAsync(id, ct).ConfigureAwait(false);
        }
    }
}
