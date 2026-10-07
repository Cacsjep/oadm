using Oadm.Core.LiveView.Rtp;
using Oadm.Core.LiveView.Rtsp;

namespace Oadm.Core.Tests.LiveView;

/// <summary>
/// Recorded RTP from 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11) in Fixtures/LiveView:
/// <c>NAME.sdp</c> (DESCRIBE body) and <c>NAME.rtp</c> (each packet as a 16-bit big-endian length + RTP bytes).
/// Re-record with <see cref="RtpRecorderTests"/>.
/// </summary>
internal static class RtpFixtures
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "LiveView");

    public static string Sdp(string name) => File.ReadAllText(Path.Combine(Directory, name + ".sdp"));

    public static IReadOnlyList<byte[]> Packets(string name) => Decode(File.ReadAllBytes(Path.Combine(Directory, name + ".rtp")));

    public static IReadOnlyList<byte[]> Decode(byte[] file)
    {
        var packets = new List<byte[]>();
        for (var i = 0; i + 2 <= file.Length;)
        {
            var length = (file[i] << 8) | file[i + 1];
            i += 2;
            packets.Add(file.AsSpan(i, length).ToArray());
            i += length;
        }

        return packets;
    }

    public static byte[] Encode(IEnumerable<byte[]> packets)
    {
        using var ms = new MemoryStream();
        foreach (var p in packets)
        {
            ms.WriteByte((byte)(p.Length >> 8));
            ms.WriteByte((byte)p.Length);
            ms.Write(p);
        }

        return ms.ToArray();
    }

    /// <summary>Runs all packets of a fixture through the matching depacketizer.</summary>
    public static (List<AccessUnit> Units, NalDepacketizer Depacketizer) Depacketize(string name, IEnumerable<byte[]>? packets = null)
    {
        var track = SdpVideoTrack.Parse(Sdp(name))!;
        NalDepacketizer depacketizer = track.Codec == Core.LiveView.VideoCodecKind.H265 ? new H265Depacketizer() : new H264Depacketizer();
        foreach (var nal in track.ParameterSets())
        {
            depacketizer.AddParameterSet(nal);
        }

        var units = new List<AccessUnit>();
        foreach (var bytes in packets ?? Packets(name))
        {
            Assert.True(RtpPacket.TryParse(bytes, out var packet));
            units.AddRange(depacketizer.Push(packet));
        }

        return (units, depacketizer);
    }
}
