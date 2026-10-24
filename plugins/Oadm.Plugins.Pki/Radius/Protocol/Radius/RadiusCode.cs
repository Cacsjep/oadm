namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>RADIUS packet codes (RFC 2865 3, RFC 5997 for Status-Server).</summary>
public enum RadiusCode : byte
{
    AccessRequest = 1,
    AccessAccept = 2,
    AccessReject = 3,
    AccountingRequest = 4,
    AccountingResponse = 5,
    AccessChallenge = 11,
    StatusServer = 12,
    StatusClient = 13,
}

/// <summary>RADIUS attribute types this server reads or writes (RFC 2865 5, RFC 2868, RFC 2869 5, RFC 3162, RFC 3579 3).</summary>
public static class RadiusAttributeType
{
    public const byte UserName = 1;
    public const byte UserPassword = 2;
    public const byte ChapPassword = 3;
    public const byte NasIpAddress = 4;
    public const byte NasPort = 5;
    public const byte ServiceType = 6;
    public const byte FramedMtu = 12;
    public const byte ReplyMessage = 18;
    public const byte State = 24;
    public const byte Class = 25;
    public const byte VendorSpecific = 26;
    public const byte SessionTimeout = 27;
    public const byte CalledStationId = 30;
    public const byte CallingStationId = 31;
    public const byte NasIdentifier = 32;
    public const byte ProxyState = 33;
    public const byte NasPortType = 61;
    public const byte TunnelType = 64;
    public const byte TunnelMediumType = 65;
    public const byte ArapPassword = 70;
    public const byte EapMessage = 79;
    public const byte MessageAuthenticator = 80;
    public const byte TunnelPrivateGroupId = 81;
    public const byte NasPortId = 87;
    public const byte NasIpv6Address = 95;
}

/// <summary>One attribute as it was on the wire: type and raw value (without the type and length octets).</summary>
/// <param name="Type">The attribute type (RFC 2865 5).</param>
/// <param name="Value">The raw value, 0..253 bytes.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The RFC name of the thing (RFC 2865 5).")]
public readonly record struct RadiusAttribute(byte Type, ReadOnlyMemory<byte> Value)
{
    /// <summary>The largest value of one attribute: 255 minus the type and length octets (RFC 2865 5).</summary>
    public const int MaxValueLength = 253;

    /// <summary>An attribute with a copy of <paramref name="value"/>.</summary>
    public static RadiusAttribute Of(byte type, ReadOnlySpan<byte> value) => new(type, value.ToArray());

    /// <summary>A text attribute (RFC 8044 3.4, UTF-8).</summary>
    public static RadiusAttribute Text(byte type, string value) => new(type, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>A 32-bit integer attribute in network order (RFC 8044 3.1).</summary>
    public static RadiusAttribute Number(byte type, uint value)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new RadiusAttribute(type, bytes);
    }

    /// <summary>
    /// One EAP packet as consecutive EAP-Message attributes of at most 253 bytes each (RFC 3579 3.1). An empty packet is
    /// one attribute of length 2 (EAP-Start, RFC 3579 2.1).
    /// </summary>
    public static IReadOnlyList<RadiusAttribute> EapMessages(ReadOnlyMemory<byte> eapPacket)
    {
        if (eapPacket.IsEmpty)
        {
            return [new RadiusAttribute(RadiusAttributeType.EapMessage, ReadOnlyMemory<byte>.Empty)];
        }

        var list = new List<RadiusAttribute>((eapPacket.Length + MaxValueLength - 1) / MaxValueLength);
        for (var offset = 0; offset < eapPacket.Length; offset += MaxValueLength)
        {
            list.Add(new RadiusAttribute(RadiusAttributeType.EapMessage, eapPacket.Slice(offset, Math.Min(MaxValueLength, eapPacket.Length - offset))));
        }

        return list;
    }
}
