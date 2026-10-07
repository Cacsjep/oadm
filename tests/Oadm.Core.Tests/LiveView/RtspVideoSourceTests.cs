using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.LiveView;

/// <summary>RtspVideoSource against a scripted RTSP server on loopback that replays the recorded RTP.</summary>
public sealed class RtspVideoSourceTests
{
    [Theory]
    [InlineData("h264-640x360", VideoCodecKind.H264)]
    [InlineData("h265-640x360", VideoCodecKind.H265)]
    public async Task OpensWithDigestAndYieldsTheRecordedFrames(string fixture, VideoCodecKind codec)
    {
        await using var server = new FakeRtspServer(fixture, "root", "secret");
        await using var source = await RtspVideoSource.OpenAsync(Options(server.Port, codec, "secret"), CancellationToken.None);

        var frames = new List<LiveVideoFrame>();
        await foreach (var frame in source.ReadFramesAsync(CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(15, frames.Count);
        Assert.True(frames[0].IsKeyframe);
        Assert.All(frames, f => Assert.Equal((640, 360), (f.Width, f.Height)));
        Assert.Equal(["DESCRIBE", "DESCRIBE", "SETUP", "PLAY"], server.Methods.Take(4));
        Assert.True(server.AuthorizedDescribe, "second DESCRIBE carries a valid Digest response");
        Assert.DoesNotContain(server.RawRequests, r => r.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongPasswordIsReportedAsUnauthorized()
    {
        await using var server = new FakeRtspServer("h264-640x360", "root", "secret");
        var ex = await Assert.ThrowsAsync<LiveViewException>(() => RtspVideoSource.OpenAsync(Options(server.Port, VideoCodecKind.H264, "wrong"), CancellationToken.None));
        Assert.Equal(LiveViewError.Unauthorized, ex.Error);
    }

    [Fact]
    public async Task CodecMismatchIsNotSupported()
    {
        await using var server = new FakeRtspServer("h264-640x360", "root", "secret");
        var ex = await Assert.ThrowsAsync<LiveViewException>(() => RtspVideoSource.OpenAsync(Options(server.Port, VideoCodecKind.H265, "secret"), CancellationToken.None));
        Assert.Equal(LiveViewError.NotSupported, ex.Error);
    }

    [Fact]
    public async Task ClosedPortIsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var ex = await Assert.ThrowsAsync<LiveViewException>(() => RtspVideoSource.OpenAsync(Options(port, VideoCodecKind.H264, "x"), CancellationToken.None));
        Assert.Equal(LiveViewError.Unreachable, ex.Error);
    }

    private static RtspSourceOptions Options(int port, VideoCodecKind codec, string password) => new()
    {
        Address = "127.0.0.1",
        Port = port,
        Credentials = new NetworkCredential("root", password),
        Codec = codec,
        Size = new ImageSize(640, 360),
        Fps = 10,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    };

    /// <summary>One-connection RTSP server: Digest challenge, DESCRIBE with the fixture SDP, SETUP, PLAY, then the RTP.</summary>
    private sealed class FakeRtspServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _fixture;
        private readonly string _user;
        private readonly string _password;
        private readonly Task _run;
        private readonly CancellationTokenSource _cts = new();

        public FakeRtspServer(string fixture, string user, string password)
        {
            _fixture = fixture;
            _user = user;
            _password = password;
            _listener.Start();
            _run = Task.Run(RunAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Methods { get; } = [];

        public List<string> RawRequests { get; } = [];

        public bool AuthorizedDescribe { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _run;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            using var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            const string nonce = "abc123";
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
                RawRequests.Add(text);
                var method = text.Split(' ')[0];
                Methods.Add(method);
                var cseq = text.Split('\n').First(l => l.StartsWith("CSeq:", StringComparison.Ordinal))["CSeq:".Length..].Trim();
                var auth = text.Split('\n').FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.Ordinal));

                if (!Authorized(auth, method, nonce))
                {
                    await WriteAsync(stream, $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"AXIS_TEST\", nonce=\"{nonce}\", qop=\"auth\"\r\n\r\n");
                    continue;
                }

                switch (method)
                {
                    case "DESCRIBE":
                        AuthorizedDescribe = true;
                        var sdp = RtpFixtures.Sdp(_fixture);
                        var body = Encoding.UTF8.GetBytes(sdp);
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Type: application/sdp\r\nContent-Base: rtsp://camera.invalid/axis-media/media.amp/\r\nContent-Length: {body.Length}\r\n\r\n");
                        await stream.WriteAsync(body, _cts.Token);
                        break;
                    case "SETUP":
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\nSession: 4711;timeout=60\r\n\r\n");
                        break;
                    case "PLAY":
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 4711\r\n\r\n");
                        foreach (var packet in RtpFixtures.Packets(_fixture))
                        {
                            byte[] header = [(byte)'$', 0, (byte)(packet.Length >> 8), (byte)packet.Length];
                            await stream.WriteAsync(header, _cts.Token);
                            await stream.WriteAsync(packet, _cts.Token);
                        }

                        // An RTCP packet and a keep-alive answer in between must be skipped by the client.
                        await stream.WriteAsync(new byte[] { (byte)'$', 1, 0, 2, 0x80, 0xC8 }, _cts.Token);
                        await WriteAsync(stream, "RTSP/1.0 200 OK\r\nCSeq: 99\r\n\r\n");
                        tcp.Client.Shutdown(SocketShutdown.Send);
                        return;
                    default:
                        await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n\r\n");
                        break;
                }
            }
        }

        private bool Authorized(string? header, string method, string nonce)
        {
            if (header is null)
            {
                return false;
            }

            var p = DigestAuthenticator.ParseParameters(header["Authorization: Digest ".Length..]);
            var ha1 = Md5($"{_user}:AXIS_TEST:{_password}");
            var ha2 = Md5($"{method}:{p["uri"]}");
            var expected = Md5($"{ha1}:{nonce}:{p["nc"]}:{p["cnonce"]}:auth:{ha2}");
            return p["username"] == _user && p["response"] == expected;
        }

#pragma warning disable CA5351 // Digest is MD5 by definition
        private static string Md5(string text) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

        private async Task WriteAsync(NetworkStream stream, string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _cts.Token);
    }
}
