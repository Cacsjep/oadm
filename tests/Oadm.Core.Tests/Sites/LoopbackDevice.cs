using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Oadm.Core.Tests.Vapix;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// A small device on a loopback port, plain HTTP or HTTPS with its own certificate: <c>basicdeviceinfo.cgi</c> answers
/// 401 with the realm <c>AXIS_&lt;serial&gt;</c> (the probe's Axis check), <c>wssession.cgi</c> a token, an upgrade becomes
/// a WebSocket that echoes, anything else 200 "ok". Connections are kept open (HTTP keep-alive) unless the request says
/// <c>Connection: close</c>; <c>/hold</c> starts an answer that never ends. A test maps a site address to <see cref="EndPoint"/> in the <c>FakeSiteTransport</c>.
/// </summary>
internal sealed class LoopbackDevice : IAsyncDisposable
{
    public const string Serial = "ACCC8E000001";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2? _certificate;
    private readonly List<Task> _connections = [];
    private Task _accept = Task.CompletedTask;
    private int _accepted;

    private LoopbackDevice(X509Certificate2? certificate)
    {
        _certificate = certificate;
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public int Port => EndPoint.Port;

    /// <summary>TCP connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>The SHA-256 fingerprint of the HTTPS certificate (upper hex), null for plain HTTP.</summary>
    public string? Fingerprint => _certificate is null ? null : Convert.ToHexString(SHA256.HashData(_certificate.RawData));

    public static LoopbackDevice StartHttp() => Start(null);

    public static LoopbackDevice StartHttps()
    {
        using var created = TestCertificates.SelfSigned(ip: "127.0.0.1");
        // Windows TLS needs a key it can load: round-trip through PKCS#12.
        return Start(X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null));
    }

    private static LoopbackDevice Start(X509Certificate2? certificate)
    {
        var device = new LoopbackDevice(certificate);
        device._listener.Start();
        device._accept = Task.Run(device.AcceptAsync);
        return device;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        Task[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections);
        _certificate?.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _accepted);
            lock (_connections)
            {
                _connections.Add(Task.Run(() => ServeAsync(tcp)));
            }
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        using var _ = tcp;
        try
        {
            Stream stream = tcp.GetStream();
            if (_certificate is not null)
            {
                var tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(_certificate);
                stream = tls;
            }

            await using (stream)
            {
                await ServeRequestsAsync(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException
            or OperationCanceledException or WebSocketException)
        {
            // The client refused the certificate, closed the connection, or the test ended.
        }
    }

    private async Task ServeRequestsAsync(Stream stream)
    {
        while (!_stop.IsCancellationRequested)
        {
            var head = await ReadHeadAsync(stream, _stop.Token);
            if (head is null)
            {
                return;
            }

            var lines = head.Split("\r\n");
            var path = lines[0].Split(' ')[1];
            var length = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } header
                ? int.Parse(header.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            if (length > 0)
            {
                await stream.ReadExactlyAsync(new byte[length], _stop.Token);
            }

            var close = lines.Any(l => l.Equals("Connection: close", StringComparison.OrdinalIgnoreCase));
            if (lines.FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) is { } keyLine)
            {
                await EchoWebSocketAsync(stream, keyLine.Split(':', 2)[1].Trim());
                return;
            }

            if (path.StartsWith("/hold", StringComparison.Ordinal))
            {
                // A long answer that never ends (an MJPEG stream, a long poll): the connection stays busy until the test ends.
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\n\r\nx"), _stop.Token);
                await stream.FlushAsync(_stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }

            var answer = path.StartsWith("/axis-cgi/basicdeviceinfo.cgi", StringComparison.Ordinal)
                ? $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Digest realm=\"AXIS_{Serial}\", nonce=\"abc\", qop=\"auth\"\r\nContent-Length: 0\r\n"
                : path.StartsWith("/axis-cgi/wssession.cgi", StringComparison.Ordinal)
                    ? "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 8\r\n\r\ntok3n42\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";
            if (!answer.EndsWith("\r\n\r\n", StringComparison.Ordinal) && !answer.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                answer += "\r\n";
            }

            if (close)
            {
                answer = answer.Replace("\r\n\r\n", "\r\nConnection: close\r\n\r\n", StringComparison.Ordinal);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
            await stream.FlushAsync(_stop.Token);
            if (close)
            {
                return;
            }
        }
    }

    private async Task EchoWebSocketAsync(Stream stream, string key)
    {
#pragma warning disable CA5350 // SHA-1 is what RFC 6455 prescribes for the handshake.
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        var buffer = new byte[256];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _stop.Token);
                return;
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes("echo " + Encoding.UTF8.GetString(buffer, 0, result.Count)), WebSocketMessageType.Text, true, _stop.Token);
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                return null;
            }

            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^1] == '\n' && bytes[^2] == '\r' && bytes[^3] == '\n' && bytes[^4] == '\r')
            {
                return Encoding.ASCII.GetString([.. bytes]);
            }
        }
    }
}
