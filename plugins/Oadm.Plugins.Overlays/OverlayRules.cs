using System.Globalization;

using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Overlays;

/// <summary>
/// The dialog's rules for the payload, each at its JSON field (the dialog, <c>ValidatePayload</c> and every task use them).
/// The camera's own limits (text length, font sizes, free slots) are checked per camera before the first write.
/// </summary>
public static class OverlayRules
{
    /// <summary>Most overlays one dialog sends (cameras recorded so far have 4 or 8 slots per view area).</summary>
    public const int MaxOverlays = 8;

    /// <summary>The longest text the overlay API takes (maxTextLength of every camera recorded so far).</summary>
    public const int MaxTextLength = 512;

    /// <summary>The widest font sizes a payload may ask for; each camera checks its own range.</summary>
    public const int MinFontSize = 1;

    /// <summary>See <see cref="MinFontSize"/>.</summary>
    public const int MaxFontSize = 999;

    public const string NoSettings = "The task has no settings. Nothing was changed.";
    public const string Unreadable = "The task settings cannot be read. Nothing was changed.";
    public const string EnterText = "Enter the text.";
    public const string NoControlCharacters = "Tabs and control characters are not allowed.";
    public const string AddOne = "Add an overlay.";
    public const string CustomNeedsPlace = "A custom position needs two numbers from -1 to 1.";

    public static string TooMany => string.Create(CultureInfo.InvariantCulture, $"At most {MaxOverlays} overlays.");

    public static string TooLong(int max) => string.Create(CultureInfo.InvariantCulture, $"At most {max} characters.");

    public static string SizeRange(int min, int max) => string.Create(CultureInfo.InvariantCulture, $"Enter {min} to {max}.");

    /// <summary>Every problem of the payload, at its JSON field ("overlays[1].text"); empty when it can run.</summary>
    public static IReadOnlyList<PayloadError> Check(OverlaysPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new List<PayloadError>();
        if (!Enum.IsDefined(payload.Mode))
        {
            errors.Add(new PayloadError(OverlayFields.Mode, "Choose add, replace or remove."));
        }

        var overlays = payload.Overlays ?? [];
        if (overlays.Count == 0 && payload.Mode != OverlaysMode.Replace)
        {
            errors.Add(new PayloadError(OverlayFields.Overlays, AddOne));
        }

        if (overlays.Count > MaxOverlays)
        {
            errors.Add(new PayloadError(OverlayFields.Overlays, TooMany));
            return errors;
        }

        for (var i = 0; i < overlays.Count; i++)
        {
            var overlay = overlays[i];
            if (overlay is null)
            {
                errors.Add(new PayloadError(OverlayFields.Of(i, "text"), EnterText));
                continue;
            }

            if (TextError(overlay.Text, MaxTextLength) is { } text)
            {
                errors.Add(new PayloadError(OverlayFields.Of(i, "text"), text));
            }

            if (payload.Mode == OverlaysMode.RemoveMatching)
            {
                continue; // only the text counts
            }

            if (!Enum.IsDefined(overlay.Position) || (overlay.Position == OverlayPosition.Custom && !IsCustomPlace(overlay.CustomPosition)))
            {
                errors.Add(new PayloadError(OverlayFields.Of(i, "position"), CustomNeedsPlace));
            }

            if (!Enum.IsDefined(overlay.TextColor) || !Enum.IsDefined(overlay.BackgroundColor) || !Enum.IsDefined(overlay.OutlineColor))
            {
                errors.Add(new PayloadError(OverlayFields.Of(i, "textColor"), "Choose black, white, red, transparent or semi-transparent."));
            }

            if (SizeError(overlay.FontSize, MinFontSize, MaxFontSize) is { } size)
            {
                errors.Add(new PayloadError(OverlayFields.Of(i, "fontSize"), size));
            }
        }

        return errors;
    }

    /// <summary>The text's problem, or null: not empty, at most <paramref name="max"/> characters, line breaks only.</summary>
    public static string? TextError(string? text, int max)
    {
        var normalized = OverlayText.Normalize(text ?? string.Empty);
        if (normalized.Trim().Length == 0)
        {
            return EnterText;
        }

        if (normalized.Length > max)
        {
            return TooLong(max);
        }

        return normalized.Any(c => char.IsControl(c) && c != '\n') ? NoControlCharacters : null;
    }

    /// <summary>The size's problem, or null (no size = the camera's default).</summary>
    public static string? SizeError(int? size, int min, int max) =>
        size is { } value && (value < min || value > max) ? SizeRange(min, max) : null;

    /// <summary>Two numbers from -1 to 1.</summary>
    public static bool IsCustomPlace(IReadOnlyList<double>? place) =>
        place is { Count: 2 } && place.All(v => double.IsFinite(v) && v >= -1 && v <= 1);

    /// <summary>Throws the first problem as an <see cref="ArgumentException"/> before anything is sent.</summary>
    public static void ThrowIfInvalid(OverlaysPayload payload)
    {
        if (Check(payload) is [var first, ..])
        {
            throw new ArgumentException(first.Message.TrimEnd('.') + ". Nothing was changed.");
        }
    }
}
