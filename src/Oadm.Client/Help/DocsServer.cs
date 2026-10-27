using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging;

using Oadm.Client.DeviceWeb;

namespace Oadm.Client.Help;

/// <summary>
/// The static file server of the Help window: HTTP/1.1 on 127.0.0.1 (a free port), the files below the docs folder, GET
/// and HEAD only. A folder answers with its <c>index.html</c>, a missing file or a refused path with <c>404.html</c>
/// (status 404). Paths with <c>..</c>, encoded slashes or backslashes, absolute paths and links that lead out of the folder
/// are refused. The docs are public: no token, no cookies. Runs from the constructor until <see cref="Dispose"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The client's loopback docs server for the Help window; it serves local files and reaches no device.")]
public sealed partial class DocsServer : IDisposable
{
    /// <summary>Requests one connection may carry before it is closed.</summary>
    public const int MaxRequestsPerConnection = 1000;

    /// <summary>Connections served at the same time; more wait for the listener.</summary>
    public const int MaxConnections = 32;

    /// <summary>A connection that sends no request for this long is closed.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private const int ChunkBytes = 64 * 1024;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".wasm"] = "application/wasm",
        // The search index of Pagefind: binary parts the page's script reads.
        [".pf_fragment"] = "application/octet-stream",
        [".pf_index"] = "application/octet-stream",
        [".pf_meta"] = "application/octet-stream",
    };

    private readonly string _root;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly string _host;
    private int _disposed;

    /// <param name="root">The docs folder (full path).</param>
    /// <param name="time">The clock of the idle timeout; the tests pass a fake one.</param>
    public DocsServer(string root, ILogger logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(logger);
        _root = RealPath(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _host = "127.0.0.1:" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        Origin = new Uri("http://" + _host + "/");
        _ = AcceptAsync(_stop.Token);
    }

    /// <summary>"http://127.0.0.1:51234/": the start page of the docs.</summary>
    public Uri Origin { get; }

    /// <summary>The port the server listens on.</summary>
    public int Port => Origin.Port;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Stops listening (the port is free again) and ends the open connections.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        _slots.Dispose();
    }

    /// <summary>The media type of a file name; unknown types are binary.</summary>
    public static string MediaTypeOf(string fileName) =>
        MediaTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
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
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new HttpHeadReader(stream);
                for (var served = 0; served < MaxRequestsPerConnection && !ct.IsCancellationRequested; served++)
                {
                    HttpRequestHead? request;
                    using (var idle = new CancellationTokenSource(IdleTimeout, _time))
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token))
                    {
                        try
                        {
                            request = await reader.ReadHeadAsync(wait.Token).ConfigureAwait(false);
                        }
                        catch (InvalidDataException ex)
                        {
                            LogBadRequest(ex.Message);
                            await WriteTextAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (request is null)
                    {
                        return;
                    }

                    var last = served == MaxRequestsPerConnection - 1;
                    if (!await HandleAsync(request, stream, last, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The browser closed the connection, it stayed idle or the server stopped.
        }
        finally
        {
            try
            {
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // stopped meanwhile
            }
        }
    }

    /// <summary>Answers one request; false closes the connection.</summary>
    private async Task<bool> HandleAsync(HttpRequestHead request, NetworkStream stream, bool last, CancellationToken ct)
    {
        if (!string.Equals(request.Header("Host"), _host, StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
            return false;
        }

        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteTextAsync(stream, 405, "Method Not Allowed", ct, "Allow: GET, HEAD\r\n").ConfigureAwait(false);
            return false;
        }

        if (request.HasBody)
        {
            // GET and HEAD carry no body; one that does is not read, so the connection cannot be used again.
            await WriteTextAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
            return false;
        }

        var keepAlive = !request.WantsClose && !last;
        var head = request.Method == "HEAD";
        var target = Resolve(request.Target, out var path);
        switch (target.Kind)
        {
            case TargetKind.File:
                await WriteFileAsync(stream, 200, "OK", target.Path!, head, keepAlive, ct).ConfigureAwait(false);
                break;
            case TargetKind.Folder:
                // "/getting-started" -> "/getting-started/": the page's relative links need the slash.
                var query = request.Target.IndexOf('?', StringComparison.Ordinal) is var q and >= 0 ? request.Target[q..] : string.Empty;
                await WriteRedirectAsync(stream, path + "/" + query, keepAlive, ct).ConfigureAwait(false);
                break;
            default:
                if (FileUnderRoot(Path.Combine(_root, "404.html")) is { } notFound)
                {
                    await WriteFileAsync(stream, 404, "Not Found", notFound, head, keepAlive, ct).ConfigureAwait(false);
                }
                else
                {
                    await WriteTextAsync(stream, 404, "Not Found", ct, head: head, keepAlive: keepAlive).ConfigureAwait(false);
                }

                break;
        }

        return keepAlive;
    }

    private enum TargetKind
    {
        Missing,
        File,
        Folder,
    }

    private readonly record struct Target(TargetKind Kind, string? Path);

    /// <summary>
    /// The file of a request target, or Missing for a refused path. <paramref name="path"/> is the target's path without
    /// query (for the folder redirect).
    /// </summary>
    private Target Resolve(string requestTarget, out string path)
    {
        var end = requestTarget.IndexOfAny(['?', '#']);
        path = end >= 0 ? requestTarget[..end] : requestTarget;
        var relative = RelativePath(path);
        if (relative is null)
        {
            return new Target(TargetKind.Missing, null);
        }

        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!IsUnderRoot(full))
        {
            return new Target(TargetKind.Missing, null);
        }

        if (Directory.Exists(full))
        {
            if (!LinksStayInside(full))
            {
                return new Target(TargetKind.Missing, null);
            }

            if (!path.EndsWith('/'))
            {
                return new Target(TargetKind.Folder, null);
            }

            return FileUnderRoot(Path.Combine(full, "index.html")) is { } index
                ? new Target(TargetKind.File, index)
                : new Target(TargetKind.Missing, null);
        }

        return FileUnderRoot(full) is { } file ? new Target(TargetKind.File, file) : new Target(TargetKind.Missing, null);
    }

    /// <summary>
    /// The path below the root of a request path ("/guide/a.html" -> "guide/a.html"), or null when it is refused: not
    /// starting with one slash, encoded slashes or backslashes, backslashes, ".." or "." segments, empty segments,
    /// control characters, drive letters or stream names (":").
    /// </summary>
    internal static string? RelativePath(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%00", StringComparison.Ordinal))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (decoded.Contains('\\', StringComparison.Ordinal) || decoded.Contains(':', StringComparison.Ordinal)
            || decoded.Contains('%', StringComparison.Ordinal) || decoded.Any(char.IsControl))
        {
            // '%' after decoding: a double-encoded path (%252e%252e), never a docs file.
            return null;
        }

        var segments = decoded[1..].Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment is "." or ".." || (segment.Length == 0 && i != segments.Length - 1))
            {
                return null;
            }
        }

        return string.Join(Path.DirectorySeparatorChar, segments.Where(s => s.Length > 0));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private bool IsUnderRoot(string full) =>
        full.Equals(_root, PathComparison)
        || (full.StartsWith(_root, PathComparison) && full.Length > _root.Length && full[_root.Length] == Path.DirectorySeparatorChar);

    /// <summary>The file when it exists below the root and no link on its way leads out of the root; else null.</summary>
    private string? FileUnderRoot(string full) =>
        IsUnderRoot(full) && File.Exists(full) && LinksStayInside(full) ? full : null;

    /// <summary>
    /// Every link (symbolic link, junction) between the root and <paramref name="full"/> points below the root. A link that
    /// leads out of the folder makes the path refused.
    /// </summary>
    private bool LinksStayInside(string full)
    {
        try
        {
            for (var current = full; current.Length > _root.Length && IsUnderRoot(current); current = Path.GetDirectoryName(current)!)
            {
                FileSystemInfo info = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
                if (info.LinkTarget is not null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || !IsUnderRoot(Path.GetFullPath(target.FullName)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The root itself may be a link (a development folder): the server works on its target.</summary>
    private static string RealPath(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            return info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Path.GetFullPath(target.FullName)
                : folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return folder;
        }
    }

    private static string Head(int status, string reason, string type, long length, bool keepAlive, string extra = "") =>
        string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {length}\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\n{extra}{(keepAlive ? "Connection: keep-alive" : "Connection: close")}\r\n\r\n");

    private static async Task WriteFileAsync(NetworkStream stream, int status, string reason, string file, bool head, bool keepAlive, CancellationToken ct)
    {
        FileStream content;
        try
        {
            content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteTextAsync(stream, 404, "Not Found", ct, head: head, keepAlive: keepAlive).ConfigureAwait(false);
            return;
        }

        await using (content.ConfigureAwait(false))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Head(status, reason, MediaTypeOf(file), content.Length, keepAlive)), ct).ConfigureAwait(false);
            if (!head)
            {
                await content.CopyToAsync(stream, ChunkBytes, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteRedirectAsync(NetworkStream stream, string location, bool keepAlive, CancellationToken ct)
    {
        var head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 301 Moved Permanently\r\nLocation: {location}\r\nContent-Length: 0\r\nCache-Control: no-cache\r\n{(keepAlive ? "Connection: keep-alive" : "Connection: close")}\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string reason, CancellationToken ct, string extra = "", bool head = false, bool keepAlive = false)
    {
        var body = Encoding.UTF8.GetBytes(reason);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Head(status, reason, "text/plain; charset=utf-8", body.Length, keepAlive, extra)), ct).ConfigureAwait(false);
        if (!head)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Help docs server: bad request ({Reason})")]
    private partial void LogBadRequest(string reason);
}
