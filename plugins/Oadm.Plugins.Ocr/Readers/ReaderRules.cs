using System.Globalization;

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

    /// <summary>Watching readers at most while the recognition runs on a graphics card.</summary>
    public const int MaxWatchingGpu = 50;

    /// <summary>The frame rates a watching reader offers on a graphics card.</summary>
    public static readonly IReadOnlyList<int> FpsChoicesGpu = [1, 2, 4, 8, 15];

    /// <summary>The smallest and largest picture side the readers ask for.</summary>
    public const int MinSide = 160;

    public const int MaxSide = 7680;

    /// <summary>The area of a reader without areas (the whole picture), in the results and the events' Area field.</summary>
    public const string WholePictureName = "Whole picture";

    public const string NameMissing = "Enter a name.";

    public static readonly string NameTooLong = string.Create(CultureInfo.InvariantCulture, $"Use at most {MaxNameLength} characters.");

    public const string CameraMissing = "Choose a camera.";

    public const string SourceMissing = "Choose a video source.";

    public const string ResolutionMissing = "Choose a resolution.";

    public const string AreaNameMissing = "Enter a name for each area.";

    public const string AreaOutside = "An area lies outside the picture.";

    public const string FormatMissing = "Enter a format.";

    public static readonly string FormatTooLong = string.Create(CultureInfo.InvariantCulture, $"A format has at most {MaxFormatLength} characters.");

    public const string FormatWithoutPlaceholder = "A format needs at least one #, A or *.";

    public static readonly string TooManyAreas = string.Create(CultureInfo.InvariantCulture, $"A reader has at most {OcrLimits.MaxAreas} areas.");

    public static readonly string TooManyReaders = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {OcrLimits.MaxReaders} readers. Delete one first.");

    /// <summary>The reader of an id the caller does not see or that was deleted.</summary>
    public const string Gone = "The reader no longer exists.";

    /// <summary>A reader whose camera is no longer in its owner's devices (or whose device group was deleted): it reads nothing.</summary>
    public const string Off = "The reader's camera is no longer in its device group.";

    /// <summary>Reading without the recognition parts (before wave 3 wires them in).</summary>
    public const string NotInstalled = "Text recognition is not installed completely. Install the add-on again.";

    public static string NameTaken(string name) => $"A reader named {name} exists already.";

    public static string AreaNameTaken(string name) => $"Two areas are named {name}.";

    /// <summary>Watching readers at most per site (each stream holds one of the site node's large transfers).</summary>
    public const int MaxWatchingPerSite = 4;

    /// <summary>"At most 4 readers can watch at once at the site Building B. Set another reader there to On demand first."</summary>
    public static string TooManyWatchingAtSite(string? siteName) => string.Create(
        CultureInfo.InvariantCulture,
        $"At most {MaxWatchingPerSite} readers can watch at once at the site {(string.IsNullOrWhiteSpace(siteName) ? "of this camera" : siteName)}. Set another reader there to On demand first.");

    /// <summary>"At most 20 readers can watch at once. Set another reader to On demand first."</summary>
    public static string TooManyWatching(bool onGpu) => string.Create(
        CultureInfo.InvariantCulture,
        $"At most {MaxWatching(onGpu)} readers can watch at once. Set another reader to On demand first.");

    /// <summary>"Choose 1, 2 or 4 frames per second."</summary>
    public static string FpsNotAllowed(bool onGpu)
    {
        var choices = FpsChoices(onGpu);
        var list = string.Join(", ", choices.Take(choices.Count - 1).Select(c => c.ToString(CultureInfo.InvariantCulture)));
        return string.Create(CultureInfo.InvariantCulture, $"Choose {list} or {choices[^1]} frames per second.");
    }

    /// <summary>Watching readers at most: 20 on the CPU, 50 on a graphics card.</summary>
    public static int MaxWatching(bool onGpu) => onGpu ? MaxWatchingGpu : OcrLimits.MaxWatching;

    /// <summary>The frame rates: 1, 2, 4 on the CPU; 1, 2, 4, 8, 15 on a graphics card.</summary>
    public static IReadOnlyList<int> FpsChoices(bool onGpu) => onGpu ? FpsChoicesGpu : OcrLimits.FpsChoices;

    /// <summary>The areas a reading has: the reader's own, or the whole picture as one.</summary>
    public static IReadOnlyList<Area> EffectiveAreas(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.Areas.Count > 0 ? reader.Areas : [Area.WholePicture(WholePictureName)];
    }

    /// <summary>The reader as it is stored: names trimmed, a format only with Format, no owner from the page.</summary>
    public static Reader Normalize(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader with
        {
            Name = reader.Name?.Trim() ?? string.Empty,
            Areas = [.. (reader.Areas ?? []).Select(a => a with
            {
                Name = a.Name?.Trim() ?? string.Empty,
                Expected = a.Expected is { Kind: ExpectedTextKind.Format } e ? e with { Format = e.Format?.Trim() } : ExpectedText.AnyText,
            })],
            OwnerScopeKey = null,
        };
    }

    /// <summary>The first problem of a normalized reader (<see cref="Normalize"/>), or null.</summary>
    public static string? Problem(Reader reader, bool onGpu)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Name.Length == 0)
        {
            return NameMissing;
        }

        if (reader.Name.Length > MaxNameLength || reader.Name.Any(char.IsControl))
        {
            return NameTooLong;
        }

        if (reader.DeviceId == Guid.Empty)
        {
            return CameraMissing;
        }

        if (reader.VideoSource < 1)
        {
            return SourceMissing;
        }

        if (reader.Resolution.Width is < MinSide or > MaxSide || reader.Resolution.Height is < MinSide or > MaxSide)
        {
            return ResolutionMissing;
        }

        if (!Enum.IsDefined(reader.Mode))
        {
            return "Choose On demand or Watching.";
        }

        if (!FpsChoices(onGpu).Contains(reader.Fps))
        {
            return FpsNotAllowed(onGpu);
        }

        if (reader.Areas.Count > OcrLimits.MaxAreas)
        {
            return TooManyAreas;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in reader.Areas)
        {
            if (AreaProblem(area) is { } problem)
            {
                return problem;
            }

            if (!names.Add(area.Name))
            {
                return AreaNameTaken(area.Name);
            }
        }

        return null;
    }

    /// <summary>The problem of one area, or null.</summary>
    public static string? AreaProblem(Area area)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (string.IsNullOrWhiteSpace(area.Name))
        {
            return AreaNameMissing;
        }

        if (area.Name.Length > MaxNameLength || area.Name.Any(char.IsControl))
        {
            return NameTooLong;
        }

        if (!area.Box.IsValid)
        {
            return AreaOutside;
        }

        return area.Expected.Kind == ExpectedTextKind.Format ? FormatProblem(area.Expected.Format) : null;
    }

    /// <summary>The problem of a format, or null: not empty, at most 64 characters, at least one #, A or *.</summary>
    public static string? FormatProblem(string? format)
    {
        var text = format?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return FormatMissing;
        }

        if (text.Length > MaxFormatLength || text.Any(char.IsControl))
        {
            return FormatTooLong;
        }

        return text.Any(c => c is '#' or 'A' or '*') ? null : FormatWithoutPlaceholder;
    }
}
