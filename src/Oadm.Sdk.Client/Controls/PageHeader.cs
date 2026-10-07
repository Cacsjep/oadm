using Avalonia;
using Avalonia.Controls;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// Lets a plugin page put content into the host's page header instead of a card heading: set
/// <c>ui:PageHeader.Subtitle</c> (secondary line under the page title) and/or
/// <c>ui:PageHeader.Trailing</c> (e.g. a status chip, right of the page title) on the page's root
/// control. The host shows them next to the page title; the trailing control keeps the page's
/// DataContext, so its bindings work. Then the page's card starts directly with the form, with no
/// repeated title and no empty heading row.
/// </summary>
public static class PageHeader
{
    public static readonly AttachedProperty<string?> SubtitleProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Subtitle", typeof(PageHeader));

    public static readonly AttachedProperty<Control?> TrailingProperty =
        AvaloniaProperty.RegisterAttached<Control, Control?>("Trailing", typeof(PageHeader));

    public static string? GetSubtitle(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SubtitleProperty);
    }

    public static void SetSubtitle(Control element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SubtitleProperty, value);
    }

    public static Control? GetTrailing(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TrailingProperty);
    }

    public static void SetTrailing(Control element, Control? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TrailingProperty, value);
    }
}
