using System.Globalization;

namespace Oadm.Plugins.Overlays;

/// <summary>What one camera needs: the overlays to remove (from the list just read), to add, and those already there.</summary>
/// <param name="Remove">Text overlays of view area 1 to remove, by the identity of the list just read.</param>
/// <param name="Add">Overlays to add.</param>
/// <param name="AlreadyThere">Overlays of the list the camera already shows exactly like this.</param>
/// <param name="NotFound">Remove matching: texts of the list the camera does not show.</param>
public sealed record OverlayPlan(
    IReadOnlyList<DeviceOverlay> Remove,
    IReadOnlyList<TextOverlay> Add,
    IReadOnlyList<TextOverlay> AlreadyThere,
    IReadOnlyList<string> NotFound)
{
    public bool ChangesNothing => Remove.Count == 0 && Add.Count == 0;
}

/// <summary>
/// Plans one camera from the overlays and limits read right before the writes. Matches by content, never by a stored identity
/// (identities can change after a restart). Every limit is checked here, before the first write.
/// </summary>
public static class OverlayPlanner
{
    /// <summary>The plan, or an <see cref="OverlayDeviceException"/> ending in "Nothing was changed." when the camera cannot take it.</summary>
    public static OverlayPlan Build(OverlaysPayload payload, DeviceOverlays current, OverlayCapabilities limits)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(limits);
        var existing = current.TextsOf(DynamicOverlayApi.ViewArea);
        if (payload.Mode == OverlaysMode.RemoveAll)
        {
            return new OverlayPlan(existing, [], [], []);
        }

        if (payload.Mode == OverlaysMode.RemoveMatching)
        {
            var texts = payload.Overlays.Select(o => OverlayText.Normalize(o.Text)).ToHashSet(StringComparer.Ordinal);
            var remove = existing.Where(e => texts.Contains(OverlayText.Normalize(e.Overlay.Text))).ToList();
            var shown = remove.Select(r => OverlayText.Normalize(r.Overlay.Text)).ToHashSet(StringComparer.Ordinal);
            var missing = payload.Overlays.Select(o => OverlayText.Normalize(o.Text)).Distinct(StringComparer.Ordinal).Where(t => !shown.Contains(t)).ToList();
            return new OverlayPlan(remove, [], [], missing);
        }

        CheckLimits(payload.Overlays, limits);
        var unused = existing.ToList();
        var add = new List<TextOverlay>();
        var there = new List<TextOverlay>();
        foreach (var wanted in payload.Overlays)
        {
            var match = unused.FirstOrDefault(e => Same(e.Overlay, wanted));
            if (match is null)
            {
                add.Add(wanted);
            }
            else
            {
                unused.Remove(match);
                there.Add(wanted);
            }
        }

        var removals = payload.Mode == OverlaysMode.Replace ? unused : [];
        CheckSlots(add.Count, existing.Count - removals.Count, current.ImagesOf(DynamicOverlayApi.ViewArea), limits);
        return new OverlayPlan(removals, add, there, []);
    }

    /// <summary>The camera already shows <paramref name="wanted"/>: same text, place, colours, and size (when one is asked for).</summary>
    public static bool Same(TextOverlay shown, TextOverlay wanted)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(wanted);
        return string.Equals(OverlayText.Normalize(shown.Text), OverlayText.Normalize(wanted.Text), StringComparison.Ordinal)
            && SamePlace(shown, wanted)
            && shown.TextColor == wanted.TextColor
            && shown.BackgroundColor == wanted.BackgroundColor
            && shown.OutlineColor == wanted.OutlineColor
            && (wanted.FontSize is null || wanted.FontSize == shown.FontSize);
    }

    private static bool SamePlace(TextOverlay a, TextOverlay b)
    {
        if (a.Position != b.Position)
        {
            return false;
        }

        return a.Position != OverlayPosition.Custom
            || (a.CustomPosition is { Count: 2 } p && b.CustomPosition is { Count: 2 } q && Math.Abs(p[0] - q[0]) < 0.001 && Math.Abs(p[1] - q[1]) < 0.001);
    }

    /// <summary>Text length and size against the camera's own limits.</summary>
    private static void CheckLimits(IReadOnlyList<TextOverlay> overlays, OverlayCapabilities limits)
    {
        foreach (var overlay in overlays)
        {
            var text = OverlayText.Normalize(overlay.Text);
            if (text.Length > limits.MaxTextLength)
            {
                throw new OverlayDeviceException(string.Create(CultureInfo.InvariantCulture,
                    $"The text \"{OverlayNames.Brief(text)}\" is longer than the {limits.MaxTextLength} characters the camera takes. Nothing was changed."));
            }

            if (overlay.FontSize is { } size && ((limits.MinFontSize is { } min && size < min) || (limits.MaxFontSize is { } max && size > max)))
            {
                throw new OverlayDeviceException(string.Create(CultureInfo.InvariantCulture,
                    $"The camera takes sizes from {limits.MinFontSize ?? OverlayRules.MinFontSize} to {limits.MaxFontSize ?? OverlayRules.MaxFontSize}, not {size}. Nothing was changed."));
            }
        }
    }

    /// <summary>
    /// The free slots of view area 1: numAvailableSlots is the view area's total (it stays the same after an add, AXIS OS 10.12
    /// and 12.11), less the slots of the text and image overlays that stay.
    /// </summary>
    private static void CheckSlots(int adds, int textsStaying, int images, OverlayCapabilities limits)
    {
        if (adds == 0 || limits.Slots is not { } total)
        {
            return;
        }

        var free = Math.Max(0, total - (textsStaying * limits.SlotsPerText) - (images * limits.SlotsPerImage));
        var room = free / limits.SlotsPerText;
        if (adds > room)
        {
            throw new OverlayDeviceException(room == 0
                ? "The camera has no free overlay slot in view area 1. Nothing was changed."
                : string.Create(CultureInfo.InvariantCulture,
                    $"The camera has room for {room} more {(room == 1 ? "overlay" : "overlays")} in view area 1, not {adds}. Nothing was changed."));
        }
    }
}
