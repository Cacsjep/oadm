using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Sdk.Client.Controls;

/// <summary>The ranges of <see cref="DateRangeButton"/>.</summary>
public enum DateRangePreset
{
    All,
    LastHour,
    Today,
    Last24Hours,
    Last7Days,
    Custom,
}

/// <summary>One choice of the range flyout: "Last 24 hours".</summary>
/// <param name="Preset">The range it stands for.</param>
/// <param name="Title">Its text in the flyout and on the button.</param>
public sealed record DateRangeOption(DateRangePreset Preset, string Title);

/// <summary>
/// The date range of a log or list (<see cref="DateRangeButton"/>, SDK 0.15.0): the presets Last hour, Today, Last 24 hours,
/// Last 7 days and All, or Custom with From and To (date and 24-hour time, this computer's time). A preset is relative:
/// <see cref="Resolve"/> gives its instants at the moment of the query. The button text says the applied range ("All",
/// "Last 24 hours", "12 Nov 09:00 – 14 Nov 18:00"); <see cref="Changed"/> fires when it changes. Not kept: All at each start.
/// The Custom fields validate like every form (To after From, the error directly below To).
/// </summary>
public sealed partial class DateRangeChoice : ValidatingViewModel
{
    private readonly TimeProvider _time;
    private DateTimeOffset? _customFrom;
    private DateTimeOffset? _customTo;

    /// <summary>A range of All.</summary>
    /// <param name="time">The clock and this computer's time zone (tests pass a fake one); null: the system's.</param>
    public DateRangeChoice(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Validation
            .Rule(nameof(FromDate), FromRule)
            .Rule(nameof(ToDate), ToRule);
        Validation.Validate();
    }

    /// <summary>The choices of the flyout, in order.</summary>
    public static IReadOnlyList<DateRangeOption> Options { get; } =
    [
        new(DateRangePreset.LastHour, "Last hour"),
        new(DateRangePreset.Today, "Today"),
        new(DateRangePreset.Last24Hours, "Last 24 hours"),
        new(DateRangePreset.Last7Days, "Last 7 days"),
        new(DateRangePreset.All, "All"),
        new(DateRangePreset.Custom, "Custom"),
    ];

    /// <summary>The applied range changed (a preset was chosen or Custom applied).</summary>
    public event EventHandler? Changed;

    /// <summary>The applied range.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(IsAll))]
    public partial DateRangePreset Preset { get; private set; }

    /// <summary>No range: every entry.</summary>
    public bool IsAll => Preset == DateRangePreset.All;

    /// <summary>The flyout is open (bound two-way by the button).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>The choice marked in the open flyout; Custom shows From and To.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomShown))]
    public partial DateRangePreset Marked { get; set; }

    /// <summary>From and To show in the flyout.</summary>
    public bool IsCustomShown => Marked == DateRangePreset.Custom;

    /// <summary>The From day (the time of day comes from <see cref="FromTime"/>).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? FromDate { get; set; }

    /// <summary>The From time of day (24 hours).</summary>
    [ObservableProperty]
    public partial TimeSpan? FromTime { get; set; }

    /// <summary>The To day (the time of day comes from <see cref="ToTime"/>).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? ToDate { get; set; }

    /// <summary>The To time of day (24 hours).</summary>
    [ObservableProperty]
    public partial TimeSpan? ToTime { get; set; }

    /// <summary>The visible error of From (below its pickers).</summary>
    public string? FromError => ErrorOf(nameof(FromDate));

    /// <summary>The visible error of To.</summary>
    public string? ToError => ErrorOf(nameof(ToDate));

    /// <summary>The button text: "All", "Last 24 hours", "12 Nov 09:00 – 14 Nov 18:00".</summary>
    public string Text => Preset == DateRangePreset.Custom && _customFrom is { } from && _customTo is { } to
        ? Short(from) + " – " + Short(to)
        : Options.Single(o => o.Preset == Preset).Title;

    /// <summary>
    /// The instants of the applied range now: From (null = from the oldest) and To (null = up to now). Today starts at
    /// midnight of this computer's day.
    /// </summary>
    public (DateTimeOffset? From, DateTimeOffset? To) Resolve()
    {
        var now = _time.GetUtcNow();
        return Preset switch
        {
            DateRangePreset.LastHour => (now.AddHours(-1), null),
            DateRangePreset.Today => (LocalMidnight(now), null),
            DateRangePreset.Last24Hours => (now.AddHours(-24), null),
            DateRangePreset.Last7Days => (now.AddDays(-7), null),
            DateRangePreset.Custom => (_customFrom, _customTo),
            _ => (null, null),
        };
    }

    /// <summary>Marks a choice in the flyout: a preset applies at once and closes it; Custom shows From and To.</summary>
    [RelayCommand]
    public void Choose(DateRangePreset preset)
    {
        Marked = preset;
        if (preset == DateRangePreset.Custom)
        {
            return;
        }

        Apply(preset, null, null);
        IsOpen = false;
    }

    private bool CanApplyCustom() => IsFormValid;

    /// <summary>Applies From and To (Apply in the flyout); shows the errors instead while they are not valid.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCustom))]
    public void ApplyCustom()
    {
        Validation.ShowAll();
        RaiseErrors();
        if (!IsFormValid || Combine(FromDate, FromTime) is not { } from || Combine(ToDate, ToTime) is not { } to)
        {
            return;
        }

        Apply(DateRangePreset.Custom, from, to);
        IsOpen = false;
    }

    /// <summary>Back to All (logout).</summary>
    public void Reset() => Apply(DateRangePreset.All, null, null);

    /// <summary>Sets a custom range directly (tests, a range another page hands over).</summary>
    public void SetCustom(DateTimeOffset from, DateTimeOffset to)
    {
        FillCustom(from, to);
        Apply(DateRangePreset.Custom, from, to);
    }

    /// <summary>The flyout opens: it marks the applied range and fills From and To with it (or with today).</summary>
    public void OnOpening()
    {
        Marked = Preset;
        var (from, to) = Resolve();
        var now = _time.GetUtcNow();
        if (from is null)
        {
            from = LocalMidnight(now);
            to = from.Value.AddDays(1);
        }

        FillCustom(from.Value, to ?? NextHour(now));
        Validation.Reset();
        RaiseErrors();
    }

    protected override void OnValidationChanged()
    {
        RaiseErrors();
        ApplyCustomCommand.NotifyCanExecuteChanged();
    }

    // A time picker belongs to the date picker of its row: the row's error shows once either was changed.
    partial void OnFromTimeChanged(TimeSpan? value) => Validation.Touch(nameof(FromDate));

    partial void OnToTimeChanged(TimeSpan? value) => Validation.Touch(nameof(ToDate));

    private void Apply(DateRangePreset preset, DateTimeOffset? from, DateTimeOffset? to)
    {
        var changed = preset != Preset || from != _customFrom || to != _customTo;
        _customFrom = from;
        _customTo = to;
        Preset = preset;
        Marked = preset;
        OnPropertyChanged(nameof(Text));
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FillCustom(DateTimeOffset from, DateTimeOffset to)
    {
        var localFrom = TimeZoneInfo.ConvertTime(from, _time.LocalTimeZone);
        var localTo = TimeZoneInfo.ConvertTime(to, _time.LocalTimeZone);
        FromDate = new DateTimeOffset(localFrom.Date, localFrom.Offset);
        FromTime = new TimeSpan(localFrom.Hour, localFrom.Minute, 0);
        ToDate = new DateTimeOffset(localTo.Date, localTo.Offset);
        ToTime = new TimeSpan(localTo.Hour, localTo.Minute, 0);
    }

    private void RaiseErrors()
    {
        OnPropertyChanged(nameof(FromError));
        OnPropertyChanged(nameof(ToError));
    }

    private string? FromRule() =>
        FromDate is null ? "Choose the start date." : FromTime is null ? "Choose the start time." : null;

    private string? ToRule()
    {
        if (ToDate is null || ToTime is null)
        {
            return ToDate is null ? "Choose the end date." : "Choose the end time.";
        }

        return Combine(FromDate, FromTime) is { } from && Combine(ToDate, ToTime) is { } to && to <= from ? "To must be after From." : null;
    }

    /// <summary>The day of a date picker and the time of a time picker as an instant of this computer's time zone.</summary>
    private DateTimeOffset? Combine(DateTimeOffset? date, TimeSpan? time)
    {
        if (date is not { } day || time is not { } of)
        {
            return null;
        }

        var local = day.Date + of;
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
    }

    private DateTimeOffset LocalMidnight(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone);
        return new DateTimeOffset(local.Date, _time.LocalTimeZone.GetUtcOffset(local.Date));
    }

    private DateTimeOffset NextHour(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone);
        var hour = local.Date.AddHours(local.Hour + 1);
        return new DateTimeOffset(hour, _time.LocalTimeZone.GetUtcOffset(hour));
    }

    /// <summary>"12 Nov 09:00", with the year when it is not this year.</summary>
    private string Short(DateTimeOffset time)
    {
        var local = TimeZoneInfo.ConvertTime(time, _time.LocalTimeZone);
        var thisYear = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).Year;
        return local.ToString(local.Year == thisYear ? "d MMM HH:mm" : "d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}
