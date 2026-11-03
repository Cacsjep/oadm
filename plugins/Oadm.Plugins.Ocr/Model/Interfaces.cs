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
}

/// <summary>
/// Makes an area's value from its lines (docs/spec/ocr.md "Several lines" and "Expected text"): orders the lines top to
/// bottom and left to right, applies the format with its look-alike fixes. Pure: no I/O.
/// </summary>
public interface ITextMatcher
{
    /// <summary>The value, whether it matches and the raw text of <paramref name="lines"/> for <paramref name="expected"/>.</summary>
    AreaMatch Match(IReadOnlyList<TextLine> lines, ExpectedText expected);
}

/// <summary>An area's value with what the events need beyond <see cref="AreaMatch"/>.</summary>
/// <param name="Match">Value, whether it matches and the raw text.</param>
/// <param name="AllMatches">Every different match of the format (at most 10); empty without a format.</param>
/// <param name="Confidence">0 to 100: the lowest character probability of the value (its lines' <see cref="TextLine.Confidence"/>); 0 without a value.</param>
/// <param name="MatchBox">Where the value was found, relative to the picture; null without a value.</param>
public sealed record AreaValue(AreaMatch Match, IReadOnlyList<string> AllMatches, double Confidence, RelativeBox? MatchBox);

/// <summary>
/// Optional next to <see cref="ITextMatcher"/>: a matcher that also gives every different match, the value's own confidence
/// and its box (<c>TextMatcher</c> does). Without it the readers take the single match, the lowest line confidence and the
/// box around all lines.
/// </summary>
public interface IAreaValueMatcher
{
    /// <summary>The value of an area's <paramref name="lines"/> for <paramref name="expected"/>.</summary>
    AreaValue MatchValue(IReadOnlyList<TextLine> lines, ExpectedText expected);
}

/// <summary>Optional next to <see cref="ITextRecognizer"/>: what the recognition runs on.</summary>
public interface IRecognizerCapabilities
{
    /// <summary>True while the recognition runs on a graphics card (more watching readers, higher frame rates).</summary>
    bool OnGpu { get; }
}

/// <summary>
/// Optional next to <see cref="ITextRecognizer"/>: what reads the pictures and the setting "Use the graphics card"
/// (docs/spec/ocr.md "GPU"). The plugin stores the setting and hands it over at start and on every change; the recognizer
/// applies it (false: the CPU, <see cref="IRecognizerCapabilities.OnGpu"/> false). Without it the page shows the CPU and no
/// graphics card.
/// </summary>
public interface IEngineInfo : IRecognizerCapabilities
{
    /// <summary>The graphics card's name ("NVIDIA GeForce RTX 5070 Ti") while it reads, else "CPU (8 cores)".</summary>
    string RunsOn { get; }

    /// <summary>A graphics card was found and passed the test reading.</summary>
    bool GpuAvailable { get; }

    /// <summary>Applies the setting "Use the graphics card" (on by default).</summary>
    void SetUseGpu(bool useGpu);
}

/// <summary>Cuts the picture of a found value out of a frame for the page. Optional.</summary>
public interface IPictureCutter
{
    /// <summary>The part <paramref name="box"/> of <paramref name="jpeg"/> as JPEG; null when it cannot be cut.</summary>
    byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box);
}
