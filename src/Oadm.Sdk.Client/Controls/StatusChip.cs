using Avalonia;
using Avalonia.Controls;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one status chip: transparent fill, 1 px border in the status color, normal text
/// (theme style <c>Border.pill</c> with <c>ok</c>, <c>warn</c>, <c>error</c>, <c>accent</c>).
/// Without a status flag the border is neutral grey.
/// </summary>
public sealed class StatusChip : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusChip, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsOkProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsOk));

    public static readonly StyledProperty<bool> IsWarningProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsWarning));

    public static readonly StyledProperty<bool> IsErrorProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsError));

    public static readonly StyledProperty<bool> IsAccentProperty =
        AvaloniaProperty.Register<StatusChip, bool>(nameof(IsAccent));

    private readonly TextBlock _text = new();

    public StatusChip()
    {
        Classes.Add("pill");
        Child = _text;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
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

    /// <summary>Styled as a <see cref="Border"/>, so the theme's <c>Border.pill</c> styles apply unchanged.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
        else if (change.Property == IsOkProperty)
        {
            Classes.Set("ok", IsOk);
        }
        else if (change.Property == IsWarningProperty)
        {
            Classes.Set("warn", IsWarning);
        }
        else if (change.Property == IsErrorProperty)
        {
            Classes.Set("error", IsError);
        }
        else if (change.Property == IsAccentProperty)
        {
            Classes.Set("accent", IsAccent);
        }
    }
}
