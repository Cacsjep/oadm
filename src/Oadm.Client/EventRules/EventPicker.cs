using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.EventRules;

/// <summary>
/// The event select of the rule dialog: looks like a select field and shows the chosen event with its folders
/// ("Devices › Selected devices › Device unreachable"); a click opens a panel below it with a search box, the
/// <see cref="EventTree"/> as one virtualized list (a folder opens or closes on click, an event is chosen on click or
/// Enter) and the description of the event under the pointer. Escape or a click outside closes it.
/// </summary>
public sealed class EventPicker : UserControl
{
    public static readonly StyledProperty<EventTree?> TreeProperty =
        AvaloniaProperty.Register<EventPicker, EventTree?>(nameof(Tree));

    public static readonly StyledProperty<EventTypeOption?> SelectedProperty =
        AvaloniaProperty.Register<EventPicker, EventTypeOption?>(nameof(Selected), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<EventPicker, string>(nameof(Placeholder), "Choose an event");

    private static readonly FuncValueConverter<int, Thickness> Indent = new(depth => new Thickness(depth * 18, 0, 0, 0));

    private readonly Button _field;
    private readonly TextBlock _path = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup _popup;
    private readonly SearchBox _search = new() { Placeholder = "Search events" };
    private readonly ListBox _list;
    private readonly TextBlock _empty = new() { Text = "No event matches the search.", IsVisible = false };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };

    public EventPicker()
    {
        _search.Classes.Add("stretch"); // fills the panel
        _path.Classes.Add("treeSelectPath");
        _name.Classes.Add("treeSelectName");
        var chevron = new OadmIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(OadmIcon.DataProperty, chevron.GetResourceObservable("Icon.chevronDown"));
        Grid.SetColumn(_name, 1);
        Grid.SetColumn(chevron, 2);
        _field = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4, Children = { _path, _name, chevron } },
        };
        _field.Classes.Add("treeSelect");
        _field.Click += (_, _) => SetOpen(!_popup!.IsOpen);

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            DataTemplates =
            {
                new FuncDataTemplate<EventTreeFolder>((_, _) => FolderRow(), supportsRecycling: true),
                new FuncDataTemplate<EventTreeEvent>((_, _) => EventRow(), supportsRecycling: true),
            },
        };
        _list.Classes.Add("eventTree");
        _list.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, (_, e) => Describe((e.Source as Control)?.DataContext as EventTreeEvent), RoutingStrategies.Bubble, handledEventsToo: true);
        _list.SelectionChanged += (_, _) => Describe(_list.SelectedItem as EventTreeEvent);
        _search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _search.PropertyChanged += (_, e) =>
        {
            if (e.Property == SearchBox.TextProperty && Tree is { } tree)
            {
                tree.Query = _search.Text ?? string.Empty;
            }
        };
        _empty.Classes.Add("secondary");
        _description.Classes.Add("secondary");
        var description = new Border { Child = _description };
        description.Classes.Add("treePopupFooter");
        var body = new DockPanel { Children = { _search, description, _empty, _list } };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(description, Dock.Bottom);
        DockPanel.SetDock(_empty, Dock.Top);
        var panel = new Border { Child = body };
        panel.Classes.Add("treePopup");
        _popup = new Popup
        {
            Child = panel,
            PlacementTarget = _field,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
        };
        _popup.Opened += (_, _) => OnOpened();
        _popup.Closed += (_, _) => _field.Classes.Set("open", false);
        Content = new Panel { Children = { _field, _popup } };
        UpdateField();
    }

    /// <summary>The events (one tree per select: its folders stay as the user opened them).</summary>
    public EventTree? Tree
    {
        get => GetValue(TreeProperty);
        set => SetValue(TreeProperty, value);
    }

    /// <summary>The chosen event (two way).</summary>
    public EventTypeOption? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>The panel is open (for tests).</summary>
    public bool IsOpen => _popup.IsOpen;

    /// <summary>Opens or closes the panel.</summary>
    public void SetOpen(bool open)
    {
        if (open && Tree is null)
        {
            return;
        }

        _popup.IsOpen = open;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TreeProperty)
        {
            if (change.OldValue is EventTree old)
            {
                old.PropertyChanged -= OnTreeChanged;
            }

            var tree = change.NewValue as EventTree;
            _list.ItemsSource = tree?.Rows;
            if (tree is not null)
            {
                tree.PropertyChanged += OnTreeChanged;
            }
        }
        else if (change.Property == SelectedProperty || change.Property == PlaceholderProperty)
        {
            UpdateField();
        }
    }

    private void OnTreeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EventTree.IsEmpty))
        {
            _empty.IsVisible = Tree?.IsEmpty == true;
        }
    }

    private void UpdateField()
    {
        if (Selected is { } option)
        {
            var folders = EventTree.Folders(option);
            _path.Text = string.Join(EventTree.Separator, folders) + EventTree.Separator;
            _path.IsVisible = true;
            _name.Text = EventTree.TextIn(option, folders[^1]);
            _name.Classes.Set("placeholder", false);
        }
        else
        {
            _path.IsVisible = false;
            _name.Text = Placeholder;
            _name.Classes.Set("placeholder", true);
        }

        ToolTip.SetTip(_field, Selected is { } chosen ? EventTree.PathText(chosen) : null);
    }

    private void OnOpened()
    {
        _field.Classes.Set("open", true);
        if (_popup.Child is Control child)
        {
            child.Width = _field.Bounds.Width;
        }

        _search.Text = string.Empty;
        var row = Tree?.Reveal(Selected);
        _list.SelectedItem = row;
        if (row is not null)
        {
            _list.ScrollIntoView(row);
        }

        Describe(row);
        _search.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private void Describe(EventTreeEvent? row)
    {
        if (row is not null)
        {
            _description.Text = row.Description.Length > 0 ? FirstParagraph(row.Description) : "No description.";
        }
        else if (string.IsNullOrEmpty(_description.Text))
        {
            _description.Text = "Point at an event to read what it means.";
        }
    }

    // The descriptions of device triggers list their values after a blank line; the footer shows the sentence only.
    private static string FirstParagraph(string text)
    {
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private void OnTapped(object? sender, TappedEventArgs e) => Invoke((e.Source as Control)?.DataContext as EventTreeRow);

    private void Invoke(EventTreeRow? row)
    {
        switch (row)
        {
            case EventTreeFolder folder:
                Tree?.Toggle(folder);
                _list.SelectedItem = folder;
                break;
            case EventTreeEvent leaf:
                Selected = leaf.Option;
                SetOpen(false);
                _field.Focus();
                break;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            Invoke(_list.SelectedItem as EventTreeRow);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SetOpen(false);
            _field.Focus();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Tree?.Rows.Count > 0)
        {
            // Into the list: the first event (or row) below the search.
            _list.SelectedItem = Tree.Rows.OfType<EventTreeEvent>().FirstOrDefault() ?? Tree.Rows[0];
            _list.ContainerFromItem(_list.SelectedItem!)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Tree?.Rows.OfType<EventTreeEvent>().ToList() is [var only])
        {
            Invoke(only); // the search found one event: Enter chooses it
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SetOpen(false);
            _field.Focus();
            e.Handled = true;
        }
    }

    private static Grid FolderRow()
    {
        var down = Chevron("Icon.chevronDown");
        down.Bind(IsVisibleProperty, new Binding(nameof(EventTreeFolder.IsOpen)));
        var right = Chevron("Icon.chevronRight");
        right.Bind(IsVisibleProperty, new Binding(nameof(EventTreeFolder.IsOpen)) { Converter = BoolConverters.Not });
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.Classes.Add("eventTreeFolder");
        name.Bind(TextBlock.TextProperty, new Binding(nameof(EventTreeRow.Text)));
        Grid.SetColumn(name, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Children = { down, right, name } };
        row.Bind(MarginProperty, new Binding(nameof(EventTreeRow.Depth)) { Converter = Indent });
        return row;
    }

    private static Grid EventRow()
    {
        var icon = new OadmIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable("Icon.zap"));
        icon.Classes.Add("accent");
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(EventTreeRow.Text)));
        Grid.SetColumn(name, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Children = { icon, name } };
        row.Bind(MarginProperty, new Binding(nameof(EventTreeRow.Depth)) { Converter = Indent });
        return row;
    }

    private static OadmIcon Chevron(string key)
    {
        var icon = new OadmIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }
}
