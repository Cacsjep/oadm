using System.Diagnostics;

using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Tests.Hardware;
using Oadm.Server.Tests.Support;
using Oadm.Sdk.Devices;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// Pulls live video from the first dev camera through the in-process server (RTSP with the stored
/// credentials, relayed over gRPC). Read-only for the camera: it only streams.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class LiveViewHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task PrefersH265AndRelaysDecodableAccessUnits()
    {
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var id = await AddCameraAsync(host);

        var (frames, firstFrame, seconds) = await PullAsync(host, id, 20, Proto.VideoCodec.H264, Proto.VideoCodec.H265);
        Assert.All(frames, f => Assert.Equal(Proto.VideoCodec.H265, f.Codec));
        AssertFrames(frames);
        Console.WriteLine($"H.265 via server: first frame after {firstFrame.TotalMilliseconds:F0} ms, {Rate(frames, seconds)}");
    }

    [HardwareFact]
    public async Task H264OnlyViewerGetsH264()
    {
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var id = await AddCameraAsync(host);

        var (frames, firstFrame, seconds) = await PullAsync(host, id, 20, Proto.VideoCodec.H264);
        Assert.All(frames, f => Assert.Equal(Proto.VideoCodec.H264, f.Codec));
        AssertFrames(frames);
        Console.WriteLine($"H.264 via server: first frame after {firstFrame.TotalMilliseconds:F0} ms, {Rate(frames, seconds)}");
    }

    [HardwareFact]
    public async Task ListsTheEnabledViewAreasAndStreamsViewArea2()
    {
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var id = await AddCameraAsync(host);

        var sources = await host.LiveView.ListSourcesAsync(new Proto.LiveViewSourcesRequest { DeviceId = id.ToString() });
        foreach (var s in sources.Sources)
        {
            Console.WriteLine($"source camera={s.Camera} name={s.Name} sensor={s.Sensor} max={s.MaxWidth}x{s.MaxHeight}");
        }

        Assert.True(sources.Sources.Count >= 1);
        if (sources.Sources.Count < 2)
        {
            return; // camera reconfigured to a single view area
        }

        var second = sources.Sources[1];
        var request = new Proto.LiveViewRequest { DeviceId = id.ToString(), MaxWidth = 640, MaxHeight = 360, Fps = 10, Camera = second.Camera };
        request.AcceptedCodecs.Add(Proto.VideoCodec.H264);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = host.LiveView.Watch(request, cancellationToken: timeout.Token);
        var frames = new List<Proto.LiveViewFrame>();
        while (frames.Count < 5 && await call.ResponseStream.MoveNext(timeout.Token))
        {
            frames.Add(call.ResponseStream.Current);
        }

        Assert.Equal(5, frames.Count);
        Assert.All(frames, f => Assert.Equal(second.Camera, f.Camera));
        Assert.True(frames[0].Keyframe);
    }

    private static void AssertFrames(List<Proto.LiveViewFrame> frames)
    {
        Assert.True(frames.Count >= 5);
        Assert.True(frames[0].Keyframe, "the first frame a viewer gets is a keyframe");
        Assert.False(frames[0].CodecConfig.IsEmpty, "keyframes carry the parameter sets");
        Assert.All(frames, f => Assert.Equal((640, 360), (f.Width, f.Height)));
        Assert.All(frames, f => Assert.Equal(new byte[] { 0, 0, 0, 1 }, f.Data.Span[..4].ToArray()));
    }

    private static string Rate(List<Proto.LiveViewFrame> frames, double seconds)
    {
        var bytes = frames.Sum(f => (long)f.Data.Length);
        return $"{frames.Count} frames in {seconds:F2} s = {frames.Count / seconds:F1} fps, {bytes * 8 / seconds / 1000:F0} kbit/s";
    }

    private static async Task<Guid> AddCameraAsync(TestServerHost host)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device
            {
                Serial = "B8A44F631339",
                Address = Camera.Address,
                Model = "P3265-V",
                Status = DeviceStatus.Ok,
                Scheme = Camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
            },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, Camera.User, Camera.Password, CancellationToken.None);
        return device.Id;
    }

    private static async Task<(List<Proto.LiveViewFrame> Frames, TimeSpan FirstFrame, double Seconds)> PullAsync(
        TestServerHost host, Guid id, int count, params Proto.VideoCodec[] codecs)
    {
        var request = new Proto.LiveViewRequest { DeviceId = id.ToString(), MaxWidth = 640, MaxHeight = 360, Fps = 10 };
        request.AcceptedCodecs.AddRange(codecs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();
        using var call = host.LiveView.Watch(request, cancellationToken: timeout.Token);
        var frames = new List<Proto.LiveViewFrame>();
        TimeSpan first = default;
        TimeSpan firstFrameAt = default;
        while (frames.Count < count && await call.ResponseStream.MoveNext(timeout.Token))
        {
            if (frames.Count == 0)
            {
                first = watch.Elapsed;
                firstFrameAt = watch.Elapsed;
            }

            frames.Add(call.ResponseStream.Current);
        }

        var seconds = (watch.Elapsed - firstFrameAt).TotalSeconds;
        return (frames, first, seconds);
    }
}
