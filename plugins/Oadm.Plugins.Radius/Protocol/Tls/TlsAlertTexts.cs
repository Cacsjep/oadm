using System.Collections.Frozen;
using System.Globalization;

namespace Oadm.Plugins.Radius.Protocol.Tls;

/// <summary>Who sent a TLS alert.</summary>
public enum TlsAlertDirection
{
    /// <summary>The device refused OADM.</summary>
    SentByDevice,
    /// <summary>OADM refused the device.</summary>
    SentByOadm,
}

/// <summary>A TLS alert of a session: its number (RFC 5246 7.2, RFC 8446 6) and who sent it.</summary>
public readonly record struct TlsAlert(byte Description, TlsAlertDirection Direction)
{
    /// <summary>The plain text, e.g. "The device does not trust the server certificate (TLS alert 48, unknown CA)".</summary>
    public string Text => TlsAlertTexts.Describe(Description, Direction);
}

/// <summary>
/// The one table of TLS alert texts (docs/spec/radius-server.md "TLS alerts"): every failure caused by an alert names the
/// alert, its number and who sent it, never the raw enum name alone. The device-sent form starts with "The device …" where it
/// reads better; the OADM-sent form of a certificate alert speaks of the client certificate.
/// </summary>
public static class TlsAlertTexts
{
    private sealed record Entry(string Name, string Text, string? DeviceText, string? OadmText);

    private static readonly FrozenDictionary<byte, Entry> Table = new Dictionary<byte, Entry>
    {
        [10] = new("unexpected message", "Unexpected message", null, null),
        [20] = new("bad record MAC", "Message damaged on the way", null, null),
        [40] = new("handshake failure", "No common security settings", null, null),
        [42] = new("bad certificate", "Certificate rejected", "The device rejected the server certificate", "Client certificate rejected"),
        [43] = new("unsupported certificate", "Certificate type not supported", "The device does not support the server certificate type", "Client certificate type not supported"),
        [44] = new("certificate revoked", "Certificate revoked", "The device says the server certificate is revoked", "Client certificate revoked"),
        [45] = new("certificate expired", "Certificate expired", "The device says the server certificate expired", "Client certificate expired"),
        [46] = new("certificate unknown", "Certificate not accepted", "The device did not accept the server certificate", "Client certificate not accepted"),
        [47] = new("illegal parameter", "Invalid value in the handshake", null, null),
        [48] = new("unknown CA", "Certificate not issued by a trusted CA", "The device does not trust the server certificate", "Client certificate not issued by a trusted CA"),
        [49] = new("access denied", "Access denied", null, null),
        [50] = new("decode error", "Message could not be read", null, null),
        [51] = new("decrypt error", "Signature check failed", null, null),
        [70] = new("protocol version", "TLS version not supported", null, null),
        [71] = new("insufficient security", "Security settings too weak", null, null),
        [80] = new("internal error", "Internal error", null, null),
        [86] = new("inappropriate fallback", "Fallback refused", null, null),
        [90] = new("user canceled", "Canceled", null, null),
        [109] = new("missing extension", "Required extension missing", null, null),
        [110] = new("unsupported extension", "Extension not supported", null, null),
        [112] = new("unrecognized name", "Server name not known", null, null),
        [116] = new("certificate required", "No client certificate sent", null, null),
    }.ToFrozenDictionary();

    /// <summary>The alert's RFC name ("unknown CA"), or null for a number without an entry.</summary>
    public static string? Name(byte description) => Table.TryGetValue(description, out var entry) ? entry.Name : null;

    /// <summary>The short text without number ("Certificate not issued by a trusted CA"), or "TLS alert n".</summary>
    public static string Text(byte description, TlsAlertDirection direction) =>
        Table.TryGetValue(description, out var entry)
            ? (direction == TlsAlertDirection.SentByDevice ? entry.DeviceText : entry.OadmText) ?? entry.Text
            : "TLS alert " + description.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// "No client certificate sent (TLS alert 40, handshake failure)": OADM refused a device that sent no certificate, with
    /// the alert it really sent (TLS 1.3: 116 certificate required, RFC 8446 4.4.2.4; earlier: 40 handshake failure,
    /// RFC 5246 7.4.6).
    /// </summary>
    public static string NoClientCertificate(byte alert) =>
        string.Create(CultureInfo.InvariantCulture, $"{Table[116].Text} (TLS alert {alert}, {Name(alert) ?? "unknown"})");

    /// <summary>"&lt;text&gt; (TLS alert &lt;n&gt;, &lt;name&gt;)", or "TLS alert &lt;n&gt;" for a number without an entry.</summary>
    public static string Describe(byte description, TlsAlertDirection direction) =>
        Table.TryGetValue(description, out var entry)
            ? string.Create(CultureInfo.InvariantCulture, $"{Text(description, direction)} (TLS alert {description}, {entry.Name})")
            : "TLS alert " + description.ToString(CultureInfo.InvariantCulture);
}
