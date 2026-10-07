using System.Net;

using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.LiveView;

public sealed class NegotiationAndAuthTests
{
    [Fact]
    public void PrefersH265WhenBothSidesSupportIt()
    {
        Assert.Equal([VideoCodecKind.H265, VideoCodecKind.H264],
            LiveViewNegotiation.Order([VideoCodecKind.H264, VideoCodecKind.H265], [VideoCodecKind.H264, VideoCodecKind.H265]));
    }

    [Fact]
    public void ClientWithoutH265GetsH264()
    {
        Assert.Equal([VideoCodecKind.H264], LiveViewNegotiation.Order([VideoCodecKind.H264, VideoCodecKind.H265], [VideoCodecKind.H264]));
    }

    [Fact]
    public void EmptyClientListMeansH264()
    {
        Assert.Equal([VideoCodecKind.H264], LiveViewNegotiation.Order([VideoCodecKind.H265, VideoCodecKind.H264], []));
    }

    [Fact]
    public void NoCommonCodecGivesAnEmptyList()
    {
        Assert.Empty(LiveViewNegotiation.Order([VideoCodecKind.H264], [VideoCodecKind.H265]));
    }

    [Fact]
    public void CameraFormatsMapToCodecs()
    {
        var parameters = VapixParsers.ParseParameterList(
            "root.Properties.Image.Format=jpeg,mjpeg,h264,h265\n"
            + "root.Properties.Image.Resolution=1920x1080,1440x1080,1280x720,800x600,640x480,640x360,320x180\n");
        var caps = LiveViewCapabilities.FromImageCapabilities(VapixParsers.ParseImageCapabilities(parameters));
        Assert.Equal([VideoCodecKind.H264, VideoCodecKind.H265], caps.Codecs);
        Assert.Equal(7, caps.Resolutions.Count);
        Assert.Equal(new ImageSize(1920, 1080), caps.Resolutions[0]);
    }

    [Fact]
    public void UnknownFormatsDefaultToH264()
    {
        var caps = LiveViewCapabilities.FromImageCapabilities(new ImageCapabilities([], [], []));
        Assert.Equal([VideoCodecKind.H264], caps.Codecs);
    }

    [Theory]
    [InlineData(640, 360, "640x360")]
    [InlineData(700, 500, "640x360")]   // keeps 16:9, does not pick 640x480
    [InlineData(1280, 800, "1280x720")]
    [InlineData(100, 100, "320x180")]   // nothing fits: smallest of the sensor aspect
    [InlineData(0, 0, "640x360")]       // defaults
    public void ResolutionFitsTheBoxAndKeepsTheSensorAspect(int maxW, int maxH, string expected)
    {
        ImageSize[] supported = [new(1920, 1080), new(1440, 1080), new(1280, 720), new(800, 600), new(640, 480), new(640, 360), new(320, 180)];
        Assert.Equal(expected, LiveViewNegotiation.ChooseResolution(supported, maxW, maxH).ToString());
    }

    [Fact]
    public void ResolutionWithoutCameraListIsTheRequestedBox()
    {
        Assert.Equal(new ImageSize(800, 450), LiveViewNegotiation.ChooseResolution([], 800, 450));
    }

    [Fact]
    public void FpsIsClamped()
    {
        Assert.Equal(10, LiveViewNegotiation.ClampFps(0));
        Assert.Equal(30, LiveViewNegotiation.ClampFps(120));
        Assert.Equal(5, LiveViewNegotiation.ClampFps(5));
    }

    [Fact]
    public void DigestMatchesTheRfc2617Example()
    {
        var digest = DigestAuthenticator.FromChallenges(
            ["Basic realm=\"x\"", "Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""],
            new NetworkCredential("Mufasa", "Circle Of Life"),
            () => "0a4f113b")!;

        var header = digest.Authorize("GET", "/dir/index.html");
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", header, StringComparison.Ordinal);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicOnlyChallengeIsRefused()
    {
        Assert.Null(DigestAuthenticator.FromChallenges(["Basic realm=\"AXIS_B8A44F631339\""], new NetworkCredential("root", "x")));
    }

    [Fact]
    public void RtspUrlUsesPort554AndRequestsAKeyframePerSecond()
    {
        var url = RtspVideoSource.BuildUrl(new RtspSourceOptions { Address = "10.0.0.48:443", Codec = VideoCodecKind.H265, Size = new ImageSize(640, 360), Fps = 10 });
        Assert.Equal("rtsp://10.0.0.48/axis-media/media.amp?videocodec=h265&camera=1&resolution=640x360&fps=10&videokeyframeinterval=10&audio=0", url.AbsoluteUri);

        var third = RtspVideoSource.BuildUrl(new RtspSourceOptions { Address = "10.0.0.48", Codec = VideoCodecKind.H264, Size = new ImageSize(640, 360), Camera = 3 });
        Assert.Contains("videocodec=h264&camera=3&", third.AbsoluteUri, StringComparison.Ordinal);

        var v6 = RtspVideoSource.BuildUrl(new RtspSourceOptions { Address = "fe80::1", Codec = VideoCodecKind.H264, Size = new ImageSize(320, 180) });
        Assert.StartsWith("rtsp://[fe80::1]/axis-media/media.amp?videocodec=h264&camera=1&resolution=320x180", v6.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RTP/AVP/TCP;unicast;interleaved=0-1;ssrc=1234", (byte)0)]
    [InlineData("RTP/AVP/TCP;unicast;interleaved=2-3", (byte)2)]
    public void InterleavedChannelIsReadFromTheTransport(string transport, byte expected)
    {
        Assert.Equal(expected, RtspVideoSource.ParseInterleavedChannel(transport));
    }
}
