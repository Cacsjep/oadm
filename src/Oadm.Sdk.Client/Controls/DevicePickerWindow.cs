using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The shared device picker of every dialog that needs a set of devices (host and plugins, one window, never a copy):
/// title bar, one card with the count and a search, Select all / Select none for the devices the search shows, and a
/// virtualized list with a check box per device (address, model; a device that cannot be chosen is greyed with the
/// reason), then Cancel / OK. With <see cref="DevicePickerOptions"/> a column on the left offers the device of the event,
/// All devices, or Chosen devices and tags with the tag list (devices of a chosen tag show checked with the tag).
/// The device group editor (<see cref="DevicePickerOptions.Group"/>) shows Sites and Tags on the left, the devices they
/// cover checked and locked with Except / Include per row, and a form of the host above (name, description), with
/// <see cref="Accept"/> deciding whether the main button closes the window.
/// Open it with <see cref="ShowAsync"/>; the view model is <see cref="DevicePickerViewModel"/>.
/// </summary>
public sealed class DevicePickerWindow : Window
{
    private readonly DialogTitleBar _title = new();
    private readonly Control _picker;
    private readonly double _pickerHeight;

    /// <summary>The window for <paramref name="viewModel"/>; usually opened through the static ShowAsync methods.</summary>
    public DevicePickerWindow(DevicePickerViewModel viewModel)
        : this(viewModel, null)
    {
    }

    /// <summary>
    /// The window for <paramref name="viewModel"/> with <paramref name="form"/> (the host's fields, e.g. a device group's
    /// name and description) on top of the card.
    /// </summary>
    public DevicePickerWindow(DevicePickerViewModel viewModel, Control? form)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = viewModel;
        Width = viewModel.HasChoices ? 1000 : 760;
        Height = form is null ? 640 : 720;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        Heading = "Choose devices";

        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        summary.Classes.Add("secondary");
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(DevicePickerViewModel.Summary)));
        var all = Action("Select all", "check", nameof(DevicePickerViewModel.SelectAllCommand), "Choose every device the search shows");
        var none = Action("Select none", "close", nameof(DevicePickerViewModel.SelectNoneCommand), "Unchoose every device the search shows");
        all.IsVisible = none.IsVisible = !viewModel.Options.SingleDevice;
        var search = new SearchBox { Placeholder = "Search devices", VerticalAlignment = VerticalAlignment.Center };
        search.Bind(SearchBox.TextProperty, new Binding(nameof(DevicePickerViewModel.SearchText)) { Mode = BindingMode.TwoWay });
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(all, 0);
        Grid.SetColumn(none, 1);
        Grid.SetColumn(search, 3);
        bar.Children.AddRange([all, none, search]);
        summary.Margin = new Thickness(2, 12, 0, 0);

        var header = Row(new TextBlock(), Header("Address"), Header("Model"), Header(string.Empty), viewModel.IsGroup ? new TextBlock() : null);
        header.Margin = new Thickness(0, 12, 0, 4);

        var list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<DevicePickItem>((_, _) => ItemRow(viewModel.IsGroup), supportsRecycling: true),
        };
        list.Classes.Add("devicePicker");
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DevicePickerViewModel.Visible)));
        var empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Classes.Add("secondary");
        empty.Bind(TextBlock.TextProperty, new Binding(nameof(DevicePickerViewModel.EmptyText)));
        empty.Bind(IsVisibleProperty, new Binding(nameof(DevicePickerViewModel.IsEmpty)));
        var listArea = new Panel { Children = { list, empty } };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        error.Classes.Add("error");
        error.Bind(TextBlock.TextProperty, new Binding(nameof(DevicePickerViewModel.ErrorText)));
        error.Bind(IsVisibleProperty, new Binding(nameof(DevicePickerViewModel.HasError)));

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(summary, 1);
        Grid.SetRow(header, 2);
        Grid.SetRow(listArea, 3);
        Grid.SetRow(error, 4);
        body.Children.AddRange([bar, summary, header, listArea, error]);
        body.Bind(IsEnabledProperty, new Binding(nameof(DevicePickerViewModel.IsChosen)));
        Control content = body;
        if (viewModel.HasChoices)
        {
            var choices = viewModel.IsGroup ? GroupChoices() : Choices(viewModel);
            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), ColumnSpacing = 24 };
            Grid.SetColumn(body, 1);
            split.Children.AddRange([choices, body]);
            content = split;
        }

        _picker = content;
        _pickerHeight = Height;

        if (form is not null)
        {
            var stack = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 16 };
            Grid.SetRow(content, 1);
            stack.Children.AddRange([form, content]);
            content = stack;
        }

        var card = new Border { Child = content, Margin = new Thickness(16, 0, 16, 8) };
        card.Classes.Add("card");

        AcceptButton = new Button { Content = "OK", IsDefault = true };
        AcceptButton.Classes.Add("primary");
        var footer = new DialogFooter();
        AcceptButton.Click += async (_, _) =>
        {
            if (Accept is null)
            {
                Close(true);
                return;
            }

            if (IsAccepting)
            {
                return;
            }

            // While the host saves, Cancel, Escape and the X wait: a save that lands after the window closed would look
            // cancelled.
            IsAccepting = true;
            footer.CancelButton.IsEnabled = false;
            bool accepted;
            try
            {
                accepted = await Accept().ConfigureAwait(true);
            }
            finally
            {
                IsAccepting = false;
                footer.CancelButton.IsEnabled = true;
            }

            if (accepted)
            {
                Close(true);
            }
        };
        footer.CancelButton.Click += (_, _) => Close(false);
        footer.Actions.Add(AcceptButton);
        Closing += (_, e) => e.Cancel |= IsAccepting;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(card, 1);
        Grid.SetRow(footer, 2);
        root.Children.AddRange([_title, card, footer]);
        Content = root;
    }

    /// <summary>Its view model.</summary>
    public DevicePickerViewModel ViewModel { get; }

    /// <summary>
    /// The device part shows (default). False: only the host's form above it (e.g. a policy kind without devices); the window
    /// is then as high as the form. Changes while the window is open.
    /// </summary>
    public bool IsPickerVisible
    {
        get => _picker.IsVisible;
        set
        {
            if (_picker.IsVisible == value)
            {
                return;
            }

            _picker.IsVisible = value;
            SizeToContent = value ? SizeToContent.Manual : SizeToContent.Height;
            if (value)
            {
                Height = _pickerHeight;
            }
        }
    }

    /// <summary><see cref="Accept"/> runs (the host saves): the window cannot be cancelled or closed meanwhile.</summary>
    public bool IsAccepting { get; private set; }

    /// <summary>The main button of the footer ("OK"): the host may change its text and bind it (e.g. "Save", disabled with a reason).</summary>
    public Button AcceptButton { get; }

    /// <summary>
    /// Runs when the main button is clicked: the window closes with true only when it returns true (e.g. the host saved
    /// and the server accepted). Null: the button closes the window at once.
    /// </summary>
    public Func<Task<bool>>? Accept { get; set; }

    /// <summary>The window title (title bar and task switcher).</summary>
    public string? Heading
    {
        get => _title.Text;
        set
        {
            _title.Text = value;
            Title = value;
        }
    }

    /// <summary>
    /// Shows the picker with all devices, tags and (with <see cref="DevicePickerOptions.EventDeviceText"/>) the device of
    /// the event; returns the choice, or null when cancelled.
    /// </summary>
    public static async Task<DeviceChoice?> ShowChoiceAsync(
        Window owner,
        IEnumerable<IDeviceInfo> devices,
        DeviceChoice initial,
        DevicePickerOptions options,
        string heading = "Choose devices")
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new DevicePickerWindow(new DevicePickerViewModel(devices, initial, options)) { Heading = heading };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true) ? window.ViewModel.Choice : null;
    }

    /// <summary>
    /// Shows the picker for exactly one device (<see cref="DevicePickerOptions.SingleDevice"/>) over <paramref name="owner"/>;
    /// returns its id, or null when cancelled or none was chosen.
    /// </summary>
    public static async Task<Guid?> ShowOneAsync(
        Window owner,
        IEnumerable<IDeviceInfo> devices,
        Guid? selected = null,
        Func<IDeviceInfo, string?>? unavailableReason = null,
        string heading = "Choose a device")
    {
        ArgumentNullException.ThrowIfNull(owner);
        var model = new DevicePickerViewModel(devices, DeviceChoice.Devices(selected is { } id ? [id] : []), new DevicePickerOptions { SingleDevice = true }, unavailableReason);
        var window = new DevicePickerWindow(model) { Heading = heading };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true) && model.SelectedIds.Count == 1 ? model.SelectedIds[0] : null;
    }

    /// <summary>
    /// Shows the picker over <paramref name="owner"/>; returns the chosen devices (in list order), or null when cancelled.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>?> ShowAsync(
        Window owner,
        IEnumerable<IDeviceInfo> devices,
        IEnumerable<Guid>? selected = null,
        Func<IDeviceInfo, string?>? unavailableReason = null,
        string heading = "Choose devices")
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new DevicePickerWindow(new DevicePickerViewModel(devices, selected, unavailableReason)) { Heading = heading };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true) ? window.ViewModel.SelectedIds : null;
    }

    /// <summary>The left column: the device of the event, All devices, Chosen devices and tags, and the tag list.</summary>
    private static Control Choices(DevicePickerViewModel viewModel)
    {
        var group = "choice-" + Guid.NewGuid().ToString("N");
        var panel = new StackPanel { Spacing = 8 };
        if (viewModel.Options.EventDeviceText is { } eventText)
        {
            panel.Children.Add(Radio(eventText, nameof(DevicePickerViewModel.IsEventDevice), group));
        }

        if (viewModel.Options.AllowAll)
        {
            panel.Children.Add(Radio("All devices", nameof(DevicePickerViewModel.IsAll), group));
        }

        panel.Children.Add(Radio(viewModel.Options.AllowTags ? "Chosen devices and tags" : "Chosen devices", nameof(DevicePickerViewModel.IsChosen), group));
        if (!viewModel.Options.AllowTags)
        {
            return panel;
        }

        var title = new TextBlock { Text = "Tags", Margin = new Thickness(0, 12, 0, 0) };
        title.Classes.Add("sectionTitle");
        var tags = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<TagPickItem>((_, _) => TagRow(), supportsRecycling: true),
        };
        tags.Classes.Add("devicePicker");
        tags.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DevicePickerViewModel.Tags)));
        tags.Bind(IsVisibleProperty, new Binding(nameof(DevicePickerViewModel.HasTags)));
        var none = new TextBlock { Text = "No tags yet. Tag devices on the Devices page.", TextWrapping = TextWrapping.Wrap };
        none.Classes.Add("secondary");
        none.Bind(IsVisibleProperty, new Binding(nameof(DevicePickerViewModel.ShowNoTags)));
        var tagArea = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var tagBody = new Panel { Children = { tags, none } };
        Grid.SetRow(tagBody, 1);
        tagArea.Children.AddRange([title, tagBody]);
        tagArea.Bind(IsEnabledProperty, new Binding(nameof(DevicePickerViewModel.IsChosen)));

        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(tagArea, 1);
        column.Children.AddRange([panel, tagArea]);
        return column;
    }

    /// <summary>The group editor's left column: Sites (with "This server") and Tags, each with its device count.</summary>
    private static Grid GroupChoices()
    {
        var sitesTitle = new TextBlock { Text = "Sites" };
        sitesTitle.Classes.Add("sectionTitle");
        var sites = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            MaxHeight = 200,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<SitePickItem>((_, _) => CountRow(nameof(SitePickItem.IsChecked), nameof(SitePickItem.Name), nameof(SitePickItem.CountText), "Devices at this site"), supportsRecycling: true),
        };
        sites.Classes.Add("devicePicker");
        sites.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DevicePickerViewModel.Sites)));

        var tagsTitle = new TextBlock { Text = "Tags", Margin = new Thickness(0, 12, 0, 0) };
        tagsTitle.Classes.Add("sectionTitle");
        var tags = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<TagPickItem>((_, _) => TagRow(), supportsRecycling: true),
        };
        tags.Classes.Add("devicePicker");
        tags.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DevicePickerViewModel.Tags)));
        tags.Bind(IsVisibleProperty, new Binding(nameof(DevicePickerViewModel.HasTags)));
        var none = new TextBlock { Text = "No tags yet. Tag devices on the Devices page.", TextWrapping = TextWrapping.Wrap };
        none.Classes.Add("secondary");
        none.Bind(IsVisibleProperty, new Binding(nameof(DevicePickerViewModel.ShowNoTags)));
        var tagBody = new Panel { Children = { tags, none } };

        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        Grid.SetRow(sites, 1);
        Grid.SetRow(tagsTitle, 2);
        Grid.SetRow(tagBody, 3);
        column.Children.AddRange([sitesTitle, sites, tagsTitle, tagBody]);
        return column;
    }

    private static RadioButton Radio(string text, string property, string group)
    {
        var radio = new RadioButton { Content = text, GroupName = group };
        radio.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return radio;
    }

    private static Grid TagRow() => CountRow(nameof(TagPickItem.IsChecked), nameof(TagPickItem.Name), nameof(TagPickItem.CountText), "Devices with this tag");

    /// <summary>A check box with a name and a device count on the right (tags, the group editor's sites).</summary>
    private static Grid CountRow(string isChecked, string name, string countText, string countTip)
    {
        var check = new CheckBox();
        check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(isChecked) { Mode = BindingMode.TwoWay });
        check.Bind(ContentControl.ContentProperty, new Binding(name));
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        count.Classes.Add("secondary");
        count.Bind(TextBlock.TextProperty, new Binding(countText));
        ToolTip.SetTip(count, countTip);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 32 };
        Grid.SetColumn(count, 1);
        row.Children.AddRange([check, count]);
        return row;
    }

    private static Button Action(string text, string icon, string command, string tip)
    {
        var button = new Button { Content = new IconLabel { Text = text, Icon = ToolbarButton.ResolveIcon(icon) } };
        button.Classes.Add("secondary");
        button.Bind(Button.CommandProperty, new Binding(command));
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static TextBlock Header(string text)
    {
        var header = new TextBlock { Text = text };
        header.Classes.Add("secondary");
        return header;
    }

    /// <summary>The columns of the list: check box, address, model, reason (the group editor: Except / Include last).</summary>
    private static Grid Row(Control check, Control address, Control model, Control rest, Control? action = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(action is null ? "40,160,180,*" : "40,160,180,*,Auto"), MinHeight = 32 };
        Grid.SetColumn(address, 1);
        Grid.SetColumn(model, 2);
        Grid.SetColumn(rest, 3);
        Control[] cells = action is null ? [check, address, model, rest] : [check, address, model, rest, action];
        if (action is not null)
        {
            Grid.SetColumn(action, 4);
        }

        foreach (var cell in cells)
        {
            cell.VerticalAlignment = VerticalAlignment.Center;
        }

        row.Children.AddRange(cells);
        return row;
    }

    /// <summary>The group editor's row action: Except for a device a site or tag covers, Include for an excepted one.</summary>
    private static Panel ExceptActions()
    {
        static Button Link(string text, string command, string visible, string tip)
        {
            var link = new Button { Content = text, Margin = new Thickness(8, 0) };
            link.Classes.Add("link");
            link.Bind(Button.CommandProperty, new Binding(command));
            link.Bind(IsVisibleProperty, new Binding(visible));
            ToolTip.SetTip(link, tip);
            return link;
        }

        return new Panel
        {
            Children =
            {
                Link("Except", nameof(DevicePickItem.ExceptCommand), nameof(DevicePickItem.CanExcept), "Leave this device out of the group"),
                Link("Include", nameof(DevicePickItem.IncludeCommand), nameof(DevicePickItem.CanInclude), "Take this device back into the group"),
            },
        };
    }

    private static Grid ItemRow(bool group)
    {
        var check = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center };
        check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(DevicePickItem.Chosen)) { Mode = BindingMode.TwoWay });
        check.Bind(IsEnabledProperty, new Binding(nameof(DevicePickItem.CanToggle)));
        var address = new TextBlock();
        address.Bind(TextBlock.TextProperty, new Binding(nameof(DevicePickItem.Address)));
        var model = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        model.Bind(TextBlock.TextProperty, new Binding(nameof(DevicePickItem.Model)));
        var reason = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        reason.Classes.Add("secondary");
        reason.Bind(TextBlock.TextProperty, new Binding(nameof(DevicePickItem.Note)));
        reason.Bind(ToolTip.TipProperty, new Binding(nameof(DevicePickItem.Note)));
        var row = Row(check, address, model, reason, group ? ExceptActions() : null);
        row.Bind(OpacityProperty, new Binding(nameof(DevicePickItem.IsAvailable)) { Converter = AvailableOpacity });
        return row;
    }

    private static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> AvailableOpacity = new(available => available ? 1.0 : 0.5);
}
