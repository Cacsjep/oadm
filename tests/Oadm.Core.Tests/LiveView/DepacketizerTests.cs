using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtp;
using Oadm.Core.LiveView.Rtsp;

namespace Oadm.Core.Tests.LiveView;

public sealed class DepacketizerTests
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    [Theory]
    [InlineData("h264-640x360", 5, 5)]    // IDR
    [InlineData("h265-640x360", 16, 21)]  // IRAP (BLA, IDR, CRA)
    public void RecordedStreamYieldsOneAccessUnitPerFrameStartingWithAKeyframe(string fixture, int keyNalTypeMin, int keyNalTypeMax)
    {
        var (units, depacketizer) = RtpFixtures.Depacketize(fixture);

        Assert.Equal(15, units.Count);
        Assert.Equal(0, depacketizer.DroppedAccessUnits);
        Assert.True(units[0].IsKeyframe);
        Assert.NotNull(units[0].CodecConfig);
        Assert.All(units, u => Assert.Equal(StartCode, u.Data[..4]));
        Assert.All(units.Skip(1).Where(u => !u.IsKeyframe), u => Assert.Null(u.CodecConfig));

        // Timestamps advance by about 9000 (90 kHz / 10 fps; the camera clock jitters by a few ticks).
        for (var i = 1; i < units.Count; i++)
        {
            Assert.InRange(units[i].RtpTimestamp - units[i - 1].RtpTimestamp, 8900u, 9100u);
        }

        var types = NalTypes(units[0].Data, fixture.StartsWith("h265", StringComparison.Ordinal));
        Assert.Contains(types, t => t >= keyNalTypeMin && t <= keyNalTypeMax);
    }

    [Fact]
    public void H264KeyframeCarriesSpsAndPpsBeforeTheIdr()
    {
        var (units, _) = RtpFixtures.Depacketize("h264-640x360");
        var types = NalTypes(units[0].Data, h265: false);
        Assert.True(types.IndexOf(7) >= 0 && types.IndexOf(8) > types.IndexOf(7) && types.IndexOf(5) > types.IndexOf(8), string.Join(",", types));
    }

    [Fact]
    public void H265KeyframeCarriesVpsSpsPps()
    {
        var (units, _) = RtpFixtures.Depacketize("h265-640x360");
        var types = NalTypes(units[0].Data, h265: true);
        Assert.Contains(32, types);
        Assert.Contains(33, types);
        Assert.Contains(34, types);
    }

    [Fact]
    public void LostPacketDropsFramesUntilTheNextKeyframe()
    {
        var packets = RtpFixtures.Packets("h264-640x360").ToList();
        var all = RtpFixtures.Depacketize("h264-640x360").Units;
        var secondKey = all.FindIndex(1, u => u.IsKeyframe);

        // Drop one packet of the third frame.
        var thirdFrameTs = all[2].RtpTimestamp;
        var victim = packets.FindIndex(p => RtpPacket.TryParse(p, out var rtp) && rtp.Timestamp == thirdFrameTs);
        packets.RemoveAt(victim);
        var (units, depacketizer) = RtpFixtures.Depacketize("h264-640x360", packets);

        Assert.Equal(2, units.Count(u => u.RtpTimestamp < thirdFrameTs));
        Assert.DoesNotContain(units, u => u.RtpTimestamp == thirdFrameTs);
        var resumed = units.First(u => u.RtpTimestamp > thirdFrameTs);
        if (secondKey > 0)
        {
            Assert.True(resumed.IsKeyframe);
            Assert.Equal(all[secondKey].RtpTimestamp, resumed.RtpTimestamp);
        }

        Assert.True(depacketizer.DroppedAccessUnits > 0);
    }

    [Fact]
    public void StartsOnlyAtAKeyframe()
    {
        var packets = RtpFixtures.Packets("h264-640x360");
        var all = RtpFixtures.Depacketize("h264-640x360").Units;

        // Start in the middle of the first GOP: everything before the next keyframe is dropped.
        var startTs = all[3].RtpTimestamp;
        var (units, _) = RtpFixtures.Depacketize("h264-640x360", packets.Where(p => RtpPacket.TryParse(p, out var r) && r.Timestamp >= startTs));
        Assert.All(units.Take(1), u => Assert.True(u.IsKeyframe));
    }

    [Fact]
    public void H264StapAAndFuAAreReassembled()
    {
        var depacketizer = new H264Depacketizer();
        byte[] sps = [0x67, 0x42, 0x00, 0x1E];
        byte[] pps = [0x68, 0xCE, 0x38, 0x80];
        byte[] stapA = [0x78, 0, (byte)sps.Length, .. sps, 0, (byte)pps.Length, .. pps];
        byte[] idrBody = [.. Enumerable.Range(0, 10).Select(i => (byte)i)];

        Assert.Empty(depacketizer.Push(Packet(1, 1000, false, stapA)));
        Assert.Empty(depacketizer.Push(Packet(2, 1000, false, [0x7C, 0x85, .. idrBody[..5]]))); // FU-A start, type 5
        var units = depacketizer.Push(Packet(3, 1000, true, [0x7C, 0x45, .. idrBody[5..]]));     // FU-A end

        var unit = Assert.Single(units);
        Assert.True(unit.IsKeyframe);
        byte[] expected = [.. StartCode, .. sps, .. StartCode, .. pps, .. StartCode, 0x65, .. idrBody];
        Assert.Equal(expected, unit.Data);
        Assert.Equal([.. StartCode, .. sps, .. StartCode, .. pps], unit.CodecConfig!);
    }

    [Fact]
    public void H264KeyframeWithoutInbandParameterSetsGetsThemFromTheSdp()
    {
        var depacketizer = new H264Depacketizer();
        depacketizer.AddParameterSet([0x67, 1, 2]);
        depacketizer.AddParameterSet([0x68, 3]);
        var unit = Assert.Single(depacketizer.Push(Packet(10, 5, true, [0x65, 9, 9])));
        Assert.Equal([0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 9, 9], unit.Data);
    }

    [Fact]
    public void MissingMarkerCompletesTheUnitOnTimestampChange()
    {
        var depacketizer = new H264Depacketizer();
        Assert.Empty(depacketizer.Push(Packet(1, 100, false, [0x65, 1])));
        var unit = Assert.Single(depacketizer.Push(Packet(2, 200, false, [0x41, 2])));
        Assert.Equal(100u, unit.RtpTimestamp);
    }

    [Fact]
    public void H265FragmentationUnitRebuildsTheNalHeader()
    {
        var depacketizer = new H265Depacketizer();
        // FU header type 49: 0x62 0x01; FU header S=1 type 19 (IDR_W_RADL)
        Assert.Empty(depacketizer.Push(Packet(1, 7, false, [0x62, 0x01, 0x80 | 19, 0xAA])));
        var unit = Assert.Single(depacketizer.Push(Packet(2, 7, true, [0x62, 0x01, 0x40 | 19, 0xBB])));
        Assert.True(unit.IsKeyframe);
        Assert.Equal([0, 0, 0, 1, 19 << 1, 0x01, 0xAA, 0xBB], unit.Data);
    }

    [Fact]
    public void RtpHeaderWithCsrcExtensionAndPaddingIsStripped()
    {
        byte[] packet =
        [
            0xB1, 0xE0, 0x00, 0x05, 0, 0, 0, 9, 0, 0, 0, 1, // V=2 P=1 X=1 CC=1, M=1 PT=96
            0, 0, 0, 2,                                     // CSRC
            0xBE, 0xDE, 0, 1, 1, 2, 3, 4,                   // extension, 1 word
            0x65, 0x77,                                     // payload
            0, 0, 3,                                        // padding (3 bytes)
        ];
        Assert.True(RtpPacket.TryParse(packet, out var rtp));
        Assert.True(rtp.Marker);
        Assert.Equal(96, rtp.PayloadType);
        Assert.Equal(5, rtp.SequenceNumber);
        Assert.Equal(9u, rtp.Timestamp);
        Assert.Equal([0x65, 0x77], rtp.Payload.ToArray());
    }

    [Fact]
    public void SdpOfTheRecordedStreamsParses()
    {
        var h264 = SdpVideoTrack.Parse(RtpFixtures.Sdp("h264-640x360"))!;
        Assert.Equal(VideoCodecKind.H264, h264.Codec);
        Assert.Equal(96, h264.PayloadType);
        Assert.Equal(90000, h264.ClockRate);
        Assert.Equal(2, h264.ParameterSets().Count);

        var h265 = SdpVideoTrack.Parse(RtpFixtures.Sdp("h265-640x360"))!;
        Assert.Equal(VideoCodecKind.H265, h265.Codec);
        Assert.Equal(3, h265.ParameterSets().Count);
        var control = h265.ResolveControl(new Uri("rtsp://camera.invalid/axis-media/media.amp/"));
        Assert.StartsWith("rtsp://camera.invalid/axis-media/media.amp/stream=0?", control.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void SdpRelativeControlResolvesAgainstTheContentBase()
    {
        var track = SdpVideoTrack.Parse("v=0\r\nm=video 0 RTP/AVP 97\r\na=rtpmap:97 H264/90000\r\na=control:trackID=1\r\nm=audio 0 RTP/AVP 0\r\na=control:trackID=2\r\n")!;
        Assert.Equal(97, track.PayloadType);
        Assert.Equal("rtsp://cam/axis-media/media.amp/trackID=1", track.ResolveControl(new Uri("rtsp://cam/axis-media/media.amp")).AbsoluteUri);
    }

    [Fact]
    public void AnAccessUnitLargerThan8MegabytesIsDroppedAndTheNextKeyframeResumes()
    {
        Assert.Equal(8 * 1024 * 1024, NalDepacketizer.DefaultMaxAccessUnitBytes);
        var depacketizer = new H264Depacketizer();
        ushort seq = 0;
        var units = new List<AccessUnit>();

        void Frame(uint ts, int size)
        {
            units.AddRange(depacketizer.Push(Packet(seq++, ts, false, [0x67, 1, 2, 3]))); // SPS
            units.AddRange(depacketizer.Push(Packet(seq++, ts, false, [0x68, 4]))); // PPS
            const int chunk = 60_000;
            for (var offset = 0; offset < size; offset += chunk)
            {
                var start = offset == 0;
                var end = offset + chunk >= size;
                var payload = new byte[2 + Math.Min(chunk, size - offset)];
                payload[0] = 0x60 | 28; // FU-A
                payload[1] = (byte)((start ? 0x80 : 0) | (end ? 0x40 : 0) | 5); // IDR
                units.AddRange(depacketizer.Push(Packet(seq++, ts, end, payload)));
            }
        }

        Frame(1000, 9 * 1024 * 1024); // oversized keyframe
        Assert.Empty(units);
        Assert.Equal(1, depacketizer.OversizedAccessUnits);
        Assert.Equal(1, depacketizer.DroppedAccessUnits);

        Frame(4000, 100_000); // the next keyframe
        var unit = Assert.Single(units);
        Assert.True(unit.IsKeyframe);
        Assert.InRange(unit.Data.Length, 100_000, 101_000);
    }

    private static RtpPacket Packet(ushort seq, uint ts, bool marker, byte[] payload) => new(marker, 96, seq, ts, 1, payload);

    private static List<int> NalTypes(byte[] annexB, bool h265)
    {
        var types = new List<int>();
        for (var i = 0; i + 4 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                var b = annexB[i + 4];
                types.Add(h265 ? (b >> 1) & 0x3F : b & 0x1F);
                i += 3;
            }
        }

        return types;
    }
}
