using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

// Stdio bootstrap trusts the operating system's CA store. The launcher supplies the ordinary OADM key.
var token = Environment.GetEnvironmentVariable("OADM_MCP_KEY");
if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint)
    || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.AbsolutePath != "/mcp"
    || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
    || token is not { Length: 48 } || !token.StartsWith("oadm_", StringComparison.Ordinal)
    || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
{
    Console.Error.WriteLine("Supply an HTTPS OADM /mcp endpoint and an OADM access key.");
    return 2;
}

using var ca = Environment.GetEnvironmentVariable("OADM_MCP_CA") is { Length: > 0 } caPath
    ? X509Certificate2.CreateFromPem(File.ReadAllText(caPath)) : null;
using var http = CreateTransport(ca);
http.Timeout = TimeSpan.FromSeconds(60);
while (await Console.In.ReadLineAsync() is { } line)
{
    try
    {
        using var json = JsonDocument.Parse(line);
        var message = json.RootElement;
        var method = message.GetProperty("method").GetString();
        if (!message.TryGetProperty("id", out var id))
            continue;
        if (method == "initialize")
        {
            var protocol = message.GetProperty("params").GetProperty("protocolVersion").GetString();
            await WriteAsync(new
            {
                jsonrpc = "2.0", id,
                result = new { protocolVersion = protocol, capabilities = new { tools = new { }, resources = new { } },
                    serverInfo = new { name = "OADM", version = "1.0.0" } }
            });
            continue;
        }
        if (method == "ping")
        {
            await WriteAsync(new { jsonrpc = "2.0", id, result = new { } });
            continue;
        }
        if (method is not ("tools/list" or "tools/call" or "resources/list" or "resources/read"))
        {
            await FailAsync(id, -32601, "Method not supported.");
            continue;
        }
        var outgoing = JsonNode.Parse(line)!.AsObject();
        var parameters = outgoing["params"]?.AsObject() ?? [];
        if (outgoing["params"] is null)
            outgoing["params"] = parameters;
        var metadata = parameters["_meta"]?.AsObject() ?? [];
        if (parameters["_meta"] is null)
            parameters["_meta"] = metadata;
        metadata["io.modelcontextprotocol/protocolVersion"] = "2026-07-28";
        metadata["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "OADM desktop bridge", ["version"] = "1.0.0" };
        metadata["io.modelcontextprotocol/clientCapabilities"] = new JsonObject();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (method == "tools/call")
            request.Headers.Add("Mcp-Name", message.GetProperty("params").GetProperty("name").GetString());
        request.Content = new StringContent(outgoing.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request);
            // Never forward HTML, redirect locations or upstream authentication errors.
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                await WriteAsync(reply.RootElement);
            }
            else if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var stream = new StreamReader(await response.Content.ReadAsStreamAsync());
                var data = new StringBuilder();
                while (await stream.ReadLineAsync() is { Length: > 0 } field)
                {
                    if (field.StartsWith("data:", StringComparison.Ordinal))
                        data.AppendLine(field[5..].TrimStart());
                }
                using var reply = JsonDocument.Parse(data.ToString());
                await WriteAsync(reply.RootElement);
            }
            else
                await FailAsync(id, -32000, response.StatusCode == HttpStatusCode.Unauthorized
                    ? "The OADM access key was refused." : "OADM could not complete the request.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await FailAsync(id, -32000, "OADM could not complete the request.");
        }
    }
    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException or FormatException)
    {
        await FailAsync(null, -32600, "Invalid request.");
    }
}
return 0;

static Task WriteAsync(object output) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(output));
static Task FailAsync(object? id, int code, string message) => WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } });


[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
    Justification = "The connector talks to the configured OADM MCP server, never to a device.")]
static HttpClient CreateTransport(X509Certificate2? ca)
{
    var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
    if (ca is not null)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(ca);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        handler.SslOptions.CertificateChainPolicy = policy;
    }
    return new HttpClient(handler);
}
