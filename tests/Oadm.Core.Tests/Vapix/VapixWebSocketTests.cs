using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>
/// <see cref="VapixClient.ConnectWebSocketAsync"/> against a scripted device on loopback: a session token from
/// wssession.cgi first, then the upgrade with <c>wssession=</c> (verified on AXIS OS 12.11: the token works once).
/// </summary>
public sealed class VapixWebSocketTests
{
    [Fact]
    public async Task ItLogsInWithAOneTimeTokenAndTalksJson()
    {
        await using var device = ScriptedDevice.Start(tokenStatus: 200);
        using var client = new VapixClient(new Uri($"http://127.0.0.1:{device.Port}/"), new HttpClientHandler());

        using var socket = await client.ConnectWebSocketAsync("/vapix/ws-data-stream?sources=events", TimeSpan.FromSeconds(10), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal("echo hello", Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.Equal("/vapix/ws-data-stream?sources=events&wssession=tok3n42", device.UpgradePath);
    }

    [Fact]
    public async Task ARefusedLoginIsUnauthorized()
    {
        await using var device = ScriptedDevice.Start(tokenStatus: 401);
        using var client = new VapixClient(new Uri($"http://127.0.0.1:{device.Port}/"), new HttpClientHandler());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectWebSocketAsync("/vapix/ws-data-stream?sources=events", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Null(device.UpgradePath);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("vapix/ws-data-stream")]
    public async Task OnlyPathsOnTheDeviceAreAccepted(string path)
    {
        using var client = new VapixClient(new Uri("http://127.0.0.1:9/"), new HttpClientHandler());
        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectWebSocketAsync(path, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    /// <summary>Answers wssession.cgi and one WebSocket upgrade (echo); keeps-alive connections are served in a loop.</summary>
    private sealed class ScriptedDevice : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly int _tokenStatus;
        private Task _accept = Task.CompletedTask;

        private ScriptedDevice(int tokenStatus) => _tokenStatus = tokenStatus;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string? UpgradePath { get; private set; }

        public static ScriptedDevice Start(int tokenStatus)
        {
            var device = new ScriptedDevice(tokenStatus);
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

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(tcp));
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using var _ = tcp;
            var stream = tcp.GetStream();
            while (true)
            {
                var head = await ReadHeadAsync(stream);
                if (head is null)
                {
                    return;
                }

                var lines = head.Split("\r\n");
                var path = lines[0].Split(' ')[1];
                if (path.StartsWith("/axis-cgi/wssession.cgi", StringComparison.Ordinal))
                {
                    var body = _tokenStatus == 200 ? "tok3n42\n" : string.Empty;
                    var reason = _tokenStatus == 200 ? "OK" : "Unauthorized";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {_tokenStatus} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
                    continue;
                }

                UpgradePath = path;
                var key = lines.First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
#pragma warning disable CA5350 // SHA-1 is what RFC 6455 prescribes for the handshake.
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                var buffer = new byte[64];
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                await socket.SendAsync(Encoding.UTF8.GetBytes("echo " + Encoding.UTF8.GetString(buffer, 0, result.Count)), WebSocketMessageType.Text, true, CancellationToken.None);
                await Task.Delay(500);
                return;
            }
        }

        private static async Task<string?> ReadHeadAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one) == 0)
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
}
