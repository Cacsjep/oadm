using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Oadm.Plugins.VapixCommander.Library.Tests.Hardware;

/// <summary>
/// Executes every READ-ONLY library command (writes:false) whose requires are met on each dev camera, with default
/// field values, and checks HTTP 2xx, no VAPIX error and that every response.extract resolves.
/// Commands with writes:true are never sent. Set OADM_RECORD_FIXTURES to a folder to save the responses.
/// </summary>
[Trait("Category", "Hardware")]
public sealed partial class ReaderHardwareTests(ITestOutputHelper output)
{
    /// <summary>
    /// Readers whose API is listed by the device but that legitimately answer with a VAPIX error on that model.
    /// They are still sent; the test asserts that the device answers with the documented error.
    /// </summary>
    /// <summary>Device errors that are a correct answer for some models (the reader succeeds on the others).</summary>
    private static readonly Dictionary<string, string> AcceptedDeviceErrors = new(StringComparer.Ordinal)
    {
        // AXIS P3265-V lists light-control but has no IR/white light: error 1005 "No light hardware found".
        ["image.light.info"] = "code 1005",
    };

    /// <summary>Readers of I/O ports: skipped on a camera without any (AXIS P12 Mk III: port.cgi HTTP 400, "No such port").</summary>
    private static readonly HashSet<string> NeedIoPorts = new(StringComparer.Ordinal) { "io.ports.active", "io.supervised.get" };

    [GeneratedRegex("camera|encoder|video|intercom|door station", RegexOptions.IgnoreCase)]
    private static partial Regex VideoProductRegex();

    [HardwareFact]
    public async Task Every_reader_succeeds_on_the_dev_cameras()
    {
        var failures = new List<string>();
        var recordDir = Environment.GetEnvironmentVariable("OADM_RECORD_FIXTURES");

        foreach (var camera in DevCameras.All)
        {
            using var http = CreateClient(camera);
            var apis = await GetApiListAsync(http);
            var productType = await GetProductTypeAsync(http);
            var hasVideo = VideoProductRegex().IsMatch(productType);
            var hasIoPorts = await HasIoPortsAsync(http);
            output.WriteLine($"{camera}: {apis.Count} APIs, ProdType '{productType}'{(hasIoPorts ? string.Empty : ", no I/O ports")}");

            var passed = 0;
            foreach (var command in CommandLibrary.AllCommands.Where(c => !CommandLibrary.Writes(c)))
            {
                var id = CommandLibrary.Id(command);
                // Safety net: a reader must never be a write, see LibraryTests.Readers_use_GET_or_read_methods_only.
                Assert.False(CommandLibrary.Writes(command));

                var unmet = command["requires"]!.AsArray().Select(r => r!.AsObject())
                    .Where(r => !IsMet(apis, r["api"]!.GetValue<string>(), r["minVersion"]!.GetValue<string>()))
                    .Select(r => $"{r["api"]} {r["minVersion"]}")
                    .ToList();
                if (unmet.Count > 0 || (command["hasVideoOnly"]?.GetValue<bool>() == true && !hasVideo))
                {
                    output.WriteLine($"  SKIP {id}: requires {string.Join(", ", unmet)}{(hasVideo ? string.Empty : " / video")}");
                    continue;
                }

                if (NeedIoPorts.Contains(id) && !hasIoPorts)
                {
                    output.WriteLine($"  SKIP {id}: no I/O ports");
                    continue;
                }

                var values = CommandRendering.Defaults(command);
                var rendered = CommandRendering.Render(command, values);
                var (status, body) = await SendAsync(http, rendered);
                var error = CommandRendering.Interpret(command, status, body);

                if (error is not null && AcceptedDeviceErrors.TryGetValue(id, out var accepted) && error.Contains(accepted, StringComparison.Ordinal))
                {
                    output.WriteLine($"  OK   {id} (the model's answer: {error})");
                    continue;
                }

                if (error is not null)
                {
                    failures.Add($"{camera} {id} [{rendered.Method} {rendered.PathAndQuery}]: {error}");
                    continue;
                }

                var missing = CommandLibrary.Extracts(command)
                    .Where(e => e["optional"]?.GetValue<bool>() != true && CommandRendering.Extract(e, body, command, values) is null)
                    .Select(e => e["label"]!.GetValue<string>())
                    .ToList();
                if (missing.Count > 0)
                {
                    failures.Add($"{camera} {id}: extract did not resolve: {string.Join(", ", missing)}");
                    continue;
                }

                passed++;
                var shown = string.Join("; ", CommandLibrary.Extracts(command).Take(3).Select(e =>
                    $"{e["label"]}={Short(CommandRendering.Extract(e, body, command, values))}"));
                output.WriteLine($"  OK   {id} (HTTP {status}) {shown}");

                if (!string.IsNullOrWhiteSpace(recordDir))
                {
                    Directory.CreateDirectory(recordDir);
                    await File.WriteAllTextAsync(Path.Combine(recordDir, id + ".txt"), body);
                }
            }

            output.WriteLine($"{camera}: {passed} readers passed");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Whether the camera has any I/O port (param.cgi IOPort group; an AXIS P12 Mk III has none).</summary>
    private static async Task<bool> HasIoPortsAsync(HttpClient http)
    {
        var body = await http.GetStringAsync(new Uri("axis-cgi/param.cgi?action=list&group=IOPort", UriKind.Relative));
        return body.Contains("root.IOPort.I", StringComparison.Ordinal);
    }

    private static string Short(string? value) => value is null ? "?" : value.Length <= 60 ? value : value[..60] + "...";

    private static bool IsMet(Dictionary<string, List<Version>> apis, string api, string minVersion)
    {
        // "any": the device must list the API, in any version (like the plugin's AnyVersion).
        if (minVersion == "any")
        {
            return apis.ContainsKey(api);
        }

        var min = Version.Parse(minVersion);
        return apis.TryGetValue(api, out var versions) && versions.Any(v => v >= min);
    }

    private static HttpClient CreateClient(DevCamera camera)
    {
        var baseUri = new Uri($"{camera.EffectiveScheme}://{camera.Address}/");
        var credentials = new CredentialCache
        {
            { baseUri, "Digest", new NetworkCredential(camera.User, camera.Password) },
        };
        if (camera.EffectiveScheme == Uri.UriSchemeHttps)
        {
            // Basic only over TLS, as in OADM.
            credentials.Add(baseUri, "Basic", new NetworkCredential(camera.User, camera.Password));
        }

#pragma warning disable CA2000 // disposed with the client
        var handler = new HttpClientHandler
        {
            Credentials = credentials,
            PreAuthenticate = false,
            // Dev cameras use self-signed certificates.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
#pragma warning restore CA2000
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<(int Status, string Body)> SendAsync(HttpClient http, RenderedRequest request)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.PathAndQuery.TrimStart('/'));
        if (request.Body is not null)
        {
            var mediaType = request.BodyType switch
            {
                "json" => "application/json",
                "form" => "application/x-www-form-urlencoded",
                "xml" => "application/xml",
                _ => "text/plain",
            };
            message.Content = new StringContent(request.Body, Encoding.UTF8, mediaType);
        }

        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && message.Content is not null)
            {
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var response = await http.SendAsync(message, cts.Token);
        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        return ((int)response.StatusCode, Encoding.UTF8.GetString(bytes));
    }

    private static async Task<Dictionary<string, List<Version>>> GetApiListAsync(HttpClient http)
    {
        using var content = new StringContent("""{"apiVersion":"1.0","method":"getApiList"}""", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("axis-cgi/apidiscovery.cgi", content);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsByteArrayAsync())!; // devices send "charset=utf8", which .NET rejects
        var result = new Dictionary<string, List<Version>>(StringComparer.Ordinal);
        foreach (var api in json["data"]!["apiList"]!.AsArray())
        {
            var id = api!["id"]!.GetValue<string>();
            if (Version.TryParse(api["version"]!.GetValue<string>(), out var version))
            {
                (result.TryGetValue(id, out var list) ? list : result[id] = []).Add(version);
            }
        }

        return result;
    }

    private static async Task<string> GetProductTypeAsync(HttpClient http)
    {
        using var content = new StringContent("""{"apiVersion":"1.0","method":"getAllProperties"}""", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("axis-cgi/basicdeviceinfo.cgi", content);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsByteArrayAsync())!; // devices send "charset=utf8", which .NET rejects
        return json["data"]?["propertyList"]?["ProdType"]?.GetValue<string>() ?? string.Empty;
    }
}
