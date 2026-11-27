namespace Oadm.Plugins.LicensePlates.Plates;

/// <summary>The checks of a plate and a plate tag, the same for the dialog, the import and the tools; errors by field (the JSON names).</summary>
internal static class PlateRules
{
    public const string TextField = "text";
    public const string NameField = "name";
    public const string TagsField = "tags";
    public const string FromField = "allowedFrom";
    public const string ToField = "allowedTo";
    public const string DeleteField = "deleteWhenEnds";
    public const string ColorField = "color";

    /// <summary>The plate as it is saved (text normalized, name trimmed, tags distinct) and its errors.</summary>
    public static (Plate Plate, Dictionary<string, string> Errors) Check(Plate incoming, Func<Guid, bool> tagExists)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(tagExists);
        var plate = incoming with
        {
            Text = PlateText.Normalize(incoming.Text),
            Name = (incoming.Name ?? string.Empty).Trim(),
            Tags = [.. (incoming.Tags ?? []).Distinct()],
            AllowedFrom = Minutes(incoming.AllowedFrom),
            AllowedTo = Minutes(incoming.AllowedTo),

            // Delete when it ends only with an Allowed to (the dialog's switch is on by default for a new plate).
            DeleteWhenEnds = incoming.DeleteWhenEnds && incoming.AllowedTo is not null,
        };
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (PlateText.Problem(plate.Text) is { } problem)
        {
            errors[TextField] = problem;
        }

        if (plate.Name.Length > LicensePlateLimits.MaxNameLength)
        {
            errors[NameField] = $"A name has at most {LicensePlateLimits.MaxNameLength} characters.";
        }

        if (plate.Tags.Any(t => !tagExists(t)))
        {
            errors[TagsField] = LicensePlateTexts.TagGone;
        }

        if (plate.AllowedFrom is { } from && plate.AllowedTo is { } to && to <= from)
        {
            errors[ToField] = LicensePlateTexts.AfterFrom;
        }

        return (plate, errors);
    }

    /// <summary>The tag as it is saved (name trimmed) and its errors; names are unique without regard to case.</summary>
    public static (PlateTag Tag, Dictionary<string, string> Errors) Check(PlateTag incoming, IEnumerable<PlateTag> others)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(others);
        var tag = incoming with { Name = (incoming.Name ?? string.Empty).Trim() };
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tag.Name.Length == 0)
        {
            errors[NameField] = "Enter a name.";
        }
        else if (tag.Name.Length > LicensePlateLimits.MaxTagNameLength)
        {
            errors[NameField] = $"A name has at most {LicensePlateLimits.MaxTagNameLength} characters.";
        }
        else if (tag.Name.Contains(',', StringComparison.Ordinal))
        {
            // The events carry the tag names comma separated ("contains" in a rule), the CSV too.
            errors[NameField] = "A name cannot hold a comma.";
        }
        else if (others.Any(o => o.Id != tag.Id && string.Equals(o.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
        {
            errors[NameField] = "A plate tag with this name exists.";
        }

        if (!Enum.IsDefined(tag.Color))
        {
            errors[ColorField] = "Choose a colour.";
        }

        return (tag, errors);
    }

    /// <summary>Times are kept to the minute, without a kind (wall-clock of the server).</summary>
    public static DateTime? Minutes(DateTime? time) =>
        time is { } t ? DateTime.SpecifyKind(new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0), DateTimeKind.Unspecified) : null;
}
