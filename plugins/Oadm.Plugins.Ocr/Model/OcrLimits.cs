namespace Oadm.Plugins.Ocr.Model;

/// <summary>The limits of text recognition (docs/spec/ocr.md "Rules that never break" and "Readers").</summary>
public static class OcrLimits
{
    /// <summary>Areas per reader at most.</summary>
    public const int MaxAreas = 8;

    /// <summary>Readers per server at most.</summary>
    public const int MaxReaders = 50;

    /// <summary>Watching readers per server at most.</summary>
    public const int MaxWatching = 20;

    /// <summary>The frame rates a watching reader offers.</summary>
    public static readonly IReadOnlyList<int> FpsChoices = [1, 2, 4];

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
    public const int DefaultFps = 1;

    /// <summary>
    /// Text boxes read per picture at most, over all areas together (the highest detection scores first); more are dropped
    /// and the reading carries <see cref="Readers.ReaderRules.TooManyTexts"/>.
    /// </summary>
    public const int MaxTextBoxes = 30;

    /// <summary>Lines below this confidence (0 to 100) are dropped.</summary>
    public const double MinConfidence = 50;

    /// <summary>Watching publishes a value only once it was read in this many frames in a row.</summary>
    public const int EqualFramesToPublish = 2;

    /// <summary>The longest pause before a dropped stream is opened again (the pause grows up to it).</summary>
    public static readonly TimeSpan MaxReopenPause = TimeSpan.FromMinutes(1);

    /// <summary>Threads of one recognition worker at most.</summary>
    public const int ThreadsPerWorker = 2;

    /// <summary>Recognition workers at once: <c>max(1, cores / 2)</c>.</summary>
    public static int Workers(int processorCount) => Math.Max(1, processorCount / 2);
}
