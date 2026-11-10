using System.Globalization;

using Oadm.Plugins.Ocr.Frames;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>The page's texts, one phrase per concept (CLAUDE.md "Wording").</summary>
public static class OcrTexts
{
    public const string OnAction = "On action";

    /// <summary>The icon of an On action reader. It fires real events, so the rules run.</summary>
    public const string ReadNowTip = "Read now and fire the event";

    public const string ReadNowTitle = "Read now";

    /// <summary>The short message when a reader is set to On action (once per reader and session).</summary>
    public const string OnActionNoticeTitle = "Reads only on an action";

    public const string OnActionNotice = "This reader reads only when an event rule runs the action Read text with it.\n\n"
        + "The results below the video are a live preview and fire no event. To test the event, save the reader and select the lightning icon next to Mode.";

    /// <summary>The short message when Use read text as format made a format that checks a container number.</summary>
    public const string CheckFormatTitle = "Check this format";

    public const string CheckFormatNotice = "The text looks like a container number, so this format reads only numbers with U, J or Z as the fourth letter and a fitting check digit. If the area reads other text too, change the ? to @.";

    public const string Watching = "Watching";

    /// <summary>The reader's setting for belts and passing vehicles.</summary>
    public const string MovingObjects = "Moving objects";

    /// <summary>The reader list's chip of a reader whose camera does not answer: the device grid's status name.</summary>
    public static readonly string NotReachable = ProblemChips.NotReachable;

    /// <summary>The chip of a reader beyond the watching limits (of the server or of a site).</summary>
    public const string LimitReached = ProblemChips.LimitReached;

    /// <summary>The chip of a reader whose camera refuses the stored login: the device grid's status name.</summary>
    public static readonly string LoginRefused = ProblemChips.LoginRefused;

    /// <summary>The chip of a reader whose camera does not give the pictures asked for (resolution, video source, no pictures).</summary>
    public const string StreamError = ProblemChips.StreamError;

    /// <summary>The chip of a reader whose camera is no longer in its device group.</summary>
    public const string NotInDeviceGroup = ProblemChips.NotInDeviceGroup;

    /// <summary>The chip of a reader that waits for the graphics card's test.</summary>
    public const string Starting = ProblemChips.Starting;

    /// <summary>The tooltip of Use GPU during that test.</summary>
    public const string TestingGpu = "Testing the graphics card";

    /// <summary>The chip of a reader that cannot read because the plugin is not installed completely.</summary>
    public const string NotInstalled = ProblemChips.NotInstalled;

    /// <summary>The chip of a reader with a problem the page does not know (a newer server): the full text is its tooltip.</summary>
    public const string NotReading = "Not reading";

    public const string NoAreas = "Draw one area around each text you want to read.";

    /// <summary>The tooltip of New in the Areas header.</summary>
    public const string NewAreaTip = "Draw an area on the video";

    public const string NoReading = "No reading yet";

    public const string WaitingForPicture = "Waiting for the first picture.";

    public const string DrawHint = "Drag to draw an area. Esc to stop.";

    /// <summary>Over the dimmed video of a reader without areas: text is read only inside areas.</summary>
    public const string StartHint = "Start by drawing an area";

    /// <summary>The button on the video.</summary>
    public const string Pause = "Pause";

    public const string Resume = "Resume";

    /// <summary>The chip next to it while the page shows a held frame.</summary>
    public const string Paused = PicturePauseBar.PausedText;

    /// <summary>The chip's tooltip: pausing is about this page only.</summary>
    public const string PausedTip = "Saved readers keep reading.";

    /// <summary>Below the empty table while paused: nothing to wait for.</summary>
    public const string NoReads = "No reads yet.";

    /// <summary>The dialog of a row's picture: its title and the thumbnail's tooltip.</summary>
    public const string CutOut = "Cut-out";

    public const string OpenCutOut = "Open the cut-out";

    public const string Saved = "Saved";

    public const string NotSaved = "Not saved";

    public const string NoFormat = "No format";

    public const string AnyText = "Any text";

    public const string UseReadTextAsFormat = "Use read text as format";

    /// <summary>The heading of the camera picker of New.</summary>
    public const string ChooseCamera = "Choose a camera";

    public const string NoMatch = "No reader matches the search.";

    public const string NoGpu = "No graphics card found";

    /// <summary>The overlay of the reader list while it is read.</summary>
    public const string ReadingReaders = "Reading the readers";

    /// <summary>The overlay of the reader's card while it is opened.</summary>
    public const string ReadingReader = "Reading the reader";

    /// <summary>The dialog that the stream button opens: its title.</summary>
    public const string StreamSettings = "Stream settings";

    /// <summary>The label of the stream field and its button.</summary>
    public const string Stream = "Stream";

    public const string Configure = "Configure";

    /// <summary>The tooltip of an area row's rename icon.</summary>
    public const string Rename = "Rename";

    /// <summary>The stream button: "Configure · 1920x1080 @ 10 fps" (no rate for On action; the video source is in its dialog).</summary>
    public static string StreamSummary(Resolution resolution, int? fps) => fps is { } rate
        ? string.Create(CultureInfo.InvariantCulture, $"{Configure} · {resolution} @ {rate} fps")
        : string.Create(CultureInfo.InvariantCulture, $"{Configure} · {resolution}");

    /// <summary>"Watching · 2 fps".</summary>
    public static string WatchingAt(int fps) => string.Create(CultureInfo.InvariantCulture, $"{Watching} · {fps} fps");

    /// <summary>A watching reader slower than set: "Watching · 8 fps (reads 5.1)".</summary>
    public static string WatchingReads(int fps, double reads) =>
        string.Create(CultureInfo.InvariantCulture, $"{WatchingAt(fps)} (reads {reads:0.0})");

    /// <summary>"93 %".</summary>
    public static string Percent(double confidence) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(confidence, 0, 100))} %");

    public static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The reading's note of the areas whose text is too small: "Lane: Text too small. ...".</summary>
    public static string TooSmallIn(IEnumerable<string> areas) => string.Join(", ", areas) + ": " + ReaderRules.TooSmall;

    /// <summary>What a held frame gives for an area with a format: "Area 1: DE-1011-22", "Area 1: no match".</summary>
    public static string HeldValue(string area, string? value) => area + ": " + (string.IsNullOrEmpty(value) ? "no match" : value);

    /// <summary>"Runs on: CPU (8 cores)".</summary>
    public static string RunsOn(string what) => "Runs on: " + what;

    /// <summary>
    /// The short form in the page header (the full text is its tooltip): "CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti"
    /// gives "CPU 24 cores · RTX 5070 Ti". Only the words every card repeats go; an unknown name stays as it is.
    /// </summary>
    public static string RunsOnShort(string what) => (what ?? string.Empty)
        .Replace("CPU (", "CPU ", StringComparison.Ordinal)
        .Replace(" cores)", " cores", StringComparison.Ordinal)
        .Replace("NVIDIA ", string.Empty, StringComparison.Ordinal)
        .Replace("GeForce ", string.Empty, StringComparison.Ordinal)
        .Replace("AMD ", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The reader list's short chip for the server's problem text of a reader (the full text is its tooltip): the watching
    /// limits, a refused login, a changed certificate, an offline site, a camera that does not answer, a stream that gives no
    /// pictures, a camera outside the device group. The device statuses keep the names of the device list.
    /// </summary>
    public static string ProblemChip(string problem) => ProblemChips.Of(problem, Problems);

    /// <summary>Text recognition's own problem texts for the shared chips (plugins/Shared/Vision.Client).</summary>
    private static readonly ProblemTexts Problems = new()
    {
        IsWatchingLimit = p => ReaderRules.IsWatchingLimit(p),
        StreamErrors = [FrameTexts.NoPicture, FrameTexts.NoPictures, FrameTexts.ResolutionNotOffered, FrameTexts.SourceNotOffered],
        Starting = ReaderRules.Starting,
        Off = ReaderRules.Off,
        NotInstalled = [ReaderRules.NotInstalled],
        Unknown = NotReading,
    };
}
