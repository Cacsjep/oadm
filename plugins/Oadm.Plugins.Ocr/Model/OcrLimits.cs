namespace Oadm.Plugins.Ocr.Model;

/// <summary>The limits of text recognition (docs/spec/ocr.md "Rules that never break" and "Readers").</summary>
public static class OcrLimits
{
    /// <summary>The time of one Read now, the same as a rule's action.</summary>
    public static readonly TimeSpan ReadNowTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Areas per reader at most.</summary>
    public const int MaxAreas = 8;

    /// <summary>Readers per server at most.</summary>
    public const int MaxReaders = 50;

    /// <summary>Watching readers per server at least, whatever the cores (a small server).</summary>
    public const int MinWatching = 8;

    /// <summary>Watching readers per server at most, whatever the cores.</summary>
    public const int MaxWatchingReaders = 50;

    /// <summary>Watching readers per recognition worker (<see cref="Workers"/>).</summary>
    public const int WatchingPerWorker = 4;

    /// <summary>
    /// Watching readers at most on a server with <paramref name="processorCount"/> cores: 4 per recognition worker, at least 8
    /// and at most 50 (docs/spec/ocr.md "Limits"; 8 cores: 16, 24 cores: 48). The CPU decodes every picture and takes over
    /// when a graphics card fails, so the cores set the limit. A worker reads a fixed area in about
    /// 28 ms (35 pictures per second); 4 readers at 2 frames per second use about a quarter of it, and unchanged areas cost
    /// almost nothing. Higher rates are the user's choice: the traffic light tells when the server no longer keeps up.
    /// </summary>
    public static int MaxWatching(int processorCount) => Math.Clamp(Workers(processorCount) * WatchingPerWorker, MinWatching, MaxWatchingReaders);

    /// <summary>
    /// The frame rates a watching reader offers, on every server: the recognition's load and the traffic light of the runtime
    /// stats show when a server reads slower than set.
    /// </summary>
    public static readonly IReadOnlyList<int> FpsChoices = [1, 2, 4, 8, 12, 15];

    /// <summary>Previews (a page that shows a reader reads it live, without events) per server at most.</summary>
    public const int MaxPreviews = 8;

    /// <summary>Previews of one user at most; a further one ends that user's oldest (a page closed without a word).</summary>
    public const int MaxPreviewsPerUser = 2;

    /// <summary>An area's smallest width and height, relative to the picture (the drawing minimum of the page).</summary>
    public const double MinAreaSide = 0.02;

    /// <summary>A preview ends this long after the page's last keep-alive (a closed client never leaves one running).</summary>
    public static readonly TimeSpan PreviewLease = TimeSpan.FromSeconds(30);

    /// <summary>How often the page keeps its preview alive.</summary>
    public static readonly TimeSpan PreviewKeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>The frame rate of the preview of an On action reader.</summary>
    public const int PreviewFpsOnAction = 1;

    /// <summary>The frame rate of a new reader.</summary>
    public const int DefaultFps = 4;

    /// <summary>
    /// Text boxes read per picture at most, over all areas together (the highest detection scores first); more are dropped
    /// and the reading carries <see cref="Readers.ReaderRules.TooManyTexts"/>.
    /// </summary>
    public const int MaxTextBoxes = 30;

    /// <summary>Lines below this confidence (0 to 100) are dropped.</summary>
    public const double MinConfidence = 50;

    /// <summary>Watching publishes a value only once it was read in this many frames in a row.</summary>
    public const int EqualFramesToPublish = 2;

    /// <summary>The page keeps a live reading's note this long after the last reading that had it, so it can be read on a belt.</summary>
    public static readonly TimeSpan NoteHold = TimeSpan.FromSeconds(5);

    /// <summary>The page shows "Text too small" only for an area without a value for this long that saw small text within it.</summary>
    public static readonly TimeSpan SmallTextWait = TimeSpan.FromSeconds(10);

    /// <summary>The longest pause before a dropped stream is opened again (the pause grows up to it).</summary>
    public static readonly TimeSpan MaxReopenPause = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Threads of one model call: one. Measured on 24 cores: 12 calls at once of one thread read 226 lines per second at 44
    /// core-ms each, 12 calls of 2 threads 100 lines per second at 223 core-ms; more threads per call barely shorten it.
    /// </summary>
    public const int ThreadsPerWorker = 1;

    /// <summary>
    /// Frames of one reader in work at once at most: beyond the first only while workers are free (<see cref="IRecognizerLoad.HasRoom"/>),
    /// so an idle server carries one reader at full speed and many readers share the workers as before.
    /// </summary>
    public const int MaxFramesInWork = 4;

    /// <summary>
    /// Large text finder calls at once on the CPU (at least <see cref="LargeTextFinderPixels"/> pixels). Measured on 24 cores at
    /// 640 x 736: 1 call 8 per second, 2 at once 12, 4 at once 14, 12 at once 13 at seven times the CPU: more calls only burn it.
    /// </summary>
    public const int LargeTextFinderCalls = 3;

    /// <summary>From this input size on a text finder call is large (<see cref="LargeTextFinderCalls"/>).</summary>
    public const int LargeTextFinderPixels = 200_000;

    /// <summary>Failed readings in a row that make a problem of a watching reader; a single one may be a bad frame.</summary>
    public const int FailedReadingsForProblem = 3;

    /// <summary>
    /// Rows of the last reads per reader and per preview (docs/spec/ocr.md "The last 20 reads"): server memory only, the oldest
    /// drops first.
    /// </summary>
    public const int RecentReads = 20;

    /// <summary>A row's cut-out in bytes at most, so a list holds at most 1.3 MB of pictures (a longer one is not kept).</summary>
    public const int MaxCutoutBytes = 64 * 1024;

    /// <summary>A cut-out's longer side in pixels at most: a larger piece of the picture is scaled down to it.</summary>
    public const int MaxCutoutSide = 640;

    /// <summary>Recognition workers at once: <c>max(1, cores / 2)</c>.</summary>
    public static int Workers(int processorCount) => Math.Max(1, processorCount / 2);
}
