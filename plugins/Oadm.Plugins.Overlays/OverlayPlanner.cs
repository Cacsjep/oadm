using System.Globalization;

namespace Oadm.Plugins.Overlays;

/// <summary>What one view area needs: the overlays to remove (from the list just read), to add, and those already there.</summary>
/// <param name="Remove">Text overlays of the view area to remove, by the identity of the list just read.</param>
/// <param name="Add">Overlays to add.</param>
/// <param name="AlreadyThere">Overlays of the list the camera already shows exactly like this.</param>
/// <param name="NotFound">Remove matching: texts of the list the camera does not show.</param>
/// <param name="AddFirst">
/// Replace: the free slots take the additions before the removals, so a refused add leaves the old overlays in place.
/// </param>
/// <param name="ViewArea">The view area (camera=N) the plan is for.</param>
public sealed record OverlayPlan(
    IReadOnlyList<DeviceOverlay> Remove,
    IReadOnlyList<TextOverlay> Add,
    IReadOnlyList<TextOverlay> AlreadyThere,
    IReadOnlyList<string> NotFound,
    bool AddFirst = false,
    int ViewArea = DynamicOverlayApi.ViewArea)
{
    public bool ChangesNothing => Remove.Count == 0 && Add.Count == 0;
}

/// <summary>
/// Plans one camera from the overlays and limits read right before the writes. Matches by content, never by a stored identity
/// (identities can change after a restart). Every limit is checked here, before the first write.
/// </summary>
public static class OverlayPlanner
{
    /// <summary>
    /// One plan per view area (the first of each sensor), all built before the first write: an
    /// <see cref="OverlayDeviceException"/> ending in "Nothing was changed." when one of them cannot take the list.
    /// </summary>
    public static IReadOnlyList<OverlayPlan> BuildAll(OverlaysPayload payload, DeviceOverlays current, OverlayCapabilities limits, IReadOnlyList<int> viewAreas)
    {
        ArgumentNullException.ThrowIfNull(viewAreas);
        return [.. viewAreas.Select(v => Build(payload, current, limits, v, viewAreas.Count > 1))];
    }

    /// <summary>The plan of one view area, or an <see cref="OverlayDeviceException"/> ending in "Nothing was changed." when it cannot take it.</summary>
    public static OverlayPlan Build(OverlaysPayload payload, DeviceOverlays current, OverlayCapabilities limits, int viewArea = DynamicOverlayApi.ViewArea, bool nameViewArea = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(limits);
        var existing = current.TextsOf(viewArea);
        if (payload.Mode == OverlaysMode.RemoveAll)
        {
            return new OverlayPlan(existing, [], [], [], ViewArea: viewArea);
        }

        if (payload.Mode == OverlaysMode.RemoveMatching)
        {
            var texts = payload.Overlays.Select(o => OverlayText.Normalize(o.Text)).ToHashSet(StringComparer.Ordinal);
            var remove = existing.Where(e => texts.Contains(OverlayText.Normalize(e.Overlay.Text))).ToList();
            var shown = remove.Select(r => OverlayText.Normalize(r.Overlay.Text)).ToHashSet(StringComparer.Ordinal);
            var missing = payload.Overlays.Select(o => OverlayText.Normalize(o.Text)).Distinct(StringComparer.Ordinal).Where(t => !shown.Contains(t)).ToList();
            return new OverlayPlan(remove, [], [], missing, ViewArea: viewArea);
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
        var images = current.ImagesOf(viewArea);
        CheckSlots(add.Count, existing.Count - removals.Count, images, limits, nameViewArea ? viewArea : null);
        var addFirst = removals.Count > 0 && add.Count > 0 && Room(existing.Count, images, limits) is { } room && add.Count <= room;
        return new OverlayPlan(removals, add, there, [], addFirst, viewArea);
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

    /// <summary>Text overlays a view area still takes next to these; null when the camera does not say.</summary>
    private static int? Room(int texts, int images, OverlayCapabilities limits) =>
        limits.Slots is { } total
            ? Math.Max(0, total - (texts * limits.SlotsPerText) - (images * limits.SlotsPerImage)) / limits.SlotsPerText
            : null;

    /// <summary>
    /// The free slots of a view area: numAvailableSlots is the view area's total (it stays the same after an add, AXIS OS 10.12
    /// and 12.11), less the slots of the text and image overlays that stay. The message names the view area of a camera
    /// with several sensors.
    /// </summary>
    private static void CheckSlots(int adds, int textsStaying, int images, OverlayCapabilities limits, int? viewArea)
    {
        var where = viewArea is { } v ? string.Create(CultureInfo.InvariantCulture, $" in view area {v}") : string.Empty;
        if (adds == 0 || Room(textsStaying, images, limits) is not { } room)
        {
            return;
        }

        if (adds > room)
        {
            throw new OverlayDeviceException(room == 0
                ? $"The camera has no free overlay slot{where}. Nothing was changed."
                : string.Create(CultureInfo.InvariantCulture,
                    $"The camera has room for {room} more {(room == 1 ? "overlay" : "overlays")}{where}, not {adds}. Nothing was changed."));
        }
    }
}
