using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.DeviceWeb;

/// <summary>
/// The relay of one device's web interface for the embedded browser: a small HTTP/1.1 server on 127.0.0.1 (its own
/// port, so every device has its own origin and cookies). Each request or WebSocket of the page becomes one
/// DeviceWebService.Exchange call; the server adds the device login. Only the browser that opened
/// <see cref="StartAddress"/> gets the session cookie; other programs on this computer get 403.
/// </summary>
public sealed partial class DeviceWebRelay : IDeviceWebSession
{
    /// <summary>Path that hands out the session cookie (with the token), then redirects to the page.</summary>
    public const string StartPath = "/__oadm/start";

    /// <summary>Start of the session cookie's name; the relay's port follows ("oadm_relay_51234").</summary>
    public const string CookieName = "oadm_relay";

    private static int s_nextAddress;

    /// <summary>Largest request head (request line and headers).</summary>
    public const int MaxHeadBytes = 64 * 1024;

    private const int ChunkBytes = 64 * 1024;

    private readonly IOadmApi _api;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _token;
    private readonly string _secret;
    private readonly string _host;
    private readonly string _cookie;
    private readonly Task _accept;
    private int _disposed;

    public DeviceWebRelay(IOadmApi api, Guid deviceId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _logger = logger;
        DeviceId = deviceId;
        // The start address carries _token; the session cookie carries _secret, which never appears in an address
        // (browser history, the tab's address).
        _token = NewSecret();
        _secret = NewSecret();
        _listener = Listen();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        var port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        _host = endpoint.Address + ":" + port;
        _cookie = CookieName + "_" + port;
        Origin = new Uri("http://" + _host + "/");
        _accept = AcceptAsync(_stop.Token);
    }

    /// <summary>
    /// Browsers keep cookies per host, not per port: every relay gets its own loopback address (127.1.x.y) so the
    /// devices' own login cookies (the same names on every Axis device) and the session cookies never mix between tabs.
    /// Windows and Linux route all of 127.0.0.0/8 to this computer; macOS only 127.0.0.1, which stays the fallback there
    /// and when the address cannot be bound.
    /// </summary>
    private static TcpListener Listen()
    {
        if (!OperatingSystem.IsMacOS())
        {
            var n = (uint)Interlocked.Increment(ref s_nextAddress) % (250 * 250);
            var address = new IPAddress(new byte[] { 127, 1, (byte)(1 + (n / 250)), (byte)(1 + (n % 250)) });
            var listener = new TcpListener(address, 0);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                listener.Dispose();
            }
        }

        var loopback = new TcpListener(IPAddress.Loopback, 0);
        loopback.Start();
        return loopback;
    }

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public Guid DeviceId { get; }

    /// <summary>True after <see cref="Dispose"/>.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Uri Origin { get; }

    public Uri StartAddress(string path)
    {
        var target = string.IsNullOrEmpty(path) || path[0] != '/' ? "/" + path : path;
        return new Uri(Origin, StartPath + "?token=" + _token + "&path=" + Uri.EscapeDataString(target));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new HttpHeadReader(stream);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var request = await reader.ReadHeadAsync(ct).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    if (!await HandleAsync(request, reader, stream, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or WebSocketException or InvalidDataException or Grpc.Core.RpcException)
            {
                // The browser closed the connection, the relay stopped or the request was malformed.
                if (ex is InvalidDataException)
                {
                    LogBadRequest(ex.Message);
                }
            }
        }
    }

    /// <summary>Answers one request; false closes the connection.</summary>
    private async Task<bool> HandleAsync(HttpRequestHead request, HttpHeadReader reader, NetworkStream stream, CancellationToken ct)
    {
        if (!string.Equals(request.Header("Host"), _host, StringComparison.OrdinalIgnoreCase))
        {
            await WriteSimpleAsync(stream, 400, "Bad Request", "Wrong host.", ct).ConfigureAwait(false);
            return false;
        }

        if (request.Target.StartsWith(StartPath + "?", StringComparison.Ordinal))
        {
            return await StartAsync(request, stream, ct).ConfigureAwait(false);
        }

        // Pages of other relays share the cookie scope on 127.0.0.1 (macOS): a request from another origin is refused.
        if (request.Header("Origin") is { Length: > 0 } origin && origin != "null"
            && !string.Equals(origin.TrimEnd('/'), "http://" + _host, StringComparison.OrdinalIgnoreCase))
        {
            await DrainBodyAsync(request, reader, ct).ConfigureAwait(false);
            await WriteSimpleAsync(stream, 403, "Forbidden", "Open the device from OADM.", ct).ConfigureAwait(false);
            return !request.WantsClose;
        }

        if (!HasSessionCookie(request.Header("Cookie")))
        {
            await DrainBodyAsync(request, reader, ct).ConfigureAwait(false);
            await WriteSimpleAsync(stream, 403, "Forbidden", "Open the device from OADM.", ct, DeviceWebPages.SessionLostTitle).ConfigureAwait(false);
            return !request.WantsClose;
        }

        if (request.IsWebSocket)
        {
            await RelayWebSocketAsync(request, reader, stream, ct).ConfigureAwait(false);
            return false;
        }

        return await RelayHttpAsync(request, reader, stream, ct).ConfigureAwait(false);
    }

    private async Task<bool> StartAsync(HttpRequestHead request, NetworkStream stream, CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Target[(StartPath.Length + 1)..]);
        var token = query["token"] ?? string.Empty;
        var path = query["path"] ?? "/";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(_token))
            || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal)
            || path.Any(char.IsControl))
        {
            await WriteSimpleAsync(stream, 403, "Forbidden", "Open the device from OADM.", ct).ConfigureAwait(false);
            return false;
        }

        var head = new StringBuilder()
            .Append("HTTP/1.1 302 Found\r\n")
            .Append("Location: ").Append(path).Append("\r\n")
            .Append("Set-Cookie: ").Append(_cookie).Append('=').Append(_secret).Append("; Path=/; HttpOnly; SameSite=Strict\r\n")
            .Append("Cache-Control: no-store\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        return !request.WantsClose;
    }

    private bool HasSessionCookie(string? cookies)
    {
        if (cookies is null)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(_secret);
        foreach (var part in cookies.Split(';'))
        {
            var pair = part.Trim();
            if (pair.StartsWith(_cookie + "=", StringComparison.Ordinal)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pair[(_cookie.Length + 1)..]), expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The page's request head for the server: Origin and Referer without the relay's origin, no relay cookie.</summary>
    private DeviceWebRequestHead ToDevice(HttpRequestHead request)
    {
        var head = new DeviceWebRequestHead
        {
            DeviceId = DeviceId.ToString(),
            Method = request.Method,
            PathAndQuery = request.Target,
        };
        var origin = "http://" + _host;
        foreach (var (name, value) in request.Headers)
        {
            if (HttpRequestHead.IsHopByHop(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relayed = value;
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                relayed = string.Join("; ", value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith(CookieName + "_", StringComparison.Ordinal)));
                if (relayed.Length == 0)
                {
                    continue;
                }
            }
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) && value.Equals(origin, StringComparison.OrdinalIgnoreCase))
            {
                relayed = "/";
            }
            else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase) && value.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
            {
                relayed = value[origin.Length..];
            }

            head.Headers.Add(new WebHeader { Name = name, Value = relayed });
        }

        return head;
    }

    private async Task<bool> RelayHttpAsync(HttpRequestHead request, HttpHeadReader reader, NetworkStream stream, CancellationToken ct)
    {
        var head = ToDevice(request);
        head.HasBody = request.HasBody;
        using var call = _api.ExchangeDeviceWeb(ct);
        try
        {
            await call.SendAsync(new DeviceWebFrame { Request = head }, ct).ConfigureAwait(false);
            if (request.HasBody)
            {
                await foreach (var chunk in reader.ReadBodyAsync(request, ct).ConfigureAwait(false))
                {
                    await call.SendAsync(new DeviceWebFrame { Body = ByteString.CopyFrom(chunk.Span) }, ct).ConfigureAwait(false);
                }

                await call.SendAsync(new DeviceWebFrame { End = true }, ct).ConfigureAwait(false);
            }

            await call.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            await WriteSimpleAsync(stream, 502, "Bad Gateway", ex.Status.Detail, ct).ConfigureAwait(false);
            return false;
        }

        var keepAlive = !request.WantsClose;
        var noBody = request.Method == "HEAD";
        var chunked = false;
        try
        {
            await foreach (var frame in call.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (frame.KindCase)
                {
                    case DeviceWebFrame.KindOneofCase.Response:
                        var response = frame.Response;
                        noBody |= response.Status is 204 or 304 or < 200;
                        chunked = !noBody && !response.Headers.Any(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
                        await stream.WriteAsync(ResponseHead(response, chunked, keepAlive), ct).ConfigureAwait(false);
                        break;
                    case DeviceWebFrame.KindOneofCase.Body when !noBody:
                        await WriteBodyAsync(stream, frame.Body.Memory, chunked, ct).ConfigureAwait(false);
                        break;
                    case DeviceWebFrame.KindOneofCase.End:
                        if (chunked)
                        {
                            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                        }

                        return keepAlive;
                }
            }
        }
        catch (RpcException ex)
        {
            LogExchangeFailed(request.Target, ex.Status.Detail);
        }

        // The call ended without a complete answer: the browser must not wait for more.
        return false;
    }

    private static async Task WriteBodyAsync(NetworkStream stream, ReadOnlyMemory<byte> data, bool chunked, CancellationToken ct)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (chunked)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(data.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"), ct).ConfigureAwait(false);
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }
        else
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
    }

    private static byte[] ResponseHead(DeviceWebResponseHead response, bool chunked, bool keepAlive)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(string.IsNullOrEmpty(response.Reason) ? "Status" : response.Reason).Append("\r\n");
        foreach (var header in response.Headers)
        {
            if (HttpRequestHead.IsHopByHop(header.Name) || header.Value.Contains('\r', StringComparison.Ordinal) || header.Value.Contains('\n', StringComparison.Ordinal))
            {
                continue;
            }

            var value = header.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? PageCookie(header.Value) : header.Value;
            head.Append(header.Name).Append(": ").Append(value).Append("\r\n");
        }

        if (chunked)
        {
            head.Append("Transfer-Encoding: chunked\r\n");
        }

        head.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n").Append("\r\n");
        return Encoding.Latin1.GetBytes(head.ToString());
    }

    /// <summary>
    /// A cookie of the device for the relay's origin: no Domain (the device's address is not the relay's), no Secure and
    /// no SameSite=None (the relay is plain HTTP on 127.0.0.1; the link to the device is the server's).
    /// </summary>
    internal static string PageCookie(string value) =>
        string.Join("; ", value.Split(';').Select(p => p.Trim()).Where(p =>
            p.Length > 0
            && !p.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("Secure", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("SameSite=None", StringComparison.OrdinalIgnoreCase)));

    private async Task RelayWebSocketAsync(HttpRequestHead request, HttpHeadReader reader, NetworkStream stream, CancellationToken ct)
    {
        var head = ToDevice(request);
        head.Websocket = true;
        var protocols = (request.Header("Sec-WebSocket-Protocol") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        head.SubProtocols.AddRange(protocols);
        var key = request.Header("Sec-WebSocket-Key") ?? string.Empty;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var call = _api.ExchangeDeviceWeb(both.Token);
        await using var frames = call.ReadAllAsync(both.Token).GetAsyncEnumerator(both.Token);
        try
        {
            await call.SendAsync(new DeviceWebFrame { Request = head }, ct).ConfigureAwait(false);
            if (!await frames.MoveNextAsync().ConfigureAwait(false) || frames.Current.KindCase != DeviceWebFrame.KindOneofCase.Response)
            {
                await WriteSimpleAsync(stream, 502, "Bad Gateway", "The device did not answer.", ct).ConfigureAwait(false);
                return;
            }
        }
        catch (RpcException ex)
        {
            await WriteSimpleAsync(stream, 502, "Bad Gateway", ex.Status.Detail, ct).ConfigureAwait(false);
            return;
        }

        var response = frames.Current.Response;
        if (response.Status != 101)
        {
            // Refused or not reached: the page gets the answer as it is (an error page).
            await stream.WriteAsync(ResponseHead(response, chunked: true, keepAlive: false), ct).ConfigureAwait(false);
            while (await frames.MoveNextAsync().ConfigureAwait(false) && frames.Current.KindCase == DeviceWebFrame.KindOneofCase.Body)
            {
                await WriteBodyAsync(stream, frames.Current.Body.Memory, chunked: true, ct).ConfigureAwait(false);
            }

            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            return;
        }

#pragma warning disable CA5350 // RFC 6455 defines Sec-WebSocket-Accept with SHA-1; it is no security measure
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        var handshake = new StringBuilder()
            .Append("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n")
            .Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
        if (!string.IsNullOrEmpty(response.SubProtocol))
        {
            handshake.Append("Sec-WebSocket-Protocol: ").Append(response.SubProtocol).Append("\r\n");
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake.Append("\r\n").ToString()), ct).ConfigureAwait(false);
        using var socket = WebSocket.CreateFromStream(reader.AsStream(), new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = string.IsNullOrEmpty(response.SubProtocol) ? null : response.SubProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        var fromPage = PageToDeviceAsync(socket, call, both.Token);
        var fromDevice = DeviceToPageAsync(frames, socket, both.Token);
        if (await Task.WhenAny(fromPage, fromDevice).ConfigureAwait(false) == fromPage)
        {
            // The page closed: give the device's close answer a moment to complete the handshake.
            await Task.WhenAny(fromDevice, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
        }

        await both.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(Quiet(fromPage), Quiet(fromDevice)).ConfigureAwait(false);
    }

    private static async Task PageToDeviceAsync(WebSocket socket, IDeviceWebCall call, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await call.SendAsync(new DeviceWebFrame
                {
                    Close = new DeviceWebSocketClose { Status = (int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure), Description = result.CloseStatusDescription ?? string.Empty },
                }, ct).ConfigureAwait(false);
                await call.CompleteAsync().ConfigureAwait(false);
                return;
            }

            await call.SendAsync(new DeviceWebFrame
            {
                Message = new DeviceWebSocketMessage
                {
                    Data = ByteString.CopyFrom(buffer, 0, result.Count),
                    Text = result.MessageType == WebSocketMessageType.Text,
                    EndOfMessage = result.EndOfMessage,
                },
            }, ct).ConfigureAwait(false);
        }
    }

    private static async Task DeviceToPageAsync(IAsyncEnumerator<DeviceWebFrame> frames, WebSocket socket, CancellationToken ct)
    {
        while (await frames.MoveNextAsync().ConfigureAwait(false))
        {
            var frame = frames.Current;
            if (frame.KindCase == DeviceWebFrame.KindOneofCase.Message)
            {
                var message = frame.Message;
                await socket.SendAsync(message.Data.Memory, message.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, message.EndOfMessage, ct).ConfigureAwait(false);
            }
            else if (frame.KindCase == DeviceWebFrame.KindOneofCase.Close)
            {
                var status = frame.Close.Status is >= 1000 and <= 4999 ? (WebSocketCloseStatus)frame.Close.Status : WebSocketCloseStatus.NormalClosure;
                await socket.CloseOutputAsync(status, frame.Close.Description, ct).ConfigureAwait(false);
                return;
            }
        }

        await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, ct).ConfigureAwait(false);
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or RpcException or ObjectDisposedException or InvalidOperationException)
        {
            // one side ended the socket
        }
    }

    private static async Task DrainBodyAsync(HttpRequestHead request, HttpHeadReader reader, CancellationToken ct)
    {
        if (!request.HasBody)
        {
            return;
        }

        await foreach (var _ in reader.ReadBodyAsync(request, ct).ConfigureAwait(false))
        {
        }
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, int status, string reason, string message, CancellationToken ct, string title = "OADM")
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title></head>"
            + "<body style=\"font-family:sans-serif;background:#141414;color:#f2f2f2;padding:24px\"><p>"
            + WebUtility.HtmlEncode(message) + "</p></body></html>");
        var head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Web View relay: bad request ({Reason})")]
    private partial void LogBadRequest(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Web View relay: {Path} ended early ({Reason})")]
    private partial void LogExchangeFailed(string path, string reason);
}
