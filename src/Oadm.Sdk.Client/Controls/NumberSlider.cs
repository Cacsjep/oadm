using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// A number with a slider beside it (a minimum score, a size, a number of frames): the number input first, its unit, then the
/// slider over <see cref="Minimum"/> to <see cref="Maximum"/>; both show and change the one <see cref="Text"/> (bind it two-way
/// to the view model's text of the number). Typing keeps exactly what was typed; moving the slider writes whole steps
/// (<see cref="Step"/>). Its validation error goes below the whole row through <see cref="FormField.Error"/>; with
/// <see cref="HasError"/> the input gets the red border of the theme.
/// </summary>
public sealed class NumberSlider : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NumberSlider, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NumberSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NumberSlider, double>(nameof(Maximum), 100);

    /// <summary>The slider's step; 0: one for small ranges, a hundredth of the range for large ones.</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<NumberSlider, double>(nameof(Step));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<NumberSlider, string?>(nameof(Unit));

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<NumberSlider, string?>(nameof(Placeholder));

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<NumberSlider, bool>(nameof(HasError));

    private readonly TextBox _box;
    private readonly TextBlock _unit;
    private readonly Slider _slider;
    private bool _syncing;

    public NumberSlider()
    {
        _box = new TextBox { Name = "NumberBox", Width = 72, VerticalAlignment = VerticalAlignment.Center };
        _box.TextChanged += (_, _) =>
        {
            if (!_syncing && Text != _box.Text)
            {
                Text = _box.Text;
            }
        };
        _unit = new TextBlock { VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _unit.Classes.Add("secondary");
        _slider = new Slider { Name = "NumberSlider", VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true };
        _slider.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                Text = Format(e.NewValue);
            }
        };
        Grid.SetColumn(_unit, 1);
        Grid.SetColumn(_slider, 2);
        Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8, Children = { _box, _unit, _slider } };
        UpdateRange();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    /// <summary>The number input (tests type into it).</summary>
    public TextBox Box => _box;

    /// <summary>The slider (tests move it).</summary>
    public Slider Slider => _slider;

    /// <summary>The slider's step for a range: one up to 200, else a hundredth of the range.</summary>
    public static double StepFor(double minimum, double maximum, double step) =>
        step > 0 ? step : maximum - minimum <= 200 ? 1 : Math.Round((maximum - minimum) / 100);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Sync();
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty || change.Property == StepProperty)
        {
            UpdateRange();
        }
        else if (change.Property == UnitProperty)
        {
            _unit.Text = Unit;
            _unit.IsVisible = !string.IsNullOrEmpty(Unit);
        }
        else if (change.Property == PlaceholderProperty)
        {
            _box.PlaceholderText = Placeholder;
        }
        else if (change.Property == HasErrorProperty)
        {
            Classes.Set("error", HasError);
        }
    }

    private void UpdateRange()
    {
        _syncing = true;
        try
        {
            _slider.Minimum = Minimum;
            _slider.Maximum = Math.Max(Minimum, Maximum);
            var step = StepFor(Minimum, Maximum, Step);
            _slider.TickFrequency = step;
            _slider.SmallChange = step;
            _slider.LargeChange = step * 10;
        }
        finally
        {
            _syncing = false;
        }

        Sync();
    }

    /// <summary>The box shows the text as it is; the slider the number when it is one (held inside its range).</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            if (_box.Text != Text)
            {
                _box.Text = Text;
            }

            if (double.TryParse((Text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                _slider.Value = Math.Clamp(value, _slider.Minimum, _slider.Maximum);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static string Format(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
