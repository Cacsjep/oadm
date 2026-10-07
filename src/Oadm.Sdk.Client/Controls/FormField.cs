using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.VisualTree;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one labeled form row, used by every form of the host and the plugins: the label on the left (kept level
/// with the input, also when a message appears below it), the input as content, and below the input either its
/// validation error (red, small; the input's own DataValidationErrors from <c>INotifyDataErrorInfo</c>, or
/// <see cref="Error"/> for content without data validation such as a file row) or, when there is no error, the
/// optional <see cref="Hint"/>. Nothing below the input takes space when there is no message.
/// Column widths come from the theme (<c>Oadm.FormLabelWidth</c>, <c>Oadm.FormInputWidth</c>); class
/// <c>wide</c> lets the input fill the row, class <c>inline</c> sizes the label to its text (fields in one
/// line, e.g. IP range From / To). An empty label keeps the label column, so a check box lines up with the inputs.
/// </summary>
public sealed class FormField : Grid
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> ErrorProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(Error));

    public static readonly StyledProperty<Control?> InputProperty =
        AvaloniaProperty.Register<FormField, Control?>(nameof(Input));

    /// <summary>Width of the label column; NaN sizes it to the label text.</summary>
    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<FormField, double>(nameof(LabelWidth), 180);

    /// <summary>Width of the input column; NaN fills the rest of the row.</summary>
    public static readonly StyledProperty<double> InputWidthProperty =
        AvaloniaProperty.Register<FormField, double>(nameof(InputWidth), 280);

    /// <summary>Height the label is centered in: the height of a single-line input.</summary>
    public static readonly StyledProperty<double> LabelHeightProperty =
        AvaloniaProperty.Register<FormField, double>(nameof(LabelHeight), 32);

    public static readonly DirectProperty<FormField, bool> HasErrorProperty =
        AvaloniaProperty.RegisterDirect<FormField, bool>(nameof(HasError), o => o.HasError);

    private readonly Panel _labelHost = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _label = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<Control> _inputErrors = [];
    private bool _hasError;

    static FormField()
    {
        DataValidationErrors.HasErrorsProperty.Changed.AddClassHandler<Control>(OnInputHasErrorsChanged);
    }

    public FormField()
    {
        RowDefinitions = new RowDefinitions("Auto,Auto");
        _label.Classes.Add("fieldLabel");
        _labelHost.Children.Add(_label);
        _message.Classes.Add("fieldMessage");
        SetColumn(_message, 1);
        SetRow(_message, 1);
        Children.Add(_labelHost);
        Children.Add(_message);
        UpdateColumns();
        UpdateLabel();
        UpdateMessage();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Help text below the input (secondary, small), hidden while the field shows an error.</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>
    /// An error for content without its own data validation (file rows, groups of inputs). Inputs bound to an
    /// <c>INotifyDataErrorInfo</c> property show their error themselves; leave this empty for them.
    /// </summary>
    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    /// <summary>The input (XAML content).</summary>
    [Content]
    public Control? Input
    {
        get => GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public double InputWidth
    {
        get => GetValue(InputWidthProperty);
        set => SetValue(InputWidthProperty, value);
    }

    public double LabelHeight
    {
        get => GetValue(LabelHeightProperty);
        set => SetValue(LabelHeightProperty, value);
    }

    /// <summary>The field shows an error (its input's validation error or <see cref="Error"/>).</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetAndRaise(HasErrorProperty, ref _hasError, value);
    }

    /// <summary>The text shown below the input (error or hint), for tests.</summary>
    public string? MessageText => _message.IsVisible ? _message.Text : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InputProperty)
        {
            if (change.OldValue is Control old)
            {
                Children.Remove(old);
                _inputErrors.Clear();
            }

            if (change.NewValue is Control input)
            {
                SetColumn(input, 1);
                SetRow(input, 0);
                Children.Add(input);
                if (DataValidationErrors.GetHasErrors(input))
                {
                    _inputErrors.Add(input);
                }
            }

            UpdateMessage();
        }
        else if (change.Property == LabelWidthProperty || change.Property == InputWidthProperty)
        {
            UpdateColumns();
            UpdateLabel();
        }
        else if (change.Property == LabelProperty || change.Property == LabelHeightProperty)
        {
            UpdateLabel();
        }
        else if (change.Property == HintProperty || change.Property == ErrorProperty)
        {
            UpdateMessage();
        }
    }

    private static void OnInputHasErrorsChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        FormField? field = control.FindLogicalAncestorOfType<FormField>() ?? control.FindAncestorOfType<FormField>();
        if (field is null)
        {
            return;
        }

        if (e.NewValue is true)
        {
            field._inputErrors.Add(control);
        }
        else
        {
            field._inputErrors.Remove(control);
        }

        field.UpdateMessage();
    }

    private void UpdateColumns()
    {
        bool autoLabel = double.IsNaN(LabelWidth);
        var label = autoLabel ? GridLength.Auto : new GridLength(LabelWidth);
        var input = double.IsNaN(InputWidth) ? new GridLength(1, GridUnitType.Star) : new GridLength(InputWidth);
        ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(label), new ColumnDefinition(input) };
        HorizontalAlignment = double.IsNaN(InputWidth) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    /// <summary>A row narrower than label + input (e.g. a half-width card) shrinks the input, never clips it.</summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsNaN(InputWidth) && ColumnDefinitions.Count == 2)
        {
            double width = InputWidth;
            if (!double.IsInfinity(constraint.Width) && !double.IsNaN(LabelWidth))
            {
                width = Math.Max(Math.Min(InputWidth, constraint.Width - LabelWidth), MinInputWidth);
            }

            if (!ColumnDefinitions[1].Width.IsAbsolute || Math.Abs(ColumnDefinitions[1].Width.Value - width) > 0.01)
            {
                ColumnDefinitions[1].Width = new GridLength(width);
            }
        }

        return base.MeasureOverride(constraint);
    }

    private const double MinInputWidth = 80;

    private void UpdateLabel()
    {
        _label.Text = Label;
        _labelHost.Height = LabelHeight;
        bool autoLabel = double.IsNaN(LabelWidth);
        _labelHost.IsVisible = !autoLabel || !string.IsNullOrEmpty(Label);
        _labelHost.Margin = autoLabel && !string.IsNullOrEmpty(Label) ? new Thickness(0, 0, 10, 0) : default;
    }

    private void UpdateMessage()
    {
        bool inputError = _inputErrors.Count > 0;
        bool ownError = !string.IsNullOrEmpty(Error);
        HasError = inputError || ownError;
        PseudoClasses.Set(":error", HasError);
        _message.Classes.Set("error", ownError);
        _message.Classes.Set("hint", !ownError);
        if (ownError)
        {
            _message.Text = Error;
            _message.IsVisible = true;
        }
        else
        {
            _message.Text = Hint;
            _message.IsVisible = !inputError && !string.IsNullOrEmpty(Hint);
        }
    }
}
