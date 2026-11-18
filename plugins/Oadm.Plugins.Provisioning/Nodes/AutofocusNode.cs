using System.Net;
using System.Text;
using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Autofocus (spec node 12): the Optics Control API that the live view's focus uses (<c>axis-cgi/opticscontrol.cgi</c>, API
/// <c>optics-control</c>): checked in a fresh API list and the optics' capabilities, then <c>performAutofocus</c>, which
/// answers when the search ended; at most <see cref="ProvisioningOptions.AutofocusTimeout"/>. A camera without remote focus:
/// Not needed. Nothing else is written.
/// </summary>
public sealed class AutofocusNode : DeviceNode<AutofocusSettings>
{
    public const string OpticsApi = "optics-control";
    public const string OpticsPath = "/axis-cgi/opticscontrol.cgi";

    /// <summary>The camera found no focus (error 1300).</summary>
    public const int FocusNotFound = 1300;

    public override NodeKind Kind => NodeKind.Autofocus;

    public override string Name => "Autofocus";

    public override object DefaultSettings => new AutofocusSettings();

    protected override string? CheckSettings(AutofocusSettings settings, ProfileCheckContext context) => null;

    protected override async Task<NodeResult> RunOnAsync(AutofocusSettings settings, NodeRun run, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var timeout = run.Host.Options.AutofocusTimeout;
        using var limit = new CancellationTokenSource(timeout, run.Host.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            var vapix = await run.Host.Vapix.CreateAsync(deviceId, linked.Token).ConfigureAwait(false);
            var apis = await vapix.GetApiListAsync(linked.Token).ConfigureAwait(false);
            if (!apis.Any(a => string.Equals(a.Id, OpticsApi, StringComparison.Ordinal)))
            {
                return NodeResult.NotNeeded("no remote focus.");
            }

            var (status, capabilities) = await PostAsync(vapix, """{"apiVersion":"1","method":"getCapabilities"}""", linked.Token).ConfigureAwait(false);
            if (status == HttpStatusCode.NotFound || OpticsWithFocus(capabilities) is not { } opticsId)
            {
                return NodeResult.NotNeeded("no remote focus.");
            }

            var request = JsonSerializer.Serialize(new { apiVersion = "1", method = "performAutofocus", @params = new { optics = new[] { new { opticsId } } } });
            var (_, answer) = await PostAsync(vapix, request, linked.Token).ConfigureAwait(false);
            return ErrorOf(answer) is { } error ? NodeResult.Error(error) : NodeResult.Success("Focus found.");
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return NodeResult.Error($"The autofocus did not end within {timeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            return NodeResult.Error("The camera did not answer the autofocus: " + ex.Message.TrimEnd('.') + ".");
        }
    }

    /// <summary>The id of the first optics with focus, or null (a fixed lens, no optics).</summary>
    public static string? OpticsWithFocus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("optics", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var optics in list.EnumerateArray())
        {
            if (optics.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                && caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "focus"))
            {
                return optics.TryGetProperty("opticsId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : "0";
            }
        }

        return null;
    }

    /// <summary>The plain text of an Optics Control error answer, or null.</summary>
    public static string? ErrorOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("error", out var error))
        {
            return null;
        }

        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
        return code switch
        {
            FocusNotFound => "The camera could not find focus. Set focus by hand.",
            1200 => "The camera is too hot or too cold to move the lens.",
            _ => "The camera refused the autofocus: " + (error.TryGetProperty("message", out var m) ? m.GetString() : null ?? "error " + code) + ".",
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
