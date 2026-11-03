using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// What the page reads from the server part beyond the wave 1 records (<c>Api/OcrApi.cs</c>), docs/spec/ocr.md "Page" and
/// "GPU". The extra fields are read from the same JSON as the wave 1 records (unknown members are ignored either way); every
/// one is optional, so the page also works while the server does not send it.
/// </summary>
public static class OcrPageMethods
{
    /// <summary>{} -> <see cref="EngineInfo"/>: what reads the pictures and the frame rates it offers. View. Not built on the server yet.</summary>
    public const string GetEngine = "getEngine";

    /// <summary><see cref="UseGpuRequest"/> -> <see cref="EngineInfo"/>: "Use the graphics card" on or off. Change. Not built yet.</summary>
    public const string SetUseGpu = "setUseGpu";

    /// <summary><see cref="CameraOptionsRequest"/> -> <see cref="CameraOptions"/>: the video sources and picture sizes of one camera. View. Not built yet.</summary>
    public const string GetCameraOptions = "getCameraOptions";
}

/// <summary>The answer of <see cref="OcrPageMethods.GetEngine"/> and <see cref="OcrPageMethods.SetUseGpu"/>.</summary>
public sealed record EngineInfo
{
    /// <summary>"NVIDIA GeForce RTX 5070 Ti" or "CPU (8 cores)": shown as "Runs on: ...".</summary>
    public string RunsOn { get; init; } = string.Empty;

    /// <summary>A graphics card was found and passed the test reading.</summary>
    public bool GpuAvailable { get; init; }

    /// <summary>The setting "Use the graphics card" (on by default).</summary>
    public bool UseGpu { get; init; } = true;

    /// <summary>The frame rates a watching reader may use now: 1, 2, 4 on the CPU, 1, 2, 4, 8, 15 on a GPU.</summary>
    public IReadOnlyList<int> FpsChoices { get; init; } = OcrLimits.FpsChoices;
}

/// <summary>The request of <see cref="OcrPageMethods.SetUseGpu"/>.</summary>
public sealed record UseGpuRequest
{
    public bool UseGpu { get; init; }
}

/// <summary>The request of <see cref="OcrPageMethods.GetCameraOptions"/>.</summary>
public sealed record CameraOptionsRequest
{
    public Guid DeviceId { get; init; }
}

/// <summary>A video source of a camera: its number (1 for the first) and its name ("View area 1").</summary>
public sealed record CameraSource
{
    public int Number { get; init; } = 1;

    public string Name { get; init; } = string.Empty;
}

/// <summary>The answer of <see cref="OcrPageMethods.GetCameraOptions"/>.</summary>
public sealed record CameraOptions
{
    public IReadOnlyList<CameraSource> VideoSources { get; init; } = [];

    /// <summary>The camera's picture sizes, largest first.</summary>
    public IReadOnlyList<Resolution> Resolutions { get; init; } = [];
}

/// <summary>
/// The fields of a reader list (listReaders and the readers topic) beyond <c>ReaderList</c>:
/// <c>{"readers":[{..., "problem"?}],"fpsChoices":[1,2,4],"maxWatching":20}</c>.
/// </summary>
public sealed record ReaderListExtras
{
    /// <summary>Each summary's extra fields, in the same order.</summary>
    public IReadOnlyList<ReaderSummaryExtras> Readers { get; init; } = [];

    /// <summary>The frame rates a watching reader may use now (1, 2, 4; 1, 2, 4, 8, 15 on a GPU).</summary>
    public IReadOnlyList<int>? FpsChoices { get; init; }

    /// <summary>Watching readers per server at most.</summary>
    public int? MaxWatching { get; init; }
}

/// <summary>The extra fields of a <c>ReaderSummary</c>.</summary>
public sealed record ReaderSummaryExtras
{
    /// <summary>The frame rate while Watching ("Watching · 2 fps"); null while the server does not send it ("Watching").</summary>
    public int? Fps { get; init; }

    /// <summary>
    /// What keeps the reader from reading, in plain words (the camera refuses connections); null while it reads. The list
    /// shows "Not reachable", the reader's header the whole text.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>
/// The per-area details of a reading reply (readNow, getReading) and of the reading topic beyond <c>ReadingReply</c>:
/// <c>"details":[{"area","allMatches","number","matchBox"?,"cutout"?}]</c>.
/// </summary>
public sealed record ReadingDetails
{
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];
}

/// <summary>The details of one area of a reading, matched to its result by the area's name.</summary>
public sealed record AreaDetails
{
    public string Area { get; init; } = string.Empty;

    /// <summary>Every different match of the format, separated by a space.</summary>
    public string AllMatches { get; init; } = string.Empty;

    /// <summary>The value as a number when it is one.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>Where the value's text is, relative to the whole picture; null without a value.</summary>
    public RelativeBox? MatchBox { get; init; }

    /// <summary>The value's cut-out as JPEG (getReading and readNow only); null: the page cuts it out of the frame.</summary>
    public byte[]? Cutout { get; init; }

    /// <summary>Not sent by the server yet: lines lower than 10 px were left out ("Text too small. ...").</summary>
    public bool TooSmall { get; init; }
}
