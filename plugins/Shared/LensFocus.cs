using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Shared;

/// <summary>
/// The autofocus of every lens of a camera over the Optics Control API (<c>axis-cgi/opticscontrol.cgi</c>, API id
/// <c>optics-control</c>), the API of the live view's focus. A multisensor camera has one optics per sensor (AXIS
/// P3735-PLE: 4, AXIS P4708-PLVE: 2, opticsId "0".."n-1", recorded 2026-11-24 on AXIS OS 13.0.114 and 12.11.147); a group
/// view has no optics of its own. <c>performAutofocus</c> answers when the search ended. Linked as source into the
/// Autofocus task and Provisioning's Autofocus node (internal there).
/// </summary>
internal static class LensFocus
{
    public const string OpticsApi = "optics-control";
    public const string MinVersion = "1.0";
    public const string OpticsPath = "/axis-cgi/opticscontrol.cgi";

    /// <summary>The camera found no focus (error 1300).</summary>
    public const int FocusNotFound = 1300;

    /// <summary>"The camera could not find focus. Set focus by hand."</summary>
    public const string FocusNotFoundText = "The camera could not find focus. Set focus by hand.";

    /// <summary>The ids of the lenses with remote focus, in the camera's order; empty for a fixed lens or no optics at all.</summary>
    public static async Task<IReadOnlyList<string>> LensesAsync(IVapixClient vapix, CancellationToken ct)
    {
        var (status, capabilities) = await PostAsync(vapix, """{"apiVersion":"1","method":"getCapabilities"}""", ct).ConfigureAwait(false);
        return status == HttpStatusCode.NotFound ? [] : LensesWithFocus(capabilities);
    }

    /// <summary>Runs the autofocus of one lens and waits until it ended; the plain error text, or null when focus was found.</summary>
    public static async Task<string?> AutofocusAsync(IVapixClient vapix, string opticsId, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new { apiVersion = "1", method = "performAutofocus", @params = new { optics = new[] { new { opticsId } } } });
        var (_, answer) = await PostAsync(vapix, request, ct).ConfigureAwait(false);
        return ErrorOf(answer);
    }

    /// <summary>"Camera 1" for opticsId "0": the lens of the camera's first sensor, named like the camera's own channels.</summary>
    public static string LensName(string opticsId) =>
        int.TryParse(opticsId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? "Camera " + (n + 1).ToString(CultureInfo.InvariantCulture)
            : "Lens " + opticsId;

    /// <summary>The ids of the optics with the capability focus (a missing id counts as "0").</summary>
    public static IReadOnlyList<string> LensesWithFocus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("optics", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lenses = new List<string>();
        foreach (var optics in list.EnumerateArray())
        {
            if (optics.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                && caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "focus"))
            {
                var id = optics.TryGetProperty("opticsId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                lenses.Add(string.IsNullOrEmpty(id) ? "0" : id);
            }
        }

        return lenses;
    }

    /// <summary>The plain text of an Optics Control error answer, or null.</summary>
    public static string? ErrorOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var error))
        {
            return null;
        }

        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        return code switch
        {
            FocusNotFound => FocusNotFoundText,
            1200 => "The camera is too hot or too cold to move the lens.",
            _ => "The camera refused the autofocus: " + (message ?? "error " + code.ToString(CultureInfo.InvariantCulture)).TrimEnd('.') + ".",
        };
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(IVapixClient vapix, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpticsPath) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        return (response.StatusCode, body);
    }
}
