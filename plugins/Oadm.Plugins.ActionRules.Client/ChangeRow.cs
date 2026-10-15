using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Metadata;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>
/// One part of the plugin's change forms: a Change check box (unchecked = each device keeps its own value) and the part's
/// <see cref="FormField"/> (label, input, hint, error below the input). Used by every row of the rule, recipient and
/// schedule forms, so they look and behave the same.
/// </summary>
public sealed class ChangeRow : Control
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ChangeRow, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> CanToggleProperty =
        AvaloniaProperty.Register<ChangeRow, bool>(nameof(CanToggle), true);

    public static readonly StyledProperty<bool> ShowCheckProperty =
        AvaloniaProperty.Register<ChangeRow, bool>(nameof(ShowCheck), true);

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ChangeRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<ChangeRow, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> ErrorProperty =
        AvaloniaProperty.Register<ChangeRow, string?>(nameof(Error));

    public static readonly StyledProperty<string?> InfoProperty =
        AvaloniaProperty.Register<ChangeRow, string?>(nameof(Info));

    public static readonly StyledProperty<Control?> InputProperty =
        AvaloniaProperty.Register<ChangeRow, Control?>(nameof(Input));

    private readonly FormField _field = new();
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

    public ChangeRow()
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Width = 32 };
        check.Classes.Add("inputHeight");
        ToolTip.SetTip(check, "Change");
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(IsChecked)) { Source = this, Mode = BindingMode.TwoWay });
        check.Bind(IsEnabledProperty, new Binding(nameof(CanToggle)) { Source = this });
        check.Bind(IsVisibleProperty, new Binding(nameof(ShowCheck)) { Source = this });
        _field.Classes.Add("wide");
        _field.Bind(FormField.LabelProperty, new Binding(nameof(Label)) { Source = this });
        _field.Bind(FormField.HintProperty, new Binding(nameof(Hint)) { Source = this });
        _field.Bind(FormField.ErrorProperty, new Binding(nameof(Error)) { Source = this });
        _field.Bind(FormField.InfoProperty, new Binding(nameof(Info)) { Source = this });
        Grid.SetColumn(_field, 1);
        _grid.Children.Add(check);
        _grid.Children.Add(_field);
        LogicalChildren.Add(_grid);
        VisualChildren.Add(_grid);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>False for a part that must change (a setting the new action needs).</summary>
    public bool CanToggle
    {
        get => GetValue(CanToggleProperty);
        set => SetValue(CanToggleProperty, value);
    }

    /// <summary>False where everything is set anyway (a new schedule).</summary>
    public bool ShowCheck
    {
        get => GetValue(ShowCheckProperty);
        set => SetValue(ShowCheckProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public string? Info
    {
        get => GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    /// <summary>The part's input (text box, select, ...), shown in the form field.</summary>
    [Content]
    public Control? Input
    {
        get => GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _grid.Measure(availableSize);
        return _grid.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _grid.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == InputProperty)
        {
            _field.Input = change.GetNewValue<Control?>();
        }
    }
}
