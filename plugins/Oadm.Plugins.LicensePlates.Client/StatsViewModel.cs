using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The time of the Stats tab.</summary>
public enum StatsRange
{
    Today,
    Last7Days,
    Last30Days,
}

/// <summary>A choice of the time of the Stats tab.</summary>
public sealed record StatsRangeChoice(StatsRange Range, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One camera's row of the Stats tab (sorted by any column).</summary>
public sealed record CameraStatsRow(CameraStats Stats, string LastReadText)
{
    public string Camera => Stats.Camera;

    public int Reads => Stats.Reads;

    public string ReadsText => Stats.Reads.ToString("N0", CultureInfo.CurrentCulture);

    public double AllowedShare => Stats.AllowedShare;

    public string AllowedText => PageTexts.Percent(Stats.AllowedShare);

    public double AverageConfidence => Stats.AverageConfidence;

    public string AverageConfidenceText => Math.Round(Stats.AverageConfidence).ToString("0", CultureInfo.CurrentCulture);

    public double LowShare => Stats.LowShare;

    public string LowText => PageTexts.Percent(Stats.LowShare);

    public double Below75Share => Stats.Below75Share;

    public string Below75Text => PageTexts.Percent(Stats.Below75Share);

    /// <summary>The average below 75: the warning colour; below 60: the error colour (the Log's thresholds).</summary>
    public bool IsLowConfidence => Stats.AverageConfidence is >= ConfidenceLevels.Error and < ConfidenceLevels.Warning;

    public bool IsVeryLowConfidence => Stats.AverageConfidence < ConfidenceLevels.Error;

    public DateTimeOffset LastReadSortKey => Stats.LastRead ?? DateTimeOffset.MinValue;
}

/// <summary>
/// The Stats tab (docs/spec/license-plate-center.md "Stats"): for the page's camera choice and Today, Last 7 days or Last 30
/// days, counted on the server from the log's count tables: the reads by result, the reads per hour of the
/// day (one bar per hour; over several days the sum of each hour) and the cameras.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly LicensePlatesViewModel _page;
    private int _version;

    internal StatsViewModel(LicensePlatesViewModel page)
    {
        _page = page;
        SelectedRange = RangeChoices[0];
        page.CameraChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageDeviceChoice.Choice))
            {
                OnPropertyChanged(nameof(CameraChoiceText));
                if (page.IsStatsTab)
                {
                    RequestReload();
                }
            }
        };
    }

    public static IReadOnlyList<StatsRangeChoice> RangeChoices { get; } =
        [new(StatsRange.Today, PageTexts.Today), new(StatsRange.Last7Days, PageTexts.Last7Days), new(StatsRange.Last30Days, PageTexts.Last30Days)];

    [ObservableProperty] public partial StatsRangeChoice SelectedRange { get; set; }

    public string CameraChoiceText => _page.CameraChoice.ButtonText;

    [ObservableProperty] public partial string TotalText { get; private set; } = "0";

    [ObservableProperty] public partial string AllowedText { get; private set; } = "0";

    [ObservableProperty] public partial string NotAllowedText { get; private set; } = "0";

    [ObservableProperty] public partial string UnknownText { get; private set; } = "0";

    [ObservableProperty] public partial bool HasNotAllowed { get; private set; }

    /// <summary>The average confidence of every read, a whole number 0 to 100.</summary>
    [ObservableProperty] public partial string AverageConfidenceText { get; private set; } = "0";

    [ObservableProperty] public partial bool IsConfidenceWarning { get; private set; }

    [ObservableProperty] public partial bool IsConfidenceError { get; private set; }

    /// <summary>The reads in the four confidence bands ("Below 60 · 4 %"), the two low ones in the error and warning colours.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartBar> ConfidenceBars { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartBar> HourBars { get; private set; } = [];

    public RangeObservableCollection<CameraStatsRow> PerCamera { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool HasReads { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool ShowEmpty => !IsLoading && !HasReads;

    public string EmptyText => _page.CameraChoice.IsEmpty ? PageTexts.ChooseCamerasFirst : PageTexts.NoStats;

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public void RequestReload() => LoadTask = LoadAsync(++_version);

    partial void OnSelectedRangeChanged(StatsRangeChoice value) => RequestReload();

    [RelayCommand]
    private async Task ChooseCamerasAsync()
    {
        if (_page.Dialogs is { } dialogs)
        {
            await dialogs.PickCameraChoiceAsync(_page.CameraChoice).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Refresh() => RequestReload();

    /// <summary>The From and To of the chosen time in this computer's zone (Today from midnight).</summary>
    public (DateTimeOffset From, DateTimeOffset To) Resolve()
    {
        var now = _page.Time.GetLocalNow();
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        return SelectedRange?.Range switch
        {
            StatsRange.Last7Days => (now.AddDays(-7), now),
            StatsRange.Last30Days => (now.AddDays(-30), now),
            _ => (midnight, now),
        };
    }

    private async Task LoadAsync(int version)
    {
        if (!_page.MayViewLog || _page.CameraChoice.IsEmpty)
        {
            Apply(null);
            OnPropertyChanged(nameof(EmptyText));
            return;
        }

        IsLoading = true;
        ErrorText = null;
        try
        {
            var (from, to) = Resolve();
            var choice = _page.CameraChoice;
            var request = new StatsRequest
            {
                CameraIds = choice.Choice.Kind == DeviceChoiceKind.All ? null : [.. choice.Resolve().Select(d => d.Id)],
                From = from,
                To = to,
            };
            StatsReply? reply = null;
            var error = await GuardedRun.RunAsync(async () =>
                reply = await _page.Client.CallAsync<StatsReply>(LicensePlatesMethods.Stats, request).ConfigureAwait(true)).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            ErrorText = error;
            Apply(reply);
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    internal void Apply(StatsReply? reply)
    {
        var results = reply?.Results ?? new ResultCounts(0, 0, 0);
        TotalText = N(results.Total);
        AllowedText = N(results.Allowed);
        NotAllowedText = N(results.NotAllowed);
        UnknownText = N(results.Unknown);
        HasNotAllowed = results.NotAllowed > 0;
        var average = reply?.AverageConfidence ?? 0;
        AverageConfidenceText = Math.Round(average).ToString("0", CultureInfo.CurrentCulture);
        IsConfidenceError = results.Total > 0 && average < ConfidenceLevels.Error;
        IsConfidenceWarning = results.Total > 0 && average is >= ConfidenceLevels.Error and < ConfidenceLevels.Warning;
        ConfidenceBars = Bands(reply?.Confidence);
        HasReads = results.Total > 0;
        HourBars = Hours(reply?.PerHour ?? []);
        PerCamera.ReplaceAll([.. (reply?.PerCamera ?? []).OrderByDescending(c => c.Reads).Select(c => new CameraStatsRow(c, LastRead(c.LastRead)))]);
    }

    /// <summary>24 bars "00" to "23": the reads of each hour of the day in this computer's zone.</summary>
    private IReadOnlyList<ChartBar> Hours(IReadOnlyList<HourCount> perHour)
    {
        if (perHour.Count == 0)
        {
            return [];
        }

        var sums = new int[24];
        foreach (var hour in perHour)
        {
            sums[TimeZoneInfo.ConvertTime(hour.Hour, _page.Time.LocalTimeZone).Hour] += hour.Reads;
        }

        return [.. sums.Select((reads, hour) => new ChartBar(hour.ToString("00", CultureInfo.InvariantCulture), reads))];
    }

    private string LastRead(DateTimeOffset? time)
    {
        if (time is not { } t)
        {
            return string.Empty;
        }

        var local = TimeZoneInfo.ConvertTime(t, _page.Time.LocalTimeZone);
        var today = _page.Time.GetLocalNow().Date;
        return local.ToString(local.Date == today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Four bars: below 60 (error), 60 to 74 (warning), 75 to 89 and 90 and above; the label says the share.</summary>
    private static IReadOnlyList<ChartBar> Bands(ConfidenceBands? bands)
    {
        if (bands is not { Total: > 0 } b)
        {
            return [];
        }

        string Label(string name, int count) => name + " · " + PageTexts.Percent((double)count / b.Total);
        return
        [
            new ChartBar(Label(PageTexts.ShareLow, b.Below60), b.Below60, ChartColor.Error),
            new ChartBar(Label(PageTexts.From60To74, b.From60To74), b.From60To74, ChartColor.Warning),
            new ChartBar(Label(PageTexts.From75To89, b.From75To89), b.From75To89),
            new ChartBar(Label(PageTexts.From90, b.From90), b.From90),
        ];
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
