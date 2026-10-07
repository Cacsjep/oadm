using System.Net;

using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.LiveView;

/// <summary>
/// Read-only RTSP tests against the first dev camera. <see cref="RecordFixtures"/> rewrites the RTP
/// fixtures when OADM_RECORD_RTP_DIR points at tests/Oadm.Core.Tests/Fixtures/LiveView.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class RtpRecorderTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task RecordFixtures()
    {
        var target = Environment.GetEnvironmentVariable("OADM_RECORD_RTP_DIR");
        if (string.IsNullOrEmpty(target))
        {
            return; // only records on request
        }

        foreach (var codec in new[] { VideoCodecKind.H264, VideoCodecKind.H265 })
        {
            var name = codec == VideoCodecKind.H265 ? "h265-640x360" : "h264-640x360";
            var options = Options(codec);
            await using var client = await RtspClient.ConnectAsync(Camera.Address, RtspClient.DefaultPort, new NetworkCredential(Camera.User, Camera.Password), TimeSpan.FromSeconds(5), CancellationToken.None);
            var url = RtspVideoSource.BuildUrl(options);
            var describe = await client.SendAsync("DESCRIBE", url, [new("Accept", "application/sdp")], CancellationToken.None);
            Assert.Equal(200, describe.StatusCode);
            var track = SdpVideoTrack.Parse(describe.Body)!;
            var baseUrl = describe.Header("Content-Base") is { } cb ? new Uri(cb) : url;
            var setup = await client.SendAsync("SETUP", track.ResolveControl(baseUrl), [new("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"), new("Blocksize", "64000")], CancellationToken.None);
            Assert.Equal(200, setup.StatusCode);
            Assert.Equal(200, (await client.SendAsync("PLAY", baseUrl, [new("Range", "npt=0.000-")], CancellationToken.None)).StatusCode);

            // 15 frames at 10 fps: the first keyframe plus a second of P frames.
            var packets = new List<byte[]>();
            var frames = 0;
            while (frames < 15)
            {
                var packet = await client.ReadInterleavedAsync(CancellationToken.None);
                Assert.NotNull(packet);
                if (packet.Value.Channel != 0)
                {
                    continue;
                }

                packets.Add(packet.Value.Data);
                if ((packet.Value.Data[1] & 0x80) != 0)
                {
                    frames++;
                }
            }

            await client.SendWithoutResponseAsync("TEARDOWN", baseUrl, CancellationToken.None);
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, name + ".sdp"), describe.Body.Replace(Camera.Address, "camera.invalid", StringComparison.Ordinal));
            await File.WriteAllBytesAsync(Path.Combine(target, name + ".rtp"), RtpFixtures.Encode(packets));
        }
    }

    /// <summary>
    /// Writes the access units of the recorded fixtures for the client's fake server
    /// (src/Oadm.Client/Api/FakeLiveView.*.au) when OADM_WRITE_FAKE_LIVEVIEW points at that folder.
    /// Format per access unit: 1 byte keyframe flag, 4 bytes big-endian length, Annex B data.
    /// </summary>
    [Fact]
    public async Task WriteFakeLiveViewResources()
    {
        var target = Environment.GetEnvironmentVariable("OADM_WRITE_FAKE_LIVEVIEW");
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        foreach (var (fixture, name) in new[] { ("h264-640x360", "FakeLiveView.h264.au"), ("h265-640x360", "FakeLiveView.h265.au") })
        {
            using var ms = new MemoryStream();
            foreach (var unit in RtpFixtures.Depacketize(fixture).Units)
            {
                ms.WriteByte(unit.IsKeyframe ? (byte)1 : (byte)0);
                var length = unit.Data.Length;
                ms.Write([(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
                ms.Write(unit.Data);
            }

            await File.WriteAllBytesAsync(Path.Combine(target, name), ms.ToArray());
        }
    }

    [HardwareFact]
    public async Task RtspSourceDeliversDecodableAccessUnits()
    {
        foreach (var codec in new[] { VideoCodecKind.H264, VideoCodecKind.H265 })
        {
            await using var source = await RtspVideoSource.OpenAsync(Options(codec), CancellationToken.None);
            var frames = new List<LiveVideoFrame>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var started = DateTime.UtcNow;
            await foreach (var frame in source.ReadFramesAsync(cts.Token))
            {
                frames.Add(frame);
                if (frames.Count == 20)
                {
                    break;
                }
            }

            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            Assert.Equal(20, frames.Count);
            Assert.True(frames[0].IsKeyframe);
            Assert.NotNull(frames[0].CodecConfig);
            Assert.All(frames, f => Assert.Equal(codec, f.Codec));
            Assert.All(frames, f => Assert.Equal(new byte[] { 0, 0, 0, 1 }, f.Data[..4]));

            // videokeyframeinterval=fps: a second keyframe within 20 frames at 10 fps.
            Assert.True(frames.Skip(1).Any(f => f.IsKeyframe), "no second keyframe within 2 s");
            var bytes = frames.Sum(f => (long)f.Data.Length);
            Console.WriteLine($"{codec}: 20 frames in {seconds:F2} s, {bytes * 8 / seconds / 1000:F0} kbit/s");
        }
    }

    private static RtspSourceOptions Options(VideoCodecKind codec) => new()
    {
        Address = Camera.Address,
        Credentials = new NetworkCredential(Camera.User, Camera.Password),
        Codec = codec,
        Size = new ImageSize(640, 360),
        Fps = 10,
    };
}
