using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;

using Grpc.Core;

using Google.Protobuf;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.DeviceWeb;

/// <summary>
/// gRPC DeviceWebService: relays the web interface of a device for the Web View plugin. Every request of the embedded
/// browser reaches the device through the device's own VAPIX client, so the stored login answers the device's challenge
/// and the pinned certificate is checked; passwords never reach the client. Read and write requests pass unchanged
/// (the user works in the device's web interface like in a browser).
/// </summary>
public sealed partial class DeviceWebGrpcService(
    DeviceRepository devices,
    VapixClientFactory clients,
    Oadm.Core.Persistence.OadmPaths paths,
    AuditLog audit,
    IHostApplicationLifetime lifetime,
    ILogger<DeviceWebGrpcService> logger,
    Oadm.Core.Plugins.PluginRegistry? plugins = null) : Proto.DeviceWebService.DeviceWebServiceBase
{
    /// <summary>The Web View plugin: the relay works only while it is on.</summary>
    public const string WebViewPackageId = "oadm.web-view";

    /// <summary>Request bodies spooled to disk at the same time (uploads through the web interface).</summary>
    public const int MaxSpooledBodies = 4;

    /// <summary>How long one "Opened web interface" entry covers a user's requests to a device.</summary>
    public static readonly TimeSpan AuditWindow = TimeSpan.FromHours(8);

    private static readonly SemaphoreSlim Spooling = new(MaxSpooledBodies, MaxSpooledBodies);
    private static readonly ConcurrentDictionary<(string User, Guid Device), DateTime> Audited = new();
    private static int _spoolFolderCleared;

    /// <summary>Largest body chunk sent to the client.</summary>
    public const int ChunkBytes = 64 * 1024;

    /// <summary>Request bodies up to this size stay in memory; larger ones (uploads) go to a file in the data folder.</summary>
    public const int MemoryBodyBytes = 16 * 1024 * 1024;

    /// <summary>Largest request body (a firmware or app upload through the web interface).</summary>
    public const long MaxBodyBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Time for the device to answer a request with its headers (uploads through the web interface take long).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Headers that belong to one connection, or that OADM sets itself, never relayed.</summary>
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host", "Authorization", "WWW-Authenticate", "Sec-WebSocket-Key",
        "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Accept", "Sec-WebSocket-Protocol",
    };

    public override async Task<Proto.OpenDeviceWebReply> Open(Proto.OpenDeviceWebRequest request, ServerCallContext context)
    {
        RequirePluginOn();
        var id = GrpcGuard.ParseId(request.DeviceId, "device id");
        var ct = context.CancellationToken;
        var device = await devices.GetAsync(id, ct).ConfigureAwait(false) ?? throw GrpcGuard.NotFound(DeviceMessages.Removed);
        if (Refusal(device.Status) is { } refusal)
        {
            return new Proto.OpenDeviceWebReply { Ok = false, Message = refusal };
        }

        await AuditOpenAsync(device, force: true, ct).ConfigureAwait(false);
        return new Proto.OpenDeviceWebReply { Ok = true };
    }

    private void RequirePluginOn()
    {
        if (plugins is not null && plugins.HasPackage(WebViewPackageId) && !plugins.IsEnabled(WebViewPackageId))
        {
            throw GrpcGuard.FailedPrecondition("The Web View plugin is turned off.");
        }
    }

    /// <summary>
    /// "Opened web interface" in the audit log: on Open, and on a request of a user that did not open the device through
    /// Open within <see cref="AuditWindow"/> (a client that skips Open is still recorded).
    /// </summary>
    private async Task AuditOpenAsync(Device device, bool force, CancellationToken ct)
    {
        var user = CallerContext.Current?.UserName ?? "";
        var key = (user, device.Id);
        var now = DateTime.UtcNow;
        if (!force && Audited.TryGetValue(key, out var last) && now - last < AuditWindow)
        {
            return;
        }

        Audited[key] = now;
        var target = string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} ({device.Model})";
        await audit.WriteAsync(AuditActions.WebInterfaceOpened, target, null, ct).ConfigureAwait(false);
    }

    /// <summary>Why the web interface of a device cannot be opened; null when it can.</summary>
    public static string? Refusal(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => DeviceMessages.CredentialsRequired,
        DeviceStatus.PasswordNotSet => DeviceMessages.PasswordNotSet,
        _ => null,
    };

    public override async Task Exchange(IAsyncStreamReader<Proto.DeviceWebFrame> requestStream, IServerStreamWriter<Proto.DeviceWebFrame> responseStream, ServerCallContext context)
    {
        // Web content is mostly compressed already (images, video, gzip); gRPC compression would only cost time.
        context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        if (!await requestStream.MoveNext(ct).ConfigureAwait(false) || requestStream.Current.KindCase != Proto.DeviceWebFrame.KindOneofCase.Request)
        {
            throw GrpcGuard.InvalidArgument("The first frame must be the request.");
        }

        var head = requestStream.Current.Request;
        var id = GrpcGuard.ParseId(head.DeviceId, "device id");
        if (!IsDevicePath(head.PathAndQuery))
        {
            throw GrpcGuard.InvalidArgument("The path must start with / and stay on the device.");
        }

        RequirePluginOn();
        var device = await devices.GetAsync(id, ct).ConfigureAwait(false) ?? throw GrpcGuard.NotFound(DeviceMessages.Removed);
        // Refused statuses are not contacted: a rejected login would be sent again with every request of the page.
        if (Refusal(device.Status) is { } refusal)
        {
            throw GrpcGuard.FailedPrecondition(refusal);
        }

        await AuditOpenAsync(device, force: false, ct).ConfigureAwait(false);
        var client = await clients.GetClientAsync(device, ct).ConfigureAwait(false);
        if (head.Websocket)
        {
            await RelayWebSocketAsync(client, head, requestStream, responseStream, ct).ConfigureAwait(false);
        }
        else
        {
            await RelayHttpAsync(client, head, requestStream, responseStream, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One leading "/", no "//", "\" or control characters: the request stays on the device.</summary>
    public static bool IsDevicePath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal) && !path.Any(char.IsControl) && !path.Contains(' ', StringComparison.Ordinal);

    private async Task RelayHttpAsync(VapixClient client, Proto.DeviceWebRequestHead head, IAsyncStreamReader<Proto.DeviceWebFrame> requestStream,
        IServerStreamWriter<Proto.DeviceWebFrame> responseStream, CancellationToken ct)
    {
        // The body is read completely first: a refused first try (login) is sent again, which a body streamed from the page
        // cannot do.
        await using var body = head.HasBody ? await ReadBodyAsync(requestStream, ct).ConfigureAwait(false) : null;
        var pageCookie = head.Headers.FirstOrDefault(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))?.Value;
        var requests = new List<HttpRequestMessage>(2);
        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(new HttpMethod(head.Method), new Uri(head.PathAndQuery, UriKind.Relative));
            request.Options.Set(VapixRequestOptions.Timeout, RequestTimeout);
            request.Options.Set(VapixRequestOptions.StreamResponse, true);
            if (body is not null)
            {
                body.Position = 0;
                request.Content = new StreamContent(new KeepOpenStream(body));
            }

            foreach (var header in head.Headers)
            {
                if (!header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    AddRequestHeader(request, header.Name, DeviceValue(client.BaseAddress, header.Name, header.Value));
                }
            }

            requests.Add(request);
            return request;
        }

        try
        {
            await SendAndRelayAsync(client, head, Build, pageCookie, responseStream, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }
        }
    }

    private async Task SendAndRelayAsync(VapixClient client, Proto.DeviceWebRequestHead head, Func<HttpRequestMessage> build, string? pageCookie,
        IServerStreamWriter<Proto.DeviceWebFrame> responseStream, CancellationToken ct)
    {
        HttpResponseMessage response;
        IReadOnlyList<string> refusedCookies;
        try
        {
            (response, refusedCookies) = await client.SendRelayAsync(build, pageCookie, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogRequestFailed(head.PathAndQuery, ex.Message);
            await WriteErrorPageAsync(responseStream, ErrorText(ex), ct).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            var reply = new Proto.DeviceWebResponseHead { Status = (int)response.StatusCode, Reason = response.ReasonPhrase ?? string.Empty };
            // Cookies the device set on the refused first try go to the page too (the answer's own ones come after them).
            foreach (var cookie in refusedCookies)
            {
                reply.Headers.Add(new Proto.WebHeader { Name = "Set-Cookie", Value = cookie });
            }

            // The raw values as the device sent them (parsed ones split "Server: Apache/2.4 (Unix)" into tokens).
            foreach (var (name, values) in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
            {
                if (SkippedHeaders.Contains(name))
                {
                    continue;
                }

                foreach (var value in values)
                {
                    reply.Headers.Add(new Proto.WebHeader { Name = name, Value = PageValue(client.BaseAddress, name, value) });
                }
            }

            await responseStream.WriteAsync(new Proto.DeviceWebFrame { Response = reply }, ct).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[ChunkBytes];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await responseStream.WriteAsync(new Proto.DeviceWebFrame { Body = ByteString.CopyFrom(buffer, 0, read) }, ct).ConfigureAwait(false);
                }
            }

            await responseStream.WriteAsync(new Proto.DeviceWebFrame { End = true }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The buffered body for one try; disposing the request leaves it open for the next try.</summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The whole request body, rewindable: in memory up to <see cref="MemoryBodyBytes"/>, beyond that in a file in the
    /// data folder that is deleted when the stream closes (never a shared temp folder).
    /// </summary>
    private async Task<Stream> ReadBodyAsync(IAsyncStreamReader<Proto.DeviceWebFrame> requestStream, CancellationToken ct)
    {
        Stream buffer = new MemoryStream();
        long total = 0;
        var spooling = false;
        try
        {
            while (await requestStream.MoveNext(ct).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.End)
                {
                    break;
                }

                if (frame.KindCase != Proto.DeviceWebFrame.KindOneofCase.Body)
                {
                    continue;
                }

                total += frame.Body.Length;
                if (total > MaxBodyBytes)
                {
                    throw GrpcGuard.InvalidArgument("The request body is larger than 4 GB.");
                }

                if (buffer is MemoryStream memory && total > MemoryBodyBytes)
                {
                    if (!await Spooling.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                    {
                        throw new RpcException(new Status(StatusCode.ResourceExhausted, "Too many uploads at the same time. Try again."));
                    }

                    spooling = true;
                    var folder = Path.Combine(paths.DataDirectory, "web-relay");
                    ClearSpoolFolderOnce(folder);
                    Directory.CreateDirectory(folder);
                    if (FreeBytes(folder) is { } free && free < Oadm.Core.Uploads.UploadStore.MinFreeDiskBytes + MemoryBodyBytes)
                    {
                        throw new RpcException(new Status(StatusCode.ResourceExhausted, "The server's disk is almost full."));
                    }

                    var file = new FileStream(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".body"), FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                    memory.Position = 0;
                    await memory.CopyToAsync(file, ct).ConfigureAwait(false);
                    await memory.DisposeAsync().ConfigureAwait(false);
                    buffer = file;
                }

                // Every 256 MB written: the disk keeps its free gigabyte.
                if (spooling && (total - frame.Body.Length) / (16 * MemoryBodyBytes) != total / (16 * MemoryBodyBytes)
                    && FreeBytes(Path.Combine(paths.DataDirectory, "web-relay")) is { } left && left < Oadm.Core.Uploads.UploadStore.MinFreeDiskBytes)
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "The server's disk is almost full."));
                }

                await buffer.WriteAsync(frame.Body.Memory, ct).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return spooling ? new ReleasingStream(buffer) : buffer;
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            if (spooling)
            {
                Spooling.Release();
            }

            throw;
        }
    }

    /// <summary>Files left by a crash (DeleteOnClose does not run then) are removed once per server start.</summary>
    private static void ClearSpoolFolderOnce(string folder)
    {
        if (Interlocked.Exchange(ref _spoolFolderCleared, 1) != 0 || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.body"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // in use by a request of this start
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static long? FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null; // network share: only the size limit applies
        }
    }

    /// <summary>A spooled body; closing it frees its slot.</summary>
    private sealed class ReleasingStream(Stream inner) : Stream
    {
        private int _released;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
            {
                inner.Dispose();
                Spooling.Release();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                Spooling.Release();
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RelayWebSocketAsync(VapixClient client, Proto.DeviceWebRequestHead head, IAsyncStreamReader<Proto.DeviceWebFrame> requestStream,
        IServerStreamWriter<Proto.DeviceWebFrame> responseStream, CancellationToken ct)
    {
        var headers = head.Headers
            .Where(h => !SkippedHeaders.Contains(h.Name))
            .Select(h => KeyValuePair.Create(h.Name, DeviceValue(client.BaseAddress, h.Name, h.Value)));
        ClientWebSocket socket;
        try
        {
            socket = await client.ConnectRelayWebSocketAsync(head.PathAndQuery, [.. head.SubProtocols], headers, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogRequestFailed(head.PathAndQuery, ex.Message);
            await WriteErrorPageAsync(responseStream, ErrorText(ex), ct).ConfigureAwait(false);
            return;
        }

        using (socket)
        {
            await responseStream.WriteAsync(new Proto.DeviceWebFrame
            {
                Response = new Proto.DeviceWebResponseHead { Status = 101, Reason = "Switching Protocols", SubProtocol = socket.SubProtocol ?? string.Empty },
            }, ct).ConfigureAwait(false);

            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var toDevice = ToDeviceAsync(requestStream, socket, both.Token);
            var fromDevice = FromDeviceAsync(socket, responseStream, both.Token);
            if (await Task.WhenAny(toDevice, fromDevice).ConfigureAwait(false) == toDevice)
            {
                // The page closed: the device's close answer goes back to the page before the call ends.
                await Task.WhenAny(fromDevice, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
            }

            await both.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quiet(toDevice), Quiet(fromDevice)).ConfigureAwait(false);
        }
    }

    private static async Task ToDeviceAsync(IAsyncStreamReader<Proto.DeviceWebFrame> requestStream, ClientWebSocket socket, CancellationToken ct)
    {
        while (await requestStream.MoveNext(ct).ConfigureAwait(false))
        {
            var frame = requestStream.Current;
            if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.Message)
            {
                var message = frame.Message;
                await socket.SendAsync(message.Data.Memory, message.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, message.EndOfMessage, ct).ConfigureAwait(false);
            }
            else if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.Close)
            {
                var status = frame.Close.Status is >= 1000 and <= 4999 ? (WebSocketCloseStatus)frame.Close.Status : WebSocketCloseStatus.NormalClosure;
                await socket.CloseOutputAsync(status, frame.Close.Description, ct).ConfigureAwait(false);
                return;
            }
        }

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
    }

    private static async Task FromDeviceAsync(ClientWebSocket socket, IServerStreamWriter<Proto.DeviceWebFrame> responseStream, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await responseStream.WriteAsync(new Proto.DeviceWebFrame
                {
                    Close = new Proto.DeviceWebSocketClose { Status = (int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure), Description = result.CloseStatusDescription ?? string.Empty },
                }, ct).ConfigureAwait(false);
                return;
            }

            await responseStream.WriteAsync(new Proto.DeviceWebFrame
            {
                Message = new Proto.DeviceWebSocketMessage
                {
                    Data = ByteString.CopyFrom(buffer, 0, result.Count),
                    Text = result.MessageType == WebSocketMessageType.Text,
                    EndOfMessage = result.EndOfMessage,
                },
            }, ct).ConfigureAwait(false);
        }
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or RpcException or InvalidOperationException)
        {
            // one side ended the socket
        }
    }

    private static void AddRequestHeader(HttpRequestMessage request, string name, string value)
    {
        if (SkippedHeaders.Contains(name))
        {
            return;
        }

        if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase) || name.Equals("Allow", StringComparison.OrdinalIgnoreCase))
        {
            request.Content?.Headers.TryAddWithoutValidation(name, value);
            return;
        }

        request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>
    /// A header of the page for the device: the relay sends Origin as "/" and Referer as a path (it has its own origin on
    /// 127.0.0.1), the device sees its own address, like when the page is opened directly.
    /// </summary>
    internal static string DeviceValue(Uri deviceBase, string name, string value)
    {
        var origin = deviceBase.GetLeftPart(UriPartial.Authority);
        if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) && value == "/")
        {
            return origin;
        }

        if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase) && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
        {
            return origin + value;
        }

        return value;
    }

    /// <summary>A header of the device for the page: a Location on the device's own address becomes a path.</summary>
    internal static string PageValue(Uri deviceBase, string name, string value)
    {
        // Same host on any scheme or port (http to https, another port of the device): the page stays in the relay.
        if (name.Equals("Location", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(value, UriKind.Absolute, out var location)
            && location.Scheme is "http" or "https"
            && string.Equals(location.Host, deviceBase.Host, StringComparison.OrdinalIgnoreCase))
        {
            return location.PathAndQuery + location.Fragment;
        }

        return value;
    }

    private static async Task WriteErrorPageAsync(IServerStreamWriter<Proto.DeviceWebFrame> responseStream, string message, CancellationToken ct)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>OADM</title></head>"
            + "<body style=\"font-family:sans-serif;background:#141414;color:#f2f2f2;padding:24px\"><p>"
            + WebUtility.HtmlEncode(message) + "</p></body></html>";
        var head = new Proto.DeviceWebResponseHead { Status = 502, Reason = "Bad Gateway" };
        head.Headers.Add(new Proto.WebHeader { Name = "Content-Type", Value = "text/html; charset=utf-8" });
        head.Headers.Add(new Proto.WebHeader { Name = "Cache-Control", Value = "no-store" });
        await responseStream.WriteAsync(new Proto.DeviceWebFrame { Response = head }, ct).ConfigureAwait(false);
        await responseStream.WriteAsync(new Proto.DeviceWebFrame { Body = ByteString.CopyFromUtf8(html) }, ct).ConfigureAwait(false);
        await responseStream.WriteAsync(new Proto.DeviceWebFrame { End = true }, ct).ConfigureAwait(false);
    }

    /// <summary>The shared device texts for a request that did not reach the device.</summary>
    internal static string ErrorText(Exception ex) => ex switch
    {
        CertificateChangedException => DeviceMessages.CertificateChanged,
        VapixAuthenticationException => DeviceMessages.Unauthorized,
        TaskCanceledException { InnerException: TimeoutException } => DeviceMessages.TimedOut,
        HttpRequestException { InnerException: System.Net.Sockets.SocketException socket } => DeviceMessages.Unreachable(socket.SocketErrorCode.ToString()),
        HttpRequestException http => DeviceMessages.Unreachable(http.Message),
        WebSocketException ws => DeviceMessages.Unreachable(ws.Message),
        _ => ex.Message,
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Web interface request {Path} failed: {Reason}")]
    private partial void LogRequestFailed(string path, string reason);
}
