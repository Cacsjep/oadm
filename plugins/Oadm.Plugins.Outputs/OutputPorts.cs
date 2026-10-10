using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Outputs;

public enum OutputCommand
{
    Active,
    Inactive,
    Pulse,
    Toggle,
}

/// <summary>Switches an output port. <c>port</c> is the number the user sees (1 = the first port).</summary>
public interface IOutputPorts
{
    /// <summary>Returns a short result for the event log ("Output 1 active").</summary>
    Task<string> SetAsync(int port, OutputCommand command, TimeSpan pulse, CancellationToken ct);
}

/// <summary>
/// I/O port management API (<c>io-port-management</c> 1.x, <c>/axis-cgi/io/portmanagement.cgi</c>, JSON): getPorts,
/// setPorts and setStateSequence. Ports are numbered from "0"; a port is active when its state differs from its normal
/// state (open / closed). Verified on an AXIS A9210 (AXIS OS 12.10.73.2, I/O port management 1.1): setPorts and
/// setStateSequence switch the relay, the sequence ends in the normal state.
/// </summary>
public sealed class PortManagementApi(IVapixClient vapix) : IOutputPorts
{
    public const string ApiId = "io-port-management";
    public const string Path = "/axis-cgi/io/portmanagement.cgi";

    public async Task<string> SetAsync(int port, OutputCommand command, TimeSpan pulse, CancellationToken ct)
    {
        var id = (port - 1).ToString(CultureInfo.InvariantCulture);
        var data = await CallAsync("getPorts", null, ct).ConfigureAwait(false);
        var item = data?["items"]?.AsArray().FirstOrDefault(p => p?["port"]?.ToString() == id)
            ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The device has no port {port}."));
        if (!string.Equals(item["direction"]?.ToString(), "output", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Port {port} is an input. Set it to output in the device's I/O settings."));
        }

        var normal = item["normalState"]?.ToString() ?? "open";
        var label = item["name"]?.ToString() is { Length: > 0 } name
            ? string.Create(CultureInfo.InvariantCulture, $"Output {port} ({name})")
            : string.Create(CultureInfo.InvariantCulture, $"Output {port}");
        var activeState = normal == "open" ? "closed" : "open";
        var isActive = !string.Equals(item["state"]?.ToString(), normal, StringComparison.OrdinalIgnoreCase);
        switch (command)
        {
            case OutputCommand.Pulse:
                await CallAsync("setStateSequence", new JsonObject
                {
                    ["port"] = id,
                    ["sequence"] = new JsonArray(
                        new JsonObject { ["state"] = activeState, ["time"] = (int)pulse.TotalMilliseconds },
                        new JsonObject { ["state"] = normal, ["time"] = 0 }),
                }, ct).ConfigureAwait(false);
                return string.Create(CultureInfo.InvariantCulture, $"{label} pulsed for {pulse.TotalSeconds:0} s");
            default:
                var active = command switch
                {
                    OutputCommand.Active => true,
                    OutputCommand.Inactive => false,
                    _ => !isActive,
                };
                await CallAsync("setPorts", new JsonObject
                {
                    ["ports"] = new JsonArray(new JsonObject { ["port"] = id, ["state"] = active ? activeState : normal }),
                }, ct).ConfigureAwait(false);
                return $"{label} {(active ? "active" : "inactive")}";
        }
    }

    private async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var body = new JsonObject { ["apiVersion"] = "1.0", ["context"] = "oadm", ["method"] = method };
        if (parameters is not null)
        {
            body["params"] = parameters;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
        }

        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The device sent an answer that is not JSON.");
        }

        if (answer?["error"] is { } error)
        {
            throw new InvalidOperationException(error["message"]?.ToString() ?? "The device refused the request.");
        }

        return answer?["data"];
    }
}

/// <summary>
/// The older port.cgi: <c>action=1:/</c> (active), <c>1:\</c> (inactive), <c>1:/5000\</c> (pulse, ms; the answer comes
/// after the pulse) and <c>checkactive=1</c> ("port1=active") for Toggle. Port numbers start at 1 (port 1 = port "0" of the
/// port management API). Verified on an AXIS A9210 (AXIS OS 12.10.73.2), which refuses <c>checkoutput</c>.
/// </summary>
public sealed class PortCgi(IVapixClient vapix) : IOutputPorts
{
    public const string Path = "/axis-cgi/io/port.cgi";

    public async Task<string> SetAsync(int port, OutputCommand command, TimeSpan pulse, CancellationToken ct)
    {
        var n = port.ToString(CultureInfo.InvariantCulture);
        if (command == OutputCommand.Pulse)
        {
            await GetAsync($"action={n}:/{(int)pulse.TotalMilliseconds}%5C", ct, pulse + TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return string.Create(CultureInfo.InvariantCulture, $"Output {port} pulsed for {pulse.TotalSeconds:0} s");
        }

        var active = command switch
        {
            OutputCommand.Active => true,
            OutputCommand.Inactive => false,
            _ => !(await GetAsync($"checkactive={n}", ct).ConfigureAwait(false)).Contains("=active", StringComparison.OrdinalIgnoreCase),
        };
        await GetAsync($"action={n}:{(active ? "/" : "%5C")}", ct).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"Output {port} {(active ? "active" : "inactive")}");
    }

    private async Task<string> GetAsync(string query, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path + "?" + query);
        if (timeout is { } wait)
        {
            request.Options.Set(VapixRequestOptions.Timeout, wait);
        }

        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
        }

        if (text.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(text.Trim().Split('\n')[0]);
        }

        return text;
    }
}
