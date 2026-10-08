using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The progress line used for scans and uploads: the theme progress bar (0 to 100, flowing
/// "stream" dots while <see cref="IsActive"/>) with a secondary status text on its right.
/// </summary>
public sealed class ProgressRow : Grid
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ProgressRow, double>(nameof(Value));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ProgressRow, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ProgressRow, bool>(nameof(IsActive), true);

    /// <summary>False hides the bar and leaves the status text alone on the left (e.g. "Import finished, ...").</summary>
    public static readonly StyledProperty<bool> ShowBarProperty =
        AvaloniaProperty.Register<ProgressRow, bool>(nameof(ShowBar), true);

    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public ProgressRow()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _text.Classes.Add("secondary");
        _bar.Classes.Add("stream");
        SetColumn(_text, 1);
        Children.Add(_bar);
        Children.Add(_text);
    }

    /// <summary>Progress in percent (0 to 100).</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>True while work is running: shows the flowing stream in the unfilled part.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool ShowBar
    {
        get => GetValue(ShowBarProperty);
        set => SetValue(ShowBarProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
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
            _bar.Classes.Set("stream", IsActive);
        }
        else if (change.Property == ShowBarProperty)
        {
            _bar.IsVisible = ShowBar;
            SetColumn(_text, ShowBar ? 1 : 0);
            _text.Margin = new Thickness(ShowBar ? 12 : 0, 0, 0, 0);
        }
    }
}
