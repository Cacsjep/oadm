using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The devices grouped by tag in one virtualized list (Web View, Live View): a group header with a chevron (a click
/// collapses it), the devices with <see cref="DeviceIcon"/> and address below. A click or Enter on a device raises
/// <see cref="DeviceInvoked"/>. The rows come from a <see cref="DeviceTreeModel"/>.
/// </summary>
public sealed class DeviceTree : UserControl
{
    public static readonly StyledProperty<DeviceTreeModel?> ModelProperty =
        AvaloniaProperty.Register<DeviceTree, DeviceTreeModel?>(nameof(Model));

    private readonly ListBox _list;

    public DeviceTree()
    {
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            DataTemplates =
            {
                new FuncDataTemplate<DeviceTreeGroup>((_, _) => GroupRow(), supportsRecycling: true),
                new FuncDataTemplate<DeviceTreeDevice>((_, _) => DeviceRow(), supportsRecycling: true),
            },
        };
        _list.Classes.Add("deviceTree");
        _list.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.KeyDown += OnKeyDown;
        Content = _list;
    }

    /// <summary>The rows to show.</summary>
    public DeviceTreeModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The selected row (the page marks the active device with it).</summary>
    public DeviceTreeRow? SelectedRow
    {
        get => _list.SelectedItem as DeviceTreeRow;
        set => _list.SelectedItem = value;
    }

    /// <summary>A device row was clicked or Enter was pressed on it.</summary>
    public event EventHandler<DeviceTreeDevice>? DeviceInvoked;

    /// <summary>The rows were rebuilt (a device changed, the search): the page selects its row again.</summary>
    public event EventHandler? RowsChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            if (change.OldValue is DeviceTreeModel old)
            {
                old.Rows.CollectionChanged -= OnRowsChanged;
            }

            var model = change.NewValue as DeviceTreeModel;
            _list.ItemsSource = model?.Rows;
            if (model is not null)
            {
                model.Rows.CollectionChanged += OnRowsChanged;
            }
        }
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RowsChanged?.Invoke(this, EventArgs.Empty);

    private void OnTapped(object? sender, TappedEventArgs e) => Invoke((e.Source as Control)?.DataContext as DeviceTreeRow);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            Invoke(_list.SelectedItem as DeviceTreeRow);
            e.Handled = true;
        }
    }

    private void Invoke(DeviceTreeRow? row)
    {
        switch (row)
        {
            case DeviceTreeGroup group:
                var selected = SelectedRow;
                Model?.Toggle(group);
                SelectedRow = selected is DeviceTreeDevice d && Model?.Rows.Contains(d) == true ? d : null;
                RowsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case DeviceTreeDevice device:
                DeviceInvoked?.Invoke(this, device);
                break;
        }
    }

    private static Grid GroupRow()
    {
        var down = Chevron("Icon.chevronDown");
        down.Bind(IsVisibleProperty, new Binding(nameof(DeviceTreeGroup.IsExpanded)));
        var right = Chevron("Icon.chevronRight");
        right.Bind(IsVisibleProperty, new Binding(nameof(DeviceTreeGroup.IsExpanded)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.Classes.Add("treeGroup");
        name.Bind(TextBlock.TextProperty, new Binding(nameof(DeviceTreeGroup.Name)));
        Grid.SetColumn(name, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Children = { down, right, name } };
    }

    private static OadmIcon Chevron(string key)
    {
        var icon = new OadmIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }

    private static StackPanel DeviceRow()
    {
        var icon = new DeviceIcon { ShowStatusToolTip = true, Width = 16, Height = 16 };
        icon.Bind(DeviceIcon.CategoryProperty, new Binding(nameof(DeviceTreeDevice.Category)));
        icon.Bind(DeviceIcon.StatusProperty, new Binding(nameof(DeviceTreeDevice.Status)));
        var address = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        address.Bind(TextBlock.TextProperty, new Binding(nameof(DeviceTreeDevice.Address)));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, address } };
        row.Classes.Add("deviceTreeDevice");
        return row;
    }
}
