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

/// <summary>One direction's reads by result.</summary>
public sealed record DirectionStatsRow(string Direction, string Reads, string Allowed, string NotAllowed, string Unknown, int SortReads);

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

    public DateTimeOffset LastReadSortKey => Stats.LastRead ?? DateTimeOffset.MinValue;
}

/// <summary>One of the 10 most read plates.</summary>
public sealed record PlateCountRow(int Rank, string Plate, string Reads);

/// <summary>
/// The Stats tab (docs/spec/license-plate-center.md "Stats"): for the page's camera choice and Today, Last 7 days or Last 30
/// days, counted on the server from the log's count tables: the reads by result and by direction, the reads per hour of the
/// day (one bar per hour; over several days the sum of each hour), the cameras, and the 10 most read plates and unknown plates.
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

    [ObservableProperty] public partial IReadOnlyList<ChartBar> HourBars { get; private set; } = [];

    public RangeObservableCollection<DirectionStatsRow> Directions { get; } = [];

    public RangeObservableCollection<CameraStatsRow> PerCamera { get; } = [];

    public RangeObservableCollection<PlateCountRow> TopPlates { get; } = [];

    public RangeObservableCollection<PlateCountRow> TopUnknown { get; } = [];

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
        HasReads = results.Total > 0;
        Directions.ReplaceAll([.. (reply?.ByDirection ?? [])
            .OrderBy(d => d.Direction switch { PlateDirection.In => 0, PlateDirection.Out => 1, _ => 2 })
            .Select(d => new DirectionStatsRow(LicensePlateTexts.Direction(d.Direction), N(d.Counts.Total), N(d.Counts.Allowed), N(d.Counts.NotAllowed), N(d.Counts.Unknown), d.Counts.Total))]);
        HourBars = Hours(reply?.PerHour ?? []);
        PerCamera.ReplaceAll([.. (reply?.PerCamera ?? []).OrderByDescending(c => c.Reads).Select(c => new CameraStatsRow(c, LastRead(c.LastRead)))]);
        TopPlates.ReplaceAll(Top(reply?.TopPlates));
        TopUnknown.ReplaceAll(Top(reply?.TopUnknown));
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

    private static IReadOnlyList<PlateCountRow> Top(IReadOnlyList<PlateCount>? plates) =>
        [.. (plates ?? []).Take(10).Select((p, i) => new PlateCountRow(i + 1, p.Plate, N(p.Reads)))];

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
