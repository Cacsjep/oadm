using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>A reader in the list: name, mode, camera, last text. Updated in place by id.</summary>
public sealed partial class ReaderRow : ObservableObject
{
    public ReaderRow(ReaderSummary summary, string camera)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Id = summary.Id;
        Apply(summary, camera);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Guid DeviceId { get; private set; }

    /// <summary>"AXIS Q1798-LE · 10.0.0.48".</summary>
    [ObservableProperty]
    public partial string Camera { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText), nameof(IsWatching), nameof(IsProblem), nameof(IsKeepingUp), nameof(IsSlowWatching))]
    public partial ReaderMode Mode { get; private set; }

    /// <summary>The frame rate while Watching.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    public partial int Fps { get; private set; }

    /// <summary>The full text of what keeps the reader from reading (the header's chip); null while it reads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText), nameof(IsWatching), nameof(IsProblem), nameof(IsFailing), nameof(IsKeepingUp), nameof(IsSlowWatching))]
    public partial string? Problem { get; private set; }

    /// <summary>The value of the first area of the last reading, or "No reading yet".</summary>
    [ObservableProperty]
    public partial string LastText { get; private set; } = OcrTexts.NoReading;

    public DateTimeOffset? LastTime { get; private set; }

    /// <summary>On action / Watching · 2 fps / Watching · 8 fps (reads 5.1) / the problem's short chip (Limit reached, Credentials required, ...).</summary>
    public string ModeText => IsProblem ? OcrTexts.ProblemChip(Problem!)
        : Mode != ReaderMode.Watching ? OcrTexts.OnAction
        : IsSlow ? OcrTexts.WatchingReads(Fps, Reads ?? 0)
        : OcrTexts.WatchingAt(Fps);

    public bool IsWatching => Mode == ReaderMode.Watching && !IsProblem;

    /// <summary>Watching and keeping its frame rate: the green chip.</summary>
    public bool IsKeepingUp => IsWatching && !IsSlow;

    /// <summary>What the reader read per second over the last 10 s (the runtime stats); null without stats.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    public partial double? Reads { get; private set; }

    /// <summary>Watching below 90 % of its frame rate: the orange chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText), nameof(IsKeepingUp), nameof(IsSlowWatching))]
    public partial bool IsSlow { get; private set; }

    /// <summary>The orange chip shows (slow and nothing worse).</summary>
    public bool IsSlowWatching => IsSlow && IsWatching;

    /// <summary>The runtime stats of this reader (null: not watching now).</summary>
    public void SetRate(ReaderRate? rate)
    {
        Reads = rate?.Reads;
        IsSlow = rate?.Slow ?? false;
    }

    /// <summary>The server reports a problem (the camera refuses connections), in either mode.</summary>
    public bool IsProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>The red chip: a problem, not the wait for the graphics card's test.</summary>
    public bool IsFailing => IsProblem && Problem != Readers.ReaderRules.Starting;

    /// <summary>Name and camera in lower case, for the search.</summary>
    public string SearchText { get; private set; } = string.Empty;

    public void Apply(ReaderSummary summary, string camera)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Name = summary.Name;
        DeviceId = summary.DeviceId;
        Camera = camera;
        Mode = summary.Mode;
        Fps = summary.Fps;
        Problem = summary.Problem;
        SetLast(summary.LastText, summary.LastTime);
        SearchText = (Name + " " + Camera).ToLowerInvariant();
    }

    /// <summary>A new reading arrived (the live event).</summary>
    public void SetLast(string? text, DateTimeOffset? time)
    {
        LastTime = time;
        LastText = time is null ? OcrTexts.NoReading : FirstLine(text);
    }

    /// <summary>The camera's text changed (the device list changed).</summary>
    public void SetCamera(string camera)
    {
        Camera = camera;
        SearchText = (Name + " " + Camera).ToLowerInvariant();
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}

/// <summary>
/// A read of the last 20 (docs/spec/ocr.md "The last 20 reads"), a row of the results, or an area of the held frame's reading
/// (<see cref="ReaderEditorViewModel.HeldAreas"/>, never in the table). The editor owns <see cref="CutOut"/> and keeps a row
/// while it stays in the list, so its picture is decoded once.
/// </summary>
public sealed partial class ResultRow : ObservableObject
{
    /// <summary>A read of the last 20.</summary>
    public ResultRow(RecentRead read, IImage? cutOut)
    {
        ArgumentNullException.ThrowIfNull(read);
        ReadId = read.Id;
        Area = read.Area;
        Text = read.Text;
        Confidence = read.Confidence;
        ConfidenceText = OcrTexts.Percent(read.Confidence);
        HasFormat = read.HasFormat;
        Matches = read.MatchesFormat;
        Time = OcrTexts.Time(read.Time);
        CutOut = cutOut;
        Picture = read.Cutout;
    }

    /// <summary>An area of a held frame's reading.</summary>
    public ResultRow(AreaResult result, AreaDetails? details, bool hasFormat, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(result);
        Area = result.Area;
        // With a format only the value shows; what else was read stays in the event's Text read.
        Text = result.Text.Length > 0 || hasFormat ? result.Text : result.TextRead;
        Confidence = result.Confidence;
        ConfidenceText = Text.Length == 0 ? string.Empty : OcrTexts.Percent(result.Confidence);
        HasFormat = hasFormat;
        Matches = result.MatchesFormat;
        TooSmall = details?.TooSmall ?? false;
        Time = OcrTexts.Time(time);
    }

    /// <summary>The read's id; null for a held frame's area.</summary>
    public long? ReadId { get; }

    public string Area { get; }

    /// <summary>
    /// The value; without a format the area's rows, one per line (docs/spec/ocr.md "Several lines"); with a format and no
    /// match nothing ("Expected text": the raw text stays in Text read).
    /// </summary>
    public string Text { get; }

    public double Confidence { get; }

    public string ConfidenceText { get; }

    public bool HasFormat { get; }

    public bool Matches { get; }

    public bool IsMatch => HasFormat && Matches;

    public bool IsNoMatch => HasFormat && !Matches;

    /// <summary>Yes / No / No format.</summary>
    public string MatchText => !HasFormat ? OcrTexts.NoFormat : Matches ? OcrInfo.Yes : OcrInfo.No;

    public bool TooSmall { get; }

    public static string TooSmallText => Readers.ReaderRules.TooSmall;

    public string Time { get; }

    /// <summary>The value's cut-out of the frame; null without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCutOut))]
    public partial IImage? CutOut { get; set; }

    public bool HasCutOut => CutOut is not null;

    /// <summary>The cut-out as the server sent it; the dialog decodes its own copy, the row's may go while it is open.</summary>
    public byte[]? Picture { get; set; }
}
