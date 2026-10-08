using System.Text;

using Oadm.Core.LiveView.Rtp;
using Oadm.Core.LiveView.Rtsp;

namespace Oadm.Core.Tests.LiveView;

/// <summary>
/// <see cref="MetadataDepacketizer"/> and <see cref="SdpMetadataTrack"/> with the event stream recorded from 10.0.0.48
/// (Fixtures/LiveView/events.*, AXIS P3265-V, AXIS OS 12.11: one document per RTP packet) and with synthetic
/// fragmentation, loss and oversize documents.
/// </summary>
public sealed class MetadataDepacketizerTests
{
    [Fact]
    public void SdpOfTheDeviceHasTheOnvifMetadataMedia()
    {
        var track = SdpMetadataTrack.Parse(RtpFixtures.Sdp("events"));

        Assert.NotNull(track);
        Assert.Equal(98, track.PayloadType);
        Assert.Equal(90000, track.ClockRate);
        Assert.Equal(
            "rtsp://camera.invalid/axis-media/media.amp/stream=0?video=0&audio=0&event=on",
            track.ResolveControl(new Uri("rtsp://camera.invalid/axis-media/media.amp/")).ToString());
    }

    [Fact]
    public void SdpWithoutMetadataHasNoTrack()
    {
        Assert.Null(SdpMetadataTrack.Parse(RtpFixtures.Sdp("h264-640x360")));
        Assert.Null(SdpMetadataTrack.Parse("v=0\r\nm=application 0 RTP/AVP 99\r\na=rtpmap:99 vnd.other/90000\r\n"));
    }

    [Fact]
    public void RecordedStreamYieldsEveryDocument()
    {
        var depacketizer = new MetadataDepacketizer();
        var documents = Run(depacketizer, RtpFixtures.Packets("events"));

        Assert.Equal(129, documents.Count);
        Assert.Equal(0, depacketizer.LostDocuments);
        Assert.All(documents, d => Assert.StartsWith("<?xml", d.Xml, StringComparison.Ordinal));
        Assert.All(documents, d => Assert.EndsWith("</tt:MetadataStream>", d.Xml.TrimEnd(), StringComparison.Ordinal));
        Assert.True(documents.Count(d => d.Xml.Contains("PropertyOperation=\"Initialized\"", StringComparison.Ordinal)) >= 100);
    }

    [Fact]
    public void FragmentsAreJoinedUntilTheMarker()
    {
        var originals = Documents();
        var packets = Fragment(originals.Take(5), parts: 3);
        var documents = Run(new MetadataDepacketizer(), packets);

        Assert.Equal(originals.Take(5).Select(d => d.Xml), documents.Select(d => d.Xml));
    }

    [Fact]
    public void GapInsideADocumentDropsOnlyThatDocument()
    {
        var originals = Documents();
        var packets = Fragment(originals.Skip(1).Take(4), parts: 3).ToList();
        packets.RemoveAt(4); // middle fragment of the second document
        var depacketizer = new MetadataDepacketizer();
        var documents = Run(depacketizer, packets);

        Assert.Equal(1, depacketizer.LostDocuments);
        Assert.Equal(Picks(1, 3, 4).Select(i => originals[i].Xml), documents.Select(d => d.Xml));
    }

    [Fact]
    public void GapAtADocumentBoundaryKeepsTheNextDocument()
    {
        var originals = Documents();
        var packets = Fragment(originals.Skip(1).Take(4), parts: 1).ToList();
        packets.RemoveAt(1); // a whole document
        var depacketizer = new MetadataDepacketizer();
        var documents = Run(depacketizer, packets);

        Assert.Equal(1, depacketizer.LostDocuments);
        Assert.Equal(Picks(1, 3, 4).Select(i => originals[i].Xml), documents.Select(d => d.Xml));
    }

    [Fact]
    public void LostStartOfADocumentDiscardsItsTail()
    {
        var originals = Documents();
        var packets = Fragment(originals.Skip(1).Take(3), parts: 2).ToList();
        packets.RemoveAt(2); // first half of the second document: its tail does not start a document
        var depacketizer = new MetadataDepacketizer();
        var documents = Run(depacketizer, packets);

        Assert.Equal(1, depacketizer.LostDocuments);
        Assert.Equal(Picks(1, 3).Select(i => originals[i].Xml), documents.Select(d => d.Xml));
    }

    [Fact]
    public void JoiningInTheMiddleOfADocumentWaitsForTheNextOne()
    {
        var originals = Documents();
        var packets = Fragment(originals.Skip(1).Take(2), parts: 2).Skip(1).ToList();
        var documents = Run(new MetadataDepacketizer(), packets);

        Assert.Equal([originals[2].Xml], documents.Select(d => d.Xml));
    }

    [Fact]
    public void OversizeDocumentIsDropped()
    {
        var originals = Documents();
        var depacketizer = new MetadataDepacketizer(maxDocumentBytes: 1500);
        var big = "<?xml version=\"1.0\"?><tt:MetadataStream>" + new string('x', 3000) + "</tt:MetadataStream>";
        var packets = Fragment([new MetadataDocument(big, 0), originals[1]], parts: 4);
        var documents = Run(depacketizer, packets);

        Assert.Equal(1, depacketizer.LostDocuments);
        Assert.Equal([originals[1].Xml], documents.Select(d => d.Xml));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><a/>", true)]
    [InlineData("\r\n<tt:MetadataStream xmlns:tt=\"x\">", true)]
    [InlineData("<tt:Event><wsnt:NotificationMessage>", false)]
    [InlineData("</tt:MetadataStream>", false)]
    [InlineData("ssage>", false)]
    public void DocumentStartIsRecognized(string text, bool expected) =>
        Assert.Equal(expected, MetadataDepacketizer.StartsDocument(Encoding.UTF8.GetBytes(text)));

    private static int[] Picks(params int[] indexes) => indexes;

    internal static List<MetadataDocument> Documents() => Run(new MetadataDepacketizer(), RtpFixtures.Packets("events"));

    internal static List<MetadataDocument> Run(MetadataDepacketizer depacketizer, IEnumerable<byte[]> packets)
    {
        var documents = new List<MetadataDocument>();
        foreach (var bytes in packets)
        {
            Assert.True(RtpPacket.TryParse(bytes, out var packet));
            if (depacketizer.Push(packet) is { } document)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    /// <summary>RTP packets (payload type 98, consecutive sequence numbers) carrying each document in <paramref name="parts"/> pieces.</summary>
    internal static IEnumerable<byte[]> Fragment(IEnumerable<MetadataDocument> documents, int parts, ushort firstSequence = 1000)
    {
        var seq = firstSequence;
        foreach (var document in documents)
        {
            var bytes = Encoding.UTF8.GetBytes(document.Xml);
            var size = (bytes.Length + parts - 1) / parts;
            for (var offset = 0; offset < bytes.Length; offset += size)
            {
                var length = Math.Min(size, bytes.Length - offset);
                var marker = offset + length >= bytes.Length;
                yield return Rtp(seq++, marker, bytes.AsSpan(offset, length));
            }
        }
    }

    internal static byte[] Rtp(ushort seq, bool marker, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0) | 98);
        packet[2] = (byte)(seq >> 8);
        packet[3] = (byte)seq;
        packet[8] = 0x12;
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }
}
