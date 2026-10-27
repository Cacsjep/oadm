using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Probes;
using Oadm.Core.LiveView.Rtp;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

using Eventually = Oadm.Sdk.Testing.Eventually;

namespace Oadm.Core.Tests.LiveView.Probes;

/// <summary>
/// Stream probes against a scripted RTSP camera on loopback: RTP over UDP with receiver reports, the TCP fallback on 461,
/// a full port range and a site route, the counters, the end of a stream, and the server's <see cref="DeviceStreamProbes"/>.
/// </summary>
public sealed class RtspStreamProbeTests
{
    private const string Fixture = "h264-640x360"; // a=framerate:10

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task UdpStreamIsCountedFromTheCamerasPortsAndReportedBack()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 99, skip: 10));
        var ports = TestPorts();
        await using var probe = await RtspStreamProbe.OpenAsync(Options(camera, ports), Ct);

        Assert.Equal(StreamProbeTransport.Udp, probe.Transport);
        Assert.Equal(1, ports.InUse);
        Assert.Contains($"client_port={probe.LocalRtpPort}-{probe.LocalRtpPort + 1}", camera.Transports.Single(), StringComparison.Ordinal);
        await Eventually.TrueAsync(() => probe.Read().Packets == 99, "99 packets arrived");

        var read = probe.Read();
        Assert.Equal(1, read.LostPackets);
        Assert.Equal(99 * 200, read.Bytes);
        Assert.Equal(("h264", 640, 360), (probe.Codec, probe.Width, probe.Height));
        Assert.Equal(10, probe.Fps); // asked 25, the camera announces 10

        // Right after PLAY one packet from each port opens the way through a firewall; the RTCP one is a receiver report.
        await Eventually.TrueAsync(() => !camera.Rtcp.IsEmpty && !camera.RtpFromClient.IsEmpty, "the camera got the first packets");
        Assert.True(camera.Rtcp.TryPeek(out var report));
        Assert.Equal(RtcpPackets.ReceiverReport, report[1]);
        Assert.Equal(probe.LocalRtpPort + 1, camera.RtcpSenderPort);

        await probe.DisposeAsync();
        await probe.Completion.WaitAsync(Eventually.DefaultTimeout);
        Assert.Equal(0, ports.InUse);
        await Eventually.TrueAsync(() => camera.Methods.Contains("TEARDOWN"), "TEARDOWN sent");
    }

    [Fact]
    public async Task ACameraThatRefusesUdpStreamsOverTcp()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 59), refuseUdp: true);
        var ports = TestPorts();
        await using var probe = await RtspStreamProbe.OpenAsync(Options(camera, ports), Ct);

        Assert.Equal(StreamProbeTransport.Tcp, probe.Transport);
        Assert.Null(probe.LocalRtpPort);
        Assert.Equal(0, ports.InUse); // the pair went back at once
        Assert.Equal(2, camera.Transports.Count);
        Assert.StartsWith("RTP/AVP;unicast;client_port=", camera.Transports[0], StringComparison.Ordinal);
        Assert.StartsWith("RTP/AVP/TCP;unicast;interleaved=0-1", camera.Transports[1], StringComparison.Ordinal);
        await Eventually.TrueAsync(() => probe.Read().Packets == 60, "60 packets arrived");
        Assert.False(probe.UdpBlocked); // refused, not blocked
    }

    [Fact]
    public async Task UdpThatNeverArrivesIsTornDownAndTheSameStreamOpensOverTcp()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 59), dropUdp: true);
        var ports = TestPorts();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var opening = RtspStreamProbe.OpenAsync(Options(camera, ports) with { Time = time, UdpStartTimeout = RtspStreamProbe.UdpStartTime }, Ct);

        // The UDP stream plays, nothing comes: after 3 s (fake time) the probe gives up on UDP.
        await Eventually.TrueAsync(() => camera.Methods.Count(m => m == "PLAY") == 1, "the UDP stream plays");
        Assert.False(opening.IsCompleted);
        await Eventually.TrueAsync(
            () =>
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                return opening.IsCompleted;
            },
            "the probe reopened");
        await using var probe = await opening;

        Assert.Equal(StreamProbeTransport.Tcp, probe.Transport);
        Assert.True(probe.UdpBlocked);
        Assert.True(((Oadm.Sdk.Devices.IDeviceStreamProbe)probe).UdpBlocked);
        Assert.Equal(0, ports.InUse);
        Assert.Equal(2, camera.Transports.Count);
        Assert.StartsWith("RTP/AVP;unicast;client_port=", camera.Transports[0], StringComparison.Ordinal);
        Assert.StartsWith("RTP/AVP/TCP;unicast;interleaved=0-1", camera.Transports[1], StringComparison.Ordinal);
        await Eventually.TrueAsync(() => camera.Methods.Contains("TEARDOWN"), "the UDP stream was torn down");
        Assert.Equal(2, camera.Sessions.Count);
        await Eventually.TrueAsync(() => probe.Read().Packets == 60, "60 packets arrived over TCP");
    }

    [Fact]
    public async Task AFullRangeAndTcpOnlyRequestsUseTcpWithoutAskingForUdp()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 9));
        var ports = new RtpUdpPorts(53000, 53001);
        using var held = ports.TryRent(port => new RtpTransportTests.FakeUdpPort(port))!;
        await using (var probe = await RtspStreamProbe.OpenAsync(Options(camera, ports), Ct))
        {
            Assert.Equal(StreamProbeTransport.Tcp, probe.Transport);
        }

        await using var second = new FakeProbeCamera(Synthetic(0, 9));
        await using (var probe = await RtspStreamProbe.OpenAsync(Options(second, TestPorts()) with { PreferUdp = false }, Ct))
        {
            Assert.Equal(StreamProbeTransport.Tcp, probe.Transport);
        }

        Assert.All(camera.Transports.Concat(second.Transports), t => Assert.StartsWith("RTP/AVP/TCP", t, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeviceAtASiteAlwaysStreamsOverTcp()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 9));
        var router = new DeviceRouter(new LoopbackSite());
        var ports = TestPorts();
        await using var probe = await RtspStreamProbe.OpenAsync(
            Options(camera, ports) with { Route = new DeviceRoute(Guid.NewGuid()), Router = router },
            Ct);

        Assert.Equal(StreamProbeTransport.Tcp, probe.Transport);
        Assert.StartsWith("RTP/AVP/TCP", Assert.Single(camera.Transports), StringComparison.Ordinal);
        Assert.Equal(0, ports.InUse);
    }

    [Fact]
    public async Task TheProbeFaultsWhenTheCameraEndsTheSession()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 9), closeAfterPlay: true);
        await using var probe = await RtspStreamProbe.OpenAsync(Options(camera, TestPorts()), Ct);

        var ended = await Assert.ThrowsAsync<DeviceStreamException>(() => probe.Completion.WaitAsync(Eventually.DefaultTimeout));
        Assert.Equal(DeviceStreamError.Unreachable, ended.Error);
        Assert.Equal(RtspStreamProbe.Closed, ended.Message);
    }

    [Fact]
    public async Task AQuietStreamKeepsItsSessionAliveAndEndsAfterTheDataTimeout()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 0)); // one packet, then nothing
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var probe = await RtspStreamProbe.OpenAsync(Options(camera, TestPorts()) with { Time = time, DataTimeout = TimeSpan.FromSeconds(40) }, Ct);

        // Session timeout 60 s: an OPTIONS every 30 s; nothing for 40 s ends the probe.
        await Eventually.TrueAsync(
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return probe.Completion.IsCompleted;
            },
            "the probe ended");

        var ended = await Assert.ThrowsAsync<DeviceStreamException>(() => probe.Completion);
        Assert.Equal(RtspStreamProbe.StoppedSending, ended.Message);
        await Eventually.TrueAsync(() => camera.Methods.Contains("OPTIONS"), "a keep-alive arrived");
        await Eventually.TrueAsync(() => camera.Rtcp.Count >= 2, "receiver reports: the first one and every 5 s");
    }

    [Fact]
    public async Task ACodecTheCameraDoesNotStreamIsNotSupported()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 9));
        var ex = await Assert.ThrowsAsync<DeviceStreamException>(() => RtspStreamProbe.OpenAsync(Options(camera, TestPorts()) with { Codec = VideoCodecKind.H265 }, Ct));

        Assert.Equal(DeviceStreamError.NotSupported, ex.Error);
        Assert.Equal(RtspStreamProbe.NoVideoStream, ex.Message);
    }

    [Fact]
    public void TheUrlAsksForTheLargestStreamWithAKeyframeEverySecond()
    {
        var url = RtspStreamProbe.BuildUrl(new RtspProbeOptions { Address = "10.0.0.48", Codec = VideoCodecKind.H265, Size = new ImageSize(3840, 2160), Fps = 50, Camera = 2 });
        Assert.Equal("rtsp://10.0.0.48/axis-media/media.amp?videocodec=h265&camera=2&resolution=3840x2160&fps=50&videokeyframeinterval=50&audio=0", url.ToString());
    }

    [Fact]
    public async Task TheServersProbesUseTheStoredCredentialsAndRefuseAChangedCertificate()
    {
        await using var camera = new FakeProbeCamera(Synthetic(0, 19));
        await using var db = await TestDatabase.CreateAsync();
        var repository = db.Get<DeviceRepository>();
        var device = await repository.AddAsync(new Device { Serial = "B8A44F000001", Address = "127.0.0.1" }, Ct);
        await db.Get<CredentialStore>().SetAsync(device.Id, "root", "secret", Ct);
        var probes = new DeviceStreamProbes(repository, db.Get<CredentialStore>(), new ImageVapix(), udpPorts: TestPorts()) { RtspPort = camera.Port };

        await using (var probe = await probes.OpenAsync(device.Id, new StreamProbeRequest(1, 640, 360, 25, "h264"), Ct))
        {
            await Eventually.TrueAsync(() => probe.Read().Packets == 20, "20 packets arrived");
        }

        Assert.Contains(camera.Requests, r => r.Contains("Authorization: Digest", StringComparison.Ordinal));
        Assert.DoesNotContain(camera.Requests, r => r.Contains("secret", StringComparison.Ordinal));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => probes.OpenAsync(Guid.NewGuid(), new StreamProbeRequest(1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => probes.OpenAsync(device.Id, new StreamProbeRequest(1, Codec: "mjpeg"), Ct));

        device.Status = DeviceStatus.CertificateChanged;
        await repository.UpdateAsync(device, Ct);
        var refused = await Assert.ThrowsAsync<DeviceStreamException>(() => probes.OpenAsync(device.Id, new StreamProbeRequest(1, 640, 360, 25, "h264"), Ct));
        Assert.Equal(DeviceMessages.CertificateChanged, refused.Message);
        Assert.Single(camera.Sessions); // the refused device was never contacted
    }

    [Fact]
    public async Task TheServersSourcesAreTheLargestStreamOfEveryEnabledSource()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repository = db.Get<DeviceRepository>();
        var device = await repository.AddAsync(new Device { Serial = "B8A44F000002", Address = "192.0.2.1" }, Ct);
        var probes = new DeviceStreamProbes(repository, db.Get<CredentialStore>(), new ImageVapix());

        var sources = await probes.ListSourcesAsync(device.Id, Ct);

        Assert.Equal(
            [new StreamProbeSource(1, "View Area 1", 2688, 1512, 30, ["h265", "h264"]), new StreamProbeSource(3, "Corridor", 1920, 1080, 30, ["h265", "h264"])],
            sources,
            SourceComparer.Instance);
    }

    [Fact]
    public void SourcesTakeTheCaptureModesFrameRateAndNeverMjpeg()
    {
        var image = new ImageCapabilities(
            ["jpeg", "mjpeg", "h264"],
            [new ImageSize(1920, 1080)],
            [new VideoSourceInfo(1, "Sensor 1", 0, [new ImageSize(1280, 720), new ImageSize(1920, 1080), new ImageSize(1440, 1440)]), new VideoSourceInfo(2, "Sensor 2", 1, [])]);

        var sources = DeviceStreamProbes.ToSources(image, new Dictionary<int, int> { [0] = 25, [1] = 60 });

        Assert.Equal(
            [new StreamProbeSource(1, "Sensor 1", 1920, 1080, 25, ["h264"]), new StreamProbeSource(2, "Sensor 2", 1920, 1080, 60, ["h264"])],
            sources,
            SourceComparer.Instance);
        Assert.Empty(DeviceStreamProbes.ToSources(image with { Formats = ["jpeg", "mjpeg"] }, null));
    }

    [Fact]
    public void TheCaptureModeFrameRateIsTheEnabledModesOne()
    {
        // Shape of capturemode.cgi getCaptureModes (Capture mode API 1.0), two sensors.
        const string json = """
            {"apiVersion":"1.0","method":"getCaptureModes","data":[
              {"channel":0,"captureMode":[
                {"captureModeId":0,"enabled":false,"maxFPS":60,"maxResolution":"1920x1080"},
                {"captureModeId":1,"enabled":true,"maxFPS":25,"maxResolution":"2688x1512"}]},
              {"channel":1,"captureMode":[{"captureModeId":0,"maxFPS":30},{"captureModeId":1,"maxFPS":50}]},
              {"channel":2,"captureMode":[{"captureModeId":0}]}]}
            """;

        Assert.Equal(new Dictionary<int, int> { [0] = 25, [1] = 50 }, VapixParsers.ParseCaptureModeMaxFps(json));
        Assert.Throws<VapixException>(() => VapixParsers.ParseCaptureModeMaxFps("""{"apiVersion":"1.0","error":{"code":2002,"message":"Unknown method"}}"""));
    }

    [Fact]
    public void RecordedPacketsWithAGapAReorderAndADuplicateGiveTheirLoss()
    {
        // The recording (15 frames at 10 fps) four times in a row, numbered on, so a gap has 50 newer packets after it.
        var recorded = RtpFixtures.Packets(Fixture);
        Assert.True(RtpPacket.TryParse(recorded[0], out var first));
        Assert.True(RtpPacket.TryParse(recorded[^1], out var last));
        var span = last.Timestamp - first.Timestamp + 9000;
        var packets = new List<byte[]>();
        for (var round = 0; round < 4; round++)
        {
            foreach (var bytes in recorded)
            {
                var copy = bytes.ToArray();
                Assert.True(RtpPacket.TryParse(copy, out var rtp));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), (ushort)(rtp.SequenceNumber + (round * recorded.Count)));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(4), rtp.Timestamp + (uint)(round * span));
                packets.Add(copy);
            }
        }

        var baseline = Count(packets);
        Assert.Equal(60, baseline.Frames);
        Assert.Equal(60, baseline.ExpectedFrames);
        Assert.Equal(0, baseline.LostPackets);

        var changed = new List<byte[]>(packets);
        var gap = Enumerable.Range(5, 20).First(i => (changed[i][1] & 0x80) == 0); // a packet inside a frame
        changed.RemoveAt(gap);                                       // lost
        (changed[30], changed[31]) = (changed[31], changed[30]);     // reordered
        changed.Insert(40, changed[40]);                             // duplicated
        var read = Count(changed);

        Assert.Equal(baseline.Packets - 1, read.Packets);
        Assert.Equal(1, read.LostPackets);
        Assert.True(RtpPacket.TryParse(packets[gap], out var gone));
        Assert.Equal(baseline.Bytes - gone.Payload.Length, read.Bytes);
        Assert.Equal(60, read.Frames);
        Assert.Equal(60, read.ExpectedFrames);

        static StreamProbeCounters Count(List<byte[]> stream)
        {
            var counter = new RtpStreamCounter(10);
            foreach (var bytes in stream)
            {
                Assert.True(RtpPacket.TryParse(bytes, out var rtp));
                counter.Add(rtp);
            }

            return counter.Read();
        }
    }

    private static RtpUdpPorts TestPorts()
    {
        // A range of its own per test, away from the server's 50000-50799 (a running OADM may hold those).
        var first = 52000 + (2 * (Interlocked.Increment(ref _nextRange) % 400 * 4));
        return new RtpUdpPorts(first, first + 7);
    }

    private static int _nextRange;

    private static RtspProbeOptions Options(FakeProbeCamera camera, RtpUdpPorts ports) => new()
    {
        Address = "127.0.0.1",
        Port = camera.Port,
        Credentials = new NetworkCredential("root", "secret"),
        Codec = VideoCodecKind.H264,
        Size = new ImageSize(640, 360),
        Fps = 25,
        Ports = ports,
        ConnectTimeout = TimeSpan.FromSeconds(30), // far beyond a stalled runner; only a test that waits for it would wait this long
        UdpStartTimeout = TimeSpan.FromSeconds(30), // loopback packets come in milliseconds; the blocked test sets its own
    };

    /// <summary>RTP packets <paramref name="first"/> to <paramref name="last"/> (one frame of 3 packets each), without <paramref name="skip"/>.</summary>
    private static List<byte[]> Synthetic(int first, int last, int? skip = null)
    {
        var packets = new List<byte[]>();
        for (var seq = first; seq <= last; seq++)
        {
            if (seq == skip)
            {
                continue;
            }

            var packet = new byte[12 + 200];
            packet[0] = 0x80;
            packet[1] = (byte)(96 | (seq % 3 == 2 ? 0x80 : 0));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)seq);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)(seq / 3 * 9000));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0xB8A44F01);
            packets.Add(packet);
        }

        return packets;
    }

    private sealed class SourceComparer : IEqualityComparer<StreamProbeSource>
    {
        public static SourceComparer Instance { get; } = new();

        public bool Equals(StreamProbeSource? x, StreamProbeSource? y) =>
            x is not null && y is not null && x with { Codecs = [] } == y with { Codecs = [] } && x.Codecs.SequenceEqual(y.Codecs);

        public int GetHashCode(StreamProbeSource obj) => obj.Camera;
    }

    /// <summary>A camera with two view areas and a disabled one (param.cgi Properties.Image and Image).</summary>
    private sealed class ImageVapix : IVapixClientFactory, IVapixClient
    {
        public Uri BaseAddress { get; } = new("http://192.0.2.1/");

        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(this);

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["Properties.Image.Format"] = "jpeg,mjpeg,h264,h265",
                ["Properties.Image.Resolution"] = "1920x1080,1280x720",
                ["Properties.Image.I0.Resolution"] = "1920x1080,2688x1512,1280x720",
                ["Image.I0.Enabled"] = "yes",
                ["Image.I0.Name"] = "View Area 1",
                ["Image.I1.Enabled"] = "no",
                ["Image.I1.Name"] = "View Area 2",
                ["Image.I2.Enabled"] = "yes",
                ["Image.I2.Name"] = "Corridor",
            });

        public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A site whose node connects on loopback (the route decides TCP, not the connection).</summary>
    private sealed class LoopbackSite : ISiteTransport
    {
        public async ValueTask<Stream> OpenStreamAsync(Guid siteId, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            return new OwningStream(tcp);
        }

        private sealed class OwningStream(TcpClient tcp) : Stream
        {
            private readonly NetworkStream _inner = tcp.GetStream();

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    tcp.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// A scripted Axis camera on loopback: Digest login, DESCRIBE with the fixture SDP, SETUP over UDP (server ports of
    /// its own, or 461) or TCP, PLAY, then the packets on the negotiated transport. It records what it got.
    /// </summary>
    private sealed class FakeProbeCamera : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly IReadOnlyList<byte[]> _packets;
        private readonly bool _refuseUdp;
        private readonly bool _closeAfterPlay;
        private readonly bool _dropUdp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        private readonly List<Task> _background = [];
        private UdpClient? _rtp;
        private UdpClient? _rtcp;

        public FakeProbeCamera(IReadOnlyList<byte[]> packets, bool refuseUdp = false, bool closeAfterPlay = false, bool dropUdp = false)
        {
            _packets = packets;
            _refuseUdp = refuseUdp;
            _closeAfterPlay = closeAfterPlay;
            _dropUdp = dropUdp;
            _listener.Start();
            _run = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Methods { get; } = new();

        public ConcurrentQueue<string> Requests { get; } = new();

        public List<string> Transports { get; } = [];

        public ConcurrentQueue<int> Sessions { get; } = new();

        public ConcurrentQueue<byte[]> Rtcp { get; } = new();

        public ConcurrentQueue<byte[]> RtpFromClient { get; } = new();

        public int RtcpSenderPort { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _rtp?.Dispose();
            _rtcp?.Dispose();
            foreach (var task in _background.Append(_run))
            {
                try
                {
                    await task;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                }
            }

            _cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                Sessions.Enqueue(Sessions.Count + 1);
                _background.Add(Task.Run(() => ServeAsync(tcp)));
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using var owned = tcp;
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            int? clientPort = null;
            while (true)
            {
                var request = new StringBuilder();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
                {
                    request.AppendLine(line);
                }

                if (line is null)
                {
                    return;
                }

                var text = request.ToString();
                Requests.Enqueue(text);
                var method = text.Split(' ')[0];
                Methods.Enqueue(method);
                var cseq = Header(text, "CSeq");
                if (Header(text, "Authorization") is null)
                {
                    await WriteAsync(stream, $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"AXIS_TEST\", nonce=\"abc\", qop=\"auth\"\r\n\r\n");
                    continue;
                }

                switch (method)
                {
                    case "DESCRIBE":
                        var body = Encoding.UTF8.GetBytes(RtpFixtures.Sdp(Fixture));
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Type: application/sdp\r\nContent-Base: rtsp://camera.invalid/axis-media/media.amp/\r\nContent-Length: {body.Length}\r\n\r\n");
                        await stream.WriteAsync(body, _cts.Token);
                        break;
                    case "SETUP":
                        var transport = Header(text, "Transport")!;
                        lock (Transports)
                        {
                            Transports.Add(transport);
                        }

                        if (transport.StartsWith("RTP/AVP/TCP", StringComparison.Ordinal))
                        {
                            await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\nSession: 4711;timeout=60\r\n\r\n");
                        }
                        else if (_refuseUdp)
                        {
                            await WriteAsync(stream, $"RTSP/1.0 461 Unsupported Transport\r\nCSeq: {cseq}\r\n\r\n");
                        }
                        else
                        {
                            clientPort = RtspTransport.Parse(transport)!.ClientRtpPort;
                            _rtp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                            _rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                            _background.Add(Task.Run(() => ReceiveAsync(_rtp, RtpFromClient, rtcp: false)));
                            _background.Add(Task.Run(() => ReceiveAsync(_rtcp, Rtcp, rtcp: true)));
                            var rtpPort = ((IPEndPoint)_rtp.Client.LocalEndPoint!).Port;
                            var rtcpPort = ((IPEndPoint)_rtcp.Client.LocalEndPoint!).Port;
                            await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nTransport: RTP/AVP;unicast;client_port={clientPort}-{clientPort + 1};server_port={rtpPort}-{rtcpPort};ssrc=B8A44F01\r\nSession: 4711;timeout=60\r\n\r\n");
                        }

                        break;
                    case "PLAY":
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 4711\r\n\r\n");
                        foreach (var packet in _packets)
                        {
                            if (clientPort is not null && _dropUdp)
                            {
                                continue; // a firewall on the way drops it
                            }

                            if (clientPort is { } udpPort)
                            {
                                await _rtp!.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, udpPort), _cts.Token);
                            }
                            else
                            {
                                byte[] header = [(byte)'$', 0, (byte)(packet.Length >> 8), (byte)packet.Length];
                                await stream.WriteAsync(header, _cts.Token);
                                await stream.WriteAsync(packet, _cts.Token);
                            }
                        }

                        if (_closeAfterPlay)
                        {
                            return;
                        }

                        break;
                    default:
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n\r\n");
                        break;
                }
            }
        }

        private async Task ReceiveAsync(UdpClient udp, ConcurrentQueue<byte[]> into, bool rtcp)
        {
            while (!_cts.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(_cts.Token);
                if (rtcp)
                {
                    RtcpSenderPort = received.RemoteEndPoint.Port;
                }

                into.Enqueue(received.Buffer);
            }
        }

        private static string? Header(string request, string name) =>
            request.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();

        private async Task WriteAsync(NetworkStream stream, string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _cts.Token);
    }
}
