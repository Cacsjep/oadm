using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Sdk.Client.Controls;

/// <summary>A row of a <see cref="NodePicker"/>: a group heading (<see cref="NodePickerGroup"/>) or a node kind (<see cref="NodePickerItem"/>).</summary>
public abstract record NodePickerEntry;

/// <summary>A group heading of the node list ("Models", "AXIS OS"); it cannot be chosen.</summary>
/// <param name="Name">The heading.</param>
public record NodePickerGroup(string Name) : NodePickerEntry;

/// <summary>A node kind in the node list: its coloured icon, its name and a one-line description below it.</summary>
/// <param name="Label">The kind's name ("Set IP").</param>
/// <param name="Description">One short line of what the node does.</param>
/// <param name="IconKey">The theme icon without <c>Icon.</c> ("network").</param>
/// <param name="BrushKey">The theme brush of the icon, the kind's colour on the canvas ("Oadm.Tag.TealBrush").</param>
/// <param name="Group">The group heading it is listed under; null: no heading.</param>
public record NodePickerItem(string Label, string Description, string IconKey, string BrushKey, string? Group) : NodePickerEntry;

/// <summary>
/// What a <see cref="NodePicker"/> shows (its data context): open or closed, where on the canvas, the search and the rows.
/// <see cref="Show"/> opens it with the kinds to offer, grouped in their order; a search narrows them by name. The picker
/// calls <see cref="Choose"/> for a click or Enter, the canvas's view model adds the node in <see cref="Chosen"/>.
/// Vision Center's pipeline editor and Provisioning's profile editor.
/// </summary>
public partial class NodePickerModel : ObservableObject
{
    private IReadOnlyList<NodePickerItem> _items = [];

    /// <summary>The list shows.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The left edge of the list on the canvas (pixels of the canvas control).</summary>
    [ObservableProperty]
    public partial double Left { get; private set; }

    /// <summary>The top edge of the list on the canvas (pixels of the canvas control).</summary>
    [ObservableProperty]
    public partial double Top { get; private set; }

    /// <summary>The search; the list shows the kinds whose name contains it.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The rows the list shows now: each group's heading, then its kinds.</summary>
    public RangeObservableCollection<NodePickerEntry> Entries { get; } = [];

    /// <summary>No kind matches the search.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The kinds the list shows now, in order.</summary>
    public IReadOnlyList<NodePickerItem> ShownItems => [.. Entries.OfType<NodePickerItem>()];

    /// <summary>A kind was chosen (a click or Enter in the search); the list is closed before.</summary>
    public event EventHandler<NodePickerItem>? Chosen;

    /// <summary>
    /// Opens the list at (<paramref name="left"/>, <paramref name="top"/>) with <paramref name="items"/>, grouped by their
    /// <see cref="NodePickerItem.Group"/> in the order they come; the search starts empty.
    /// </summary>
    public void Show(IEnumerable<NodePickerItem> items, double left, double top)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
        Left = left;
        Top = top;
        SearchText = string.Empty;
        Fill();
        IsOpen = true;
    }

    /// <summary>Closes the list.</summary>
    public virtual void Close() => IsOpen = false;

    /// <summary>Closes the list and raises <see cref="Chosen"/> with <paramref name="item"/>.</summary>
    public void Choose(NodePickerItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var handler = Chosen;
        if (handler is null)
        {
            Close();
            return;
        }

        handler(this, item);
        if (IsOpen)
        {
            Close();
        }
    }

    partial void OnSearchTextChanged(string value) => Fill();

    private void Fill()
    {
        var query = SearchText.Trim();
        var rows = new List<NodePickerEntry>();
        foreach (var group in _items.Where(i => query.Length == 0 || i.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).GroupBy(i => i.Group ?? string.Empty))
        {
            if (group.Key.Length > 0)
            {
                rows.Add(new NodePickerGroup(group.Key));
            }

            rows.AddRange(group);
        }

        Entries.ReplaceAll(rows);
        IsEmpty = rows.Count == 0;
    }
}

/// <summary>
/// The node list of a node canvas (Vision Center's pipelines, Provisioning's profiles): a popup on the canvas with a search
/// at the top and the kinds below, grouped, each with its coloured icon, its name and its line. Its data context is a
/// <see cref="NodePickerModel"/>; put it into a <see cref="Canvas"/> over the canvas control, it places itself at the
/// model's <see cref="NodePickerModel.Left"/> and <see cref="NodePickerModel.Top"/> and shows while it is open. The search
/// takes the keys when it opens: Enter adds the first kind shown, Esc closes it; a click on a kind adds it. Afterwards the
/// keys go back to <see cref="FocusAfter"/> (the canvas).
/// </summary>
public sealed class NodePicker : Border
{
    /// <summary>The placeholder of the search.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<NodePicker, string?>(nameof(Placeholder), "Search nodes");

    /// <summary>The line shown when no kind matches the search.</summary>
    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<NodePicker, string?>(nameof(EmptyText), "No node matches.");

    /// <summary>What takes the keys after a kind was chosen or the list was closed with Esc.</summary>
    public static readonly StyledProperty<IInputElement?> FocusAfterProperty =
        AvaloniaProperty.Register<NodePicker, IInputElement?>(nameof(FocusAfter));

    private readonly SearchBox _search = new() { Classes = { "stretch" } };
    private readonly TextBlock _empty = new() { Classes = { "secondary" } };
    private readonly ListBox _list = new() { Classes = { "eventTree" }, SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 0, 0, 8) };

    /// <summary>The height of the list with its search when it shows as many kinds as it can (the theme's list height plus the search).</summary>
    public const double TallestHeight = 400;

    /// <summary>Styled as a <see cref="Border"/> (the theme's <c>Border.treePopup</c>).</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>Builds the list; bind its data context to a <see cref="NodePickerModel"/>.</summary>
    public NodePicker()
    {
        Classes.Add("treePopup");
        this[!IsVisibleProperty] = new Binding(nameof(NodePickerModel.IsOpen));
        this[!Canvas.LeftProperty] = new Binding(nameof(NodePickerModel.Left));
        this[!Canvas.TopProperty] = new Binding(nameof(NodePickerModel.Top));

        _search[!SearchBox.TextProperty] = new Binding(nameof(NodePickerModel.SearchText)) { Mode = BindingMode.TwoWay };
        _search[!SearchBox.PlaceholderProperty] = new Binding(nameof(Placeholder)) { Source = this };
        _empty[!TextBlock.TextProperty] = new Binding(nameof(EmptyText)) { Source = this };
        _empty[!IsVisibleProperty] = new Binding(nameof(NodePickerModel.IsEmpty));
        _list[!ItemsControl.ItemsSourceProperty] = new Binding(nameof(NodePickerModel.Entries));
        _list.DataTemplates.Add(new FuncDataTemplate<NodePickerGroup>((group, _) =>
            new TextBlock { Classes = { "treeGroup" }, Text = group?.Name, IsHitTestVisible = false }));
        _list.DataTemplates.Add(new FuncDataTemplate<NodePickerItem>((item, _) => Row(item)));
        _list.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        _search.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);

        var panel = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_empty, Dock.Top);
        panel.Children.Add(_search);
        panel.Children.Add(_empty);
        panel.Children.Add(_list);
        Child = panel;
    }

    /// <summary>The placeholder of the search ("Search nodes").</summary>
    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>The line shown when no kind matches the search ("No node matches.").</summary>
    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>What takes the keys after a kind was chosen or Esc closed the list (the canvas control); null: nothing.</summary>
    public IInputElement? FocusAfter
    {
        get => GetValue(FocusAfterProperty);
        set => SetValue(FocusAfterProperty, value);
    }

    /// <summary>The search field (tests).</summary>
    public SearchBox Search => _search;

    /// <summary>The list of groups and kinds (tests).</summary>
    public ListBox List => _list;

    /// <summary>The list opens: its search takes the keys.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
        {
            // The search box inside, once the list is laid out (a control that just turned visible takes no focus yet).
            Dispatcher.UIThread.Post(() => (_search.Content as InputElement ?? _search).Focus(), DispatcherPriority.Input);
        }
    }

    private static Grid Row(NodePickerItem? item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 10,
            Background = Brushes.Transparent,
        };
        if (item is null)
        {
            return grid;
        }

        var icon = new NodeIcon { IconKey = item.IconKey, BrushKey = item.BrushKey, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetRowSpan(icon, 2);
        var label = new TextBlock { Text = item.Label };
        Grid.SetColumn(label, 1);
        var description = new TextBlock { Classes = { "secondary" }, Text = item.Description, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(description, 1);
        Grid.SetRow(description, 1);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(description);
        return grid;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is NodePickerItem item && DataContext is NodePickerModel model)
        {
            model.Choose(item);
            FocusAfter?.Focus();
        }
    }

    /// <summary>Enter in the search adds the first kind shown; Esc closes the list.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NodePickerModel model)
        {
            return;
        }

        if (e.Key == Key.Enter && model.ShownItems is [var first, ..])
        {
            model.Choose(first);
            FocusAfter?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            model.Close();
            FocusAfter?.Focus();
            e.Handled = true;
        }
    }
}

/// <summary>A node kind's icon in its colour, as on the canvas: the node list (<see cref="NodePicker"/>) and a node's heading.</summary>
public class NodeIcon : Control
{
    /// <summary>The theme icon without <c>Icon.</c> ("network").</summary>
    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<NodeIcon, string?>(nameof(IconKey));

    /// <summary>The theme brush of the strokes ("Oadm.Tag.TealBrush").</summary>
    public static readonly StyledProperty<string?> BrushKeyProperty =
        AvaloniaProperty.Register<NodeIcon, string?>(nameof(BrushKey));

    static NodeIcon() => AffectsRender<NodeIcon>(IconKeyProperty, BrushKeyProperty);

    /// <summary>The theme icon without <c>Icon.</c> ("network").</summary>
    public string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    /// <summary>The theme brush of the strokes ("Oadm.Tag.TealBrush"); null: the neutral tag colour.</summary>
    public string? BrushKey
    {
        get => GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    /// <summary>Draws the icon as strokes, centred, scaled to the smaller side.</summary>
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IconKey is not { } key)
        {
            return;
        }

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var brush = new CanvasTheme(this).Brush(BrushKey ?? "Oadm.Tag.NeutralBrush");
        NodeCanvasPainter.DrawIcon(context, this, key, brush, new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size));
    }
}
