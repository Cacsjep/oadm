using System.Net;

using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Testing;

/// <summary>
/// A camera for plugin tests. It knows param.cgi (<see cref="Parameters"/>: list by group, <c>action=update</c> in the
/// query or the POST body), basicdeviceinfo (<see cref="Info"/>), the API list (<see cref="Apis"/>) and restart; every
/// other request is answered by <see cref="Respond"/>, else with <see cref="Status"/> and "OK". Every request is recorded
/// in <see cref="Requests"/> as "METHOD /path?query".
/// </summary>
public class FakeCamera : IVapixClient
{
    private readonly List<string> _requests = [];

    /// <inheritdoc />
    public Uri BaseAddress { get; init; } = new("https://192.0.2.10/");

    /// <summary>The device's parameters (param.cgi), e.g. <c>["Network.UPnP.FriendlyName"] = "Entrance"</c>.</summary>
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.Ordinal);

    /// <summary>The answer of the API list (<see cref="GetApiListAsync"/>).</summary>
    public IReadOnlyList<DeviceApi> Apis { get; set; } = [new("param-cgi", "1.0")];

    /// <summary>The answer of basicdeviceinfo.</summary>
    public BasicDeviceInfo Info { get; set; } = new("B8A44F631339", "P3265-V", "AXIS P3265-V", "AXIS P3265-V Dome Camera", "12.11.77", null, "aarch64", "Dome Camera");

    /// <summary>Answers requests that are not param.cgi; null = <see cref="Status"/> with "OK".</summary>
    public Func<HttpRequestMessage, string?, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>The status of answers without <see cref="Respond"/> (also param.cgi updates).</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Every request so far, "METHOD /path?query" (the API list, basicdeviceinfo and parameter lists too).</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>How many param.cgi updates were sent.</summary>
    public int Writes { get; private set; }

    /// <summary>How often basicdeviceinfo was read.</summary>
    public int Reads { get; private set; }

    /// <summary>How often the camera was restarted.</summary>
    public int Restarts { get; private set; }

    /// <inheritdoc />
    public virtual Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        Record("POST /axis-cgi/basicdeviceinfo.cgi");
        Reads++;
        return Task.FromResult(Info);
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var list = groups.ToList();
        Record("GET /axis-cgi/param.cgi?action=list&group=" + string.Join(",", list));
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            Parameters.Where(p => list.Exists(g => p.Key.StartsWith(g, StringComparison.Ordinal))).ToDictionary(p => p.Key, p => p.Value));
    }

    /// <inheritdoc />
    public virtual Task RestartAsync(CancellationToken ct)
    {
        Record("GET /axis-cgi/restart.cgi");
        Restarts++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        Record("POST /axis-cgi/apidiscovery.cgi");
        return Task.FromResult(Apis);
    }

    /// <inheritdoc />
    public virtual async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? new Uri("/", UriKind.Relative);
        var pathAndQuery = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
        Record($"{request.Method} {pathAndQuery}");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (Respond?.Invoke(request, body) is { } answer)
        {
            return answer;
        }

        var path = pathAndQuery.Split('?')[0];
        if (path == "/axis-cgi/param.cgi")
        {
            var query = pathAndQuery.Contains('?', StringComparison.Ordinal) ? pathAndQuery.Split('?', 2)[1] : "";
            var form = request.Method == HttpMethod.Post ? body ?? "" : query;
            if (Form(form).TryGetValue("action", out var action) && action == "update")
            {
                foreach (var (key, value) in Form(form).Where(p => p.Key != "action"))
                {
                    Parameters[key] = value;
                }

                Writes++;
            }
        }

        return new HttpResponseMessage(Status) { Content = new StringContent("OK") };
    }

    private void Record(string line)
    {
        lock (_requests)
        {
            _requests.Add(line);
        }
    }

    private static Dictionary<string, string> Form(string form) =>
        form.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last() is { Length: 2 } pair ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
}
