namespace Oadm.Plugins.Ocr.Model;

/// <summary>
/// Finds and reads the text in a picture (docs/spec/ocr.md "Reading"): decodes the JPEG, cuts out each area at full
/// resolution, finds the lines, straightens and reads them. Runs on the CPU in a bounded number of workers
/// (<see cref="OcrLimits.Workers"/>); the call waits for a free one.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>
    /// The lines of each of <paramref name="areas"/> in <paramref name="jpeg"/>, in the order of the areas; no areas: nothing
    /// is read (an empty result, the picture is not decoded). At most <see cref="OcrLimits.MaxTextBoxes"/> lines over all
    /// areas together (the highest detection scores; <see cref="RecognizedPicture.TooManyTexts"/> tells). Lines below <see cref="OcrLimits.MinConfidence"/> are left out. Boxes are relative to the
    /// whole picture.
    /// </summary>
    Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct);

    /// <summary>
    /// <see cref="ReadAsync(ReadOnlyMemory{byte}, IReadOnlyList{Area}, CancellationToken)"/> of a watching reader or a preview:
    /// with <paramref name="reuseSource"/> (who reads: the reader's or the preview's id) an area whose pixels have barely
    /// changed since that source's last reading gives that reading again without the models (docs/spec/ocr.md "Unchanged
    /// areas", <see cref="RecognizedPicture.ReusedAreas"/>); null reads every area. The default reads every area.
    /// </summary>
    Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, string? reuseSource, CancellationToken ct) =>
        ReadAsync(jpeg, areas, ct);
}

/// <summary>Which pictures a reader asks a camera for.</summary>
/// <param name="DeviceId">The camera.</param>
/// <param name="VideoSource">The video source, 1 for the first.</param>
/// <param name="Resolution">The picture size.</param>
public sealed record FrameRequest(Guid DeviceId, int VideoSource, Resolution Resolution)
{
    /// <summary>The request of <paramref name="reader"/>.</summary>
    public static FrameRequest Of(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new FrameRequest(reader.DeviceId, reader.VideoSource, reader.Resolution);
    }
}

/// <summary>
/// The pictures of a camera as JPEG, always through the device's own route (sites work): single pictures and the MJPEG
/// stream of a watching reader (docs/spec/ocr.md "Readers").
/// </summary>
public interface IFrameSource
{
    /// <summary>One picture now (the camera's single picture request).</summary>
    Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct);

    /// <summary>
    /// The camera's pictures at <paramref name="fps"/> frames per second until <paramref name="ct"/> ends. A dropped stream
    /// is opened again after a pause that grows up to <see cref="OcrLimits.MaxReopenPause"/>; the sequence ends only with the
    /// token.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, CancellationToken ct);

    /// <summary>
    /// <see cref="WatchAsync(FrameRequest, int, CancellationToken)"/> that also tells why the stream gives no pictures now
    /// (<paramref name="problem"/> with the text for the page: a refusal of the camera, a resolution or video source it does
    /// not offer, no answer) and null once pictures flow again. The default tells nothing.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, CancellationToken ct) =>
        WatchAsync(request, fps, ct);
}

/// <summary>
/// Makes an area's value from its lines (docs/spec/ocr.md "Several lines" and "Expected text"): orders the lines top to
/// bottom and left to right, applies the format with its look-alike fixes. Pure: no I/O.
/// </summary>
public interface ITextMatcher
{
    /// <summary>
    /// The value of an area's <paramref name="lines"/> for <paramref name="expected"/>: whether it matches, the raw text, every
    /// different match, the value's own confidence and its box.
    /// </summary>
    AreaValue Match(IReadOnlyList<TextLine> lines, ExpectedText expected);
}

/// <summary>An area's value with what the events need beyond <see cref="AreaMatch"/>.</summary>
/// <param name="Match">Value, whether it matches and the raw text.</param>
/// <param name="AllMatches">Every different match of the format (at most 10); empty without a format.</param>
/// <param name="Confidence">0 to 100: the lowest character probability of the value (its lines' <see cref="TextLine.Confidence"/>); 0 without a value.</param>
/// <param name="MatchBox">Where the value was found, relative to the picture; null without a value.</param>
public sealed record AreaValue(AreaMatch Match, IReadOnlyList<string> AllMatches, double Confidence, RelativeBox? MatchBox)
{
    /// <summary>How the value's line lies in the picture, for its upright cut-out; null when not known.</summary>
    public LineTurn? MatchTurn { get; init; }
}

/// <summary>
/// Optional next to <see cref="ITextRecognizer"/>: what reads the pictures and the setting "Use GPU"
/// (docs/spec/ocr.md "GPU"). The plugin stores the setting and hands it over at start and on every change; the recognizer
/// applies it (false: the CPU, <see cref="OnGpu"/> false). Without it the page shows the CPU and no graphics card.
/// </summary>
public interface IEngineInfo
{
    /// <summary>True while models run on a graphics card (the limits stay the same: they follow the cores).</summary>
    bool OnGpu { get; }

    /// <summary>What reads now: "CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti" or "CPU (8 cores)"; an empty name: the plugin names the CPU.</summary>
    RecognitionDevice RunsOn { get; }

    /// <summary>A graphics card was found and passed the test reading.</summary>
    bool GpuAvailable { get; }

    /// <summary>Applies the setting "Use GPU" (on by default).</summary>
    void SetUseGpu(bool useGpu);
}

/// <summary>
/// Optional next to <see cref="ITextRecognizer"/>: how busy the recognition is (docs/spec/ocr.md "Runtime stats"). The load
/// over a window is the growth of <see cref="BusyTime"/> divided by the window times <see cref="Capacity"/>; 100 % means no
/// more model calls fit.
/// </summary>
public interface IRecognizerLoad
{
    /// <summary>The time all model calls held their worker since the start (a running sum).</summary>
    TimeSpan BusyTime { get; }

    /// <summary>
    /// Model calls that run at once now: the CPU workers (<see cref="OcrLimits.Workers"/>), 1 on a graphics card (its calls
    /// run one at a time on one shared set).
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// True while a worker is free and no model call waits: a reader may start another frame before its last one ended
    /// (<see cref="OcrLimits.MaxFramesInWork"/>). The default: one frame at a time.
    /// </summary>
    bool HasRoom => false;
}

/// <summary>Cuts the picture of a found value out of a frame for the page. Optional.</summary>
public interface IPictureCutter
{
    /// <summary>The part <paramref name="box"/> of <paramref name="jpeg"/> as JPEG; null when it cannot be cut.</summary>
    byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box);

    /// <summary>
    /// The value at <paramref name="box"/> shown upright by <paramref name="turn"/>, as the reader saw it; null turn: the level
    /// cut-out of <paramref name="box"/>. The default ignores the turn.
    /// </summary>
    byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box, LineTurn? turn) => Cut(jpeg, box);
}

/// <summary>What runs the recognition (<see cref="IEngineInfo.RunsOn"/>): the CPU with its cores, and the graphics card while models run there.</summary>
/// <param name="IsGraphicsCard">True while a graphics card helps (DirectML or CUDA).</param>
/// <param name="Name">"CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti" or "CPU (8 cores)".</param>
public sealed record RecognitionDevice(bool IsGraphicsCard, string Name)
{
    /// <summary>The page's settings line: "Runs on: CPU (8 cores)".</summary>
    public string Text => "Runs on: " + Name;
}
