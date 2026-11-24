using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The Progress cell of a grid of running work (the Tasks grid, the runs of Schedules): the progress bar with its flowing
/// stream while running and the percent right of it ("42 %"); shown only while the work runs (<see cref="IsActive"/>).
/// </summary>
public sealed class ProgressCell : Grid
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ProgressCell, double>(nameof(Value));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ProgressCell, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ProgressCell, bool>(nameof(IsActive));

    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100 };
    private readonly TextBlock _text = new() { Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };

    public ProgressCell()
    {
        ColumnDefinitions = new ColumnDefinitions("*,52");
        Margin = new Thickness(10, 0, 12, 0);
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;
        _text.Classes.Add("secondary");
        SetColumn(_text, 1);
        Children.Add(_bar);
        Children.Add(_text);
    }

    /// <summary>0 to 100.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The percent beside the bar ("42 %").</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The work runs: the cell shows, the bar flows.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The bar (tests).</summary>
    public ProgressBar Bar => _bar;

    protected override Type StyleKeyOverride => typeof(Grid);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            _bar.Value = Value;
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
        else if (change.Property == IsActiveProperty)
        {
            IsVisible = IsActive;
            _bar.Classes.Set("stream", IsActive);
        }
    }
}
