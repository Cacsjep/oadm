using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>
/// The keys of an Access-Accept: MS-MPPE-Recv-Key and MS-MPPE-Send-Key (RFC 2548 2.4.2, 2.4.3), vendor 311, encrypted with the
/// shared secret and the Request Authenticator. Recv = MSK(0,31), Send = MSK(32,63) (RFC 5216 2.3).
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "RFC 2548 2.4.2 defines the MD5 construction.")]
public static class MppeKeys
{
    /// <summary>The Microsoft vendor id (RFC 2548 2).</summary>
    public const uint MicrosoftVendorId = 311;

    public const byte SendKeyType = 16;

    public const byte RecvKeyType = 17;

    /// <summary>The MSK length (RFC 5216 2.3).</summary>
    public const int MskLength = 64;

    /// <summary>The two attributes of <paramref name="msk"/> with two distinct random salts (most significant bit set).</summary>
    public static IReadOnlyList<RadiusAttribute> Attributes(ReadOnlySpan<byte> msk, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> requestAuthenticator)
    {
        if (msk.Length != MskLength)
        {
            throw new ArgumentException("The MSK has 64 bytes.", nameof(msk));
        }

        Span<byte> salts = stackalloc byte[4];
        do
        {
            RandomNumberGenerator.Fill(salts);
            salts[0] |= 0x80;
            salts[2] |= 0x80;
        }
        while (salts[0] == salts[2] && salts[1] == salts[3]);

        return
        [
            VendorAttribute(RecvKeyType, salts[..2], Encrypt(msk[..32], secret, requestAuthenticator, salts[..2])),
            VendorAttribute(SendKeyType, salts[2..], Encrypt(msk[32..], secret, requestAuthenticator, salts[2..])),
        ];
    }

    /// <summary>
    /// The encrypted String field (RFC 2548 2.4.2): P = Key-Length + Key, zero-padded to a multiple of 16;
    /// b(1) = MD5(S + R + A), c(1) = p(1) xor b(1); b(i) = MD5(S + c(i-1)), c(i) = p(i) xor b(i).
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> requestAuthenticator, ReadOnlySpan<byte> salt)
    {
        if (key.Length > 239)
        {
            throw new ArgumentException("The key is too long for one attribute.", nameof(key));
        }

        var plain = new byte[((key.Length + 1 + 15) / 16) * 16];
        plain[0] = (byte)key.Length;
        key.CopyTo(plain.AsSpan(1));
        var cipher = new byte[plain.Length];
        var seed = new byte[secret.Length + 16 + salt.Length];
        secret.CopyTo(seed);
        requestAuthenticator.CopyTo(seed.AsSpan(secret.Length));
        salt.CopyTo(seed.AsSpan(secret.Length + 16));
        var b = MD5.HashData(seed);
        for (var block = 0; block < plain.Length / 16; block++)
        {
            for (var i = 0; i < 16; i++)
            {
                cipher[(block * 16) + i] = (byte)(plain[(block * 16) + i] ^ b[i]);
            }

            var next = new byte[secret.Length + 16];
            secret.CopyTo(next);
            cipher.AsSpan(block * 16, 16).CopyTo(next.AsSpan(secret.Length));
            b = MD5.HashData(next);
        }

        return cipher;
    }

    /// <summary>The reverse of <see cref="Encrypt"/> (what a switch does): the key, or null when the length octet is wrong.</summary>
    public static byte[]? Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> requestAuthenticator, ReadOnlySpan<byte> salt)
    {
        if (cipher.Length == 0 || cipher.Length % 16 != 0)
        {
            return null;
        }

        var plain = new byte[cipher.Length];
        var seed = new byte[secret.Length + 16 + salt.Length];
        secret.CopyTo(seed);
        requestAuthenticator.CopyTo(seed.AsSpan(secret.Length));
        salt.CopyTo(seed.AsSpan(secret.Length + 16));
        var b = MD5.HashData(seed);
        for (var block = 0; block < cipher.Length / 16; block++)
        {
            for (var i = 0; i < 16; i++)
            {
                plain[(block * 16) + i] = (byte)(cipher[(block * 16) + i] ^ b[i]);
            }

            var next = new byte[secret.Length + 16];
            secret.CopyTo(next);
            cipher.Slice(block * 16, 16).CopyTo(next.AsSpan(secret.Length));
            b = MD5.HashData(next);
        }

        return plain[0] <= plain.Length - 1 ? plain.AsSpan(1, plain[0]).ToArray() : null;
    }

    /// <summary>Vendor-Specific (RFC 2865 5.26): Vendor-Id, then Vendor-Type, Vendor-Length, Salt, String (RFC 2548 2.4.2).</summary>
    private static RadiusAttribute VendorAttribute(byte vendorType, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> encrypted)
    {
        var value = new byte[4 + 2 + 2 + encrypted.Length];
        BinaryPrimitives.WriteUInt32BigEndian(value, MicrosoftVendorId);
        value[4] = vendorType;
        value[5] = (byte)(2 + 2 + encrypted.Length);
        salt.CopyTo(value.AsSpan(6));
        encrypted.CopyTo(value.AsSpan(8));
        return new RadiusAttribute(RadiusAttributeType.VendorSpecific, value);
    }
}

/// <summary>VLAN assignment in an Access-Accept (RFC 3580 3.31, RFC 2868 3.1): one tunnel, tag 0 in every attribute.</summary>
public static class VlanAttributes
{
    /// <summary>Tunnel-Type VLAN (RFC 3580 3.31).</summary>
    public const uint TunnelTypeVlan = 13;

    /// <summary>Tunnel-Medium-Type 802 (RFC 3580 3.31; IEEE-802 = 6 in RFC 2868 3.2).</summary>
    public const uint TunnelMedium802 = 6;

    /// <summary>The smallest and the largest VLAN ID (RFC 3580 3.31: 12 bits, 1..4094).</summary>
    public const int MinVlanId = 1;

    public const int MaxVlanId = 4094;

    /// <summary>Tunnel-Type = VLAN, Tunnel-Medium-Type = 802, Tunnel-Private-Group-ID = the VLAN ID as text.</summary>
    public static IReadOnlyList<RadiusAttribute> Attributes(int vlanId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(vlanId, MinVlanId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vlanId, MaxVlanId);
        var id = System.Text.Encoding.ASCII.GetBytes(vlanId.ToString(CultureInfo.InvariantCulture));
        var group = new byte[1 + id.Length];
        id.CopyTo(group, 1); // tag 0x00 first (RFC 2868 3.6: a tag of 0x00..0x1F is a tag)
        return
        [
            new RadiusAttribute(RadiusAttributeType.TunnelType, Tagged(TunnelTypeVlan)),
            new RadiusAttribute(RadiusAttributeType.TunnelMediumType, Tagged(TunnelMedium802)),
            new RadiusAttribute(RadiusAttributeType.TunnelPrivateGroupId, group),
        ];
    }

    /// <summary>Tag (1 octet, 0) + 3-octet value (RFC 2868 3.1, 3.2).</summary>
    private static byte[] Tagged(uint value) => [0, (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
