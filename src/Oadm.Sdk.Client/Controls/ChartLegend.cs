using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Sdk.Client.Controls;

/// <summary>One line of a <see cref="ChartLegend"/>: its name, colour and whether the chart shows it (SDK 0.17.0).</summary>
public sealed partial class ChartLegendItem(string key, string name, ChartColor color, string? detail = null) : ObservableObject
{
    /// <summary>The id of the line (a camera's id).</summary>
    public string Key { get; } = key;

    /// <summary>The line's name in the legend ("Total", a camera).</summary>
    public string Name { get; } = name;

    /// <summary>The line's colour role; the chart draws the line in it.</summary>
    public ChartColor Color { get; } = color;

    /// <summary>A short number right of the name ("1,204"); null: none.</summary>
    public string? Detail { get; } = detail;

    /// <summary>The chart shows the line; a click on the legend line turns it on or off.</summary>
    [ObservableProperty] public partial bool IsShown { get; set; } = true;
}

/// <summary>
/// The legend of a <see cref="LineChart"/> with many lines (SDK 0.17.0; a line per camera): a virtualized list beside the
/// chart with a check box, the line's colour and its name per <see cref="ChartLegendItem"/>; a click shows or hides the
/// line. The page builds the chart's series from the shown items (set <see cref="LineChart.ShowLegend"/> to false).
/// </summary>
public sealed class ChartLegend : UserControl
{
    public static readonly StyledProperty<IEnumerable<ChartLegendItem>?> ItemsProperty =
        AvaloniaProperty.Register<ChartLegend, IEnumerable<ChartLegendItem>?>(nameof(Items));

    private readonly ItemsControl _list = new()
    {
        Name = "LegendList",
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
    };

    public ChartLegend()
    {
        _list.ItemTemplate = new FuncDataTemplate<ChartLegendItem>((item, _) =>
        {
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable(ChartPaint.KeyOf(item?.Color ?? ChartColor.Primary)));
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(ChartLegendItem.Name)));
            ToolTip.SetTip(name, item?.Name);
            var detail = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
            detail.Bind(TextBlock.TextProperty, new Binding(nameof(ChartLegendItem.Detail)));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 0, 14, 0) }; // clear of the scroll bar
            row.Children.Add(dot);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);
            var box = new CheckBox { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            box.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(ChartLegendItem.IsShown)) { Mode = BindingMode.TwoWay });
            return box;
        });
        Content = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    /// <summary>The lines in legend order (Total first, then the busiest).</summary>
    public IEnumerable<ChartLegendItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>The list (tests).</summary>
    public ItemsControl List => _list;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            _list.ItemsSource = Items;
        }
    }
}
