using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// A monitoring card: a status icon and a title, a large count below and an optional detail line (e.g. what the count
/// is made of). A card in the page's card style; the status (<c>ok</c>, <c>warn</c>, <c>error</c> theme classes) colors the
/// icon and the count, without one it is neutral. Pages set the status only while the count needs attention, so a zero
/// stays calm grey.
/// </summary>
public sealed class StatCard : Border
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<StatCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<StatCard, string?>(nameof(Value));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<StatCard, string?>(nameof(Detail));

    public static readonly StyledProperty<bool> IsOkProperty =
        AvaloniaProperty.Register<StatCard, bool>(nameof(IsOk));

    public static readonly StyledProperty<bool> IsWarningProperty =
        AvaloniaProperty.Register<StatCard, bool>(nameof(IsWarning));

    public static readonly StyledProperty<bool> IsErrorProperty =
        AvaloniaProperty.Register<StatCard, bool>(nameof(IsError));

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _value = new();
    private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    public StatCard()
    {
        Classes.Add("card");
        Classes.Add("statCard");
        _icon.Classes.Add("statIcon");
        _title.Classes.Add("statTitle");
        _value.Classes.Add("statValue");
        _detail.Classes.Add("statDetail");

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(_icon);
        header.Children.Add(_title);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(header);
        content.Children.Add(_value);
        content.Children.Add(_detail);
        Child = content;
        Update();
    }

    /// <summary>What is counted ("Worn").</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The count, already formatted ("1,204").</summary>
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>One small line below the count; hidden when empty.</summary>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Green icon and count. Set it only while the count matters.</summary>
    public bool IsOk
    {
        get => GetValue(IsOkProperty);
        set => SetValue(IsOkProperty, value);
    }

    /// <summary>Amber icon and count, while the count needs attention.</summary>
    public bool IsWarning
    {
        get => GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    /// <summary>Red icon and count, while the count needs attention.</summary>
    public bool IsError
    {
        get => GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == ValueProperty || change.Property == DetailProperty)
        {
            Update();
        }
        else if (change.Property == IsOkProperty || change.Property == IsWarningProperty || change.Property == IsErrorProperty)
        {
            Classes.Set("ok", IsOk && !IsWarning && !IsError);
            Classes.Set("warn", IsWarning && !IsError);
            Classes.Set("error", IsError);
            UpdateIcon();
        }
    }

    private string IconKey => IsError ? "Icon.status.error"
        : IsWarning ? "Icon.status.warning"
        : IsOk ? "Icon.status.ok"
        : "Icon.status.neutral";

    private void UpdateIcon()
    {
        if (this.TryFindResource(IconKey, ActualThemeVariant, out object? found) && found is Geometry geometry)
        {
            _icon.Data = geometry;
        }
    }

    private void Update()
    {
        _title.Text = Title;
        _value.Text = Value;
        _detail.Text = Detail;
        _detail.IsVisible = !string.IsNullOrEmpty(Detail);
    }
}
