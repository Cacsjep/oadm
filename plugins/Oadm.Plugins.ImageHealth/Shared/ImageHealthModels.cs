using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.ImageHealth;

/// <summary>Ids, names and limits shared by the server part, the page, the fake mode and the tests.</summary>
public static class ImageHealthPluginInfo
{
    public const string PluginId = "oadm.image-health";
    public const string DisplayName = "Image Health Dashboard";

    public const string IconKey = "eye";

    /// <summary>
    /// The app's status: 200 + JSON while it runs, 503 when it is installed but stopped, 404 when it is not installed.
    /// Needs the stored credentials (401 without).
    /// </summary>
    public const string StatusPath = "/local/AXISImageHealthAnalytics/v1/status";

    /// <summary>Auto refresh checks again this often (user decision 2026-10-09: 10 s, off by default).</summary>
    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>Rows per pushed event; more changed rows go out in several events.</summary>
    public const int MaxRowsPerEvent = 1_000;
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics (<c>ICorePluginContext.Events</c>).</summary>
public static class ImageHealthMethods
{
    /// <summary>
    /// {} -> <see cref="ImageHealthState"/>: asks every video device for the app status once (the page calls it when it
    /// opens, on Refresh and every 10 s with Auto refresh). While a check runs, it returns the current state without a
    /// second check. Rows and progress follow as events.
    /// </summary>
    public const string Check = "check";

    /// <summary>{} -> <see cref="ImageHealthState"/>: the current rows and counts, without asking any device.</summary>
    public const string GetState = "getState";

    /// <summary>Event: <see cref="RowsEvent"/>, changed and removed rows (batched every 500 ms).</summary>
    public const string RowsTopic = "rows";

    /// <summary>Event: <see cref="ImageHealthState"/> without rows: the check progress and counts changed.</summary>
    public const string StateTopic = "state";
}

/// <summary>State of the app on a camera (<see cref="ImageHealthRow.App"/>).</summary>
public static class AppStates
{
    /// <summary>Not checked yet.</summary>
    public const string Checking = "Checking";

    /// <summary>The app runs; the detections are filled.</summary>
    public const string Running = "Running";

    /// <summary>The app is installed but stopped (HTTP 503).</summary>
    public const string NotRunning = "Not running";

    /// <summary>The camera could not be read; <see cref="ImageHealthRow.Text"/> says why.</summary>
    public const string Error = "Error";
}

/// <summary>
/// The detections of AXIS Image Health Analytics, as the keys of its status answer
/// (<c>GET /local/AXISImageHealthAnalytics/v1/status</c>, AIHA 3.2.2):
/// <c>{"block":"normal","blur":"pending","redirect":"detected","under-exposure":"disabled","unsuitability":"suitable",...}</c>.
/// </summary>
public static class AihaDetections
{
    public const string Blur = "blur";
    public const string Block = "block";
    public const string Redirect = "redirect";
    public const string UnderExposure = "under-exposure";
    public const string Unsuitability = "unsuitability";

    /// <summary>Every detection in column order.</summary>
    public static readonly IReadOnlyList<string> All = [Blur, Block, Redirect, UnderExposure, Unsuitability];
}

/// <summary>A detection's state as the dashboard shows it.</summary>
public static class DetectionStates
{
    /// <summary>"normal", or "suitable" for unsuitability.</summary>
    public const string Ok = "OK";

    /// <summary>"pending": the app saw something and waits to confirm it.</summary>
    public const string Pending = "Pending";

    /// <summary>"detected", or "unsuitable" for unsuitability.</summary>
    public const string Detected = "Detected";

    /// <summary>"disabled": the detection is turned off in the app.</summary>
    public const string Off = "Off";

    /// <summary>The dashboard state of a raw value; another value is shown as the camera sent it.</summary>
    public static string FromRaw(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "normal" or "suitable" => Ok,
        "pending" => Pending,
        "detected" or "unsuitable" => Detected,
        "disabled" => Off,
        _ => raw.Trim(),
    };
}

/// <summary>
/// One camera with AXIS Image Health Analytics installed (running or not), or one that could not be read
/// (<see cref="AppStates.Error"/>). A detection is a <see cref="DetectionStates"/> value, null while the app does not run.
/// <see cref="ChangedUtc"/>: when OADM last saw a detection change between two checks.
/// </summary>
public sealed record ImageHealthRow(
    Guid DeviceId,
    string Address,
    string? Model,
    string App,
    string? Text,
    string? Blur,
    string? Block,
    string? Redirect,
    string? UnderExposure,
    string? Unsuitability,
    DateTimeOffset? ChangedUtc)
{
    /// <summary>The detection by its <see cref="AihaDetections"/> key.</summary>
    public string? Detection(string key) => key switch
    {
        AihaDetections.Blur => Blur,
        AihaDetections.Block => Block,
        AihaDetections.Redirect => Redirect,
        AihaDetections.UnderExposure => UnderExposure,
        AihaDetections.Unsuitability => Unsuitability,
        _ => null,
    };
}

/// <summary>
/// Progress of the check and the counts. <see cref="Rows"/> is filled in the replies of check and getState, empty in the
/// <see cref="ImageHealthMethods.StateTopic"/> event.
/// </summary>
/// <param name="Checking">A check is running.</param>
/// <param name="Checked">Video devices checked so far in this check.</param>
/// <param name="Total">Video devices to check.</param>
/// <param name="Running">Cameras where the app runs.</param>
/// <param name="NotRunning">Cameras where the app is installed but stopped.</param>
/// <param name="WithoutApp">Cameras without the app.</param>
/// <param name="Failed">Cameras that could not be read.</param>
/// <param name="CheckedUtc">When the last check finished; null before the first.</param>
/// <param name="Rows">Every row.</param>
public sealed record ImageHealthState(
    bool Checking,
    int Checked,
    int Total,
    int Running,
    int NotRunning,
    int WithoutApp,
    int Failed,
    DateTimeOffset? CheckedUtc,
    IReadOnlyList<ImageHealthRow> Rows);

/// <summary>Changed rows, and the devices that left the table (app removed, device removed from OADM).</summary>
public sealed record RowsEvent(IReadOnlyList<ImageHealthRow> Rows, IReadOnlyList<Guid> Removed);

/// <summary>JSON of the page contract (camelCase, like the other core plugins).</summary>
public static class ImageHealthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("Missing payload.", nameof(json));
}
