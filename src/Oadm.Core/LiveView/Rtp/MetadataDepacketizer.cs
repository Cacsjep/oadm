using System.Buffers;
using System.Text;

namespace Oadm.Core.LiveView.Rtp;

/// <summary>One complete metadata document of an RTP metadata stream.</summary>
public sealed record MetadataDocument(string Xml, uint RtpTimestamp);

/// <summary>
/// Reassembles ONVIF metadata documents (<c>application/vnd.onvif.metadata</c>, ONVIF Streaming Specification 5.2.1.1)
/// from RTP: the payloads of consecutive packets are joined by sequence number and the RTP marker bit ends a document.
/// A sequence gap drops the document it hits (counted in <see cref="LostDocuments"/>) and everything up to the next
/// marker; a packet right after a lost document boundary is kept only when it starts a new XML document. Documents
/// above <see cref="MaxDocumentBytes"/> are dropped the same way.
/// </summary>
public sealed class MetadataDepacketizer
{
    /// <summary>Largest document kept (1 MB).</summary>
    public const int DefaultMaxDocumentBytes = 1024 * 1024;

    private readonly ArrayBufferWriter<byte> _document = new(16 * 1024);
    private ushort? _lastSequence;
    private bool _discarding;

    public MetadataDepacketizer(int maxDocumentBytes = DefaultMaxDocumentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        MaxDocumentBytes = maxDocumentBytes;
    }

    public int MaxDocumentBytes { get; }

    /// <summary>Documents dropped because of lost packets or their size.</summary>
    public int LostDocuments { get; private set; }

    /// <summary>Feeds one packet; returns the document it completed, or null.</summary>
    public MetadataDocument? Push(RtpPacket packet)
    {
        var payload = packet.Payload.Span;
        if (_lastSequence is { } last && (ushort)(last + 1) != packet.SequenceNumber)
        {
            // Packets are missing. Inside a document: that document is broken. At a boundary: at least one whole
            // document (or the start of this one) was lost. Keep this packet only when it starts a new document.
            LostDocuments++;
            _document.ResetWrittenCount();
            _discarding = !StartsDocument(payload);
        }
        else if (_lastSequence is null)
        {
            // First packet of the stream: a document start, or the tail of one sent before we joined.
            _discarding = !StartsDocument(payload);
        }

        _lastSequence = packet.SequenceNumber;

        if (!_discarding)
        {
            if (_document.WrittenCount + payload.Length > MaxDocumentBytes)
            {
                LostDocuments++;
                _document.ResetWrittenCount();
                _discarding = true;
            }
            else
            {
                _document.Write(payload);
            }
        }

        if (!packet.Marker)
        {
            return null;
        }

        var discarded = _discarding;
        _discarding = false;
        if (discarded || _document.WrittenCount == 0)
        {
            _document.ResetWrittenCount();
            return null;
        }

        var xml = Encoding.UTF8.GetString(_document.WrittenSpan);
        _document.ResetWrittenCount();
        return new MetadataDocument(xml, packet.Timestamp);
    }

    /// <summary>True when the payload begins an XML document ("&lt;?xml" or a start tag after optional whitespace/BOM).</summary>
    internal static bool StartsDocument(ReadOnlySpan<byte> payload)
    {
        var i = 0;
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
        {
            i = 3;
        }

        while (i < payload.Length && payload[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
        {
            i++;
        }

        if (i >= payload.Length || payload[i] != '<')
        {
            return false;
        }

        var rest = payload[(i + 1)..];
        if (rest.StartsWith("?xml"u8))
        {
            return true;
        }

        // A root start tag such as <tt:MetadataStream ...>; a fragment tail would start with a closing or inner tag.
        if (rest.IsEmpty || rest[0] == '/')
        {
            return false;
        }

        var end = rest.IndexOfAny((byte)' ', (byte)'>', (byte)'\n');
        var name = end < 0 ? rest : rest[..end];
        return name.EndsWith("MetadataStream"u8);
    }
}
