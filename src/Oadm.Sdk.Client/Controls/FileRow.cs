using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// A chosen local file: file icon, file name, secondary details (size, what it is for), an error
/// line when the file was rejected, and the "Choose file..." button on the right. Used by every
/// dialog that uploads a file (firmware, ACAP packages, ...).
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
        AvaloniaProperty.Register<FileRow, string?>(nameof(ButtonText), "Choose file...");

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<FileRow, ICommand?>(nameof(Command));

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly IconLabel _buttonLabel = new();
    private readonly Button _button = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };

    public FileRow()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        _details.Classes.Add("secondary");
        _error.Classes.Add("error");
        _button.Classes.Add("secondary");
        _button.Content = _buttonLabel;

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_name);
        text.Children.Add(_details);
        text.Children.Add(_error);

        SetColumn(text, 1);
        SetColumn(_button, 2);
        Children.Add(_icon);
        Children.Add(text);
        Children.Add(_button);

        _icon.Bind(OadmIcon.DataProperty, this.GetResourceObservable("Icon.file", v => v as Geometry));
        _buttonLabel.Bind(IconLabel.IconProperty, this.GetResourceObservable("Icon.folder", v => v as Geometry));
        Update();
    }

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

    public string? ButtonText
    {
        get => GetValue(ButtonTextProperty);
        set => SetValue(ButtonTextProperty, value);
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
        else if (change.Property == FileNameProperty || change.Property == DetailsProperty
            || change.Property == ErrorProperty || change.Property == ButtonTextProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _name.Text = FileName;
        _details.Text = Details;
        _details.IsVisible = !string.IsNullOrEmpty(Details);
        _error.Text = Error;
        _error.IsVisible = !string.IsNullOrEmpty(Error);
        _buttonLabel.Text = ButtonText;
    }
}
