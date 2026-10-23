using System.Buffers.Binary;
using System.Net;
using Oadm.Plugins.Radius.Protocol.Text;

namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>
/// The attributes of an Access-Request this server uses (docs/spec/radius-server.md "Parsing and RFC compliance"). Every
/// one is optional except the Message-Authenticator (checked before, <see cref="RadiusAuthenticators.VerifyRequest"/>); a
/// missing one is null or empty. Text is sanitized (<see cref="RadiusText"/>). The NAS identifiers are shown, never trusted:
/// the switch is the packet's source address.
/// </summary>
public sealed class AccessRequest
{
    private AccessRequest(RadiusPacket packet)
    {
        Packet = packet;
    }

    /// <summary>The packet itself (raw attributes in order).</summary>
    public RadiusPacket Packet { get; }

    /// <summary>The joined EAP-Message attributes (RFC 3579 3.1); null without any; empty for EAP-Start (RFC 3579 2.1).</summary>
    public byte[]? Eap { get; private init; }

    /// <summary>User-Name (RFC 2865 5.1): the EAP identity the switch copied (RFC 3579 2.1).</summary>
    public string? UserName { get; private init; }

    /// <summary>NAS-IP-Address (RFC 2865 5.4).</summary>
    public IPAddress? NasIpAddress { get; private init; }

    /// <summary>NAS-IPv6-Address (RFC 3162 2.1).</summary>
    public IPAddress? NasIpv6Address { get; private init; }

    /// <summary>NAS-Identifier (RFC 2865 5.32).</summary>
    public string? NasIdentifier { get; private init; }

    /// <summary>NAS-Port (RFC 2865 5.5).</summary>
    public uint? NasPort { get; private init; }

    /// <summary>NAS-Port-Id (RFC 2869 5.17), e.g. "GigabitEthernet1/0/5".</summary>
    public string? NasPortId { get; private init; }

    /// <summary>NAS-Port-Type (RFC 2865 5.41).</summary>
    public uint? NasPortType { get; private init; }

    /// <summary>Calling-Station-Id (RFC 2865 5.31, RFC 3580 3.21) as sent (sanitized).</summary>
    public string? CallingStationId { get; private init; }

    /// <summary>The MAC address of <see cref="CallingStationId"/>.</summary>
    public MacAddress Mac { get; private init; } = MacAddress.None;

    /// <summary>Called-Station-Id (RFC 2865 5.30).</summary>
    public string? CalledStationId { get; private init; }

    /// <summary>Framed-MTU (RFC 2865 5.12, RFC 3580 3.10), 64..65535.</summary>
    public int? FramedMtu { get; private init; }

    /// <summary>The State attribute (RFC 2865 5.24), raw.</summary>
    public byte[]? State { get; private init; }

    /// <summary>The Proxy-State attributes in their order (RFC 2865 5.33): echoed unchanged into the answer.</summary>
    public IReadOnlyList<RadiusAttribute> ProxyStates { get; private init; } = [];

    /// <summary>NAS-Port-Id when sent, else NAS-Port, else empty (docs/spec "Port").</summary>
    public string Port => !string.IsNullOrEmpty(NasPortId) ? NasPortId : NasPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Reads an Access-Request. Malformed (dropped silently, RFC 2865 3): a Message-Authenticator not exactly once with 16
    /// bytes (RFC 3579 3.3 table: exactly 1); EAP-Message attributes that are not consecutive (RFC 3579 3.1 "MUST be
    /// consecutive"); EAP together with User-Password, CHAP-Password or ARAP-Password (RFC 3579 3.3 note 1); State,
    /// Framed-MTU, NAS-Port, NAS-Port-Type or an address more than once or with the wrong size (RFC 2865 5.44 table "0-1",
    /// RFC 8044 sizes); a Framed-MTU outside 64..65535 (RFC 2865 5.12). Text attributes take their first value.
    /// </summary>
    public static AccessRequestReadResult Read(RadiusPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var authenticators = packet.Count(RadiusAttributeType.MessageAuthenticator);
        if (authenticators != 1 || packet.Find(RadiusAttributeType.MessageAuthenticator)!.Value.Value.Length != RadiusAuthenticators.MessageAuthenticatorLength)
        {
            return Bad(authenticators == 0 ? RadiusMalformation.None : RadiusMalformation.BadMessageAuthenticator,
                authenticators == 0 ? "No Message-Authenticator" : "Message-Authenticator repeated or of the wrong size");
        }

        byte[]? eap = null;
        var eapRunEnded = false;
        var previousWasEap = false;
        using var eapBytes = new MemoryStream();
        foreach (var attribute in packet.Attributes)
        {
            var isEap = attribute.Type == RadiusAttributeType.EapMessage;
            if (isEap)
            {
                if (eapRunEnded)
                {
                    return Bad(RadiusMalformation.BadEapMessage, "EAP-Message attributes not consecutive");
                }

                eapBytes.Write(attribute.Value.Span);
                eap ??= [];
            }
            else if (previousWasEap)
            {
                eapRunEnded = true;
            }

            previousWasEap = isEap;
        }

        if (eap is not null)
        {
            eap = eapBytes.ToArray();
            if (packet.Find(RadiusAttributeType.UserPassword) is not null || packet.Find(RadiusAttributeType.ChapPassword) is not null || packet.Find(RadiusAttributeType.ArapPassword) is not null)
            {
                return Bad(RadiusMalformation.BadEapMessage, "EAP-Message together with a password");
            }
        }

        if (!Single(packet, RadiusAttributeType.State, 1, RadiusAttribute.MaxValueLength, out var state)
            || !Single(packet, RadiusAttributeType.FramedMtu, 4, 4, out var mtu)
            || !Single(packet, RadiusAttributeType.NasPort, 4, 4, out var nasPort)
            || !Single(packet, RadiusAttributeType.NasPortType, 4, 4, out var nasPortType)
            || !Single(packet, RadiusAttributeType.NasIpAddress, 4, 4, out var nasIp)
            || !Single(packet, RadiusAttributeType.NasIpv6Address, 16, 16, out var nasIpv6))
        {
            return Bad(RadiusMalformation.BadAttributeValue, "State, Framed-MTU, NAS-Port, NAS-Port-Type or a NAS address repeated or of the wrong size");
        }

        int? framedMtu = mtu is null ? null : (int)BinaryPrimitives.ReadUInt32BigEndian(mtu);
        if (framedMtu is < 64 or > 65535)
        {
            return Bad(RadiusMalformation.BadAttributeValue, $"Framed-MTU {(uint)framedMtu.Value} outside 64..65535");
        }

        var calling = TextOf(packet, RadiusAttributeType.CallingStationId);
        var request = new AccessRequest(packet)
        {
            Eap = eap,
            UserName = TextOf(packet, RadiusAttributeType.UserName),
            NasIdentifier = TextOf(packet, RadiusAttributeType.NasIdentifier),
            NasPortId = TextOf(packet, RadiusAttributeType.NasPortId),
            CallingStationId = calling,
            Mac = MacAddress.Parse(calling),
            CalledStationId = TextOf(packet, RadiusAttributeType.CalledStationId),
            FramedMtu = framedMtu,
            State = state,
            NasPort = nasPort is null ? null : BinaryPrimitives.ReadUInt32BigEndian(nasPort),
            NasPortType = nasPortType is null ? null : BinaryPrimitives.ReadUInt32BigEndian(nasPortType),
            NasIpAddress = nasIp is null ? null : new IPAddress(nasIp),
            NasIpv6Address = nasIpv6 is null ? null : new IPAddress(nasIpv6),
            ProxyStates = packet.All(RadiusAttributeType.ProxyState),
        };
        return new AccessRequestReadResult(request, RadiusMalformation.None, null);
    }

    private static AccessRequestReadResult Bad(RadiusMalformation error, string detail) => new(null, error, detail);

    private static string? TextOf(RadiusPacket packet, byte type) => packet.Find(type) is { } attribute ? RadiusText.Decode(attribute.Value.Span) : null;

    private static bool Single(RadiusPacket packet, byte type, int minLength, int maxLength, out byte[]? value)
    {
        value = null;
        foreach (var attribute in packet.Attributes)
        {
            if (attribute.Type != type)
            {
                continue;
            }

            if (value is not null || attribute.Value.Length < minLength || attribute.Value.Length > maxLength)
            {
                value = null;
                return false;
            }

            value = attribute.Value.ToArray();
        }

        return true;
    }
}

/// <summary>A read Access-Request, or why it is malformed. A missing Message-Authenticator is its own case (Error None, Request null).</summary>
public readonly record struct AccessRequestReadResult(AccessRequest? Request, RadiusMalformation Error, string? Detail)
{
    /// <summary>True when the request has no Message-Authenticator (dropped and counted as "Wrong secret").</summary>
    public bool MissingMessageAuthenticator => Request is null && Error == RadiusMalformation.None;
}
