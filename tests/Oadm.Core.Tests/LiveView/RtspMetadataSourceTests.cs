using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Core.LiveView.Rtsp;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.LiveView;

/// <summary><see cref="RtspMetadataSource"/> against a scripted RTSP server on loopback that replays the recorded event stream.</summary>
public sealed class RtspMetadataSourceTests
{
    [Fact]
    public async Task OpensWithDigestAndYieldsTheRecordedDocuments()
    {
        await using var server = new FakeEventRtspServer();
        await using var source = await RtspMetadataSource.OpenAsync(Options(server.Port, "secret"), CancellationToken.None);

        var documents = new List<DeviceMetadataDocument>();
        await foreach (var document in source.ReadAsync(CancellationToken.None))
        {
            documents.Add(document);
        }

        Assert.Equal(129, documents.Count);
        Assert.Equal(0, source.LostDocuments);
        Assert.Equal(["DESCRIBE", "DESCRIBE", "SETUP", "PLAY"], server.Methods.Take(4));
        Assert.Contains("/axis-media/media.amp?video=0&audio=0&event=on RTSP/1.0", server.RawRequests[0], StringComparison.Ordinal);
        Assert.Contains("stream=0?video=0&audio=0&event=on", server.RawRequests[2], StringComparison.Ordinal);
        Assert.DoesNotContain(server.RawRequests, r => r.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain(server.RawRequests, r => r.Contains("Basic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongPasswordIsUnauthorized()
    {
        await using var server = new FakeEventRtspServer();
        var ex = await Assert.ThrowsAsync<DeviceStreamException>(() => RtspMetadataSource.OpenAsync(Options(server.Port, "wrong"), CancellationToken.None));
        Assert.Equal(DeviceStreamError.Unauthorized, ex.Error);
        Assert.Equal("Unauthorized - HTTP 401 (check the credentials)", ex.Message);
    }

    [Fact]
    public async Task DeviceWithoutMetadataMediaHasNoEventStream()
    {
        await using var server = new FakeEventRtspServer { Sdp = RtpFixtures.Sdp("h264-640x360") };
        var ex = await Assert.ThrowsAsync<DeviceStreamException>(() => RtspMetadataSource.OpenAsync(Options(server.Port, "secret"), CancellationToken.None));
        Assert.Equal(DeviceStreamError.NotSupported, ex.Error);
        Assert.Equal(RtspMetadataSource.NoEventStream, ex.Message);
    }

    [Fact]
    public async Task ClosedPortIsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var ex = await Assert.ThrowsAsync<DeviceStreamException>(() => RtspMetadataSource.OpenAsync(Options(port, "x"), CancellationToken.None));
        Assert.Equal(DeviceStreamError.Unreachable, ex.Error);
        Assert.StartsWith("Unreachable - ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuietStreamIsKeptAliveAndAnsweredKeepAlivesKeepItOpen()
    {
        await using var server = new FakeEventRtspServer { Packets = [], StayOpen = true, AnswerKeepAlives = true };
        var options = Options(server.Port, "secret") with { KeepAliveInterval = TimeSpan.FromMilliseconds(200), DeadTimeout = TimeSpan.FromMilliseconds(1500) };
        await using var source = await RtspMetadataSource.OpenAsync(options, CancellationToken.None);

        // Twice the dead timeout: without the answered keep-alives the read would end as unreachable. The 1.5 s leave
        // room for a busy CI machine answering one keep-alive late.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in source.ReadAsync(cts.Token))
            {
            }
        });
        Assert.True(server.Methods.Count(m => m == "OPTIONS") >= 3, string.Join(",", server.Methods));
    }

    [Fact]
    public async Task SilentDeviceEndsTheReadAsUnreachable()
    {
        await using var server = new FakeEventRtspServer { Packets = [], StayOpen = true, AnswerKeepAlives = false };
        var options = Options(server.Port, "secret") with { KeepAliveInterval = TimeSpan.FromMilliseconds(200), DeadTimeout = TimeSpan.FromMilliseconds(700) };
        await using var source = await RtspMetadataSource.OpenAsync(options, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DeviceStreamException>(async () =>
        {
            await foreach (var unused in source.ReadAsync(CancellationToken.None))
            {
            }
        });
        Assert.Equal(DeviceStreamError.Unreachable, ex.Error);
    }

    [Fact]
    public async Task DisposeSendsTeardown()
    {
        await using var server = new FakeEventRtspServer { Packets = [], StayOpen = true, AnswerKeepAlives = true };
        var source = await RtspMetadataSource.OpenAsync(Options(server.Port, "secret"), CancellationToken.None);
        await source.DisposeAsync();
        await server.WaitForAsync("TEARDOWN", TimeSpan.FromSeconds(5));
    }

    private static RtspMetadataOptions Options(int port, string password) => new()
    {
        Address = "127.0.0.1",
        Port = port,
        Credentials = new NetworkCredential("root", password),
        ConnectTimeout = TimeSpan.FromSeconds(3),
    };
}

/// <summary>
/// Scripted RTSP event server on loopback: Digest challenge (user root, password secret), DESCRIBE with the recorded
/// events SDP, SETUP, PLAY, then <see cref="Packets"/>; closes or stays open (answering keep-alives or not).
/// Accepts any number of connections one after another.
/// </summary>
internal sealed class FakeEventRtspServer : IAsyncDisposable
{
    private const string Nonce = "n0nce";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _run;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    public FakeEventRtspServer()
    {
        _listener.Start();
        _run = Task.Run(RunAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Sdp { get; init; } = RtpFixtures.Sdp("events");

    public IReadOnlyList<byte[]> Packets { get; init; } = RtpFixtures.Packets("events");

    public bool StayOpen { get; init; }

    public bool AnswerKeepAlives { get; init; } = true;

    public List<string> Methods { get; } = [];

    public List<string> RawRequests { get; } = [];

    public async Task WaitForAsync(string method, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_gate)
            {
                if (Methods.Contains(method))
                {
                    return;
                }
            }

            await Task.Delay(20);
        }

        Assert.Fail($"{method} not received: {string.Join(",", Methods)}");
    }

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
        while (!_cts.IsCancellationRequested)
        {
            using var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
            try
            {
                await ServeAsync(tcp);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
            }
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
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
            var method = text.Split(' ')[0];
            lock (_gate)
            {
                RawRequests.Add(text);
                Methods.Add(method);
            }

            var cseq = text.Split('\n').First(l => l.StartsWith("CSeq:", StringComparison.Ordinal))["CSeq:".Length..].Trim();
            var auth = text.Split('\n').FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.Ordinal));
            if (!Authorized(auth, method))
            {
                await WriteAsync(stream, $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"AXIS_TEST\", nonce=\"{Nonce}\", qop=\"auth\"\r\n\r\n");
                continue;
            }

            switch (method)
            {
                case "DESCRIBE":
                    var body = Encoding.UTF8.GetBytes(Sdp);
                    await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Type: application/sdp\r\nContent-Base: rtsp://camera.invalid/axis-media/media.amp/\r\nContent-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(body, _cts.Token);
                    break;
                case "SETUP":
                    await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\nSession: 4711;timeout=60\r\n\r\n");
                    break;
                case "PLAY":
                    await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 4711\r\n\r\n");
                    foreach (var packet in Packets)
                    {
                        byte[] header = [(byte)'$', 0, (byte)(packet.Length >> 8), (byte)packet.Length];
                        await stream.WriteAsync(header, _cts.Token);
                        await stream.WriteAsync(packet, _cts.Token);
                    }

                    if (!StayOpen)
                    {
                        tcp.Client.Shutdown(SocketShutdown.Send);
                        return;
                    }

                    break;
                case "OPTIONS" when !AnswerKeepAlives:
                    break;
                case "TEARDOWN":
                    return;
                default:
                    await WriteAsync(stream, $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n\r\n");
                    break;
            }
        }
    }

    private static bool Authorized(string? header, string method)
    {
        if (header is null)
        {
            return false;
        }

        var p = DigestAuthenticator.ParseParameters(header["Authorization: Digest ".Length..]);
        var ha1 = Md5("root:AXIS_TEST:secret");
        var ha2 = Md5($"{method}:{p["uri"]}");
        var expected = Md5($"{ha1}:{Nonce}:{p["nc"]}:{p["cnonce"]}:auth:{ha2}");
        return p["username"] == "root" && p["response"] == expected;
    }

#pragma warning disable CA5351 // Digest is MD5 by definition
    private static string Md5(string text) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

    private async Task WriteAsync(NetworkStream stream, string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _cts.Token);
}
