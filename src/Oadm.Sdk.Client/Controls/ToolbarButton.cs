using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one toolbar button: <see cref="IconLabel"/> (icon + text) in a <c>Button.toolbar</c>, flat (Fluent command button,
/// decision 2026-10-25: the icon in the accent colour, the text neutral; icon only: the text colour). The icon is a geometry or a theme icon key
/// ("restart" resolves the application resource <c>Icon.restart</c>; unknown keys show the plugin
/// icon). Used by every Devices page toolbar entry, built-in or plugin.
/// </summary>
public class ToolbarButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ToolbarButton, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<ToolbarButton, string?>(nameof(IconKey));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ToolbarButton, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsPrimaryProperty =
        AvaloniaProperty.Register<ToolbarButton, bool>(nameof(IsPrimary));

    public static readonly StyledProperty<bool> IsIconOnlyProperty =
        AvaloniaProperty.Register<ToolbarButton, bool>(nameof(IsIconOnly));

    private readonly IconLabel _label = new() { Spacing = 8 };

    public ToolbarButton()
    {
        Content = _label;
        Classes.Add("toolbar");
    }

    /// <summary>The icon; or set <c>IconKey</c>.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Theme icon key, used when <see cref="Icon"/> is not set.</summary>
    public string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    /// <summary>The button text; the tooltip when the button shows only its icon.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The main command of the toolbar: class <c>main</c> only, never filled (toolbars have no filled buttons, decision 2026-10-25).</summary>
    public bool IsPrimary
    {
        get => GetValue(IsPrimaryProperty);
        set => SetValue(IsPrimaryProperty, value);
    }

    /// <summary>
    /// Compact: only the icon (theme class <c>iconOnly</c>), <see cref="Text"/> becomes the tooltip unless one is set.
    /// For entries that would not fit the toolbar at the minimum window width.
    /// </summary>
    public bool IsIconOnly
    {
        get => GetValue(IsIconOnlyProperty);
        set => SetValue(IsIconOnlyProperty, value);
    }

    /// <summary>The icon + text content (for tests).</summary>
    public IconLabel Label => _label;

    /// <summary>
    /// Styled as a <see cref="Button"/>, so the theme's <c>Button.toolbar</c> styles apply unchanged (<see cref="IsPrimary"/>
    /// adds the class <c>main</c>, never <c>primary</c>: a toolbar button is never filled).
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The theme geometry <c>Icon.&lt;key&gt;</c>, else <c>Icon.plugin</c>, else null.</summary>
    public static Geometry? ResolveIcon(string? key)
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(key) && app.TryGetResource("Icon." + key, app.ActualThemeVariant, out var value) && value is Geometry geometry)
        {
            return geometry;
        }

        return app.TryGetResource("Icon.plugin", app.ActualThemeVariant, out var fallback) ? fallback as Geometry : null;
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        UpdateIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == IconKeyProperty)
        {
            UpdateIcon();
        }
        else if (change.Property == TextProperty)
        {
            _label.Text = Text;
            UpdateIconOnlyTip();
        }
        else if (change.Property == IsIconOnlyProperty)
        {
            Classes.Set("iconOnly", IsIconOnly);
            _label.IsTextVisible = !IsIconOnly;
            UpdateIconOnlyTip();
        }
        else if (change.Property == IsPrimaryProperty)
        {
            // Toolbars have no filled buttons (Fluent, decision 2026-10-25): the main one is a command button like the others.
            Classes.Set("main", IsPrimary);
        }
    }

    private void UpdateIconOnlyTip()
    {
        if (IsIconOnly && ToolTip.GetTip(this) is null or string)
        {
            ToolTip.SetTip(this, Text);
        }
    }

    private void UpdateIcon() => _label.Icon = Icon ?? (IconKey is null ? null : ResolveIcon(IconKey));
}

/// <summary>The vertical line between toolbar groups (theme style <c>Border.vseparator</c>).</summary>
public sealed class ToolbarSeparator : Border
{
    public ToolbarSeparator()
    {
        Classes.Add("vseparator");
    }

    protected override Type StyleKeyOverride => typeof(Border);
}
