using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

using MimeKit;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications;

/// <summary>
/// Event rule action "Send email": the mail server is part of each action (no shared
/// setting, so an action stands on its own and is copied with its rule), STARTTLS / TLS / none, optional login (password
/// stored encrypted), "Allow untrusted certificates", To / Subject / Body with placeholders, body as text or HTML (HTML
/// escapes the values and gets a plain-text part too). Runs once per rule edge, no device. Sent with MailKit.
/// </summary>
public sealed partial class SendEmailAction : IActionPlugin
{
    public const string ActionId = "oadm.notifications.email";

    public string Id => ActionId;

    public string DisplayName => "Send email";

    public string? IconKey => "send";

    public string Group => "Notifications";

    public string? Description => "Sends an email through your mail server.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Text("host", "Mail server", required: true),
        ActionField.Number("port", "Port", 1, 65535, 587),
        ActionField.Choice("security", "Encryption", [new("starttls", "STARTTLS (usually port 587)"), new("tls", "TLS (usually port 465)"), new("none", "None")], "starttls"),
        ActionField.Bool("allowUntrusted", "Allow untrusted certificates"),
        ActionField.Text("user", "User name") with { Hint = "Optional" },
        ActionField.Password("password", "Password"),
        ActionField.Text("from", "From", required: true),
        ActionField.Text("to", "To", required: true) with { Hint = "Several addresses separated by commas" },
        ActionField.Text("subject", "Subject", required: true, defaultValue: "{rule}: {event} on {device}"),
        ActionField.Choice("format", "Format", [new("text", "Text"), new("html", "HTML")], "text"),
        ActionField.Code("body", "Body", languageKey: "format", required: true) with
        {
            Default = "{event} on {device} at {time}.\n\nRule: {rule}\nDevice: {model}, {serial}\n{webUrl}",
        },
        ActionField.Bool("onOff", "Also send when the rule turns off") with { OffEdge = true },
    ];

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tag();

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var invocation = Settings(settings);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (invocation.Get("from") is { Length: > 0 } from && !from.Contains('{', StringComparison.Ordinal) && !MailboxAddress.TryParse(from, out _))
        {
            errors["from"] = "Enter an email address.";
        }

        if (invocation.Get("to") is { Length: > 0 } to && !to.Contains('{', StringComparison.Ordinal) && Addresses(to) is null)
        {
            errors["to"] = "Enter email addresses separated by commas.";
        }

        if (invocation.Get("host") is { Length: > 0 } host && Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
        {
            errors["host"] = "Enter a host name or IP address.";
        }

        return errors;
    }

    public string Describe(JsonElement settings) => "Email to " + Settings(settings).Get("to");

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("onOff", out var on) && on.ValueKind == JsonValueKind.True);

    public TimeSpan? MaxRunTime(JsonElement settings) => TimeSpan.FromSeconds(60);

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        if (!MailboxAddress.TryParse(ctx.Text(invocation.Get("from")), out var from))
        {
            throw new InvalidOperationException("The From address is not valid. Nothing was sent.");
        }

        var to = Addresses(ctx.Text(invocation.Get("to")))
            ?? throw new InvalidOperationException("The To addresses are not valid. Nothing was sent.");
        var message = new MimeMessage { Subject = OneLine(ctx.Text(invocation.Get("subject"))) };
        message.From.Add(from);
        message.To.AddRange(to);
        var body = invocation.Get("body") ?? string.Empty;
        var builder = new BodyBuilder();
        if (invocation.Get("format") == "html")
        {
            builder.HtmlBody = ctx.Text(body, PlaceholderEscape.Xml);
            builder.TextBody = WebUtility.HtmlDecode(Tag().Replace(builder.HtmlBody, string.Empty)).Trim();
        }
        else
        {
            builder.TextBody = ctx.Text(body);
        }

        message.Body = builder.ToMessageBody();

        var host = invocation.Get("host")?.Trim() ?? string.Empty;
        var port = invocation.GetInt("port", 587);
        var security = invocation.Get("security") switch
        {
            "tls" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        using var client = new SmtpClient { Timeout = 30_000 };
        if (invocation.GetBool("allowUntrusted"))
        {
#pragma warning disable CA5359 // The administrator turned on "Allow untrusted certificates" for this action.
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        try
        {
            await client.ConnectAsync(host, port, security, ct).ConfigureAwait(false);
            if (invocation.Get("user") is { Length: > 0 } user)
            {
                await client.AuthenticateAsync(user, invocation.Get("password") ?? string.Empty, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            throw new InvalidOperationException("The mail server refused the user name or password: " + ex.Message, ex);
        }
        catch (SslHandshakeException ex)
        {
            throw new InvalidOperationException("The mail server certificate is not trusted (turn on \"Allow untrusted certificates\" or check the encryption): " + ex.Message, ex);
        }
        catch (CommandException ex)
        {
            throw new InvalidOperationException("The mail server refused the email: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ProtocolException)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{host}:{port} cannot be reached: {ex.Message}"), ex);
        }

        ctx.Log("Sent to " + string.Join(", ", to.Select(a => a.Address)));
    }

    /// <summary>The addresses of a comma or semicolon separated list; null when one is not an email address or none is given.</summary>
    internal static List<MailboxAddress>? Addresses(string? text)
    {
        var parts = (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var list = new List<MailboxAddress>(parts.Length);
        foreach (var part in parts)
        {
            if (!MailboxAddress.TryParse(part, out var address) || !address.Address.Contains('@', StringComparison.Ordinal))
            {
                return null;
            }

            list.Add(address);
        }

        return list;
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static ActionInvocation Settings(JsonElement settings) => new(Guid.Empty, string.Empty, ActionEdge.Fired, settings, null,
        new ActionTrigger(string.Empty, string.Empty, null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));
}
