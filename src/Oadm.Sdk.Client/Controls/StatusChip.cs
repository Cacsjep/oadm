using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one status display: a small icon colored by the status (check, warning, error, running,
/// neutral) followed by plain text in the normal text color, like every other cell. Optional
/// <see cref="Detail"/> (e.g. an error message) follows the text. No chip, no border, no fill.
/// Theme classes on the control: <c>ok</c>, <c>warn</c>, <c>error</c>, <c>accent</c> (running);
/// without one it is neutral. The running icon spins.
/// </summary>
public sealed class StatusChip : Grid
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusChip, string?>(nameof(Text));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<StatusChip, string?>(nameof(Detail));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<StatusChip, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<bool> IsOkProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsOk));

    public static readonly StyledProperty<bool> IsWarningProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsWarning));

    public static readonly StyledProperty<bool> IsErrorProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsError));

    public static readonly StyledProperty<bool> IsAccentProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsAccent));

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public StatusChip()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Classes.Add("status");
        _icon.Classes.Add("statusIcon");
        _text.Classes.Add("statusText");
        SetColumn(_icon, 0);
        SetColumn(_text, 1);
        Children.Add(_icon);
        Children.Add(_text);
        UpdateText();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Extra text after <see cref="Text"/>, e.g. the error message of a failed task.</summary>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Overrides the status icon (default comes from the theme per status class).</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsOk
    {
        get => GetValue(IsOkProperty);
        set => SetValue(IsOkProperty, value);
    }

    public bool IsWarning
    {
        get => GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    public bool IsError
    {
        get => GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    public bool IsAccent
    {
        get => GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == DetailProperty)
        {
            UpdateText();
        }
        else if (change.Property == IconProperty)
        {
            UpdateIcon();
        }
        else if (change.Property == IsOkProperty)
        {
            Classes.Set("ok", IsOk);
            UpdateIcon();
        }
        else if (change.Property == IsWarningProperty)
        {
            Classes.Set("warn", IsWarning);
            UpdateIcon();
        }
        else if (change.Property == IsErrorProperty)
        {
            Classes.Set("error", IsError);
            UpdateIcon();
        }
        else if (change.Property == IsAccentProperty)
        {
            Classes.Set("accent", IsAccent);
            UpdateIcon();
        }
    }

    private string IconKey => IsError ? "Icon.status.error"
        : IsWarning ? "Icon.status.warning"
        : IsAccent ? "Icon.status.running"
        : IsOk ? "Icon.status.ok"
        : "Icon.status.neutral";

    private void UpdateIcon()
    {
        if (Icon is not null)
        {
            _icon.Data = Icon;
        }
        else if (this.TryFindResource(IconKey, ActualThemeVariant, out object? found) && found is Geometry geometry)
        {
            _icon.Data = geometry;
        }
    }

    private void UpdateText()
    {
        var text = string.IsNullOrEmpty(Detail) ? Text
            : string.IsNullOrEmpty(Text) ? Detail
            : $"{Text}: {Detail}";
        _text.Text = text;
        _text.IsVisible = !string.IsNullOrEmpty(text);
        // Like IconLabel: centered without an Inter correction (measured, ButtonAlignmentTests).
        _text.Margin = new Thickness(8, 0, 0, 0);
    }
}
