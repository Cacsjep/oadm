using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The dialog of <see cref="ServerFolderPicker"/>: a folder on the server's computer, never the local folder dialog (the
/// client may run on another computer). Title bar, one card with Up, Drives and New folder, the path bar (Enter opens a
/// typed path; the open folder's check or a refusal below it), the inline New folder name with Create and Cancel, the
/// virtualized list of subfolders (double click or Enter opens) with the loading overlay while it reads, then Cancel and
/// the primary Choose for the open folder. Open it with <see cref="ShowAsync"/>; the view model is
/// <see cref="ServerFolderPickerViewModel"/>.
/// </summary>
public sealed class ServerFolderWindow : Window
{
    /// <summary>The title when the caller gives none.</summary>
    public const string DefaultHeading = "Choose a folder on the server";

    private readonly DialogTitleBar _title = new();

    /// <summary>The window for <paramref name="viewModel"/>; usually opened through <see cref="ShowAsync"/>.</summary>
    public ServerFolderWindow(ServerFolderPickerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = viewModel;
        Width = 680;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        Heading = DefaultHeading;

        var up = Command("Up", "arrowUp", nameof(ServerFolderPickerViewModel.UpCommand));
        var drives = Command("Drives", "hardDrive", nameof(ServerFolderPickerViewModel.DrivesCommand));
        NewFolderButton = Command("New folder", "add", nameof(ServerFolderPickerViewModel.NewFolderCommand));
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        commands.Children.AddRange([up, drives, NewFolderButton]);

        PathBox = new TextBox { PlaceholderText = "Full path on the server" };
        PathBox.Bind(TextBox.TextProperty, new Binding(nameof(ServerFolderPickerViewModel.PathText)) { Mode = BindingMode.TwoWay });
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ViewModel.GoCommand.Execute(null);
            }
        };
        var pathField = new FormField { Label = "Folder", Input = PathBox, Margin = new Thickness(0, 12, 0, 0) };
        pathField.Classes.Add("wide");

        var nameBox = new TextBox { PlaceholderText = "Name of the new folder" };
        nameBox.Bind(TextBox.TextProperty, new Binding(nameof(ServerFolderPickerViewModel.NewFolderName)) { Mode = BindingMode.TwoWay });
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ViewModel.CreateCommand.Execute(null);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ViewModel.CancelNewFolderCommand.Execute(null);
            }
        };
        NameBox = nameBox;
        CreateButton = new Button { Content = "Create", VerticalAlignment = VerticalAlignment.Top };
        CreateButton.Classes.Add("primary");
        CreateButton.Bind(Button.CommandProperty, new Binding(nameof(ServerFolderPickerViewModel.CreateCommand)));
        var cancelCreate = new Button { Content = "Cancel", VerticalAlignment = VerticalAlignment.Top };
        cancelCreate.Classes.Add("secondary");
        cancelCreate.Bind(Button.CommandProperty, new Binding(nameof(ServerFolderPickerViewModel.CancelNewFolderCommand)));
        var nameField = new FormField { Label = "New folder", Input = nameBox };
        nameField.Classes.Add("wide");
        var editor = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetColumn(CreateButton, 1);
        Grid.SetColumn(cancelCreate, 2);
        editor.Children.AddRange([nameField, CreateButton, cancelCreate]);
        editor.Bind(IsVisibleProperty, new Binding(nameof(ServerFolderPickerViewModel.IsCreating)));

        List = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<ServerFolder>((_, _) => FolderRow(), supportsRecycling: true),
        };
        List.Classes.Add("devicePicker");
        List.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ServerFolderPickerViewModel.Folders)));
        List.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(ServerFolderPickerViewModel.SelectedFolder)) { Mode = BindingMode.TwoWay });
        List.DoubleTapped += (_, _) => ViewModel.OpenFolderCommand.Execute(ViewModel.SelectedFolder);
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ViewModel.SelectedFolder is { } folder)
            {
                e.Handled = true;
                ViewModel.OpenFolderCommand.Execute(folder);
            }
        };
        var empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Classes.Add("secondary");
        empty.Bind(TextBlock.TextProperty, new Binding(nameof(ServerFolderPickerViewModel.EmptyText)));
        empty.Bind(IsVisibleProperty, new Binding(nameof(ServerFolderPickerViewModel.IsEmpty)));
        Loading = new LoadingOverlay { Text = ServerFolderPickerViewModel.LoadingText };
        Loading.Bind(IsVisibleProperty, new Binding(nameof(ServerFolderPickerViewModel.IsLoading)));
        var listArea = new Panel { Margin = new Thickness(0, 12, 0, 0), Children = { List, empty, Loading } };
        var more = new TextBlock { Text = ServerFolderPickerViewModel.MoreText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        more.Classes.Add("secondary");
        more.Bind(IsVisibleProperty, new Binding(nameof(ServerFolderPickerViewModel.More)));

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(pathField, 1);
        Grid.SetRow(editor, 2);
        Grid.SetRow(listArea, 3);
        Grid.SetRow(more, 4);
        body.Children.AddRange([commands, pathField, editor, listArea, more]);
        var card = new Border { Child = body, Margin = new Thickness(16, 0, 16, 8) };
        card.Classes.Add("card");

        ChooseButton = new Button { Content = "Choose", IsDefault = false };
        ChooseButton.Classes.Add("primary");
        ChooseButton.Bind(IsEnabledProperty, new Binding(nameof(ServerFolderPickerViewModel.CanChoose)));
        ChooseButton.Bind(ToolTip.TipProperty, new Binding(nameof(ServerFolderPickerViewModel.ChooseTip)));
        ToolTip.SetShowOnDisabled(ChooseButton, true);
        ChooseButton.Click += async (_, _) =>
        {
            if (await ViewModel.ChooseAsync().ConfigureAwait(true))
            {
                Close(ViewModel.ChosenPath);
            }
        };
        var footer = new DialogFooter();
        footer.CancelButton.Click += (_, _) => Close(null);
        footer.Actions.Add(ChooseButton);

        // One accent button: while the New folder editor is open, its Create is the primary one.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerFolderPickerViewModel.IsCreating))
            {
                ChooseButton.Classes.Set("primary", !ViewModel.IsCreating);
                ChooseButton.Classes.Set("secondary", ViewModel.IsCreating);
                if (ViewModel.IsCreating)
                {
                    NameBox.Focus();
                }
            }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(card, 1);
        Grid.SetRow(footer, 2);
        root.Children.AddRange([_title, card, footer]);
        Content = root;
        Opened += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Its view model.</summary>
    public ServerFolderPickerViewModel ViewModel { get; }

    /// <summary>The path bar (tests).</summary>
    public TextBox PathBox { get; }

    /// <summary>The name of a new folder (tests).</summary>
    public TextBox NameBox { get; }

    /// <summary>The folder list (tests).</summary>
    public ListBox List { get; }

    /// <summary>The loading overlay of the list (tests).</summary>
    public LoadingOverlay Loading { get; }

    /// <summary>New folder (tests).</summary>
    public Button NewFolderButton { get; }

    /// <summary>Create of the New folder editor (tests).</summary>
    public Button CreateButton { get; }

    /// <summary>The main button of the footer.</summary>
    public Button ChooseButton { get; }

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
    /// Shows the dialog over <paramref name="owner"/>, opened on <paramref name="startPath"/> (else the drives); returns the
    /// chosen folder's full path on the server, or null when cancelled.
    /// </summary>
    public static async Task<string?> ShowAsync(Window owner, IServerFolders folders, string? startPath = null, string? heading = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(folders);
        var window = new ServerFolderWindow(new ServerFolderPickerViewModel(folders, startPath)) { Heading = heading ?? DefaultHeading };
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }

    private static ToolbarButton Command(string text, string icon, string command)
    {
        var button = new ToolbarButton { Text = text, IconKey = icon };
        button.Bind(Button.CommandProperty, new Binding(command));
        return button;
    }

    private static Grid FolderRow()
    {
        var icon = new OadmIcon { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
        icon.Bind(OadmIcon.DataProperty, new Binding(nameof(ServerFolder.IsRoot)) { Converter = FolderIcon });
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(ServerFolder.Name)));
        name.Bind(ToolTip.TipProperty, new Binding(nameof(ServerFolder.Path)));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinHeight = 32, Background = Brushes.Transparent };
        Grid.SetColumn(name, 1);
        row.Children.AddRange([icon, name]);
        return row;
    }

    private static readonly Avalonia.Data.Converters.FuncValueConverter<bool, Geometry?> FolderIcon =
        new(isRoot => ToolbarButton.ResolveIcon(isRoot ? "hardDrive" : "folder"));
}
