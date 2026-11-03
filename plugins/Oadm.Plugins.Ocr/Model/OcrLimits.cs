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

    /// <summary>The frame rate of a new reader.</summary>
    public const int DefaultFps = 1;

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
