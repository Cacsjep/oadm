using Avalonia;
using Avalonia.Controls;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// Lets a plugin page put content into the host's page header instead of a card heading: set
/// <c>ui:PageHeader.Subtitle</c> (secondary line under the page title) and/or
/// <c>ui:PageHeader.Trailing</c> (e.g. a status chip, right of the page title) on the page's root
/// control. The host shows them next to the page title; the trailing control keeps the page's
/// DataContext, so its bindings work. Then the page's card starts directly with the form, with no
/// repeated title and no empty heading row. <c>ui:PageHeader.IsHidden="True"</c> leaves the header row out (pages
/// that need every pixel, e.g. Live View; the rail entry names the page).
/// </summary>
public static class PageHeader
{
    public static readonly AttachedProperty<string?> SubtitleProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Subtitle", typeof(PageHeader));

    public static readonly AttachedProperty<Control?> TrailingProperty =
        AvaloniaProperty.RegisterAttached<Control, Control?>("Trailing", typeof(PageHeader));

    public static readonly AttachedProperty<bool> IsHiddenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsHidden", typeof(PageHeader));

    /// <summary>Whether the page leaves out the header row.</summary>
    public static bool GetIsHidden(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsHiddenProperty);
    }

    /// <summary>True leaves out the header row (pages that need every pixel).</summary>
    public static void SetIsHidden(Control element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsHiddenProperty, value);
    }

    /// <summary>The short line right of the page title.</summary>
    public static string? GetSubtitle(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SubtitleProperty);
    }

    /// <summary>Sets the short line right of the page title: one sentence that says what the page is for.</summary>
    public static void SetSubtitle(Control element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SubtitleProperty, value);
    }

    /// <summary>The control right of the page title.</summary>
    public static Control? GetTrailing(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TrailingProperty);
    }

    /// <summary>Puts a control right of the page title; it keeps the page's data context.</summary>
    public static void SetTrailing(Control element, Control? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TrailingProperty, value);
    }
}
