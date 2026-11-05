using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications;

/// <summary>
/// The recipient types of the actions: a recipient holds the target and its login, the action keeps the message. The host
/// merges a recipient's fields into the action's settings under these keys before a run.
/// </summary>
public static class NotificationRecipients
{
    public const string HttpType = "oadm.notifications.recipient.http";

    public const string EmailType = "oadm.notifications.recipient.email";

    public const string TcpType = "oadm.notifications.recipient.tcp";

    /// <summary>http and https in one type: URL, login, "Allow untrusted certificates".</summary>
    public static RecipientType Http { get; } = new(
        HttpType,
        "HTTP",
        "send",
        [
            ActionField.Text("url", "URL", required: true) with { EmptyText = "Enter a URL." },
            ActionField.Choice("auth", "Login", [new("none", "None"), new("basic", "Basic"), new("digest", "Digest"), new("bearer", "Bearer token")], "none"),
            ActionField.Text("user", "User name") with { VisibleWhenKey = "auth", VisibleWhenValue = "basic|digest" },
            ActionField.Password("password", "Password") with { VisibleWhenKey = "auth", VisibleWhenValue = "basic|digest" },
            ActionField.Password("token", "Token") with { VisibleWhenKey = "auth", VisibleWhenValue = "bearer", MaxLength = 4096 },
            ActionField.Bool("allowUntrusted", "Allow untrusted certificates"),
        ],
        SendHttpAction.ActionId)
    {
        Target = "{url}",
    };

    /// <summary>One mailbox or group: the mail server, its login, From and To.</summary>
    public static RecipientType Email { get; } = new(
        EmailType,
        "Email",
        "send",
        [
            ActionField.Text("host", "Mail server", required: true) with { EmptyText = "Enter a host name or IP address." },
            ActionField.Number("port", "Port", 1, 65535, 587),
            ActionField.Choice("security", "Encryption", [new("starttls", "STARTTLS (usually port 587)"), new("tls", "TLS (usually port 465)"), new("none", "None")], "starttls"),
            ActionField.Bool("allowUntrusted", "Allow untrusted certificates"),
            ActionField.Text("user", "User name") with { Hint = "Optional" },
            ActionField.Password("password", "Password"),
            ActionField.Text("from", "From", required: true) with { EmptyText = "Enter an email address." },
            ActionField.Text("to", "To", required: true) with { Hint = "Several addresses separated by commas", EmptyText = "Enter an email address." },
        ],
        SendEmailAction.ActionId)
    {
        Target = "{host}:{port}",
    };

    /// <summary>A host and port, optional TLS.</summary>
    public static RecipientType Tcp { get; } = new(
        TcpType,
        "TCP",
        "send",
        [
            ActionField.Text("host", "Host", required: true) with { Hint = "Host name or IP address", EmptyText = "Enter a host name or IP address." },
            ActionField.Number("port", "Port", 1, 65535, 8000),
            ActionField.Bool("tls", "Use TLS"),
            ActionField.Bool("allowUntrusted", "Allow untrusted certificates") with { VisibleWhenKey = "tls", VisibleWhenValue = "true" },
        ],
        SendTcpAction.ActionId)
    {
        Target = "{host}:{port}",
    };

    /// <summary>The select of an action: the recipients of one type.</summary>
    internal static ActionField Field(string typeId) => ActionField.Recipient("recipient", "Recipient", [typeId]);
}
