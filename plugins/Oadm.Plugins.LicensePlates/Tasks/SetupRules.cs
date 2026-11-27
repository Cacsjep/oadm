using System.Globalization;

using Oadm.Plugins.LicensePlates.Alpv;

namespace Oadm.Plugins.LicensePlates.Tasks;

/// <summary>
/// The checks of the task Set up (the dialog, Copy settings and the task itself, before the first write) and the changes it
/// sends: only the recognition values that differ from the camera's, only at the paths the camera's configuration returned
/// (rule 6).
/// </summary>
internal static class SetupRules
{
    public const int MaxCorners = 8;
    public const int MinCorners = 3;
    public const int MaxPoint = 1000;
    public const int MaxAngle = 360_000;

    /// <summary>The errors of a payload by field ("areas", "recognition.plateWidthMax"); empty when it is valid.</summary>
    public static Dictionary<string, string> Check(SetupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.Areas is { } areas)
        {
            if (areas.Count is 0 or > 2 || areas.All(a => a.Id != 1) || areas.Any(a => a.Id is not (1 or 2)) || areas.Select(a => a.Id).Distinct().Count() != areas.Count)
            {
                errors["areas"] = "Draw area 1, and area 2 if it is used.";
            }

            foreach (var area in areas)
            {
                var field = string.Create(CultureInfo.InvariantCulture, $"areas.{area.Id}");
                if (area.Corners.Count is < MinCorners or > MaxCorners)
                {
                    errors[field] = $"An area has {MinCorners} to {MaxCorners} corners.";
                }
                else if (area.Corners.Any(c => c.X is < 0 or > MaxPoint || c.Y is < 0 or > MaxPoint))
                {
                    errors[field] = "Keep the corners inside the picture.";
                }
                else if (area.Angle is < 0 or > MaxAngle)
                {
                    errors[field] = "The direction is 0 to 360 degrees.";
                }
            }
        }

        if (payload.Recognition is { } r)
        {
            // Codes of ALPV's lists (AlpvLists): a region number, a country of 2 or 3 letters or "0" (none), a state code or empty.
            if (r.Region is { } region && (region.Trim().Length is 0 or > 6 || !region.Trim().All(char.IsAsciiDigit)))
            {
                errors["recognition.region"] = "Choose a region.";
            }

            if (r.CameraLocation is { } location && location.Trim() is { Length: > 0 } code && code != AlpvLists.NoCountry
                && (code.Length is < 2 or > 3 || !code.All(char.IsAsciiLetterUpper)))
            {
                errors["recognition.cameraLocation"] = "Choose a country.";
            }

            if (r.State is { } state && (state.Trim().Length > 8 || !state.Trim().All(char.IsAsciiLetterOrDigit)))
            {
                errors["recognition.state"] = "Choose a state.";
            }

            Range(errors, "recognition.plateWidth", r.PlateWidthMin, r.PlateWidthMax, 1, 4_000, "The plate width is 1 to 4,000 pixels.", "Choose a width from the smallest to the largest.");
            Range(errors, "recognition.characters", r.CharactersMin, r.CharactersMax, 1, 20, "A plate has 1 to 20 characters.", "Choose a number from the fewest to the most.");
            // ALPV keeps 0.2 to 1.0 (the Set up slider: 20 to 100 %); anything else is refused before the first write.
            if (r.ConfidenceThreshold is { } t && (double.IsNaN(t) || t is < 0.2 or > 1))
            {
                errors["recognition.confidenceThreshold"] = "The threshold is 0.2 to 1.";
            }
        }

        return errors;
    }

    private static void Range(Dictionary<string, string> errors, string field, int? min, int? max, int low, int high, string outside, string order)
    {
        if (min is { } a && (a < low || a > high))
        {
            errors[field + "Min"] = outside;
        }

        if (max is { } b && (b < low || b > high))
        {
            errors[field + "Max"] = outside;
        }

        if (min is { } from && max is { } to && from > to && !errors.ContainsKey(field + "Max"))
        {
            errors[field + "Max"] = order;
        }
    }

    /// <summary>The recognition values to write: path and text, only where the camera has the field and the value differs.</summary>
    public static IReadOnlyDictionary<string, string> Changes(AlpvConfig config, RecognitionSettings? wanted)
    {
        ArgumentNullException.ThrowIfNull(config);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted is null)
        {
            return changes;
        }

        void Text(string path, string? value)
        {
            if (value is not null && config.Has(path) && !string.Equals(config.Get(path), value.Trim(), StringComparison.Ordinal))
            {
                changes[path] = value.Trim();
            }
        }

        void Whole(IEnumerable<string> paths, int? value)
        {
            if (value is not { } v)
            {
                return;
            }

            foreach (var path in config.Present(paths))
            {
                if (config.Whole(path) != v)
                {
                    changes[path] = v.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        Text(AlpvConfig.Region, wanted.Region);
        Text(AlpvConfig.CameraLocation, wanted.CameraLocation);
        Text(AlpvConfig.State, wanted.State);
        Whole([AlpvConfig.PlateWidthMin], wanted.PlateWidthMin);
        Whole([AlpvConfig.PlateWidthMax], wanted.PlateWidthMax);
        Whole(AlpvConfig.CharactersMin, wanted.CharactersMin);
        Whole(AlpvConfig.CharactersMax, wanted.CharactersMax);
        if (wanted.ConfidenceThreshold is { } t && config.Has(AlpvConfig.Threshold) && (config.Number(AlpvConfig.Threshold) is not { } current || Math.Abs(current - t) > 0.0000005))
        {
            changes[AlpvConfig.Threshold] = t.ToString("0.######", CultureInfo.InvariantCulture);
        }

        return changes;
    }

    /// <summary>The paths of <paramref name="changes"/> whose values the camera did not keep (read back after the write).</summary>
    public static IReadOnlyList<string> NotKept(AlpvConfig after, IReadOnlyDictionary<string, string> changes)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changes);
        return [.. changes.Where(c =>
        {
            var now = after.Get(c.Key);
            if (string.Equals(now, c.Value, StringComparison.Ordinal))
            {
                return false;
            }

            return !(double.TryParse(now, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                && Math.Abs(a - b) < 0.0000005);
        }).Select(c => c.Key)];
    }
}
