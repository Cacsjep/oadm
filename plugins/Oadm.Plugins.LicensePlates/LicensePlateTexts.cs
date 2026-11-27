using System.Globalization;

using Oadm.Sdk.Sites;

namespace Oadm.Plugins.LicensePlates;

/// <summary>The plugin's texts, one phrase per concept (CLAUDE.md "Wording"), shared by the server and the client part.</summary>
public static class LicensePlateTexts
{
    public const string Connecting = "Connecting";
    public const string Connected = "Connected";
    public const string ConnectionLost = "Connection lost";
    public const string EventsOff = "Events are off in ALPV";
    public const string AlpvStopped = "ALPV is stopped";
    public const string AlpvNotInstalled = "ALPV is not installed";
    public const string AlpvNoLicense = "ALPV has no license";
    public const string AlpvNeedsVersion3 = "ALPV 3 is needed";
    public const string OtherSensor = "ALPV reads another sensor";
    public const string Rotated = "Rotated pictures are set up on the ALPV page";
    public const string NothingChanged = "Nothing was changed.";

    public const string Allowed = "Allowed";
    public const string NotAllowed = "Not allowed";
    public const string Unknown = "Unknown plate";

    public const string In = "In";
    public const string Out = "Out";
    public const string UnknownDirection = "Unknown";

    public const string PlateExists = "This plate is already in the list.";
    public const string PlateGone = "The plate is no longer in the list.";
    public const string TagGone = "The plate tag no longer exists.";
    public const string CameraGone = "The camera is no longer in License Plate Center.";
    public const string AfterFrom = "Choose a time after Allowed from.";
    public const string NoFolder = "No folder for the log. Choose one in Settings.";
    public const string NotRunning = "License Plate Center is not running.";
    public const string PreviewGone = "The import ended. Choose the file again.";
    public const string NoCameraConnected = "No camera is connected.";
    public const string CameraNameTaken = "Another camera has this name.";

    public static readonly string TooManyPlates = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {LicensePlateLimits.MaxPlates:N0} plates.");
    public static readonly string TooManyTags = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {LicensePlateLimits.MaxTags} plate tags.");

    /// <summary>The status in words; <paramref name="detail"/> for <see cref="CameraStatus.DeviceProblem"/> and the site's name.</summary>
    public static string Status(CameraStatus status, string? detail = null) => status switch
    {
        CameraStatus.Connecting => Connecting,
        CameraStatus.Connected => Connected,
        CameraStatus.ConnectionLost => ConnectionLost,
        CameraStatus.EventsOff => EventsOff,
        CameraStatus.AlpvStopped => AlpvStopped,
        CameraStatus.AlpvNotInstalled => AlpvNotInstalled,
        CameraStatus.AlpvNoLicense => AlpvNoLicense,
        CameraStatus.AlpvNeedsVersion3 => AlpvNeedsVersion3,
        CameraStatus.OtherSensor => OtherSensor,
        CameraStatus.SiteOffline => SiteTexts.SiteOffline(detail),
        _ => detail ?? ConnectionLost,
    };

    public static string Result(ReadResult result) => result switch
    {
        ReadResult.Allowed => Allowed,
        ReadResult.NotAllowed => NotAllowed,
        _ => Unknown,
    };

    public static string Direction(PlateDirection direction) => direction switch
    {
        PlateDirection.In => In,
        PlateDirection.Out => Out,
        _ => UnknownDirection,
    };

    /// <summary>"Not allowed, ended 31 Oct 2026", "Not allowed, no tag allowed here", "Allowed", "Unknown plate".</summary>
    public static string Reason(ReadResult result, ReadReason reason, DateTime? time) => (result, reason) switch
    {
        (ReadResult.NotAllowed, ReadReason.NoTagHere) => "Not allowed, no tag allowed here",
        (ReadResult.NotAllowed, ReadReason.NotYet) => "Not allowed, starts " + Day(time),
        (ReadResult.NotAllowed, ReadReason.Ended) => "Not allowed, ended " + Day(time),
        (ReadResult.NotAllowed, ReadReason.SeveralPlates) => "Not allowed, matches more than one plate",
        _ => Result(result),
    };

    /// <summary>"Read as BN4I7HA".</summary>
    public static string ReadAs(string text) => "Read as " + text;

    /// <summary>"41 of 42 cameras connected".</summary>
    public static string ConnectedCount(int connected, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"{connected} of {total} {(total == 1 ? "camera" : "cameras")} connected");

    /// <summary>"Connection lost from 13:58 to 14:06" (times as given, in the reader's zone).</summary>
    public static string Gap(DateTimeOffset from, DateTimeOffset until) =>
        string.Create(CultureInfo.InvariantCulture, $"Connection lost from {from:HH:mm} to {until:HH:mm}");

    /// <summary>"The newest 10,000 of 48,213 reads".</summary>
    public static string Newest(int shown, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"The newest {shown:N0} of {total:N0} reads");

    /// <summary>"1 read", "48,213 reads".</summary>
    public static string Reads(int count) =>
        count == 1 ? "1 read" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} reads");

    private static string Day(DateTime? time) => time is { } t ? t.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty;
}
