using System.Globalization;

using Oadm.Plugins.LicensePlates.Plates;

namespace Oadm.Plugins.LicensePlates.Reads;

/// <summary>Which of ALPV's events a notification is.</summary>
public enum AlpvEventKind
{
    New = 0,
    Update = 1,
    Lost = 2,
}

/// <summary>
/// One event of ALPV (<c>tnsaxis:CameraApplicationPlatform/ALPV.New</c>, <c>.Update</c>, <c>.Lost</c>) with the fields OADM
/// uses (docs/spec/license-plate-center.md "ALPV on the camera"). ALPV's own list result (<c>listName</c>, <c>listMode</c>,
/// <c>listDescription</c>, <c>action</c>) is ignored.
/// </summary>
public sealed record AlpvEvent
{
    /// <summary>The topics OADM asks the event stream for.</summary>
    public static readonly IReadOnlyList<string> Topics =
    [
        "tnsaxis:CameraApplicationPlatform/ALPV.New",
        "tnsaxis:CameraApplicationPlatform/ALPV.Update",
        "tnsaxis:CameraApplicationPlatform/ALPV.Lost",
    ];

    public AlpvEventKind Kind { get; init; }

    /// <summary>The text as ALPV read it.</summary>
    public string RawText { get; init; } = string.Empty;

    /// <summary>The text for matching (<see cref="PlateText.OfRead"/>).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary><c>lpConfidence</c>, 0 to 1.</summary>
    public double Confidence { get; init; }

    public string CarId { get; init; } = string.Empty;

    public string? CarState { get; init; }

    public PlateDirection Direction { get; init; }

    public int? Area { get; init; }

    public string? Country { get; init; }

    public string? View { get; init; }

    public string? VehicleType { get; init; }

    public string? VehicleColor { get; init; }

    public string? VehicleMake { get; init; }

    public string? VehicleModel { get; init; }

    /// <summary>The plate crop (base64 as ALPV sends it); null when none.</summary>
    public string? Image { get; init; }

    /// <summary><c>capture_timestamp</c>; null when ALPV did not send one.</summary>
    public DateTimeOffset? Captured { get; init; }

    /// <summary>The kind of a topic ("…/ALPV.New", "…/ALPV/Update"); null for any other topic.</summary>
    public static AlpvEventKind? KindOf(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        if (!topic.Contains("ALPV", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var last = topic[(topic.LastIndexOfAny(['/', '.', ':']) + 1)..];
        return last.ToUpperInvariant() switch
        {
            "NEW" => AlpvEventKind.New,
            "UPDATE" => AlpvEventKind.Update,
            "LOST" => AlpvEventKind.Lost,
            _ => null,
        };
    }

    /// <summary>The event of a notification of the stream; null for another topic or a read without a car id.</summary>
    public static AlpvEvent? Of(StreamNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (KindOf(notification.Topic) is not { } kind)
        {
            return null;
        }

        string? Value(string name) => notification.Data.TryGetValue(name, out var v) || notification.Source.TryGetValue(name, out v)
            ? string.IsNullOrWhiteSpace(v) ? null : v.Trim()
            : null;

        var raw = Value("text") ?? string.Empty;
        var carId = Value("carID") ?? Value("carId");
        if (carId is null)
        {
            return null;
        }

        return new AlpvEvent
        {
            Kind = kind,
            RawText = raw,
            Text = PlateText.OfRead(raw),
            Confidence = ConfidenceOf(Value("lpConfidence")),
            CarId = carId,
            CarState = Value("carState"),
            Direction = Value("carMoveDirection")?.ToUpperInvariant() switch
            {
                "IN" => PlateDirection.In,
                "OUT" => PlateDirection.Out,
                _ => PlateDirection.Unknown,
            },
            Area = AreaOf(Value("roiID")),
            Country = Value("country"),
            View = Value("view"),
            VehicleType = Value("vehicleType"),
            VehicleColor = Value("vehicleColor"),
            VehicleMake = Value("vehicleMake"),
            VehicleModel = Value("vehicleModel"),
            Image = Value("lpImage"),
            Captured = long.TryParse(Value("capture_timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : notification.Time,
        };
    }

    /// <summary>0 to 1 (a value above 1 is read as a percentage).</summary>
    internal static double ConfidenceOf(string? text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value > 1 ? value / 100 : value, 0, 1);
    }

    /// <summary>"1", "roi2", "Roi1": the area's number; null for none.</summary>
    internal static int? AreaOf(string? text)
    {
        var digits = new string((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var area) && area is 1 or 2 ? area : null;
    }

    /// <summary><see cref="Confidence"/> as the log shows it: times 100, whole.</summary>
    public int Percent => (int)Math.Round(Confidence * 100, MidpointRounding.AwayFromZero);
}
