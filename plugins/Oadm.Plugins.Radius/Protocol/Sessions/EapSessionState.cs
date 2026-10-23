using System.Globalization;
using Oadm.Plugins.Radius.Protocol.Text;
using Oadm.Plugins.Radius.Protocol.Tls;

namespace Oadm.Plugins.Radius.Protocol.Sessions;

/// <summary>The states of a session (docs/spec/radius-server.md "Session states"), in plain words by <see cref="EapSessionTexts"/>.</summary>
public enum EapSessionState
{
    /// <summary>1. The switch sent the first request; OADM asked for the identity.</summary>
    WaitingForIdentity,
    /// <summary>2. Identity received, EAP-TLS Start sent.</summary>
    StartingTls,
    /// <summary>3. The client hello arrives in fragments (fragment n of m).</summary>
    ReceivingClientHello,
    /// <summary>4. The server flight (server hello, certificate, key exchange, certificate request) is on its way; its last fragment.</summary>
    SendingServerCertificate,
    /// <summary>5. A fragment of OADM was sent; the device's acknowledgement is awaited (fragment n of m).</summary>
    WaitingForAcknowledgement,
    /// <summary>6. The device's certificate flight arrives (fragment n of m, the total from the length the device sent first).</summary>
    ReceivingClientCertificate,
    /// <summary>7. The validator checks chain, dates, use and the managed device.</summary>
    CheckingClientCertificate,
    /// <summary>8. The certificate passed; the device's Finished is awaited.</summary>
    WaitingForClientFinished,
    /// <summary>9. OADM sent its result (TLS 1.2: its Finished; TLS 1.3: the commitment message; or the alert of a refusal).</summary>
    SendingResult,
    /// <summary>10. Access-Accept sent.</summary>
    Authenticated,
    /// <summary>11. Access-Reject sent, with the reason.</summary>
    Failed,
    /// <summary>12a. No message within 30 s of the last one, or 60 s in all.</summary>
    TimedOut,
    /// <summary>12b. The switch started a new session for the port (or it was removed).</summary>
    Abandoned,
}

/// <summary>A copy of one session's state for the page, the log and the server package. Never holds keys or secrets.</summary>
public sealed record EapSessionSnapshot
{
    public required Guid Id { get; init; }

    /// <summary>The switch (<see cref="RadiusClient.Key"/>).</summary>
    public required string Client { get; init; }

    public required DateTimeOffset Started { get; init; }

    /// <summary>The last message of the switch in this session.</summary>
    public required DateTimeOffset LastMessage { get; init; }

    /// <summary>When the session ended (Authenticated, Failed, Timed out, Abandoned), else null.</summary>
    public DateTimeOffset? Ended { get; init; }

    public required EapSessionState State { get; init; }

    /// <summary>The fragment number of a fragmented message (1-based), 0 when not fragmented.</summary>
    public int Fragment { get; init; }

    /// <summary>The number of fragments of that message, 0 when not fragmented.</summary>
    public int FragmentCount { get; init; }

    /// <summary>Why it failed (Failed, or a refusal still being sent), in plain words.</summary>
    public string? Reason { get; init; }

    /// <summary>The TLS alert of the session, when one was sent or received.</summary>
    public TlsAlert? Alert { get; init; }

    /// <summary>NAS-Port-Id, else NAS-Port, else empty.</summary>
    public string Port { get; init; } = string.Empty;

    /// <summary>NAS-Port-Type by name ("Ethernet"), null when not sent.</summary>
    public string? PortType { get; init; }

    public MacAddress Mac { get; init; } = MacAddress.None;

    /// <summary>The EAP identity (sanitized; unprotected, shown only).</summary>
    public string? EapIdentity { get; init; }

    /// <summary>NAS-Identifier (shown, never trusted).</summary>
    public string? NasIdentifier { get; init; }

    /// <summary>NAS-IP-Address or NAS-IPv6-Address (shown, never trusted).</summary>
    public string? NasAddress { get; init; }

    public TlsVersion? TlsVersion { get; init; }

    /// <summary>The negotiated cipher suite by IANA name.</summary>
    public string? CipherSuite { get; init; }

    /// <summary>The certificates the device sent, leaf first (DER), for "View certificate".</summary>
    public IReadOnlyList<byte[]>? ClientCertificates { get; init; }

    /// <summary>The VLAN of an authenticated device.</summary>
    public int? VlanId { get; init; }

    /// <summary>The validator's warning for an authenticated device.</summary>
    public string? Warning { get; init; }

    /// <summary>The validator's tag (e.g. the device).</summary>
    public object? Tag { get; init; }

    /// <summary>Access-Requests of this session so far (at most 60).</summary>
    public int RoundTrips { get; init; }

    public bool IsEnded => Ended is not null;

    /// <summary>The state in plain words.</summary>
    public string Text => EapSessionTexts.Describe(this);
}

/// <summary>The plain words of the session states and the refusal texts (one phrase per concept).</summary>
public static class EapSessionTexts
{
    public const string OnlyEapTls = "Only EAP-TLS is supported";
    public const string SessionNotFound = "Session not found or expired";
    public const string MessageTooLarge = "Message too large";
    public const string TooManyRoundTrips = "Too many round trips";
    public const string UnexpectedMessage = "Unexpected message from the device";
    public const string MessageUnreadable = "Message could not be read";
    public const string NoServerCertificate = "The 802.1X server has no certificate";
    public const string TooManySessions = "Too many open sessions";
    public const string SwitchRemoved = "The switch was removed";
    public const string NewSession = "The switch started a new session for the port";
    public const string RoleReversal = "The switch sent a request instead of a response";

    /// <summary>"The device does not support EAP-TLS (it asked for PEAP)".</summary>
    public static string NotEapTls(IReadOnlyList<byte> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var methods = desired.Count == 0 || (desired.Count == 1 && desired[0] == 0)
            ? "no other method"
            : string.Join(", ", desired.Where(t => t != 0).Select(Eap.EapType.Name));
        return $"The device does not support EAP-TLS (it asked for {methods})";
    }

    /// <summary>The state of <paramref name="session"/> in plain words, e.g. "Sending server certificate: fragment 2 of 4".</summary>
    public static string Describe(EapSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var fragment = session.FragmentCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $": fragment {session.Fragment} of {session.FragmentCount}")
            : string.Empty;
        return session.State switch
        {
            EapSessionState.WaitingForIdentity => "Waiting for identity",
            EapSessionState.StartingTls => "Starting TLS",
            EapSessionState.ReceivingClientHello => "Receiving client hello" + fragment,
            EapSessionState.SendingServerCertificate => "Sending server certificate" + fragment,
            EapSessionState.WaitingForAcknowledgement => session.FragmentCount > 1
                ? string.Create(CultureInfo.InvariantCulture, $"Waiting for acknowledgement of fragment {session.Fragment} of {session.FragmentCount}")
                : "Waiting for acknowledgement",
            EapSessionState.ReceivingClientCertificate => "Receiving client certificate" + fragment,
            EapSessionState.CheckingClientCertificate => "Checking client certificate",
            EapSessionState.WaitingForClientFinished => "Waiting for client finished",
            EapSessionState.SendingResult => "Sending result" + fragment,
            EapSessionState.Authenticated => session.VlanId is { } vlan
                ? string.Create(CultureInfo.InvariantCulture, $"Authenticated with VLAN {vlan}")
                : "Authenticated",
            EapSessionState.Failed => session.Reason is { } reason ? "Failed: " + reason : "Failed",
            EapSessionState.TimedOut => "Timed out",
            _ => "Abandoned",
        };
    }
}
