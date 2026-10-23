using Oadm.Plugins.Radius.Protocol.Text;

namespace Oadm.Plugins.Radius.Protocol.Tls;

/// <summary>
/// The hook that decides who passes (docs/spec/radius-server.md "Who passes"): the server package checks the chain (OADM
/// CA or a trusted root, dates, use), the managed device it names, its site and the revocation list. Called once per
/// handshake while TLS waits, on the thread that processes the request: keep it to in-memory lookups (the device table and
/// the revocation list are snapshots). An exception refuses the device (internal error), never lets it pass.
/// </summary>
public interface IClientCertificateValidator
{
    ClientCertificateVerdict Validate(ClientCertificateCheck check);
}

/// <summary>What the validator gets.</summary>
/// <param name="Client">The switch key the request came from (<see cref="Sessions.RadiusClient.Key"/>).</param>
/// <param name="SessionId">The session.</param>
/// <param name="Chain">The certificates the device sent, leaf first (DER). Never empty.</param>
/// <param name="Version">The negotiated TLS version.</param>
/// <param name="EapIdentity">The EAP identity (unprotected, shown only, never trusted).</param>
/// <param name="Mac">The MAC address of the Calling-Station-Id (unprotected, never trusted).</param>
public sealed record ClientCertificateCheck(string Client, Guid SessionId, IReadOnlyList<byte[]> Chain, TlsVersion Version, string? EapIdentity, MacAddress Mac);

/// <summary>The validator's answer.</summary>
public sealed record ClientCertificateVerdict
{
    private ClientCertificateVerdict(bool accepted, string? reason, byte alert, int? vlanId, string? warning, object? tag)
    {
        Accepted = accepted;
        Reason = reason;
        Alert = alert;
        VlanId = vlanId;
        Warning = warning;
        Tag = tag;
    }

    public bool Accepted { get; }

    /// <summary>Why the device was refused, in plain words ("Client certificate expired on 2026-09-30").</summary>
    public string? Reason { get; }

    /// <summary>The TLS alert OADM sends when refusing (RFC 5246 7.2.2), e.g. 48 unknown CA, 45 certificate expired.</summary>
    public byte Alert { get; }

    /// <summary>The VLAN of an accepted device (1..4094), or null for none.</summary>
    public int? VlanId { get; }

    /// <summary>A warning for the log of an accepted device ("MAC address … does not match the certificate of …").</summary>
    public string? Warning { get; }

    /// <summary>Anything the server package wants back with the session (e.g. the device); never interpreted here.</summary>
    public object? Tag { get; }

    /// <summary>Lets the device pass.</summary>
    public static ClientCertificateVerdict Accept(int? vlanId = null, string? warning = null, object? tag = null)
    {
        if (vlanId is < Radius.VlanAttributes.MinVlanId or > Radius.VlanAttributes.MaxVlanId)
        {
            throw new ArgumentOutOfRangeException(nameof(vlanId), vlanId, "A VLAN ID is 1..4094.");
        }

        return new(true, null, 0, vlanId, warning, tag);
    }

    /// <summary>Refuses the device with <paramref name="reason"/> and the TLS alert <paramref name="alert"/> (default 42 bad certificate).</summary>
    public static ClientCertificateVerdict Refuse(string reason, byte alert = 42, object? tag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason, alert, null, null, tag);
    }
}
