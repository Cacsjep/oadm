using System.Globalization;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The checks of a reader before it is saved and their texts (docs/spec/ocr.md "Readers", "Rules that never break" 4, "GPU"),
/// shared by the server and the page.
/// </summary>
public static class ReaderRules
{
    /// <summary>The longest reader or area name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest format.</summary>
    public const int MaxFormatLength = 64;


    /// <summary>The smallest and largest picture side the readers ask for.</summary>
    public const int MinSide = 160;

    public const int MaxSide = 7680;

    /// <summary>The most pixels a picture may have (8K, 7680 x 4320): a larger one is never decoded (memory).</summary>
    public const long MaxPixels = 7680L * 4320;

    public const string NameMissing = "Enter a name.";

    public static readonly string NameTooLong = string.Create(CultureInfo.InvariantCulture, $"Use at most {MaxNameLength} characters.");

    public const string CameraMissing = "Choose a camera.";

    /// <summary>A save that changes the camera of a saved reader (the page offers no camera change: New starts from one).</summary>
    public const string CameraFixed = "A saved reader keeps its camera. Create a new reader for another camera.";

    public const string SourceMissing = "Choose a video source.";

    public const string ResolutionMissing = "Choose a resolution.";

    /// <summary>An area without a name (below the area's name, like the reader's).</summary>
    public const string AreaNameMissing = NameMissing;

    public const string ModeMissing = "Choose On action or Watching.";

    public const string AreaOutside = "An area lies outside the picture.";

    /// <summary>An area narrower or lower than <see cref="OcrLimits.MinAreaSide"/> of the picture (the drawing minimum).</summary>
    public static readonly string AreaTooSmall = string.Create(
        CultureInfo.InvariantCulture, $"Draw the area at least {OcrLimits.MinAreaSide * 100:0} % of the picture wide and high.");

    /// <summary>A name with a line break, a tab or another control character.</summary>
    public const string NameInvalid = "Use letters, digits and signs only.";

    /// <summary>The action Read text without a reader.</summary>
    public const string ReaderMissing = "Choose a reader.";

    public const string FormatMissing = "Enter a format.";

    public static readonly string FormatTooLong = string.Create(CultureInfo.InvariantCulture, $"A format has at most {MaxFormatLength} characters.");

    public const string FormatWithoutPlaceholder = "A format needs at least one #, @ or *.";

    public const string FormatInvalid = "Use letters, digits and signs only.";

    /// <summary>Why a reader does not read yet: the graphics card's test runs, then it starts on the card or the CPU.</summary>
    public const string Starting = "Starting: testing the graphics card.";

    /// <summary>Lines lower than 10 px were left out of an area (<see cref="AreaDetails.TooSmall"/>).</summary>
    public const string TooSmall = "Text too small. Draw a smaller area or choose a higher resolution.";

    /// <summary>A reading asked for while the plugin stops.</summary>
    public const string NotRunning = "The Text recognition plugin is not running.";

    /// <summary>A reading found more than <see cref="OcrLimits.MaxTextBoxes"/> texts (<see cref="Reading.Note"/>).</summary>
    public static readonly string TooManyTexts = string.Create(
        CultureInfo.InvariantCulture,
        $"More than {OcrLimits.MaxTextBoxes} texts found. Only the first {OcrLimits.MaxTextBoxes} are read. Draw smaller areas.");

    /// <summary>The runtime stats' traffic light: green.</summary>
    public const string KeepUp = "All readers keep up";

    /// <summary>Red.</summary>
    public const string Overloaded = "Overloaded: lower the frames per second or the number of readers";

    /// <summary>Orange because readers fall behind: "Busy: 2 readers read slower than set".</summary>
    public static string BusySlow(int readers) => readers == 1
        ? "Busy: 1 reader reads slower than set"
        : string.Create(CultureInfo.InvariantCulture, $"Busy: {readers} readers read slower than set");

    /// <summary>Orange because of the load only: "Busy: recognition is at 82 %".</summary>
    public static string BusyLoad(double load) =>
        string.Create(CultureInfo.InvariantCulture, $"Busy: recognition is at {Math.Round(Math.Clamp(load, 0, 100))} %");

    /// <summary>The action Read text with a reader without areas: text is read only inside areas.</summary>
    public const string NoAreas = "The reader has no area. Draw one on the Text recognition page.";

    /// <summary>"CPU (8 cores)": what reads without a graphics card.</summary>
    public static string CpuText(int cores) => string.Create(CultureInfo.InvariantCulture, $"CPU ({cores} {(cores == 1 ? "core" : "cores")})");

    public static readonly string TooManyAreas = string.Create(CultureInfo.InvariantCulture, $"A reader has at most {OcrLimits.MaxAreas} areas.");

    public static readonly string TooManyReaders = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {OcrLimits.MaxReaders} readers. Delete one first.");

    /// <summary>The reader of an id the caller does not see or that was deleted.</summary>
    public const string Gone = "The reader no longer exists.";

    /// <summary>A reader whose camera is no longer in its owner's devices (or whose device group was deleted): it reads nothing.</summary>
    public const string Off = "The reader's camera is no longer in its device group.";

    /// <summary>Reading while the plugin folder has no <c>Models</c> folder (<see cref="ReaderParts.Real"/>).</summary>
    public const string NotInstalled = "Text recognition is not installed completely. Install it again on the Plugins page.";

    public static string NameTaken(string name) => $"A reader named {name} exists already.";

    public static string AreaNameTaken(string name) => $"Two areas are named {name}.";

    /// <summary>Watching readers (and previews with a stream of their own) at most per site: the site node's link carries their streams.</summary>
    public const int MaxWatchingPerSite = 4;

    /// <summary>"At most 4 readers can watch at once at the site Building B. Set another reader there to On action first."</summary>
    public static string TooManyWatchingAtSite(string? siteName) => string.Create(
        CultureInfo.InvariantCulture,
        $"{LimitStart}{MaxWatchingPerSite}{WatchLimit} at the site {(string.IsNullOrWhiteSpace(siteName) ? "of this camera" : siteName)}. Set another reader there to On action first.");

    /// <summary>"At most 16 readers can watch at once. Set another reader to On action first."</summary>
    public static string TooManyWatching(int maxWatching) => string.Create(
        CultureInfo.InvariantCulture,
        $"{LimitStart}{maxWatching}{WatchLimit}. Set another reader to On action first.");

    /// <summary>A reader's problem is one of the watching limits (<see cref="TooManyWatching"/>, <see cref="TooManyWatchingAtSite"/>): the page's chip "Limit reached".</summary>
    public static bool IsWatchingLimit(string? problem) =>
        problem is not null && problem.StartsWith(LimitStart, StringComparison.Ordinal) && problem.Contains(WatchLimit, StringComparison.Ordinal);

    private const string LimitStart = "At most ";

    private const string WatchLimit = " readers can watch at once";

    /// <summary>"Choose 1, 2, 4, 8, 12 or 15 frames per second."</summary>
    public static string FpsNotAllowed()
    {
        var choices = OcrLimits.FpsChoices;
        var list = string.Join(", ", choices.Take(choices.Count - 1).Select(c => c.ToString(CultureInfo.InvariantCulture)));
        return string.Create(CultureInfo.InvariantCulture, $"Choose {list} or {choices[^1]} frames per second.");
    }

    /// <summary>"At most 8 readers can be shown live at once. Try again when another page closes."</summary>
    public static readonly string TooManyPreviews = string.Create(
        CultureInfo.InvariantCulture,
        $"At most {OcrLimits.MaxPreviews} readers can be shown live at once. Try again when another page closes.");

    /// <summary>A preview that would go beyond what the server reads at once (the watching limits, also per site).</summary>
    public const string PreviewBusy = "No live results now: OADM already reads as many cameras as it can.";

    /// <summary>Pause before the page's preview read a picture.</summary>
    public const string NothingToPause = "No picture to pause yet.";

    /// <summary>Pause on a picture beyond the limits of a reading.</summary>
    public const string PictureTooLarge = "This picture is too large to pause. Choose a lower resolution.";

    /// <summary>The refusal of Pause for a user without the host's Live view: the held picture is a piece of the camera's video.</summary>
    public const string PauseNeedsLiveView = "Your role does not allow: Live view.";

    /// <summary>
    /// The reader a preview reads (docs/spec/ocr.md "Page"): the page's draft as far as it can be read now. The camera and the
    /// resolution must be right (else <see cref="ArgumentException"/> with their text); areas outside the picture are left
    /// out, a name missing becomes "Area 2", a format with a problem reads any text, a frame rate not offered the default;
    /// an On action reader previews at <see cref="OcrLimits.PreviewFpsOnAction"/>. No owner: the server sets it.
    /// </summary>
    public static Reader PreviewOf(Reader draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var reader = Normalize(draft);
        if (reader.DeviceId == Guid.Empty)
        {
            throw new ArgumentException(CameraMissing);
        }

        if (reader.VideoSource < 1)
        {
            throw new ArgumentException(SourceMissing);
        }

        if (reader.Resolution.Width is < MinSide or > MaxSide || reader.Resolution.Height is < MinSide or > MaxSide
            || (long)reader.Resolution.Width * reader.Resolution.Height > MaxPixels)
        {
            throw new ArgumentException(ResolutionMissing);
        }

        var areas = new List<Area>();
        foreach (var area in reader.Areas.Where(a => a.Box.IsValid).Take(OcrLimits.MaxAreas))
        {
            var name = NameProblem(area.Name) is null ? area.Name : string.Create(CultureInfo.InvariantCulture, $"Area {areas.Count + 1}");
            var expected = area.Expected.Kind == ExpectedTextKind.Format && FormatProblem(area.Expected.Format) is not null ? ExpectedText.AnyText : area.Expected;
            areas.Add(area with { Name = name, Expected = expected });
        }

        var mode = Enum.IsDefined(reader.Mode) ? reader.Mode : ReaderMode.Watching;
        var fps = mode == ReaderMode.Watching
            ? OcrLimits.FpsChoices.Contains(reader.Fps) ? reader.Fps : OcrLimits.DefaultFps
            : OcrLimits.PreviewFpsOnAction;
        return reader with { Mode = mode, Fps = fps, Areas = areas };
    }

    /// <summary>
    /// The reader as it is stored: names trimmed, a format only with Format, no owner from the page; missing parts (no area
    /// list, an empty area) read as none; an On action reader's frame rate that is no choice becomes the default (it is
    /// checked only while Watching).
    /// </summary>
    public static Reader Normalize(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader with
        {
            Name = reader.Name?.Trim() ?? string.Empty,
            Fps = reader.Mode == ReaderMode.OnAction && !OcrLimits.FpsChoices.Contains(reader.Fps) ? OcrLimits.DefaultFps : reader.Fps,
            Areas = Areas(reader),
            OwnerScopeKey = null,
        };
    }

    /// <summary>A stored reader as the runtime reads it: an area list and expected texts that are never null (older or hand-made records).</summary>
    public static Reader Stored(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader with { Name = reader.Name ?? string.Empty, Areas = Areas(reader) };
    }

    private static IReadOnlyList<Area> Areas(Reader reader) => [.. (reader.Areas ?? []).OfType<Area>().Select(a => a with
    {
        Name = a.Name?.Trim() ?? string.Empty,
        Expected = a.Expected is { Kind: ExpectedTextKind.Format } e ? e with { Format = e.Format?.Trim() } : ExpectedText.AnyText,
    })];

    /// <summary>
    /// <paramref name="reader"/> at a frame rate the readers offer: its own, or the highest choice below it (a record written
    /// by hand or by an older version with 10 reads at 8). The runtime's loops read at it; the stored reader keeps its rate.
    /// </summary>
    public static Reader WithOfferedFps(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var choices = OcrLimits.FpsChoices;
        if (choices.Contains(reader.Fps))
        {
            return reader;
        }

        var lower = choices.Where(c => c < reader.Fps).ToList();
        return reader with { Fps = lower.Count > 0 ? lower.Max() : OcrLimits.DefaultFps };
    }

    /// <summary>The reader decides by 2 frames in a row instead of the vote: Moving objects, only while Watching.</summary>
    public static bool IsMoving(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.Mode == ReaderMode.Watching && reader.MovingObjects;
    }

    /// <summary>Every problem of a normalized reader (<see cref="Normalize"/>), at most one per field; empty when it can be saved.</summary>
    public static IReadOnlyList<FieldError> Problems(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var problems = new List<FieldError>();
        if (NameProblem(reader.Name) is { } name)
        {
            problems.Add(new(OcrFields.Name, name));
        }

        if (reader.DeviceId == Guid.Empty)
        {
            problems.Add(new(OcrFields.Camera, CameraMissing));
        }

        if (reader.VideoSource < 1)
        {
            problems.Add(new(OcrFields.VideoSource, SourceMissing));
        }

        if (reader.Resolution.Width is < MinSide or > MaxSide || reader.Resolution.Height is < MinSide or > MaxSide
            || (long)reader.Resolution.Width * reader.Resolution.Height > MaxPixels)
        {
            problems.Add(new(OcrFields.Resolution, ResolutionMissing));
        }

        if (!Enum.IsDefined(reader.Mode))
        {
            problems.Add(new(OcrFields.Mode, ModeMissing));
        }

        if (reader.Mode == ReaderMode.Watching && !OcrLimits.FpsChoices.Contains(reader.Fps))
        {
            // The frame rate is set only while Watching (the page hides it for On action).
            problems.Add(new(OcrFields.Fps, FpsNotAllowed()));
        }

        if (reader.Areas.Count > OcrLimits.MaxAreas)
        {
            problems.Add(new(OcrFields.Areas, TooManyAreas));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.Areas.Count; i++)
        {
            var area = reader.Areas[i];
            if (NameProblem(area.Name) is { } areaName)
            {
                problems.Add(new(OcrFields.AreaName(i), areaName));
            }
            else if (!names.Add(area.Name))
            {
                problems.Add(new(OcrFields.AreaName(i), AreaNameTaken(area.Name)));
            }

            if (!area.Box.IsValid)
            {
                problems.Add(new(OcrFields.AreaBox(i), AreaOutside));
            }
            else if (area.Box.Width < OcrLimits.MinAreaSide - 1e-9 || area.Box.Height < OcrLimits.MinAreaSide - 1e-9)
            {
                problems.Add(new(OcrFields.AreaBox(i), AreaTooSmall));
            }

            if (area.Expected.Kind == ExpectedTextKind.Format && FormatProblem(area.Expected.Format) is { } format)
            {
                problems.Add(new(OcrFields.AreaFormat(i), format));
            }
        }

        return problems;
    }

    /// <summary>The problem of a reader's or an area's name, or null: not empty, at most 64 characters, no control characters.</summary>
    public static string? NameProblem(string? name)
    {
        var text = name?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return NameMissing;
        }

        if (text.Length > MaxNameLength)
        {
            return NameTooLong;
        }

        return text.Any(char.IsControl) ? NameInvalid : null;
    }

    /// <summary>The problem of a format, or null: not empty, at most 64 characters, at least one #, @ or *.</summary>
    public static string? FormatProblem(string? format)
    {
        var text = format?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return FormatMissing;
        }

        if (text.Length > MaxFormatLength)
        {
            return FormatTooLong;
        }

        if (text.Any(char.IsControl))
        {
            return FormatInvalid;
        }

        return text.Any(c => c is '#' or '@' or '*') ? null : FormatWithoutPlaceholder;
    }
}
