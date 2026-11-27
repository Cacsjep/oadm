using System.ComponentModel;
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
    Last24Hours,
    Last7Days,
    Last30Days,
}

/// <summary>A choice of the time of the Stats tab.</summary>
public sealed record StatsRangeChoice(StatsRange Range, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Stats tab (docs/spec/license-plate-center.md "Stats", decisions 2026-11-27), like the dashboard installers know from
/// their plate systems: four cards (Last hour, Last 24 hours, Last 7 days, Total), the plate and the vehicle confidence per
/// camera (one bar per camera, coloured by the Log's thresholds, sideways when many), the reads and the confidence over
/// time (a Total line and a line per camera; Total and the 10 busiest cameras shown at first, the others in the legend).
/// Everything is counted on the server; the client only builds the bars and lines.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    /// <summary>The busiest cameras whose lines show at first; the others are in the legend, off.</summary>
    public const int ShownCameras = 10;

    /// <summary>The legend key of the Total line.</summary>
    public const string TotalKey = "total";

    private readonly LicensePlatesViewModel _page;
    private StatsReply? _reply;
    private int _version;
    private bool _applying;

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
    [
        new(StatsRange.Today, PageTexts.Today),
        new(StatsRange.Last24Hours, PageTexts.Last24Hours),
        new(StatsRange.Last7Days, PageTexts.Last7Days),
        new(StatsRange.Last30Days, PageTexts.Last30Days),
    ];

    [ObservableProperty] public partial StatsRangeChoice SelectedRange { get; set; }

    public string CameraChoiceText => _page.CameraChoice.ButtonText;

    [ObservableProperty] public partial string LastHourText { get; private set; } = "0";

    [ObservableProperty] public partial string Last24HoursText { get; private set; } = "0";

    [ObservableProperty] public partial string Last7DaysText { get; private set; } = "0";

    [ObservableProperty] public partial string TotalText { get; private set; } = "0";

    /// <summary>"Allowed 1,204 · Not allowed 31 · Unknown 412 · Average confidence 86 %": the chosen time, above the reads chart.</summary>
    [ObservableProperty] public partial string ResultsText { get; private set; } = string.Empty;

    /// <summary>One bar per camera: its average plate confidence; below 75 the warning colour, below 60 the error colour.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartBar> PlateBars { get; private set; } = [];

    /// <summary>The same for the vehicle confidence; a camera without one has no bar.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartBar> VehicleBars { get; private set; } = [];

    /// <summary>Whether any chosen camera sent a vehicle confidence.</summary>
    [ObservableProperty] public partial bool HasVehicleConfidence { get; private set; }

    /// <summary>The legend of both line charts: Total, then the cameras, busiest first.</summary>
    public RangeObservableCollection<ChartLegendItem> Legend { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> ReadSeries { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> ConfidenceSeries { get; private set; } = [];

    /// <summary>The time of the first step in this computer's zone.</summary>
    [ObservableProperty] public partial DateTimeOffset? ChartStart { get; private set; }

    [ObservableProperty] public partial TimeSpan ChartStep { get; private set; } = TimeSpan.FromHours(1);

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

    /// <summary>
    /// From, To and the step of the chosen time in this computer's zone: Today from midnight and Last 24 hours by the hour;
    /// Last 7 and 30 days by the day, from midnight 6 (29) days ago.
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To, StatsStep Step) Resolve()
    {
        var now = _page.Time.GetLocalNow();
        DateTimeOffset Midnight(int daysAgo)
        {
            var day = now.Date.AddDays(-daysAgo);
            return new DateTimeOffset(day, _page.Time.LocalTimeZone.GetUtcOffset(day));
        }

        return SelectedRange?.Range switch
        {
            StatsRange.Last24Hours => (now.AddHours(-24), now, StatsStep.Hour),
            StatsRange.Last7Days => (Midnight(6), now, StatsStep.Day),
            StatsRange.Last30Days => (Midnight(29), now, StatsStep.Day),
            _ => (Midnight(0), now, StatsStep.Hour),
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
            var (from, to, step) = Resolve();
            var choice = _page.CameraChoice;
            var zone = _page.Time.LocalTimeZone;
            var request = new StatsRequest
            {
                CameraIds = choice.Choice.Kind == DeviceChoiceKind.All ? null : [.. choice.Resolve().Select(d => d.Id)],
                From = from,
                To = to,
                Step = step,
                TimeZone = zone.Id,
                UtcOffsetMinutes = (int)zone.GetUtcOffset(to).TotalMinutes,
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
        _reply = reply;
        var totals = reply?.Totals ?? new ReadTotals(0, 0, 0, 0);
        LastHourText = Compact(totals.LastHour);
        Last24HoursText = Compact(totals.Last24Hours);
        Last7DaysText = Compact(totals.Last7Days);
        TotalText = Compact(totals.Total);
        var results = reply?.Results ?? new ResultCounts(0, 0, 0);
        HasReads = results.Total > 0 || totals.Total > 0;
        ResultsText = results.Total == 0 ? string.Empty : string.Join(" · ",
            $"{LicensePlateTexts.Allowed} {N(results.Allowed)}",
            $"{LicensePlateTexts.NotAllowed} {N(results.NotAllowed)}",
            $"{LicensePlateTexts.Unknown} {N(results.Unknown)}",
            $"{PageTexts.AverageConfidence} {Math.Round(reply!.AverageConfidence).ToString("0", CultureInfo.CurrentCulture)} %");

        var cameras = reply?.Cameras ?? [];
        var byName = cameras.OrderBy(c => c.Camera, StringComparer.CurrentCultureIgnoreCase).ToList();
        PlateBars = [.. byName.Select(c => Bar(c, c.Confidence))];
        VehicleBars = [.. byName.Select(c => Bar(c, c.VehicleConfidence))];
        HasVehicleConfidence = cameras.Any(c => c.VehicleConfidence is not null);

        ChartStep = reply?.Step == StatsStep.Day ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
        ChartStart = reply is null ? null : TimeZoneInfo.ConvertTime(reply.Start, _page.Time.LocalTimeZone);
        BuildLegend(cameras);
        BuildSeries();
    }

    /// <summary>
    /// Total and the cameras busiest first; the first <see cref="ShownCameras"/> shown, the others off. A camera the user turned
    /// on or off keeps that choice across reloads while it stays in the list.
    /// </summary>
    private void BuildLegend(IReadOnlyList<CameraSeries> cameras)
    {
        var before = Legend.ToDictionary(i => i.Key, i => i.IsShown, StringComparer.Ordinal);
        foreach (var item in Legend)
        {
            item.PropertyChanged -= OnLegendChanged;
        }

        var items = new List<ChartLegendItem>(cameras.Count + 1);
        if (cameras.Count > 0)
        {
            items.Add(new ChartLegendItem(TotalKey, PageTexts.Total, ChartColor.Strong, N(cameras.Sum(c => c.Reads))) { IsShown = before.GetValueOrDefault(TotalKey, true) });
        }

        for (var i = 0; i < cameras.Count; i++)
        {
            var key = cameras[i].CameraId.ToString("D");
            items.Add(new ChartLegendItem(key, cameras[i].Camera, ChartPalette.Of(i), N(cameras[i].Reads))
            {
                IsShown = before.TryGetValue(key, out var shown) ? shown : i < ShownCameras,
            });
        }

        foreach (var item in items)
        {
            item.PropertyChanged += OnLegendChanged;
        }

        Legend.ReplaceAll(items);
    }

    private void OnLegendChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartLegendItem.IsShown) && !_applying)
        {
            BuildSeries();
        }
    }

    /// <summary>The lines of the shown legend items: the reads per step and the average confidence per step (no reads: a gap).</summary>
    private void BuildSeries()
    {
        _applying = true;
        try
        {
            var reply = _reply;
            if (reply is null || Legend.Count == 0)
            {
                ReadSeries = [];
                ConfidenceSeries = [];
                return;
            }

            var cameras = reply.Cameras.ToDictionary(c => c.CameraId.ToString("D"), StringComparer.Ordinal);
            var reads = new List<ChartSeries>();
            var confidence = new List<ChartSeries>();
            foreach (var item in Legend.Where(i => i.IsShown))
            {
                var (perStep, average) = item.Key == TotalKey
                    ? (reply.TotalReads, reply.TotalConfidence)
                    : cameras.TryGetValue(item.Key, out var c) ? (c.ReadsPerStep, c.ConfidencePerStep) : ([], []);
                reads.Add(new ChartSeries(item.Name, [.. perStep.Select(v => (double)v)], item.Color));
                confidence.Add(new ChartSeries(item.Name, [.. average.Select(v => v ?? double.NaN)], item.Color));
            }

            ReadSeries = reads;
            ConfidenceSeries = confidence;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>A camera's bar: the average or no bar (NaN), the Log's colours, the name, the average and the reads on hover.</summary>
    private static ChartBar Bar(CameraSeries camera, double? average) =>
        new(camera.Camera, average ?? double.NaN, average switch
        {
            < ConfidenceLevels.Error => ChartColor.Error,
            < ConfidenceLevels.Warning => ChartColor.Warning,
            _ => ChartColor.Primary,
        })
        {
            Details = [LicensePlateTexts.Reads(camera.Reads)],
        };

    /// <summary>"812", "4,812", "49.6k", "123k", "4.4M".</summary>
    public static string Compact(long value)
    {
        var culture = CultureInfo.CurrentCulture;
        return value switch
        {
            < 10_000 => value.ToString("N0", culture),
            < 100_000 => (Math.Floor(value / 100.0) / 10).ToString("0.#", culture) + "k",
            < 1_000_000 => Math.Floor(value / 1000.0).ToString("0", culture) + "k",
            _ => (Math.Floor(value / 100_000.0) / 10).ToString("0.#", culture) + "M",
        };
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
