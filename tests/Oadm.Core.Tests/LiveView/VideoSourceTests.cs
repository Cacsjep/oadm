using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.LiveView;

public sealed class VideoSourceTests
{
    [Fact]
    public void RecordedSingleSensorCameraListsItsTwoEnabledViewAreas()
    {
        // 10.0.0.48 (P3265-V, AXIS OS 12.11): one sensor, 8 view areas, I0 and I1 enabled.
        var caps = VapixParsers.ParseImageCapabilities(VapixParsers.ParseParameterList(Fixtures.Read("param-list-image-sources.txt")));

        Assert.Equal(["jpeg", "mjpeg", "h264", "h265"], caps.Formats);
        Assert.Collection(
            caps.Sources,
            s => Assert.Equal((1, "View Area 1", 0), (s.Camera, s.Name, s.Sensor)),
            s => Assert.Equal((2, "View Area 2", 0), (s.Camera, s.Name, s.Sensor)));
        Assert.All(caps.Sources, s => Assert.Equal(new ImageSize(1920, 1080), s.Resolutions[0]));
    }

    [Fact]
    public void MultisensorCameraListsOneSourcePerSensor()
    {
        // Shape of a 4-sensor camera (e.g. AXIS P3727-PLE) per the VAPIX parameter documentation:
        // Image.I0..I3 map to ImageSource 0..3, I4 is a quad view of all sensors.
        var text = string.Join('\n',
            "root.Properties.Image.Format=jpeg,mjpeg,h264,h265",
            "root.Properties.Image.Resolution=2592x1944,1920x1440,1280x960,640x480",
            "root.Properties.Image.I0.Resolution=2592x1944,1280x960,640x480",
            "root.Properties.Image.I4.Resolution=3840x2880,1920x1440,640x480",
            "ImageSource.NbrOfSources=4",
            "root.Image.I0.Name=Camera 1", "root.Image.I0.Source=0", "root.Image.I0.Enabled=yes",
            "root.Image.I1.Name=Camera 2", "root.Image.I1.Source=1", "root.Image.I1.Enabled=yes",
            "root.Image.I2.Name=Camera 3", "root.Image.I2.Source=2", "root.Image.I2.Enabled=yes",
            "root.Image.I3.Name=Camera 4", "root.Image.I3.Source=3", "root.Image.I3.Enabled=yes",
            "root.Image.I4.Name=Quad view", "root.Image.I4.Source=0", "root.Image.I4.Enabled=yes",
            "root.Image.I5.Name=View Area 6", "root.Image.I5.Source=0", "root.Image.I5.Enabled=no");
        var caps = VapixParsers.ParseImageCapabilities(VapixParsers.ParseParameterList(text));

        Assert.Equal([1, 2, 3, 4, 5], caps.Sources.Select(s => s.Camera));
        Assert.Equal([0, 1, 2, 3, 0], caps.Sources.Select(s => s.Sensor));
        Assert.Equal("Quad view", caps.Sources[4].Name);
        Assert.Equal(new ImageSize(3840, 2880), caps.Sources[4].Resolutions[0]);
        Assert.Equal(new ImageSize(2592, 1944), caps.Sources[1].Resolutions[0]); // no own list: device list
    }

    [Fact]
    public void EncoderChannelsWithoutEnabledFlagAreSources()
    {
        var text = string.Join('\n',
            "root.Properties.Image.Format=jpeg,mjpeg,h264",
            "root.Properties.Image.Resolution=720x576,704x576,352x288",
            "root.Image.I0.Name=Input 1", "root.Image.I0.Source=0",
            "root.Image.I1.Name=Input 2", "root.Image.I1.Source=1");
        var caps = VapixParsers.ParseImageCapabilities(VapixParsers.ParseParameterList(text));
        Assert.Equal(["Input 1", "Input 2"], caps.Sources.Select(s => s.Name));
    }

    [Fact]
    public void DeviceWithoutImageGroupHasOneSource()
    {
        var caps = VapixParsers.ParseImageCapabilities(VapixParsers.ParseParameterList("root.Properties.Image.Resolution=1280x720,640x360"));
        var source = Assert.Single(caps.Sources);
        Assert.Equal((1, "Camera"), (source.Camera, source.Name));
        Assert.Equal(2, source.Resolutions.Count);
    }

    [Fact]
    public async Task HubStreamsTheRequestedSourceAndDefaultsToTheFirst()
    {
        var factory = new LiveViewHubTests.FakeSourceFactory
        {
            Sources = [new(1, "View Area 1", 0, [new(1920, 1080), new(640, 360)]), new(3, "Camera 3", 2, [new(1280, 960), new(640, 480)])],
        };
        await using var hub = new LiveViewHub(factory);

        await using var first = await hub.SubscribeAsync(Guid.NewGuid(), 640, 480, 10, [VideoCodecKind.H264], CancellationToken.None);
        Assert.Equal(1, first.Key.Camera);
        Assert.Equal(new ImageSize(640, 360), first.Key.Size);

        await using var third = await hub.SubscribeAsync(Guid.NewGuid(), 640, 480, 10, [VideoCodecKind.H264], CancellationToken.None, camera: 3);
        Assert.Equal(3, third.Key.Camera);
        Assert.Equal(new ImageSize(640, 480), third.Key.Size); // that source's own 4:3 list

        var ex = await Assert.ThrowsAsync<LiveViewException>(() => hub.SubscribeAsync(Guid.NewGuid(), 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None, camera: 2));
        Assert.Equal(LiveViewError.NotSupported, ex.Error);
    }

    [Fact]
    public async Task DifferentSourcesOfOneDeviceUseSeparateUpstreams()
    {
        var factory = new LiveViewHubTests.FakeSourceFactory
        {
            Sources = [new(1, "View Area 1", 0, [new(640, 360)]), new(2, "View Area 2", 0, [new(640, 360)])],
        };
        await using var hub = new LiveViewHub(factory);
        var device = Guid.NewGuid();
        await using var a = await hub.SubscribeAsync(device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None, camera: 1);
        await using var b = await hub.SubscribeAsync(device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None, camera: 2);
        Assert.Equal(2, hub.ActiveUpstreams);
        Assert.Equal([1, 2], factory.OpenedKeys.Select(k => k.Camera));
    }
}
