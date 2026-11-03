namespace Oadm.Plugins.Ocr.Model;

/// <summary>How a reader takes its pictures (docs/spec/ocr.md "Readers").</summary>
public enum ReaderMode
{
    /// <summary>One picture when an event rule runs Read text (or the page's Read now); no stream.</summary>
    OnDemand = 0,

    /// <summary>Reads continuously at <see cref="Reader.Fps"/> from the camera's MJPEG stream.</summary>
    Watching = 1,
}

/// <summary>A picture size in pixels ("1920x1080" on the camera).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct Resolution(int Width, int Height)
{
    /// <summary>The default of a new reader; the camera's largest below it when it has not this one.</summary>
    public static Resolution Default => new(1920, 1080);

    /// <summary>The camera's form: "1920x1080".</summary>
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// A rectangle relative to the picture, each value 0 to 1 (left, top, width, height), so a change of resolution keeps it in
/// place.
/// </summary>
/// <param name="X">Left edge, 0 to 1.</param>
/// <param name="Y">Top edge, 0 to 1.</param>
/// <param name="Width">Width, 0 to 1.</param>
/// <param name="Height">Height, 0 to 1.</param>
public readonly record struct RelativeBox(double X, double Y, double Width, double Height)
{
    /// <summary>The whole picture.</summary>
    public static RelativeBox Whole => new(0, 0, 1, 1);

    /// <summary>Inside the picture with a size above 0.</summary>
    public bool IsValid =>
        X is >= 0 and <= 1 && Y is >= 0 and <= 1 && Width > 0 && Height > 0 && X + Width <= 1 + 1e-9 && Y + Height <= 1 + 1e-9;
}

/// <summary>
/// A saved setup that reads text from one video source of one camera (docs/spec/ocr.md "Readers"). Owned by the device scope
/// that made it (<see cref="OwnerScopeKey"/>, docs/spec/device-scope.md).
/// </summary>
public sealed record Reader
{
    /// <summary>Stable id (a new reader gets one on its first save; empty before).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The name in the reader list and the event folder ("Gate 2").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The camera.</summary>
    public Guid DeviceId { get; init; }

    /// <summary>The camera's video source, 1 for the first (the <c>camera</c> parameter of the camera's picture requests).</summary>
    public int VideoSource { get; init; } = 1;

    /// <summary>The picture size the reader asks for.</summary>
    public Resolution Resolution { get; init; } = Resolution.Default;

    /// <summary>On demand or Watching.</summary>
    public ReaderMode Mode { get; init; } = ReaderMode.OnDemand;

    /// <summary>Frames per second while Watching, one of <see cref="OcrLimits.FpsChoices"/>.</summary>
    public int Fps { get; init; } = OcrLimits.DefaultFps;

    /// <summary>Up to <see cref="OcrLimits.MaxAreas"/> areas; none: the whole picture is one area.</summary>
    public IReadOnlyList<Area> Areas { get; init; } = [];

    /// <summary>The <c>ICallContext.DeviceScopeKey</c> of the scope that made it; set by the server, never by the page.</summary>
    public string? OwnerScopeKey { get; init; }
}
