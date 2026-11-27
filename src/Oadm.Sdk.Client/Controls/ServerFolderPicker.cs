using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The field of a folder on the server's computer (SDK 0.17.0; "Folder on the server" of a plugin's settings): a text box
/// with the path and the secondary "Choose folder" that opens <see cref="ServerFolderWindow"/> on <see cref="Folders"/>
/// (<see cref="ICorePluginClientContext.ServerFolders"/>; never the local folder dialog, the client may run elsewhere).
/// Bind <see cref="Path"/> two-way inside a <c>ui:FormField</c> (class <c>wide</c>): the view model's error of the bound
/// property shows directly below the text box like on every input, leaving the field shows it (<c>IFormFieldVisits</c>).
/// Without such an error the field shows the server's check of the path (<see cref="Problem"/>, read after a choice and
/// when the user leaves a typed path), so a plugin can refuse a bad folder; its server part checks again on save.
/// </summary>
public sealed class ServerFolderPicker : Grid
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<ServerFolderPicker, string?>(nameof(Path), defaultBindingMode: BindingMode.TwoWay, enableDataValidation: true);

    public static readonly StyledProperty<IServerFolders?> FoldersProperty =
        AvaloniaProperty.Register<ServerFolderPicker, IServerFolders?>(nameof(Folders));

    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<ServerFolderPicker, string?>(nameof(Heading), ServerFolderWindow.DefaultHeading);

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<ServerFolderPicker, string?>(nameof(Placeholder), "Full path on the server");

    public static readonly DirectProperty<ServerFolderPicker, string?> ProblemProperty =
        AvaloniaProperty.RegisterDirect<ServerFolderPicker, string?>(nameof(Problem), o => o.Problem, (o, v) => o.Problem = v);

    /// <summary>The text of the button.</summary>
    public const string ButtonText = "Choose folder";

    private readonly IconLabel _buttonLabel = new() { Text = ButtonText };
    private Exception? _bindingError;
    private string? _problem;
    private string? _checked;
    private int _checkVersion;
    private bool _syncing;

    public ServerFolderPicker()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 8;
        TextBox = new TextBox();
        TextBox.PlaceholderText = Placeholder;
        Button = new Button { Content = _buttonLabel, VerticalAlignment = VerticalAlignment.Top };
        Button.Classes.Add("secondary");
        Button.Click += async (_, _) => await ChooseAsync().ConfigureAwait(true);
        SetColumn(Button, 1);
        Children.Add(TextBox);
        Children.Add(Button);
        _buttonLabel.Icon = ToolbarButton.ResolveIcon("folder");
        TextBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncing)
            {
                _syncing = true;
                try
                {
                    SetCurrentValue(PathProperty, TextBox.Text);
                }
                finally
                {
                    _syncing = false;
                }
            }
        };
        TextBox.LostFocus += OnTextLostFocus;
        UpdateEnabled();
    }

    /// <summary>The chosen folder's full path on the server (two-way).</summary>
    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>The server's folders; without them (an older host, no administrator) Choose folder is off.</summary>
    public IServerFolders? Folders
    {
        get => GetValue(FoldersProperty);
        set => SetValue(FoldersProperty, value);
    }

    /// <summary>The dialog's title (<see cref="ServerFolderWindow.DefaultHeading"/>).</summary>
    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    /// <summary>The text box's placeholder.</summary>
    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>
    /// The server's check of <see cref="Path"/> (<see cref="IServerFolders.CheckAsync"/>), null when it passed or was not
    /// asked yet; shown below the text box while the bound property has no error of its own. Bind it
    /// <c>OneWayToSource</c> to keep Save off while it is set.
    /// </summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (SetAndRaise(ProblemProperty, ref _problem, value))
            {
                ShowError();
            }
        }
    }

    /// <summary>The text box (tests).</summary>
    public TextBox TextBox { get; }

    /// <summary>Choose folder (tests).</summary>
    public Button Button { get; }

    /// <summary>Opens the dialog on the current path and takes the chosen folder.</summary>
    public async Task ChooseAsync()
    {
        if (Folders is not { } folders || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var chosen = await ServerFolderWindow.ShowAsync(owner, folders, Path, Heading).ConfigureAwait(true);
        if (chosen is null)
        {
            return;
        }

        SetCurrentValue(PathProperty, chosen);
        _checked = chosen;
        Problem = null; // the dialog chose only a folder that passed the check
        TextBox.Focus();
    }

    /// <summary>Asks the server about the current path (after a typed path was left); the answer sets <see cref="Problem"/>.</summary>
    public async Task CheckAsync()
    {
        var path = Path?.Trim();
        if (Folders is not { } folders || string.IsNullOrEmpty(path))
        {
            Problem = null;
            _checked = path;
            return;
        }

        if (string.Equals(path, _checked, StringComparison.Ordinal))
        {
            return;
        }

        var version = ++_checkVersion;
        string? problem;
        try
        {
            problem = await folders.CheckAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = ServerErrors.Message(ex);
        }

        if (version == _checkVersion && string.Equals(path, Path?.Trim(), StringComparison.Ordinal))
        {
            _checked = path;
            Problem = problem;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty)
        {
            if (!_syncing)
            {
                _syncing = true;
                try
                {
                    TextBox.Text = Path;
                }
                finally
                {
                    _syncing = false;
                }
            }

            if (!string.Equals(Path?.Trim(), _checked, StringComparison.Ordinal))
            {
                _checkVersion++;
                Problem = null; // a new path is asked about when the user leaves it
            }
        }
        else if (change.Property == FoldersProperty)
        {
            UpdateEnabled();
        }
        else if (change.Property == PlaceholderProperty)
        {
            TextBox.PlaceholderText = Placeholder;
        }
    }

    /// <summary>The bound property's own error (<c>INotifyDataErrorInfo</c>) goes to the text box, like on any input.</summary>
    protected override void UpdateDataValidation(AvaloniaProperty property, BindingValueType state, Exception? error)
    {
        base.UpdateDataValidation(property, state, error);
        if (property == PathProperty)
        {
            _bindingError = error;
            ShowError();
        }
    }

    private void ShowError() =>
        DataValidationErrors.SetError(TextBox, _bindingError ?? (Problem is { } problem ? new DataValidationException(problem) : null));

    private void UpdateEnabled() => Button.IsEnabled = Folders is not null;

    /// <summary>Leaving the text box shows the bound property's error (like FormField does for a plain text box) and checks the path.</summary>
    private async void OnTextLostFocus(object? sender, RoutedEventArgs e)
    {
        var expression = BindingOperations.GetBindingExpressionBase(this, PathProperty);
        if (expression?.GetType().GetProperty("Description")?.GetValue(expression) is string { Length: > 0 } path
            && DataContext is Validation.IFormFieldVisits form)
        {
            form.FieldLeft(path);
        }

        try
        {
            await CheckAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // CheckAsync turns every failure into Problem; nothing else can be shown here.
        }
    }
}
