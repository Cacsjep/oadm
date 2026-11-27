using System.Globalization;

namespace Oadm.Plugins.LicensePlates;

/// <summary>
/// The columns of the Log tab the user chooses (decision 2026-11-27, like the device grid): keys, headers, the default
/// set, and the text of a cell for the grid and the CSV and PDF, so all three say the same.
/// </summary>
public static class LogColumns
{
    public const string Time = "time";
    public const string Camera = "camera";
    public const string Picture = "picture";
    public const string Plate = "plate";
    public const string ReadAs = "readAs";
    public const string Name = "name";
    public const string Tags = "tags";
    public const string Result = "result";
    public const string Direction = "direction";
    public const string Area = "area";
    public const string Country = "country";
    public const string State = "state";
    public const string VehicleType = "vehicleType";
    public const string VehicleColor = "vehicleColor";
    public const string VehicleMake = "vehicleMake";
    public const string VehicleModel = "vehicleModel";
    public const string View = "view";
    public const string Confidence = "confidence";

    /// <summary>Every column in its default order with its header and whether it shows by default (the columns before the choice).</summary>
    public static IReadOnlyList<(string Key, string Header, bool IsDefault)> All { get; } =
    [
        (Time, "Time", true),
        (Camera, "Camera", true),
        (Picture, "Picture", true),
        (Plate, "Plate", true),
        (ReadAs, "Read as", false),
        (Name, "Name", true),
        (Tags, "Tags", true),
        (Result, "Result", true),
        (Direction, "Direction", true),
        (Area, "Area", false),
        (Country, "Country", false),
        (State, "State", false),
        (VehicleType, "Vehicle type", false),
        (VehicleColor, "Vehicle colour", false),
        (VehicleMake, "Vehicle make", false),
        (VehicleModel, "Vehicle model", false),
        (View, "View", false),
        (Confidence, "Confidence", true),
    ];

    /// <summary>The keys shown before the user chose any.</summary>
    public static IReadOnlyList<string> Defaults { get; } = [.. All.Where(c => c.IsDefault).Select(c => c.Key)];

    /// <summary>The header of a key; the key itself when unknown.</summary>
    public static string Header(string key) => All.FirstOrDefault(c => c.Key == key).Header ?? key;

    /// <summary>The known keys of <paramref name="keys"/> in their order, each once; null or none: <see cref="Defaults"/>.</summary>
    public static IReadOnlyList<string> Valid(IReadOnlyList<string>? keys)
    {
        var known = All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var valid = (keys ?? []).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        return valid.Count == 0 ? Defaults : valid;
    }

    /// <summary>"27 Nov 2026 14:04:14": the day and the time, on every row (grid and files).</summary>
    public static string TimeText(DateTimeOffset local, CultureInfo culture) => local.ToString("d MMM yyyy HH:mm:ss", culture);

    /// <summary>The text of a cell other than Time, Picture and Tags; empty for a gap row's read columns.</summary>
    public static string Text(PlateRead row, string key)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Gap && key is not (Camera))
        {
            return string.Empty;
        }

        return key switch
        {
            Camera => row.Camera,
            Plate => row.Plate,
            ReadAs => row.ReadAs ?? string.Empty,
            Name => row.Name,
            Tags => string.Join(", ", row.Tags),
            Result => LicensePlateTexts.Result(row.Result),
            Direction => LicensePlateTexts.Direction(row.Direction),
            Area => row.Area?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
            Country => row.Country ?? string.Empty,
            State => row.State ?? string.Empty,
            VehicleType => row.VehicleType ?? string.Empty,
            VehicleColor => row.VehicleColor ?? string.Empty,
            VehicleMake => row.VehicleMake ?? string.Empty,
            VehicleModel => row.VehicleModel ?? string.Empty,
            View => ViewText(row.View),
            Confidence => row.Confidence.ToString(CultureInfo.CurrentCulture),
            _ => string.Empty,
        };
    }

    /// <summary>"Front", "Rear", or what the camera sent.</summary>
    public static string ViewText(string? view) => view?.ToUpperInvariant() switch
    {
        null or "" => string.Empty,
        "FRONT" => "Front",
        "REAR" => "Rear",
        _ => view,
    };
}
