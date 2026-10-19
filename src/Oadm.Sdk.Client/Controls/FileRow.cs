using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// A chosen local file: file icon, file name, secondary details (size, what it is for), an error
/// line when the file was rejected, and the "Choose file" button on the right. Used by every
/// dialog that uploads a file (firmware, ACAP packages, ...). With <see cref="IsSingleButton"/> it is one wide button that
/// shows the chosen file's name (short forms such as a compliance check).
/// </summary>
public sealed class FileRow : Grid
{
    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<FileRow, string?>(nameof(FileName));

    public static readonly StyledProperty<string?> DetailsProperty =
        AvaloniaProperty.Register<FileRow, string?>(nameof(Details));

    public static readonly StyledProperty<string?> ErrorProperty =
        AvaloniaProperty.Register<FileRow, string?>(nameof(Error));

    public static readonly StyledProperty<string?> ButtonTextProperty =
        AvaloniaProperty.Register<FileRow, string?>(nameof(ButtonText), "Choose file");

    public static readonly StyledProperty<bool> IsSingleButtonProperty =
        AvaloniaProperty.Register<FileRow, bool>(nameof(IsSingleButton));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<FileRow, ICommand?>(nameof(Command));

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly IconLabel _buttonLabel = new();
    private readonly Button _button = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly StackPanel _text = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

    public FileRow()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        RowDefinitions = new RowDefinitions("Auto,Auto");
        _details.Classes.Add("secondary");
        _error.Classes.Add("error");
        _button.Classes.Add("secondary");
        _button.Content = _buttonLabel;

        _text.Children.Add(_name);
        _text.Children.Add(_details);
        _text.Children.Add(_error);

        SetColumn(_text, 1);
        SetColumn(_button, 2);
        Children.Add(_icon);
        Children.Add(_text);
        Children.Add(_button);

        _icon.Bind(OadmIcon.DataProperty, this.GetResourceObservable("Icon.file", v => v as Geometry));
        Update();
    }

    /// <summary>The chosen file; empty shows that none is chosen.</summary>
    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    /// <summary>Secondary line, e.g. "87.0 MB · for P3265-V, AXIS OS 12.11.77".</summary>
    public string? Details
    {
        get => GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    /// <summary>Why the file cannot be used; shown in the error color.</summary>
    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    /// <summary>The text of the button ("Choose file").</summary>
    public string? ButtonText
    {
        get => GetValue(ButtonTextProperty);
        set => SetValue(ButtonTextProperty, value);
    }

    /// <summary>
    /// One button as wide as the row, on the left: it shows the chosen file's name (else <see cref="ButtonText"/>) and opens
    /// the picker; no file line and no details, the error below the button. Default false (icon, name, details, button
    /// right).
    /// </summary>
    public bool IsSingleButton
    {
        get => GetValue(IsSingleButtonProperty);
        set => SetValue(IsSingleButtonProperty, value);
    }

    /// <summary>Opens the file picker.</summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommandProperty)
        {
            _button.Command = Command;
        }
        else if (change.Property == FileNameProperty || change.Property == DetailsProperty || change.Property == ErrorProperty
            || change.Property == ButtonTextProperty || change.Property == IsSingleButtonProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        var single = IsSingleButton;
        var hasFile = !string.IsNullOrEmpty(FileName);
        _name.Text = FileName;
        _name.IsVisible = !single;
        _icon.IsVisible = !single;
        _details.Text = Details;
        _details.IsVisible = !single && !string.IsNullOrEmpty(Details);
        _error.Text = Error;
        _error.IsVisible = !string.IsNullOrEmpty(Error);
        _buttonLabel.Text = single && hasFile ? FileName : ButtonText;
        _buttonLabel.Bind(IconLabel.IconProperty, this.GetResourceObservable(single && hasFile ? "Icon.file" : "Icon.folder", v => v as Geometry));

        // Default: icon | name, details, error | button. Single: the button over the whole row, the error below it.
        SetColumn(_button, single ? 0 : 2);
        SetColumnSpan(_button, single ? 3 : 1);
        _button.HorizontalAlignment = single ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        _button.HorizontalContentAlignment = single ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _button.Margin = single ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        SetRow(_text, single ? 1 : 0);
        SetColumn(_text, single ? 0 : 1);
        SetColumnSpan(_text, single ? 3 : 1);
        _text.Margin = single && _error.IsVisible ? new Thickness(0, 4, 0, 0) : new Thickness(0);
    }
}
