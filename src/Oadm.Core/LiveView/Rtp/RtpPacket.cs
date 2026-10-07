using System.Buffers;

namespace Oadm.Core.LiveView.Rtp;

/// <summary>A parsed RTP packet (RFC 3550). <see cref="Payload"/> excludes CSRCs, extension and padding.</summary>
public readonly record struct RtpPacket(bool Marker, byte PayloadType, ushort SequenceNumber, uint Timestamp, uint Ssrc, ReadOnlyMemory<byte> Payload)
{
    public static bool TryParse(ReadOnlyMemory<byte> packet, out RtpPacket result)
    {
        result = default;
        var span = packet.Span;
        if (span.Length < 12 || (span[0] >> 6) != 2)
        {
            return false;
        }

        var padding = (span[0] & 0x20) != 0;
        var extension = (span[0] & 0x10) != 0;
        var csrcCount = span[0] & 0x0F;
        var offset = 12 + (4 * csrcCount);
        if (extension)
        {
            if (span.Length < offset + 4)
            {
                return false;
            }

            var words = (span[offset + 2] << 8) | span[offset + 3];
            offset += 4 + (4 * words);
        }

        var end = span.Length;
        if (padding && end > offset)
        {
            end -= span[^1];
        }

        if (end < offset)
        {
            return false;
        }

        result = new RtpPacket(
            Marker: (span[1] & 0x80) != 0,
            PayloadType: (byte)(span[1] & 0x7F),
            SequenceNumber: (ushort)((span[2] << 8) | span[3]),
            Timestamp: (uint)((span[4] << 24) | (span[5] << 16) | (span[6] << 8) | span[7]),
            Ssrc: (uint)((span[8] << 24) | (span[9] << 16) | (span[10] << 8) | span[11]),
            Payload: packet[offset..end]);
        return true;
    }
}

/// <summary>A complete access unit in Annex B format.</summary>
public sealed record AccessUnit(byte[] Data, bool IsKeyframe, uint RtpTimestamp, byte[]? CodecConfig);

/// <summary>Reassembles access units from the RTP packets of one video stream.</summary>
public interface IRtpDepacketizer
{
    /// <summary>Feeds one packet; returns the access units it completed (usually zero or one).</summary>
    IReadOnlyList<AccessUnit> Push(RtpPacket packet);

    /// <summary>Access units dropped because of packet loss or damage (diagnostics).</summary>
    int DroppedAccessUnits { get; }
}

/// <summary>
/// Shared logic of the H.264 (RFC 6184) and H.265 (RFC 7798) depacketizers: NAL units are
/// collected into an access unit that ends with the RTP marker bit (or a timestamp change).
/// Packet loss marks the access unit damaged and everything is dropped until the next keyframe,
/// so the decoder never sees broken references. Keyframes get the latest parameter sets
/// prepended when they do not carry them in-band.
/// </summary>
public abstract class NalDepacketizer : IRtpDepacketizer
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private readonly ArrayBufferWriter<byte> _unit = new(64 * 1024);
    private readonly List<int> _nalTypes = [];
    private readonly Dictionary<int, byte[]> _parameterSets = [];
    private ushort? _lastSequence;
    private uint? _timestamp;
    private bool _damaged;
    private bool _waitForKeyframe = true;
    private bool _inFragment;
    private int _fragmentStart;

    public int DroppedAccessUnits { get; private set; }

    /// <summary>Parameter set NAL types in decoder order (H.264: SPS, PPS; H.265: VPS, SPS, PPS).</summary>
    protected abstract IReadOnlyList<int> ParameterSetTypes { get; }

    /// <summary>Seeds parameter sets from the SDP (sprop-parameter-sets / sprop-vps etc.).</summary>
    public void AddParameterSet(byte[] nal)
    {
        ArgumentNullException.ThrowIfNull(nal);
        if (nal.Length > 0)
        {
            var type = NalType(nal);
            if (ParameterSetTypes.Contains(type))
            {
                _parameterSets[type] = nal;
            }
        }
    }

    /// <summary>Current parameter sets as Annex B, or null when none are known.</summary>
    public byte[]? CodecConfig()
    {
        if (_parameterSets.Count == 0)
        {
            return null;
        }

        using var ms = new MemoryStream();
        foreach (var type in ParameterSetTypes)
        {
            if (_parameterSets.TryGetValue(type, out var nal))
            {
                ms.Write(StartCode);
                ms.Write(nal);
            }
        }

        return ms.ToArray();
    }

    public IReadOnlyList<AccessUnit> Push(RtpPacket packet)
    {
        var completed = new List<AccessUnit>(1);
        if (_lastSequence is { } last && (ushort)(last + 1) != packet.SequenceNumber)
        {
            _damaged = true; // lost packet(s) inside or before this access unit
            _inFragment = false;
        }

        _lastSequence = packet.SequenceNumber;
        if (_timestamp is { } ts && ts != packet.Timestamp && _unit.WrittenCount > 0)
        {
            Complete(completed); // previous access unit had no marker
        }

        _timestamp = packet.Timestamp;
        if (!packet.Payload.IsEmpty)
        {
            try
            {
                ParsePayload(packet.Payload.Span);
            }
            catch (InvalidDataException)
            {
                _damaged = true;
            }
        }

        if (packet.Marker)
        {
            Complete(completed);
        }

        return completed;
    }

    /// <summary>Splits one RTP payload into NAL units and calls <see cref="AppendNal"/> / fragment methods.</summary>
    protected abstract void ParsePayload(ReadOnlySpan<byte> payload);

    protected abstract int NalType(ReadOnlySpan<byte> nal);

    protected abstract bool IsKeyframeType(int nalType);

    protected void AppendNal(ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty)
        {
            throw new InvalidDataException("Empty NAL unit.");
        }

        if (_inFragment)
        {
            _damaged = true; // end of the previous fragment was lost
        }

        _inFragment = false;
        Record(NalType(nal), nal);
        _unit.Write(StartCode);
        _unit.Write(nal);
    }

    /// <summary>Starts a fragmented NAL unit with its reconstructed header.</summary>
    protected void BeginFragment(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data)
    {
        if (_inFragment)
        {
            _damaged = true;
        }

        _inFragment = true;
        _fragmentStart = _unit.WrittenCount + StartCode.Length;
        _unit.Write(StartCode);
        _unit.Write(header);
        _unit.Write(data);
    }

    protected void ContinueFragment(ReadOnlySpan<byte> data, bool end)
    {
        if (!_inFragment)
        {
            _damaged = true; // start of the fragment was lost
            return;
        }

        _unit.Write(data);
        if (end)
        {
            _inFragment = false;
            var nal = _unit.WrittenSpan[_fragmentStart..];
            Record(NalType(nal), nal);
        }
    }

    private void Record(int type, ReadOnlySpan<byte> nal)
    {
        _nalTypes.Add(type);
        if (ParameterSetTypes.Contains(type))
        {
            _parameterSets[type] = nal.ToArray();
        }
    }

    private void Complete(List<AccessUnit> completed)
    {
        var keyframe = _nalTypes.Any(IsKeyframeType);
        var damaged = _damaged || _inFragment;
        if (_unit.WrittenCount > 0)
        {
            if (damaged)
            {
                DroppedAccessUnits++;
                _waitForKeyframe = true;
            }
            else if (_waitForKeyframe && !keyframe)
            {
                DroppedAccessUnits++;
            }
            else
            {
                _waitForKeyframe = false;
                completed.Add(Build(keyframe));
            }
        }

        _unit.ResetWrittenCount();
        _nalTypes.Clear();
        _damaged = false;
        _inFragment = false;
    }

    private AccessUnit Build(bool keyframe)
    {
        var body = _unit.WrittenSpan.ToArray();
        if (!keyframe)
        {
            return new AccessUnit(body, false, _timestamp ?? 0, null);
        }

        var config = CodecConfig();
        var missing = ParameterSetTypes.Any(t => !_nalTypes.Contains(t));
        if (config is not null && missing)
        {
            var data = new byte[config.Length + body.Length];
            config.CopyTo(data, 0);
            body.CopyTo(data, config.Length);
            body = data;
        }

        return new AccessUnit(body, true, _timestamp ?? 0, config);
    }

    protected static int ReadU16(ReadOnlySpan<byte> span, int offset) => (span[offset] << 8) | span[offset + 1];
}

/// <summary>H.264 RTP payload (RFC 6184): single NAL, STAP-A (24) and FU-A (28), packetization-mode 0/1.</summary>
public sealed class H264Depacketizer : NalDepacketizer
{
    private static readonly int[] ParameterSets = [7, 8];

    protected override IReadOnlyList<int> ParameterSetTypes => ParameterSets;

    protected override int NalType(ReadOnlySpan<byte> nal) => nal[0] & 0x1F;

    protected override bool IsKeyframeType(int nalType) => nalType == 5;

    protected override void ParsePayload(ReadOnlySpan<byte> payload)
    {
        var type = payload[0] & 0x1F;
        switch (type)
        {
            case >= 1 and <= 23:
                AppendNal(payload);
                break;
            case 24: // STAP-A
                for (var i = 1; i < payload.Length;)
                {
                    if (i + 2 > payload.Length)
                    {
                        throw new InvalidDataException("Truncated STAP-A.");
                    }

                    var size = ReadU16(payload, i);
                    i += 2;
                    if (size == 0 || i + size > payload.Length)
                    {
                        throw new InvalidDataException("Bad STAP-A unit size.");
                    }

                    AppendNal(payload.Slice(i, size));
                    i += size;
                }

                break;
            case 28: // FU-A
                if (payload.Length < 2)
                {
                    throw new InvalidDataException("Truncated FU-A.");
                }

                var fu = payload[1];
                var start = (fu & 0x80) != 0;
                var end = (fu & 0x40) != 0;
                if (start)
                {
                    byte header = (byte)((payload[0] & 0xE0) | (fu & 0x1F));
                    BeginFragment([header], payload[2..]);
                    if (end)
                    {
                        ContinueFragment([], true);
                    }
                }
                else
                {
                    ContinueFragment(payload[2..], end);
                }

                break;
            default:
                throw new InvalidDataException($"Unsupported H.264 payload type {type}.");
        }
    }
}

/// <summary>H.265 RTP payload (RFC 7798): single NAL, AP (48) and FU (49), without DONL.</summary>
public sealed class H265Depacketizer : NalDepacketizer
{
    private static readonly int[] ParameterSets = [32, 33, 34];

    protected override IReadOnlyList<int> ParameterSetTypes => ParameterSets;

    protected override int NalType(ReadOnlySpan<byte> nal) => (nal[0] >> 1) & 0x3F;

    /// <summary>IRAP pictures: BLA, IDR, CRA (16-21).</summary>
    protected override bool IsKeyframeType(int nalType) => nalType is >= 16 and <= 21;

    protected override void ParsePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            throw new InvalidDataException("Truncated H.265 payload.");
        }

        var type = (payload[0] >> 1) & 0x3F;
        switch (type)
        {
            case 48: // aggregation packet
                for (var i = 2; i < payload.Length;)
                {
                    if (i + 2 > payload.Length)
                    {
                        throw new InvalidDataException("Truncated AP.");
                    }

                    var size = ReadU16(payload, i);
                    i += 2;
                    if (size < 2 || i + size > payload.Length)
                    {
                        throw new InvalidDataException("Bad AP unit size.");
                    }

                    AppendNal(payload.Slice(i, size));
                    i += size;
                }

                break;
            case 49: // fragmentation unit
                var fu = payload[2];
                var start = (fu & 0x80) != 0;
                var end = (fu & 0x40) != 0;
                if (start)
                {
                    byte b0 = (byte)((payload[0] & 0x81) | ((fu & 0x3F) << 1));
                    BeginFragment([b0, payload[1]], payload[3..]);
                    if (end)
                    {
                        ContinueFragment([], true);
                    }
                }
                else
                {
                    ContinueFragment(payload[3..], end);
                }

                break;
            case 50:
                throw new InvalidDataException("PACI packets are not supported.");
            default:
                AppendNal(payload);
                break;
        }
    }
}
