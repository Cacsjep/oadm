namespace Oadm.Plugins.Ocr.Model;

/// <summary>
/// Finds and reads the text in a picture (docs/spec/ocr.md "Reading"): decodes the JPEG, cuts out each area at full
/// resolution, finds the lines, straightens and reads them. Runs on the CPU in a bounded number of workers
/// (<see cref="OcrLimits.Workers"/>); the call waits for a free one.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>
    /// The lines of each of <paramref name="areas"/> in <paramref name="jpeg"/>, in the order of the areas; no areas: the
    /// whole picture is one area. Lines below <see cref="OcrLimits.MinConfidence"/> are left out. Boxes are relative to the
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
