using System.Globalization;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>The page's texts, one phrase per concept (CLAUDE.md "Wording").</summary>
public static class OcrTexts
{
    public const string OnAction = "On action";

    /// <summary>The short message when a reader is set to On action (once per reader and session).</summary>
    public const string OnActionNoticeTitle = "Reads only on an action";

    public const string OnActionNotice = "This reader reads only when an event rule runs the action Read text with it.";

    public const string Watching = "Watching";

    public const string NotReachable = "Not reachable";

    public const string NoAreas = "Draw one area around each text you want to read.";

    public const string NoReading = "No reading yet";

    public const string WaitingForPicture = "Waiting for the first picture.";

    public const string DrawHint = "Drag to draw an area. Esc to stop.";

    public const string Saved = "Saved";

    public const string NotSaved = "Not saved";

    public const string NoFormat = "No format";

    public const string AnyText = "Any text";

    public const string Format = "Format";

    public const string ChooseCamera = "Choose a camera";

    public const string NoMatch = "No reader matches the search.";

    public const string LiveResults = "Live results";

    public const string NoGpu = "No graphics card found";

    /// <summary>"Watching · 2 fps".</summary>
    public static string WatchingAt(int fps) => string.Create(CultureInfo.InvariantCulture, $"{Watching} · {fps} fps");

    /// <summary>"2 of 8".</summary>
    public static string AreaCount(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} of {OcrLimits.MaxAreas}");

    /// <summary>"93 %".</summary>
    public static string Percent(double confidence) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(confidence, 0, 100))} %");

    /// <summary>"Last reading 14:32:06" in this computer's time.</summary>
    public static string LastReading(DateTimeOffset time) => "Last reading " + Time(time);

    public static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"Runs on: CPU (8 cores)".</summary>
    public static string RunsOn(string what) => "Runs on: " + what;
}
