using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays;

/// <summary>A text overlay as a camera lists it, with its identity (used only right after the list that returned it).</summary>
public sealed record DeviceOverlay(int Identity, int Camera, TextOverlay Overlay);

/// <summary>The overlays of a camera (list): the text overlays and how many image overlays each view area has.</summary>
public sealed record DeviceOverlays(IReadOnlyList<DeviceOverlay> Texts, IReadOnlyDictionary<int, int> ImagesPerCamera)
{
    /// <summary>The text overlays of one view area.</summary>
    public IReadOnlyList<DeviceOverlay> TextsOf(int camera) => [.. Texts.Where(t => t.Camera == camera)];

    /// <summary>The image overlays of one view area.</summary>
    public int ImagesOf(int camera) => ImagesPerCamera.GetValueOrDefault(camera);
}

/// <summary>The camera refused an overlay request (an "error" object in an HTTP 200 answer), with a plain message.</summary>
public sealed class OverlayDeviceException : Exception
{
    public OverlayDeviceException()
    {
    }

    public OverlayDeviceException(string message)
        : base(message)
    {
    }

    public OverlayDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OverlayDeviceException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The API's error code (300 limit reached, 309 font size); 0 without one.</summary>
    public int Code { get; }
}

/// <summary>
/// The Dynamic overlay API (<c>/axis-cgi/dynamicoverlay/dynamicoverlay.cgi</c>, JSON, API discovery id <c>dynamicoverlay</c>
/// 1.x; recorded on AXIS OS 10.12 and 12.11): getOverlayCapabilities, list, addText and remove for view area 1. The
/// deprecated <c>reference</c> and <c>scrollSpeed</c> are never sent. Errors come back as HTTP 200 with an "error" object.
/// </summary>
public static class DynamicOverlayApi
{
    public const string ApiId = "dynamicoverlay";
    public const string MinVersion = "1.0";
    public const string Path = "/axis-cgi/dynamicoverlay/dynamicoverlay.cgi";

    /// <summary>The view area every overlay goes to (camera=1).</summary>
    public const int ViewArea = 1;

    /// <summary>The longest text when the camera does not say (the API's limit).</summary>
    public const int DefaultMaxTextLength = OverlayRules.MaxTextLength;

    public static readonly Version Min = Version.Parse(MinVersion);

    /// <summary>Reads the limits (getOverlayCapabilities).</summary>
    public static async Task<OverlayCapabilities> GetCapabilitiesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseCapabilities(await CallAsync(vapix, "getOverlayCapabilities", new JsonObject(), ct).ConfigureAwait(false));

    /// <summary>Lists every overlay (list with empty params: a list without params is refused with 102).</summary>
    public static async Task<DeviceOverlays> ListAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseList(await CallAsync(vapix, "list", new JsonObject(), ct).ConfigureAwait(false));

    /// <summary>Adds one text overlay to view area 1; returns its identity.</summary>
    public static async Task<int> AddTextAsync(IVapixClient vapix, TextOverlay overlay, CancellationToken ct)
    {
        var data = await CallAsync(vapix, "addText", AddTextParams(overlay), ct).ConfigureAwait(false);
        return Int(data?["identity"]) ?? 0;
    }

    /// <summary>Removes one overlay by the identity of the list just read (remove takes only the identity).</summary>
    public static Task RemoveAsync(IVapixClient vapix, int identity, CancellationToken ct) =>
        CallAsync(vapix, "remove", new JsonObject { ["identity"] = identity }, ct);

    /// <summary>The params of addText: camera 1, the text with real line breaks, position, colours and the size when set.</summary>
    public static JsonObject AddTextParams(TextOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var parameters = new JsonObject
        {
            ["camera"] = ViewArea,
            ["text"] = OverlayText.Normalize(overlay.Text),
            ["position"] = overlay.Position == OverlayPosition.Custom && overlay.CustomPosition is { Count: 2 } place
                ? new JsonArray(place[0], place[1])
                : OverlayNames.ApiValue(overlay.Position == OverlayPosition.Custom ? OverlayPosition.BottomLeft : overlay.Position),
            ["textColor"] = OverlayNames.ApiValue(overlay.TextColor),
            ["textBGColor"] = OverlayNames.ApiValue(overlay.BackgroundColor),
            ["textOLColor"] = OverlayNames.ApiValue(overlay.OutlineColor),
        };
        if (overlay.FontSize is { } size)
        {
            parameters["fontSize"] = size;
        }

        return parameters;
    }

    /// <summary>The request body of a method.</summary>
    public static string Body(string method, JsonObject parameters) =>
        new JsonObject { ["apiVersion"] = "1.0", ["context"] = "oadm", ["method"] = method, ["params"] = parameters }.ToJsonString();

    /// <summary>getOverlayCapabilities' data; the misspelled "numberAvailbleSlots" of the documentation's example counts too.</summary>
    public static OverlayCapabilities ParseCapabilities(JsonNode? data) => new(
        Int(data?["maxTextLength"]) is > 0 and var max ? max : DefaultMaxTextLength,
        Int(data?["minFontSize"]),
        Int(data?["maxFontSize"]),
        Int(data?["numAvailableSlots"]) ?? Int(data?["numberAvailbleSlots"]),
        Int(data?["slotsPerTextOverlay"]) is > 0 and var text ? text : 1,
        Int(data?["slotsPerImageOverlay"]) is > 0 and var image ? image : 1);

    /// <summary>list's data: the text overlays with their place, colours and size; image overlays counted per view area.</summary>
    public static DeviceOverlays ParseList(JsonNode? data)
    {
        var texts = new List<DeviceOverlay>();
        foreach (var item in data?["textOverlays"] as JsonArray ?? [])
        {
            if (item is not JsonObject o)
            {
                continue;
            }

            var (position, custom) = ParsePosition(o["position"]);
            texts.Add(new DeviceOverlay(Int(o["identity"]) ?? 0, Int(o["camera"]) ?? ViewArea, new TextOverlay
            {
                Text = Text(o["text"]) ?? string.Empty,
                Position = position,
                CustomPosition = custom,
                TextColor = Color(o["textColor"]) ?? OverlayColor.Black,
                BackgroundColor = Color(o["textBGColor"]) ?? OverlayColor.Transparent,
                OutlineColor = Color(o["textOLColor"]) ?? OverlayColor.Transparent,
                FontSize = Int(o["fontSize"]),
            }));
        }

        var images = new Dictionary<int, int>();
        foreach (var item in data?["imageOverlays"] as JsonArray ?? [])
        {
            var camera = Int(item?["camera"]) ?? ViewArea;
            images[camera] = images.GetValueOrDefault(camera) + 1;
        }

        return new DeviceOverlays([.. texts.OrderBy(t => t.Identity)], images);
    }

    /// <summary>A named place, or [x, y] as <see cref="OverlayPosition.Custom"/>.</summary>
    public static (OverlayPosition Position, IReadOnlyList<double>? Custom) ParsePosition(JsonNode? node)
    {
        if (node is JsonArray { Count: 2 } pair && Number(pair[0]) is { } x && Number(pair[1]) is { } y)
        {
            return (OverlayPosition.Custom, [x, y]);
        }

        return Text(node) switch
        {
            "topLeft" => (OverlayPosition.TopLeft, null),
            "top" => (OverlayPosition.Top, null),
            "topRight" => (OverlayPosition.TopRight, null),
            "bottomLeft" => (OverlayPosition.BottomLeft, null),
            "bottom" => (OverlayPosition.Bottom, null),
            "bottomRight" => (OverlayPosition.BottomRight, null),
            _ => (OverlayPosition.Custom, [0, 0]),
        };
    }

    /// <summary>The plain message of an API error code.</summary>
    public static string ErrorMessage(int code, string? message) => code switch
    {
        300 => "The camera has no free overlay slot.",
        301 => "The camera has no view area 1.",
        302 => "The overlay is no longer on the camera.",
        303 => "The camera does not accept this position.",
        304 => "The camera does not accept this text.",
        306 or 307 or 308 => "The camera does not accept this colour.",
        309 => "The camera does not accept this size.",
        _ => string.IsNullOrWhiteSpace(message)
            ? string.Create(CultureInfo.InvariantCulture, $"The camera refused the request (error {code}).")
            : string.Create(CultureInfo.InvariantCulture, $"The camera refused the request: {message.Trim().TrimEnd('.')} (error {code})."),
    };

    /// <summary>Sends one method; the answer's data, or an <see cref="OverlayDeviceException"/> with a plain message.</summary>
    public static async Task<JsonNode?> CallAsync(IVapixClient vapix, string method, JsonObject parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(Body(method, parameters), Encoding.UTF8, "application/json"),
        };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OverlayDeviceException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
        }

        return ParseAnswer(text);
    }

    /// <summary>The data of an answer; an "error" object becomes an <see cref="OverlayDeviceException"/>.</summary>
    public static JsonNode? ParseAnswer(string text)
    {
        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new OverlayDeviceException("The camera sent an answer that cannot be read.");
        }

        if (answer?["error"] is { } error)
        {
            var code = Int(error["code"]) ?? 0;
            throw new OverlayDeviceException(code, ErrorMessage(code, Text(error["message"])));
        }

        return answer?["data"];
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<double>(out var d) && double.IsFinite(d) ? (int)d
            : value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null;
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    private static OverlayColor? Color(JsonNode? node) => Text(node) switch
    {
        "black" => OverlayColor.Black,
        "white" => OverlayColor.White,
        "red" => OverlayColor.Red,
        "transparent" => OverlayColor.Transparent,
        "semiTransparent" => OverlayColor.SemiTransparent,
        _ => null,
    };
}
