using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications;

/// <summary>
/// Event rule action "Send HTTP request": any method, URL, query parameters, headers, login
/// (none, Basic, Digest, Bearer token), a body with a format (JSON, XML, text, form) and "Allow untrusted certificates".
/// Every text takes the placeholders; their values are escaped for the part they go into (URL-encoded in the URL and the
/// query, JSON or XML escaped in the body), so "objectId": "{objectId}" stays valid JSON. Runs once per rule edge, no
/// device. Success = any 2xx answer.
/// </summary>
public sealed class SendHttpAction : IActionPlugin
{
    public const string ActionId = "oadm.notifications.http";

    /// <summary>Most characters of the answer kept for an error message.</summary>
    private const int ExcerptLength = 200;

    public string Id => ActionId;

    public string DisplayName => "Send HTTP request";

    public string? IconKey => "send";

    public string Group => "Notifications";

    public string? Description => "Sends a request to a web service, for example a webhook of Teams, Slack or a building system.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Choice("method", "Method", [new("GET", "GET"), new("POST", "POST"), new("PUT", "PUT"), new("PATCH", "PATCH"), new("DELETE", "DELETE")], "POST"),
        ActionField.Text("url", "URL", required: true),
        ActionField.KeyValues("query", "Query parameters"),
        ActionField.KeyValues("headers", "Headers"),
        ActionField.Choice("auth", "Login", [new("none", "None"), new("basic", "Basic"), new("digest", "Digest"), new("bearer", "Bearer token")], "none"),
        ActionField.Text("user", "User name") with { VisibleWhenKey = "auth", VisibleWhenValue = "basic|digest" },
        ActionField.Password("password", "Password") with { VisibleWhenKey = "auth", VisibleWhenValue = "basic|digest" },
        ActionField.Password("token", "Token") with { VisibleWhenKey = "auth", VisibleWhenValue = "bearer", MaxLength = 4096 },
        ActionField.Choice("bodyType", "Body type", [new("none", "None"), new("json", "JSON"), new("xml", "XML"), new("text", "Text"), new("form", "Form")], "json"),
        ActionField.Code("body", "Body", languageKey: "bodyType") with
        {
            VisibleWhenKey = "bodyType",
            VisibleWhenValue = "json|xml|text|form",
            Default = "{\n  \"rule\": \"{rule}\",\n  \"event\": \"{event}\",\n  \"device\": \"{device}\",\n  \"state\": \"{state}\",\n  \"time\": \"{time}\"\n}",
            Hint = "Form: one name=value per line.",
        },
        ActionField.Bool("allowUntrusted", "Allow untrusted certificates"),
        ActionField.Number("timeout", "Timeout (s)", 1, 120, 15),
        ActionField.Bool("onOff", "Also send when the rule turns off") with { OffEdge = true },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var invocation = new ActionInvocation(Guid.Empty, string.Empty, ActionEdge.Fired, settings, null,
            new ActionTrigger(string.Empty, string.Empty, null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var url = invocation.Get("url")?.Trim() ?? string.Empty;
        if (url.Length > 0 && !url.StartsWith('{') && !IsHttpUrl(url))
        {
            errors["url"] = "Enter a URL that starts with http:// or https://.";
        }

        if (invocation.Get("auth") is "basic" or "digest" && string.IsNullOrWhiteSpace(invocation.Get("user")))
        {
            errors["user"] = "Enter the user name.";
        }

        foreach (var header in invocation.GetPairs("headers"))
        {
            if (!IsToken(header.Key))
            {
                errors["headers"] = $"\"{header.Key}\" is not a valid header name.";
                break;
            }
        }

        if (invocation.Get("bodyType") == "json" && invocation.Get("body") is { Length: > 0 } body && !LooksLikeJson(body))
        {
            errors["body"] = "The body is not valid JSON.";
        }

        return errors;
    }

    public string Describe(JsonElement settings)
    {
        var invocation = new ActionInvocation(Guid.Empty, string.Empty, ActionEdge.Fired, settings, null,
            new ActionTrigger(string.Empty, string.Empty, null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));
        return $"{invocation.Get("method") ?? "POST"} {invocation.Get("url")}";
    }

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("onOff", out var on) && on.ValueKind == JsonValueKind.True);

    public TimeSpan? MaxRunTime(JsonElement settings)
    {
        var seconds = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var n) ? n : 15;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120) + 5);
    }

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var url = ctx.Text(invocation.Get("url")?.Trim(), PlaceholderEscape.Url);
        if (!IsHttpUrl(url))
        {
            throw new InvalidOperationException($"\"{url}\" is not an http:// or https:// URL. Nothing was sent.");
        }

        var query = invocation.GetPairs("query")
            .Select(p => Uri.EscapeDataString(ctx.Text(p.Key)) + "=" + Uri.EscapeDataString(ctx.Text(p.Value)))
            .ToList();
        if (query.Count > 0)
        {
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join("&", query);
        }

        var uri = new Uri(url);
        var auth = invocation.Get("auth") ?? "none";
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        if (invocation.GetBool("allowUntrusted"))
        {
#pragma warning disable CA5359 // The administrator turned on "Allow untrusted certificates" for this action.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        if (auth == "digest")
        {
            handler.Credentials = new CredentialCache
            {
                { new Uri(uri.GetLeftPart(UriPartial.Authority)), "Digest", new NetworkCredential(invocation.Get("user") ?? string.Empty, invocation.Get("password") ?? string.Empty) },
            };
        }

        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(new HttpMethod(invocation.Get("method") ?? "POST"), uri);
        var bodyType = invocation.Get("bodyType") ?? "none";
        if (bodyType != "none")
        {
            var body = invocation.Get("body") ?? string.Empty;
            request.Content = bodyType switch
            {
                "json" => new StringContent(ctx.Text(body, PlaceholderEscape.Json), Encoding.UTF8, "application/json"),
                "xml" => new StringContent(ctx.Text(body, PlaceholderEscape.Xml), Encoding.UTF8, "application/xml"),
                "form" => new StringContent(Form(ctx, body), Encoding.UTF8, "application/x-www-form-urlencoded"),
                _ => new StringContent(ctx.Text(body), Encoding.UTF8, "text/plain"),
            };
        }

        foreach (var (name, template) in invocation.GetPairs("headers"))
        {
            var value = ctx.Text(template).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                if (request.Content is null)
                {
                    request.Content = new ByteArrayContent([]);
                }

                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (auth == "basic")
        {
            var pair = Encoding.UTF8.GetBytes((invocation.Get("user") ?? string.Empty) + ":" + (invocation.Get("password") ?? string.Empty));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(pair));
        }
        else if (auth == "bearer")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", invocation.Get("token") ?? string.Empty);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(invocation.GetInt("timeout", 15), 1, 120)));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Timeout after {invocation.GetInt("timeout", 15)} s - {uri.Host} did not answer"));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"{uri.Host} cannot be reached: {Inner(ex)}", ex);
        }

        using (response)
        {
            var status = string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}").TrimEnd();
            if (response.IsSuccessStatusCode)
            {
                ctx.Log(status);
                return;
            }

            var text = await ReadExcerptAsync(response, timeout.Token).ConfigureAwait(false);
            throw new InvalidOperationException(text.Length == 0 ? status : status + ": " + text);
        }
    }

    /// <summary>"name=value" lines (or name=value&amp;...) with the values URL-encoded.</summary>
    private static string Form(IActionContext ctx, string body) =>
        string.Join("&", body.Split(['\n', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                var name = eq < 0 ? line : line[..eq];
                var value = eq < 0 ? string.Empty : line[(eq + 1)..];
                return Uri.EscapeDataString(ctx.Text(name.Trim())) + "=" + ctx.Text(value, PlaceholderEscape.Url);
            }));

    private static async Task<string> ReadExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[4096];
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(buffer, 0, read).ReplaceLineEndings(" ").Trim();
                return text.Length > ExcerptLength ? text[..ExcerptLength] + "…" : text;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    private static string Inner(Exception ex) => (ex.InnerException ?? ex).Message;

    internal static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;

    private static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => c is > ' ' and < (char)127 && !"()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal));

    /// <summary>Checks the JSON with every placeholder taken as a short text (a placeholder may stand for a number too).</summary>
    private static bool LooksLikeJson(string body)
    {
        var sample = ActionPlaceholders.Fill(body, _ => "0");
        try
        {
            using var _ = JsonDocument.Parse(sample);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
