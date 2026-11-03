using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Ocr.Api;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The runtime stats below Settings (docs/spec/ocr.md "Runtime stats"): the traffic light and the recognition load as a bar
/// (decision 2026-11-03: no further numbers, the traffic light already says whether all is well). Filled from
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

    public void Apply(OcrStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        State = stats.State;
        StateText = stats.StateText;
        Load = Math.Clamp(stats.Load, 0, 100);
        LoadText = OcrTexts.Percent(stats.Load);
        HasStats = true;
    }
}
