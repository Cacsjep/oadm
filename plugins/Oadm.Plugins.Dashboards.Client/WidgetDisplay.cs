using System.Globalization;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// What a value widget shows for a value, without UI: the text (or the icon of an On / Off widget), its color, whether it
/// is dimmed and the line below it ("Connection lost", "No value yet"), and the tooltip with the value's time.
/// </summary>
/// <param name="HasValue">A value is known.</param>
/// <param name="Text">The value (Text, Number); null for On / Off.</param>
/// <param name="Unit">The unit after a number, smaller.</param>
/// <param name="Color">The color name (<see cref="WidgetColors"/>).</param>
/// <param name="Icon">The icon of an On / Off widget (key without "Icon.").</param>
/// <param name="Caption">The text below the icon of an On / Off widget.</param>
/// <param name="Dimmed">The value may be old, or is not a number.</param>
/// <param name="Note">The line below the value: why it is dimmed, or "No value yet".</param>
/// <param name="ToolTip">"41.5 at 14:02:17".</param>
public sealed record WidgetDisplay(bool HasValue, string? Text, string? Unit, string Color, string? Icon, string? Caption, bool Dimmed, string? Note, string? ToolTip)
{
    /// <summary>The number of a Number or Gauge widget (the gauge's marker); null when the value is no number.</summary>
    public double? Number { get; init; }

    /// <summary>Nothing to show yet (before the first values arrived).</summary>
    public static readonly WidgetDisplay Empty = new(false, null, null, "Secondary", null, null, false, DashboardTexts.NoValue, null);

    /// <summary>What <paramref name="widget"/> shows for <paramref name="value"/> (null: the server sent none yet).</summary>
    public static WidgetDisplay Of(Widget widget, ValueSnapshot? value)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (!widget.IsValue || value is null || widget.Field is not { } field)
        {
            return Empty;
        }

        string? raw;
        bool? on = null;
        if (field == DashboardsPluginInfo.ActiveField)
        {
            on = value.On; // the server knows every event that is on: none = off
            raw = value.On ? "On" : "Off";
        }
        else
        {
            raw = value.Data.TryGetValue(field, out var received) ? received : null;
        }

        var stale = value.Stale;
        if (raw is null)
        {
            return Empty with { Note = stale ?? DashboardTexts.NoValue, Dimmed = stale is not null };
        }

        var tip = value.Time is { } time
            ? string.Create(CultureInfo.CurrentCulture, $"{raw} at {time.ToLocalTime():HH:mm:ss}")
            : raw;
        switch (widget.Kind)
        {
            case WidgetKind.OnOff:
                var isOn = on ?? FieldValues.IsOn(raw);
                return new WidgetDisplay(true, null, null, isOn ? widget.OnColor : widget.OffColor, isOn ? widget.OnIcon : widget.OffIcon,
                    NullIfEmpty(isOn ? widget.OnText : widget.OffText), stale is not null, stale, tip);

            case WidgetKind.Number or WidgetKind.Gauge:
                if (!FieldValues.TryNumber(raw, out var number))
                {
                    return new WidgetDisplay(true, raw, null, widget.Color, null, null, true, stale ?? DashboardTexts.NotANumber, tip);
                }

                return new WidgetDisplay(true, FieldValues.FormatNumber(raw, number, widget.Decimals), NullIfEmpty(widget.Unit),
                    FieldValues.ColorOf(number, widget.Thresholds, widget.Color), null, null, stale is not null, stale, tip) { Number = number };

            default:
                return new WidgetDisplay(true, raw, null, widget.Color, null, null, stale is not null, stale, tip);
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
