namespace Oadm.Plugins.LicensePlates.Cameras;

/// <summary>What OADM knows of one camera of the plugin now (in memory; read again after a start).</summary>
internal sealed record CameraState
{
    public CameraStatus Status { get; init; } = CameraStatus.Connecting;

    /// <summary>The text of <see cref="CameraStatus.DeviceProblem"/> or the site's name of <see cref="CameraStatus.SiteOffline"/>.</summary>
    public string? Detail { get; init; }

    public string? AlpvVersion { get; init; }

    /// <summary>Since when the camera has no connection (null while connected).</summary>
    public DateTimeOffset? LostSince { get; init; }

    /// <summary>The camera was connected at least once since it took part (a gap row follows a loss only then).</summary>
    public bool WasConnected { get; init; }

    public ImageSettingsInfo? Image { get; init; }

    public EventSwitches? Events { get; init; }

    public DateTimeOffset? LastRead { get; init; }

    /// <summary>When ALPV was last checked (the application list and the configuration).</summary>
    public DateTimeOffset? Checked { get; init; }

    /// <summary>Whether the camera has a problem for the event Camera problem: the connection lost for 30 s, ALPV stopped, events off.</summary>
    public bool IsProblem(DateTimeOffset now) =>
        Status is CameraStatus.ConnectionLost or CameraStatus.AlpvStopped or CameraStatus.EventsOff
        || (Status is CameraStatus.SiteOffline or CameraStatus.DeviceProblem or CameraStatus.Connecting && LostSince is { } since && now - since >= LicensePlateLimits.LostDelay);

    /// <summary>The problem in words for the event; null without one.</summary>
    public string? ProblemText(DateTimeOffset now) => IsProblem(now)
        ? Status is CameraStatus.Connecting ? LicensePlateTexts.ConnectionLost : LicensePlateTexts.Status(Status, Detail)
        : null;
}
