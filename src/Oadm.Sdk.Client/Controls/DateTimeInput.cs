using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one date and time input (SDK 0.15.0): a date picker and a 24-hour time picker side by side, the From and To of a
/// range (<see cref="DateRangeButton"/>, Edge recording). Bind <see cref="SelectedDate"/> and <see cref="SelectedTime"/>
/// two-way; put it in a <see cref="FormField"/> whose <c>Error</c> says what is wrong with the pair.
/// </summary>
public sealed class DateTimeInput : StackPanel
{
    public static readonly StyledProperty<DateTimeOffset?> SelectedDateProperty =
        AvaloniaProperty.Register<DateTimeInput, DateTimeOffset?>(nameof(SelectedDate), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<TimeSpan?> SelectedTimeProperty =
        AvaloniaProperty.Register<DateTimeInput, TimeSpan?>(nameof(SelectedTime), defaultBindingMode: BindingMode.TwoWay);

    private bool _syncing;

    public DateTimeInput()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        DatePicker = new DatePicker();
        TimePicker = new TimePicker { ClockIdentifier = "24HourClock" };
        DatePicker.SelectedDateChanged += (_, e) => Sync(() => SelectedDate = e.NewDate);
        TimePicker.SelectedTimeChanged += (_, e) => Sync(() => SelectedTime = e.NewTime);
        Children.Add(DatePicker);
        Children.Add(TimePicker);
    }

    /// <summary>The day (its time of day is ignored).</summary>
    public DateTimeOffset? SelectedDate
    {
        get => GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>The time of day, 24 hours.</summary>
    public TimeSpan? SelectedTime
    {
        get => GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    /// <summary>The date picker (tests).</summary>
    public DatePicker DatePicker { get; }

    /// <summary>The time picker (tests).</summary>
    public TimePicker TimePicker { get; }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedDateProperty)
        {
            Sync(() => DatePicker.SelectedDate = SelectedDate);
        }
        else if (change.Property == SelectedTimeProperty)
        {
            Sync(() => TimePicker.SelectedTime = SelectedTime);
        }
    }

    private void Sync(Action action)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            action();
        }
        finally
        {
            _syncing = false;
        }
    }
}
