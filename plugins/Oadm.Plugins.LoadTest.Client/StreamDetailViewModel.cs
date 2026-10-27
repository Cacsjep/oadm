using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>The side panel of one camera stream: its own rate, loss and jitter over its batch (<c>getStream</c>).</summary>
public sealed partial class StreamDetailViewModel : ObservableObject
{
    public StreamDetailViewModel(StreamRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Title = string.IsNullOrEmpty(row.SourceName) ? row.Name : row.Name + " · " + row.SourceName;
        var parts = new List<string> { row.Address };
        if (!string.IsNullOrEmpty(row.Model))
        {
            parts.Add(row.Model);
        }

        if (!string.IsNullOrEmpty(row.Stream))
        {
            parts.Add(row.Stream);
        }

        parts.Add("batch " + row.Batch.ToString(System.Globalization.CultureInfo.CurrentCulture));
        InfoText = string.Join(" · ", parts);
        ProblemText = row.Problem;
        NoteText = row.Note ?? (row.Problem is null && string.Equals(row.Transport, "TCP", StringComparison.OrdinalIgnoreCase) ? CheckTexts.SitesNoPacketLoss : null);
        LossName = string.Equals(row.Transport, "TCP", StringComparison.OrdinalIgnoreCase) ? "Frame loss" : "Packet loss";
    }

    public string Title { get; }

    public string InfoText { get; }

    public string? ProblemText { get; }

    public bool HasProblem => !string.IsNullOrEmpty(ProblemText);

    /// <summary>A finding that is no failure (UDP blocked, no packet loss at sites): secondary text, no error chip.</summary>
    public string? NoteText { get; }

    public bool HasNote => !string.IsNullOrEmpty(NoteText);

    /// <summary>"Packet loss", or "Frame loss" for a camera at a site.</summary>
    public string LossName { get; }

    public static double LossThreshold => LoadTestInfo.MaxPacketLossPercent;

    public static string NoValues => CheckTexts.NoValues;

    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty] public partial IReadOnlyList<ChartBand> PhaseBands { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> RateSeries { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> LossSeries { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> JitterSeries { get; private set; } = [];

    public void Apply(StreamDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        RateSeries = [new ChartSeries("Rate", [.. detail.Mbps], ChartColor.Primary)];
        LossSeries = [new ChartSeries(LossName, [.. detail.LossPercent], ChartColor.Warning)];
        JitterSeries = [new ChartSeries("Jitter", [.. detail.JitterMs], ChartColor.Primary)];
        PhaseBands = CheckGraphs.BandsOf(detail.Phases);
        IsLoading = false;
    }

    public void Fail(string message)
    {
        ErrorText = message;
        IsLoading = false;
    }
}
