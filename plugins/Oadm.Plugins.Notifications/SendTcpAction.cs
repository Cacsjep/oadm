using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications;

/// <summary>
/// Event rule action "Send TCP message": opens a connection to a host and port, writes one message and closes it, for
/// building, alarm and video systems that listen on a plain socket. The message is text, JSON or XML with the
/// placeholders (values escaped for the format) or hex bytes ("02 41 03"); an optional line end follows it. Optional TLS
/// with "Allow untrusted certificates". "Wait for an answer" reads what the other side sends back and, with "Answer must
/// contain", fails when that text does not come. Runs once per rule edge, no device.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The user's own servers on the server's network, not devices.")]
public sealed class SendTcpAction : IActionPlugin
{
    public const string ActionId = "oadm.notifications.tcp";

    /// <summary>Most bytes of an answer read.</summary>
    private const int MaxAnswerBytes = 4096;

    /// <summary>Most characters of the answer kept for the log or an error message.</summary>
    private const int ExcerptLength = 200;

    public string Id => ActionId;

    public string DisplayName => "Send TCP message";

    public string? IconKey => "send";

    public string Group => "Notifications";

    public string? Description => "Sends a message over a TCP connection, for example to a building, alarm or video system.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public bool UsesPlaceholders => true;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Text("host", "Host", required: true) with { Hint = "Host name or IP address" },
        ActionField.Number("port", "Port", 1, 65535, 8000),
        ActionField.Choice("format", "Format", [new("text", "Text"), new("json", "JSON"), new("xml", "XML"), new("hex", "Hex bytes")], "text"),
        ActionField.Code("message", "Message", languageKey: "format", required: true) with
        {
            Default = "{event} on {device} at {time}",
            Hint = "For hex bytes: pairs like 02 41 42 03, spaces optional.",
        },
        ActionField.Choice("lineEnd", "Line end", [new("none", "None"), new("cr", "CR"), new("lf", "LF"), new("crlf", "CR LF")], "none") with
        {
            VisibleWhenKey = "format",
            VisibleWhenValue = "text|json|xml",
        },
        ActionField.Bool("tls", "Use TLS"),
        ActionField.Bool("allowUntrusted", "Allow untrusted certificates") with { VisibleWhenKey = "tls", VisibleWhenValue = "true" },
        ActionField.Bool("waitAnswer", "Wait for an answer"),
        ActionField.Text("expect", "Answer must contain") with { VisibleWhenKey = "waitAnswer", VisibleWhenValue = "true", Hint = "Optional" },
        ActionField.Number("timeout", "Timeout (s)", 1, 120, 10),
        ActionField.Bool("onOff", "Also send when the rule turns off") with { OffEdge = true },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var invocation = Settings(settings);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (invocation.Get("host") is { Length: > 0 } host && !host.Contains('{', StringComparison.Ordinal)
            && Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
        {
            errors["host"] = "Enter a host name or IP address.";
        }

        var message = invocation.Get("message") ?? string.Empty;
        switch (invocation.Get("format"))
        {
            case "hex" when Hex(message) is null:
                errors["message"] = "Enter hex bytes, for example 02 41 42 03.";
                break;
            case "json" when message.Length > 0 && !LooksLikeJson(message):
                errors["message"] = "The message is not valid JSON.";
                break;
        }

        return errors;
    }

    public string Describe(JsonElement settings)
    {
        var invocation = Settings(settings);
        return string.Create(CultureInfo.InvariantCulture, $"TCP to {invocation.Get("host")}:{invocation.GetInt("port", 8000)}");
    }

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("onOff", out var on) && on.ValueKind == JsonValueKind.True);

    public TimeSpan? MaxRunTime(JsonElement settings)
    {
        var seconds = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var n) ? n : 10;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120) + 5);
    }

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var host = ctx.Text(invocation.Get("host")).Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException($"\"{host}\" is not a host name or IP address. Nothing was sent.");
        }

        var port = Math.Clamp(invocation.GetInt("port", 8000), 1, 65535);
        var bytes = Message(ctx, invocation);
        var seconds = Math.Clamp(invocation.GetInt("timeout", 10), 1, 120);
        var target = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var client = new TcpClient { NoDelay = true };
        string? answer = null;
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                if (invocation.GetBool("tls"))
                {
                    var allowUntrusted = invocation.GetBool("allowUntrusted");
#pragma warning disable CA5359 // The administrator turned on "Allow untrusted certificates" for this action.
                    var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, errors) => allowUntrusted || errors == SslPolicyErrors.None);
#pragma warning restore CA5359
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token).ConfigureAwait(false);
                    stream = tls;
                }

                await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (invocation.GetBool("waitAnswer"))
                {
                    answer = await ReadAnswerAsync(stream, invocation.Get("expect"), timeout.Token).ConfigureAwait(false);
                }

                if (stream is SslStream secure)
                {
                    await secure.ShutdownAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(answer is null && invocation.GetBool("waitAnswer") && client.Connected
                ? string.Create(CultureInfo.InvariantCulture, $"Timeout after {seconds} s - {target} sent no matching answer")
                : string.Create(CultureInfo.InvariantCulture, $"Timeout after {seconds} s - {target} did not answer"));
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            throw new InvalidOperationException($"The certificate of {target} is not trusted (turn on \"Allow untrusted certificates\" or check TLS): {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new InvalidOperationException($"{target} cannot be reached: {ex.Message}", ex);
        }

        ctx.Log(answer is null
            ? string.Create(CultureInfo.InvariantCulture, $"Sent {bytes.Length} bytes to {target}")
            : string.Create(CultureInfo.InvariantCulture, $"Sent {bytes.Length} bytes to {target}, answer: {Excerpt(answer)}"));
    }

    /// <summary>The bytes to send: hex, or the text with its values escaped for the format, UTF-8, plus the line end.</summary>
    internal static byte[] Message(IActionContext ctx, ActionInvocation invocation)
    {
        var message = invocation.Get("message") ?? string.Empty;
        var format = invocation.Get("format") ?? "text";
        if (format == "hex")
        {
            return Hex(message) ?? throw new InvalidOperationException("The message is not valid hex bytes. Nothing was sent.");
        }

        var text = format switch
        {
            "json" => ctx.Text(message, PlaceholderEscape.Json),
            "xml" => ctx.Text(message, PlaceholderEscape.Xml),
            _ => ctx.Text(message),
        };
        text += invocation.Get("lineEnd") switch
        {
            "cr" => "\r",
            "lf" => "\n",
            "crlf" => "\r\n",
            _ => string.Empty,
        };
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>The bytes of "02 41 0A", "0241 0a" or "0x02 0x41"; null for anything else or nothing.</summary>
    public static byte[]? Hex(string text)
    {
        var digits = new StringBuilder(text.Length);
        foreach (var part in text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            digits.Append(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part);
        }

        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(digits.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads until the answer contains <paramref name="expect"/> (or, without one, until the first data came), the other
    /// side closes or 4 KB arrived. Fails when the connection closes without the expected text.
    /// </summary>
    private static async Task<string> ReadAnswerAsync(Stream stream, string? expect, CancellationToken ct)
    {
        var buffer = new byte[MaxAnswerBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            var text = Encoding.UTF8.GetString(buffer, 0, length);
            if (string.IsNullOrEmpty(expect) || text.Contains(expect, StringComparison.Ordinal))
            {
                return text;
            }
        }

        var answer = Encoding.UTF8.GetString(buffer, 0, length);
        if (length == 0)
        {
            throw new InvalidOperationException("The connection was closed without an answer.");
        }

        if (!string.IsNullOrEmpty(expect) && !answer.Contains(expect, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The answer does not contain \"{expect}\": {Excerpt(answer)}");
        }

        return answer;
    }

    private static string Excerpt(string text)
    {
        var line = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length > ExcerptLength ? line[..ExcerptLength] + "…" : line;
    }

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

    private static ActionInvocation Settings(JsonElement settings) => new(Guid.Empty, string.Empty, ActionEdge.Fired, settings, null,
        new ActionTrigger(string.Empty, string.Empty, null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));
}
