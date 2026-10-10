using Oadm.Sdk.Events;

namespace Oadm.Plugins.ImageHealth;

/// <summary>Plugin setting "config": "Monitor continuously" (off by default).</summary>
public sealed record ImageHealthConfig(bool MonitorContinuously);

/// <summary>
/// The events the Image Health Dashboard publishes for event rules, all stateful per camera, only while "Monitor
/// continuously" is on: a detection is on while the app reports it as detected (pending is not on); "App not running"
/// while the app is installed but stopped; "Camera could not be checked" while the status cannot be read.
/// </summary>
public static class ImageHealthEvents
{
    public const string Group = "Image Health";
    public const string Prefix = "oadm.image-health.";

    public const string Blur = Prefix + AihaDetections.Blur;
    public const string Block = Prefix + AihaDetections.Block;
    public const string Redirect = Prefix + AihaDetections.Redirect;
    public const string UnderExposure = Prefix + AihaDetections.UnderExposure;
    public const string Unsuitability = Prefix + AihaDetections.Unsuitability;
    public const string NotRunning = Prefix + "not-running";
    public const string Unreachable = Prefix + "unreachable";

    public static readonly IReadOnlyList<EventTypeInfo> Types =
    [
        new(Blur, "Blur detected", Group, EventKind.Stateful) { Description = "The picture is out of focus." },
        new(Block, "Block detected", Group, EventKind.Stateful) { Description = "Something covers the view." },
        new(Redirect, "Redirect detected", Group, EventKind.Stateful) { Description = "The camera points somewhere else." },
        new(UnderExposure, "Under-exposure detected", Group, EventKind.Stateful) { Description = "The picture is too dark." },
        new(Unsuitability, "Unsuitable scene detected", Group, EventKind.Stateful) { Description = "The scene is not suitable for the analysis." },
        new(NotRunning, "App not running", Group, EventKind.Stateful) { Description = "AXIS Image Health Analytics is installed but stopped." },
        new(Unreachable, "Camera could not be checked", Group, EventKind.Stateful),
    ];

    /// <summary>When each event is on for a row.</summary>
    public static readonly IReadOnlyList<(string TypeId, Func<ImageHealthRow, bool> IsOn)> Rules =
    [
        (Blur, r => r.Blur == DetectionStates.Detected),
        (Block, r => r.Block == DetectionStates.Detected),
        (Redirect, r => r.Redirect == DetectionStates.Detected),
        (UnderExposure, r => r.UnderExposure == DetectionStates.Detected),
        (Unsuitability, r => r.Unsuitability == DetectionStates.Detected),
        (NotRunning, r => r.App == AppStates.NotRunning),
        (Unreachable, r => r.App == AppStates.Error),
    ];
}
