using System.Buffers.Binary;
using System.Text;

namespace Oadm.Core.LiveView.Probes;

/// <summary>
/// The RTCP a receiver sends and reads (RFC 3550 6.4): a compound receiver report (RR with one report block plus SDES
/// CNAME) that keeps a camera streaming over UDP, and the camera's sender report (its NTP time for LSR / DLSR).
/// </summary>
public static class RtcpPackets
{
    public const byte SenderReport = 200;
    public const byte ReceiverReport = 201;
    public const byte SourceDescription = 202;

    /// <summary>The CNAME of OADM's reports.</summary>
    public const string CanonicalName = "oadm";

    /// <summary>
    /// RR (one report block) + SDES CNAME. <paramref name="lastSenderReport"/> is the middle 32 bits of the NTP time of
    /// the last SR (0 without one), <paramref name="delaySinceSenderReport"/> the time since in 1/65536 s.
    /// </summary>
    public static byte[] BuildReceiverReport(uint ownSsrc, RtcpReceiverState state, byte fractionLost, uint lastSenderReport, uint delaySinceSenderReport)
    {
        var cname = Encoding.ASCII.GetBytes(CanonicalName);
        var sdesChunk = 4 + 2 + cname.Length + 1; // SSRC, CNAME item, END
        var sdesLength = 4 + ((sdesChunk + 3) & ~3);
        var packet = new byte[32 + sdesLength];
        var span = packet.AsSpan();

        // RR: V=2, P=0, RC=1, PT=201, length 7 (8 words - 1).
        span[0] = 0x81;
        span[1] = ReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 7);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], ownSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], state.Ssrc);
        var lost = (int)Math.Clamp(state.Lost, -0x800000, 0x7FFFFF); // 24-bit signed
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], ((uint)fractionLost << 24) | ((uint)lost & 0xFFFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], state.ExtendedHighestSequence);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], state.Jitter);
        BinaryPrimitives.WriteUInt32BigEndian(span[24..], lastSenderReport);
        BinaryPrimitives.WriteUInt32BigEndian(span[28..], delaySinceSenderReport);

        // SDES: V=2, SC=1, PT=202; one chunk with CNAME, END, zero padding to 32 bits.
        var sdes = span[32..];
        sdes[0] = 0x81;
        sdes[1] = SourceDescription;
        BinaryPrimitives.WriteUInt16BigEndian(sdes[2..], (ushort)((sdesLength / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(sdes[4..], ownSsrc);
        sdes[8] = 1; // CNAME
        sdes[9] = (byte)cname.Length;
        cname.CopyTo(sdes[10..]);
        return packet;
    }

    /// <summary>
    /// The fraction lost since the previous report (RFC 3550 A.3, 8-bit fixed point): the lost and expected packets of the
    /// interval.
    /// </summary>
    public static byte FractionLost(long expectedInterval, long lostInterval) =>
        expectedInterval <= 0 || lostInterval <= 0 ? (byte)0 : (byte)Math.Min(255, (lostInterval << 8) / expectedInterval);

    /// <summary>
    /// The middle 32 bits of the NTP time of the first sender report in a compound packet from <paramref name="ssrc"/>
    /// (any source when null).
    /// </summary>
    public static bool TryReadSenderReport(ReadOnlySpan<byte> compound, uint? ssrc, out uint ntpMiddle)
    {
        ntpMiddle = 0;
        while (compound.Length >= 4)
        {
            if ((compound[0] >> 6) != 2)
            {
                return false;
            }

            var length = (BinaryPrimitives.ReadUInt16BigEndian(compound[2..]) + 1) * 4;
            if (length > compound.Length)
            {
                return false;
            }

            if (compound[1] == SenderReport && length >= 28)
            {
                var sender = BinaryPrimitives.ReadUInt32BigEndian(compound[4..]);
                if (ssrc is null || sender == ssrc)
                {
                    var seconds = BinaryPrimitives.ReadUInt32BigEndian(compound[8..]);
                    var fraction = BinaryPrimitives.ReadUInt32BigEndian(compound[12..]);
                    ntpMiddle = (seconds << 16) | (fraction >> 16);
                    return true;
                }
            }

            compound = compound[length..];
        }

        return false;
    }
}
