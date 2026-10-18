using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Oadm.Client.Infrastructure;

/// <summary>
/// The mouse wheel over a closed select field or a number field scrolls the page and never changes the value (every host
/// and plugin view). Avalonia steps a focused ComboBox to the next entry and a focused NumericUpDown by its increment and
/// keeps the wheel: after choosing an action in a long form, scrolling on changed the action as soon as the field passed
/// under the pointer. Values change by click, the open list or the keyboard only.
/// </summary>
public static class WheelScroll
{
    private static bool _attached;

    /// <summary>Applies to every select and number field from now on (once per process).</summary>
    public static void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        InputElement.PointerWheelChangedEvent.AddClassHandler<ComboBox>(OnComboBoxWheel, RoutingStrategies.Tunnel);
        InputElement.PointerWheelChangedEvent.AddClassHandler<ButtonSpinner>(OnSpinnerWheel, RoutingStrategies.Tunnel);
    }

    private static void OnComboBoxWheel(ComboBox box, PointerWheelEventArgs e)
    {
        // An open list keeps the wheel (Avalonia: the page must not scroll under the open list).
        if (!box.IsDropDownOpen)
        {
            PassToParent(box, e);
        }
    }

    private static void OnSpinnerWheel(ButtonSpinner spinner, PointerWheelEventArgs e) => PassToParent(spinner, e);

    /// <summary>Ends the event at the field and raises it again from the field's parent, so the scroll area around it scrolls.</summary>
    private static void PassToParent(Control field, PointerWheelEventArgs e)
    {
        if (e.Handled || field.GetVisualParent() is not Interactive parent || TopLevel.GetTopLevel(field) is not { } root)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new PointerWheelEventArgs(
            parent, e.Pointer, root, e.GetPosition(root), e.Timestamp,
            new PointerPointProperties((RawInputModifiers)e.KeyModifiers, PointerUpdateKind.Other), e.KeyModifiers, e.Delta));
    }
}
