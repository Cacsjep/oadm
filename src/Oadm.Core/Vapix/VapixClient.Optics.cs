using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>
/// Optics Control API (<c>axis-cgi/opticscontrol.cgi</c>, API id <c>optics-control</c>): zoom and focus of cameras with
/// a remote zoom or focus lens. Verified on an AXIS P3265-V (AXIS OS 12.11, API 1.3): set calls answer at once and the
/// lens moves afterwards; <c>performAutofocus</c> answers when the search finished (about 4 s); a value out of range is
/// error 2104. Multisensor cameras have one optics per image source: the live view asks for the optics of the source's
/// sensor (opticsId = the 0-based sensor), else the first one.
/// </summary>
public sealed partial class VapixClient
{
    internal const string OpticsControlPath = "axis-cgi/opticscontrol.cgi";

    /// <summary>The autofocus search can take several seconds; the answer comes when it finished.</summary>
    private static readonly TimeSpan AutofocusTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Capabilities and current state of the device's optics; <see cref="OpticsState.None"/> without the API.</summary>
    public Task<OpticsState> GetOpticsAsync(CancellationToken ct) => GetOpticsAsync(0, ct);

    /// <summary>Capabilities and state of the optics of image source <paramref name="sensor"/> (0-based), else of the first optics.</summary>
    public async Task<OpticsState> GetOpticsAsync(int sensor, CancellationToken ct)
    {
        string capabilities;
        try
        {
            capabilities = await PostJsonAsync(OpticsControlPath, """{"apiVersion":"1","method":"getCapabilities"}""", ct).ConfigureAwait(false);
        }
        catch (VapixException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return OpticsState.None;
        }

        var caps = VapixParsers.ParseOpticsCapabilities(capabilities, sensor);
        if (caps is null)
        {
            return OpticsState.None;
        }

        var optics = await PostJsonAsync(OpticsControlPath, """{"apiVersion":"1","method":"getOptics"}""", ct).ConfigureAwait(false);
        return VapixParsers.ParseOptics(optics, caps);
    }

    /// <summary>Zoom: 1 (wide) to <see cref="OpticsState.MaxMagnification"/> (tele).</summary>
    public Task SetMagnificationAsync(string opticsId, double magnification, CancellationToken ct) =>
        OpticsCallAsync("setMagnification", opticsId, w => w.WriteNumber("magnification", Math.Round(magnification, 4)), ct);

    /// <summary>Focus: 0 (near) to 1 (far).</summary>
    public Task SetFocusAsync(string opticsId, double position, CancellationToken ct) =>
        OpticsCallAsync("setFocus", opticsId, w => w.WriteNumber("position", Math.Round(position, 4)), ct);

    /// <summary>The autofocus area as fractions of the picture (0..1).</summary>
    public Task SetFocusWindowAsync(string opticsId, OpticsWindow window, CancellationToken ct) =>
        OpticsCallAsync("setFocusWindow", opticsId, w =>
        {
            w.WriteNumber("upperLeftX", Math.Round(window.X, 4));
            w.WriteNumber("upperLeftY", Math.Round(window.Y, 4));
            w.WriteNumber("width", Math.Round(window.Width, 4));
            w.WriteNumber("height", Math.Round(window.Height, 4));
        }, ct);

    /// <summary>Runs a focus search in the autofocus area; answers when it finished.</summary>
    public Task PerformAutofocusAsync(string opticsId, CancellationToken ct) =>
        OpticsCallAsync("performAutofocus", opticsId, null, ct, AutofocusTimeout);

    /// <summary>Moves focus back to its default position (a focus search should follow).</summary>
    public Task ResetFocusAsync(string opticsId, CancellationToken ct) =>
        OpticsCallAsync("reset", opticsId, w => w.WriteBoolean("focus", true), ct);

    private async Task OpticsCallAsync(string method, string opticsId, Action<Utf8JsonWriter>? fields, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(opticsId);
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("apiVersion", "1");
            w.WriteString("method", method);
            w.WriteStartObject("params");
            w.WriteStartArray("optics");
            w.WriteStartObject();
            w.WriteString("opticsId", opticsId);
            fields?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, OpticsControlPath)
        {
            Content = new ByteArrayContent(buffer.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) { CharSet = "utf-8" } } },
        };
        if (timeout is { } t)
        {
            request.Options.Set(VapixRequestOptions.Timeout, t);
        }

        var json = await SendForStringAsync(request, ct).ConfigureAwait(false);
        VapixParsers.ThrowOnOpticsError(json);
    }
}

/// <summary>The autofocus area as fractions of the picture: upper left corner, width and height (0..1).</summary>
public readonly record struct OpticsWindow(double X, double Y, double Width, double Height)
{
    /// <summary>Clamped into the picture with at least 5 % per side.</summary>
    public OpticsWindow Clamped()
    {
        const double Min = 0.05;
        var x = Math.Clamp(X, 0, 1 - Min);
        var y = Math.Clamp(Y, 0, 1 - Min);
        return new OpticsWindow(x, y, Math.Clamp(Width, Min, 1 - x), Math.Clamp(Height, Min, 1 - y));
    }
}

/// <summary>What the lens of a device can do and where it is now.</summary>
public sealed record OpticsState
{
    public static OpticsState None { get; } = new();

    /// <summary>Id of the optics the values belong to ("0").</summary>
    public string OpticsId { get; init; } = "";

    public bool HasZoom { get; init; }

    public bool HasFocus { get; init; }

    /// <summary>Zoom range is 1 to this value (only with zoom).</summary>
    public double MaxMagnification { get; init; } = 1;

    public double Magnification { get; init; } = 1;

    /// <summary>0 (near) to 1 (far).</summary>
    public double FocusPosition { get; init; }

    public OpticsWindow? FocusWindow { get; init; }

    public bool ZoomMoving { get; init; }

    public bool FocusMoving { get; init; }

    /// <summary>True when the live view offers optics controls (zoom or focus).</summary>
    public bool IsSupported => HasZoom || HasFocus;
}

/// <summary>An Optics Control error the device answered (e.g. 1300 focus not found, 2104 value out of range).</summary>
public sealed class OpticsException(int code, string message) : VapixException(message)
{
    public const int FocusNotFound = 1300;
    public const int InvalidValue = 2104;

    public int Code { get; } = code;

    /// <summary>The text for the user.</summary>
    public string UserMessage => Code switch
    {
        FocusNotFound => "The camera could not find focus. Try another autofocus area or set focus by hand.",
        InvalidValue => "The camera refused the value.",
        1200 => "The camera is too hot or too cold to move the lens.",
        _ => "The camera refused: " + Message,
    };
}

public static partial class VapixParsers
{
    /// <summary>Capabilities of the optics of <paramref name="sensor"/> (else the first), or null when it has neither zoom nor focus.</summary>
    internal static OpticsState? ParseOpticsCapabilities(string json, int sensor = 0)
    {
        ThrowOnOpticsError(json);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("optics", out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return null;
        }

        var wanted = sensor.ToString(CultureInfo.InvariantCulture);
        var first = list.EnumerateArray().FirstOrDefault(o => (Text(o, "opticsId") ?? "0") == wanted);
        if (first.ValueKind != JsonValueKind.Object)
        {
            first = list[0];
        }

        var caps = first.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var state = new OpticsState
        {
            OpticsId = Text(first, "opticsId") ?? "0",
            HasZoom = caps.Contains("zoom") && Number(first, "maxMagnification") is > 1,
            HasFocus = caps.Contains("focus"),
            MaxMagnification = Number(first, "maxMagnification") ?? 1,
        };
        return state.IsSupported ? state : null;
    }

    /// <summary>The current values of the optics of <paramref name="caps"/>.</summary>
    internal static OpticsState ParseOptics(string json, OpticsState caps)
    {
        ThrowOnOpticsError(json);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("optics", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return caps;
        }

        foreach (var o in list.EnumerateArray())
        {
            if ((Text(o, "opticsId") ?? "0") != caps.OpticsId)
            {
                continue;
            }

            OpticsWindow? window = Number(o, "focusWindowUpperLeftX") is { } x && Number(o, "focusWindowUpperLeftY") is { } y
                && Number(o, "focusWindowWidth") is { } w && Number(o, "focusWindowHeight") is { } h
                ? new OpticsWindow(x, y, w, h)
                : null;
            return caps with
            {
                Magnification = Math.Clamp(Number(o, "magnification") ?? 1, 1, caps.MaxMagnification),
                FocusPosition = Math.Clamp(Number(o, "focusPosition") ?? 0, 0, 1),
                FocusWindow = window,
                ZoomMoving = Bool(o, "zoomMoving"),
                FocusMoving = Bool(o, "focusMoving"),
            };
        }

        return caps;
    }

    /// <summary>Throws <see cref="OpticsException"/> for an answer with <c>error</c>; <see cref="VapixException"/> for no JSON.</summary>
    internal static void ThrowOnOpticsError(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new VapixException("opticscontrol.cgi: the answer is not JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                throw new OpticsException(code, Text(error, "message") ?? "error " + code.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
