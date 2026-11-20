using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Overlays;

/// <summary>What the task does with the dialog's list on each camera.</summary>
public enum OverlaysMode
{
    /// <summary>The overlays go next to the camera's own; one the camera already shows is skipped.</summary>
    Add = 0,

    /// <summary>The camera ends up with exactly this list of text overlays in view area 1; image overlays stay.</summary>
    Replace = 1,

    /// <summary>Removes the text overlays of view area 1 whose text equals one of the list.</summary>
    RemoveMatching = 2,

    /// <summary>Removes every text overlay of view area 1; image overlays stay. Takes no overlays.</summary>
    RemoveAll = 3,
}

/// <summary>Axis's six fixed places, or a custom place read from a camera (<see cref="TextOverlay.CustomPosition"/>).</summary>
public enum OverlayPosition
{
    TopLeft = 0,
    Top = 1,
    TopRight = 2,
    BottomLeft = 3,
    Bottom = 4,
    BottomRight = 5,
    Custom = 6,
}

/// <summary>The colours the overlay API offers for the text, its background and its outline.</summary>
public enum OverlayColor
{
    Black = 0,
    White = 1,
    Red = 2,
    Transparent = 3,
    SemiTransparent = 4,
}

/// <summary>One text overlay as the dialog sends it and the query reads it. Never an identity: ids change on the camera.</summary>
public sealed record TextOverlay
{
    /// <summary>The text with its modifiers ("%F %X", "#n"); a line break is a real one (<c>\n</c>).</summary>
    [property: Description("The text. Modifiers: %F date, %X time, %a day of the week, %c date and time, #n camera name, #i IP address, #M MAC address, #R frame rate, #b bitrate, #D1 to #D16 dynamic text. A line break is a new line.")]
    [property: MaxLength(OverlayRules.MaxTextLength)]
    public string Text { get; init; } = string.Empty;

    [property: Description("topLeft, top, topRight, bottomLeft, bottom or bottomRight; custom only with customPosition.")]
    public OverlayPosition Position { get; init; } = OverlayPosition.BottomLeft;

    /// <summary>[x, y] from -1 to 1 for <see cref="OverlayPosition.Custom"/> (a camera's own place, kept when copied).</summary>
    [property: Description("Custom position: [x, y], each from -1 (left, top) to 1 (right, bottom).")]
    public IReadOnlyList<double>? CustomPosition { get; init; }

    [property: Description("The text colour.")]
    public OverlayColor TextColor { get; init; } = OverlayColor.White;

    [property: Description("The background colour.")]
    public OverlayColor BackgroundColor { get; init; } = OverlayColor.Black;

    [property: Description("The outline colour.")]
    public OverlayColor OutlineColor { get; init; } = OverlayColor.Transparent;

    /// <summary>The font size; null = the camera's default.</summary>
    [property: Description("The font size; leave it out for the camera's default. Each camera checks its own range (16 to 200 on AXIS OS 12).")]
    [property: Range(OverlayRules.MinFontSize, OverlayRules.MaxFontSize)]
    public int? FontSize { get; init; }
}

/// <summary>The dialog's payload: what to do and the overlays. Kept in memory only, like every payload.</summary>
[Description("Add, replace or remove text overlays in view area 1 of the cameras.")]
public sealed record OverlaysPayload
{
    [property: Description("add: next to the camera's overlays; replace: the camera keeps exactly these text overlays, image overlays stay; removeMatching: removes the text overlays with one of these texts; removeAll: removes every text overlay (no overlays in the list).")]
    public OverlaysMode Mode { get; init; } = OverlaysMode.Add;

    [property: Description("The text overlays, at most 8.")]
    [property: MaxLength(OverlayRules.MaxOverlays)]
    public IReadOnlyList<TextOverlay> Overlays { get; init; } = [];
}

/// <summary>A camera's overlay limits (getOverlayCapabilities).</summary>
/// <param name="MaxTextLength">Longest text.</param>
/// <param name="MinFontSize">Smallest font size; null when the camera does not say.</param>
/// <param name="MaxFontSize">Largest font size; null when the camera does not say.</param>
/// <param name="Slots">Overlay slots of one view area (numAvailableSlots: the total, it does not drop when one is added).</param>
/// <param name="SlotsPerText">Slots one text overlay takes.</param>
/// <param name="SlotsPerImage">Slots one image overlay takes.</param>
public sealed record OverlayCapabilities(int MaxTextLength, int? MinFontSize, int? MaxFontSize, int? Slots, int SlotsPerText, int SlotsPerImage);

/// <summary>The read-only query <c>listOverlays</c>: the limits and the text overlays of view area 1.</summary>
public sealed record OverlaysQueryResult(
    bool Supported,
    string? Message,
    OverlayCapabilities? Capabilities,
    IReadOnlyList<TextOverlay> Overlays);

/// <summary>JSON shared by the server and the client part (camelCase, enums as camelCase strings).</summary>
public static class OverlaysJson
{
    public const string ListOverlaysMethod = "listOverlays";

    public static JsonSerializerOptions Options { get; } = PluginJson.CreateOptions(PluginJsonEnums.CamelCase);

    public static string Serialize(OverlaysPayload payload) => JsonSerializer.Serialize(payload, Options);

    public static string Serialize(OverlaysQueryResult result) => JsonSerializer.Serialize(result, Options);

    /// <summary>Parses a payload; an <see cref="ArgumentException"/> with a plain message when it is missing or broken.</summary>
    public static OverlaysPayload ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException(OverlayRules.NoSettings);
        }

        try
        {
            return JsonSerializer.Deserialize<OverlaysPayload>(json, Options) ?? throw new ArgumentException(OverlayRules.NoSettings);
        }
        catch (JsonException)
        {
            throw new ArgumentException(OverlayRules.Unreadable);
        }
    }

    public static OverlaysQueryResult? ParseQueryResult(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<OverlaysQueryResult>(json, Options);
}

/// <summary>Plain names of the choices, the same in the dialog, the steps and the docs.</summary>
public static class OverlayNames
{
    public static string Of(OverlayPosition position) => position switch
    {
        OverlayPosition.TopLeft => "Top left",
        OverlayPosition.Top => "Top",
        OverlayPosition.TopRight => "Top right",
        OverlayPosition.BottomLeft => "Bottom left",
        OverlayPosition.Bottom => "Bottom",
        OverlayPosition.BottomRight => "Bottom right",
        _ => "Custom position",
    };

    public static string Of(OverlayColor color) => color switch
    {
        OverlayColor.Black => "Black",
        OverlayColor.White => "White",
        OverlayColor.Red => "Red",
        OverlayColor.Transparent => "Transparent",
        _ => "Semi-transparent",
    };

    /// <summary>The API's value: "topLeft", "semiTransparent".</summary>
    public static string ApiValue(OverlayPosition position) => JsonNamingPolicy.CamelCase.ConvertName(position.ToString());

    /// <summary>The API's value: "semiTransparent".</summary>
    public static string ApiValue(OverlayColor color) => JsonNamingPolicy.CamelCase.ConvertName(color.ToString());

    /// <summary>The first line of a text, at most 30 characters, for step names and task names.</summary>
    public static string Brief(string text, int max = 30)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[0].Trim();
        if (line.Length == 0)
        {
            return text.Trim().Length == 0 ? "(empty)" : "(more lines)";
        }

        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}

/// <summary>JSON names of the payload's fields, for the field errors.</summary>
internal static class OverlayFields
{
    public const string Mode = "mode";
    public const string Overlays = "overlays";

    public static string Of(int index, string field) => $"overlays[{index}].{field}";
}

/// <summary>The text of an overlay as the camera keeps it.</summary>
public static class OverlayText
{
    /// <summary>
    /// Line breaks as <c>\n</c> (a typed CR LF becomes LF). In the JSON request a real line break makes a new line; "%0A"
    /// would be read as the modifier "%A" (the day of the week), seen on AXIS OS 12.11.
    /// </summary>
    public static string Normalize(string text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
