using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Ocr.Api;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The runtime stats below Settings (docs/spec/ocr.md "Runtime stats"): the traffic light, the recognition load as a bar,
/// pictures per second, reading time, readers watching, previews, camera streams (per site) and the server CPU. Filled from
/// <c>getStats</c> and the live event <c>stats</c>.
/// </summary>
public sealed partial class OcrStatsViewModel : ObservableObject
{
    /// <summary>Stats arrived (an older server has none: the panel stays hidden).</summary>
    [ObservableProperty]
    public partial bool HasStats { get; private set; }

    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsBusy), nameof(IsOverloaded))]
    public partial LoadState State { get; private set; }

    public bool IsOk => State == LoadState.Ok;

    public bool IsBusy => State == LoadState.Busy;

    public bool IsOverloaded => State == LoadState.Overloaded;

    /// <summary>0 to 100: the bar.</summary>
    [ObservableProperty]
    public partial double Load { get; private set; }

    /// <summary>"42 %".</summary>
    [ObservableProperty]
    public partial string LoadText { get; private set; } = string.Empty;

    /// <summary>"12.3".</summary>
    [ObservableProperty]
    public partial string PicturesText { get; private set; } = string.Empty;

    /// <summary>"85 ms".</summary>
    [ObservableProperty]
    public partial string ReadTimeText { get; private set; } = string.Empty;

    /// <summary>"3 of 50".</summary>
    [ObservableProperty]
    public partial string WatchingText { get; private set; } = string.Empty;

    /// <summary>"1 of 8".</summary>
    [ObservableProperty]
    public partial string PreviewsText { get; private set; } = string.Empty;

    /// <summary>"4".</summary>
    [ObservableProperty]
    public partial string StreamsText { get; private set; } = string.Empty;

    /// <summary>Per site: "Harbour" and "2 of 4"; empty without sites.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SiteRow> Sites { get; private set; } = [];

    /// <summary>"23 %".</summary>
    [ObservableProperty]
    public partial string CpuText { get; private set; } = string.Empty;

    public void Apply(OcrStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        State = stats.State;
        StateText = stats.StateText;
        Load = Math.Clamp(stats.Load, 0, 100);
        LoadText = OcrTexts.Percent(stats.Load);
        PicturesText = OcrTexts.Number(stats.PicturesPerSecond);
        ReadTimeText = OcrTexts.Milliseconds(stats.AverageReadMs);
        WatchingText = OcrTexts.OfLimit(stats.Watching, stats.MaxWatching);
        PreviewsText = OcrTexts.OfLimit(stats.Previews, stats.MaxPreviews);
        StreamsText = stats.Streams.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Sites = [.. stats.Sites.Select(s => new SiteRow(s.Name, OcrTexts.OfLimit(s.Streams, s.Max)))];
        CpuText = OcrTexts.Percent(stats.ServerCpu);
        HasStats = true;
    }
}

/// <summary>A site's streams in the stats: "Harbour", "2 of 4".</summary>
public sealed record SiteRow(string Name, string Streams);
