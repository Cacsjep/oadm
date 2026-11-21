using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The date range of a log or list (SDK 0.15.0; the Logs page left of its search): a command button that shows the applied
/// range ("All", "Last 24 hours", "12 Nov 09:00 – 14 Nov 18:00") and opens a flyout with the presets and Custom
/// (<see cref="DateRangePanel"/>). Bind <see cref="Range"/> to the view model's <see cref="DateRangeChoice"/> and query
/// with <see cref="DateRangeChoice.Resolve"/> on <see cref="DateRangeChoice.Changed"/>.
/// </summary>
public sealed class DateRangeButton : ToolbarButton
{
    public static readonly StyledProperty<DateRangeChoice?> RangeProperty =
        AvaloniaProperty.Register<DateRangeButton, DateRangeChoice?>(nameof(Range));

    private readonly Flyout _flyout;
    private readonly DateRangePanel _panel = new();

    public DateRangeButton()
    {
        IconKey = "calendarClock";
        ToolTip.SetTip(this, "Date range");
        _flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = _panel };
        _flyout.Opening += (_, _) =>
        {
            if (Range is { } range)
            {
                range.OnOpening();
                range.IsOpen = true;
            }
        };
        _flyout.Closed += (_, _) =>
        {
            if (Range is { } range)
            {
                range.IsOpen = false;
            }
        };
        Flyout = _flyout;
    }

    /// <summary>The range the button shows and the flyout changes.</summary>
    public DateRangeChoice? Range
    {
        get => GetValue(RangeProperty);
        set => SetValue(RangeProperty, value);
    }

    /// <summary>The flyout's content (tests).</summary>
    public DateRangePanel Panel => _panel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RangeProperty)
        {
            if (change.OldValue is DateRangeChoice old)
            {
                old.PropertyChanged -= OnRangeChanged;
            }

            if (change.NewValue is DateRangeChoice range)
            {
                range.PropertyChanged += OnRangeChanged;
            }

            _panel.DataContext = Range;
            Text = Range?.Text;
        }
    }

    private void OnRangeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DateRangeChoice.Text))
        {
            Text = Range?.Text;
        }
        else if (e.PropertyName == nameof(DateRangeChoice.IsOpen) && Range is { IsOpen: false })
        {
            _flyout.Hide();
        }
    }
}

/// <summary>
/// The content of the <see cref="DateRangeButton"/> flyout (DataContext a <see cref="DateRangeChoice"/>): one choice per
/// preset (a preset applies at once), Custom with From and To (date and 24-hour time, each error directly below its row)
/// and Apply.
/// </summary>
public sealed class DateRangePanel : StackPanel
{
    private const double LabelWidth = 48;
    private readonly Dictionary<DateRangePreset, RadioButton> _choices = [];
    private DateRangeChoice? _range;
    private bool _syncing;

    public DateRangePanel()
    {
        Spacing = 2;
        MinWidth = 220;
        var group = "dateRange" + Guid.NewGuid().ToString("N");
        foreach (var option in DateRangeChoice.Options)
        {
            var choice = new RadioButton { Content = option.Title, GroupName = group };
            choice.IsCheckedChanged += (_, _) =>
            {
                if (!_syncing && choice.IsChecked == true)
                {
                    _range?.Choose(option.Preset);
                }
            };
            _choices[option.Preset] = choice;
            Children.Add(choice);
        }

        var custom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        custom.Bind(IsVisibleProperty, new Binding(nameof(DateRangeChoice.IsCustomShown)));
        custom.Children.Add(Row("From", nameof(DateRangeChoice.FromDate), nameof(DateRangeChoice.FromTime), nameof(DateRangeChoice.FromError)));
        custom.Children.Add(Row("To", nameof(DateRangeChoice.ToDate), nameof(DateRangeChoice.ToTime), nameof(DateRangeChoice.ToError)));
        var apply = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right };
        apply.Classes.Add("primary");
        apply.Bind(Button.CommandProperty, new Binding(nameof(DateRangeChoice.ApplyCustomCommand)));
        apply.Bind(ToolTip.TipProperty, new Binding(nameof(DateRangeChoice.FormError)));
        ToolTip.SetShowOnDisabled(apply, true);
        custom.Children.Add(apply);
        Children.Add(custom);
    }

    /// <summary>The choice of a preset (tests).</summary>
    public RadioButton ChoiceOf(DateRangePreset preset) => _choices[preset];

    protected override Type StyleKeyOverride => typeof(StackPanel);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_range is not null)
        {
            _range.PropertyChanged -= OnRangeChanged;
        }

        _range = DataContext as DateRangeChoice;
        if (_range is not null)
        {
            _range.PropertyChanged += OnRangeChanged;
        }

        Mark();
    }

    private static FormField Row(string label, string date, string time, string error)
    {
        var input = new DateTimeInput();
        input.Bind(DateTimeInput.SelectedDateProperty, new Binding(date));
        input.Bind(DateTimeInput.SelectedTimeProperty, new Binding(time));
        var field = new FormField { Label = label, LabelWidth = LabelWidth, InputWidth = double.NaN, Input = input };
        field.Bind(FormField.ErrorProperty, new Binding(error));
        return field;
    }

    private void OnRangeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DateRangeChoice.Marked))
        {
            Mark();
        }
    }

    private void Mark()
    {
        _syncing = true;
        try
        {
            foreach (var (preset, choice) in _choices)
            {
                choice.IsChecked = _range is not null && _range.Marked == preset;
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
