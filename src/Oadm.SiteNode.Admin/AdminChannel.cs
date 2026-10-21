using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Oadm.SiteNode.Admin;

/// <summary>
/// Where the service listens for its app and command line: a named pipe on Windows, a Unix socket elsewhere. The installed
/// node uses fixed names; a node with its own data folder (tests, a second node on one machine) gets names from it.
/// </summary>
public sealed record AdminEndpoint(string PipeName, string SocketPath)
{
    public const string DefaultPipeName = "oadm-site-node";

    /// <summary>The socket of the installed service (systemd RuntimeDirectory=oadm-site-node).</summary>
    public const string DefaultSocketPath = "/run/oadm-site-node/admin.sock";

    public static AdminEndpoint Default { get; } = new(DefaultPipeName, DefaultSocketPath);

    /// <summary>The endpoint of a node with its own data folder.</summary>
    public static AdminEndpoint ForDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var full = Path.GetFullPath(dataDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..12];
        return new AdminEndpoint(DefaultPipeName + "-" + hash, Path.Combine(full, "admin.sock"));
    }
}

/// <summary>Sends one request to the service and reads its answer.</summary>
public sealed class AdminClient(AdminEndpoint endpoint)
{
    /// <summary>Requests and answers are one line of JSON each, at most this long.</summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    public AdminEndpoint Endpoint { get; } = endpoint;

    /// <summary>
    /// Only the installed node's server is accepted (<see cref="AdminServerCheck"/>); a node with its own data folder may
    /// also run as the current user. Tests set it.
    /// </summary>
    internal bool StrictServerCheck { get; init; } = endpoint.PipeName == AdminEndpoint.DefaultPipeName;

    /// <summary>The answer, or a failed answer with "The OADM Site Node service is not running." when it does not answer.</summary>
    public async Task<AdminResponse> SendAsync(AdminRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stream stream;
        try
        {
            stream = await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (AdminServerNotTrustedException e)
        {
            return AdminResponse.Fail(e.Message);
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or UnauthorizedAccessException)
        {
            return AdminResponse.Fail(AdminTexts.ServiceNotRunning);
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await AdminLines.WriteAsync(stream, request, ct).ConfigureAwait(false);
                return await AdminLines.ReadAsync<AdminResponse>(stream, ct).ConfigureAwait(false)
                    ?? AdminResponse.Fail(AdminTexts.ServiceNotRunning);
            }
            catch (Exception e) when (e is IOException or JsonException or SocketException)
            {
                return AdminResponse.Fail(AdminTexts.ServiceNotRunning);
            }
        }
    }

    private async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            // Identification lets the service see who asks (administrator or not), nothing more.
            var pipe = new NamedPipeClientStream(".", Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                AdminServerCheck.CheckPipe(pipe, StrictServerCheck); // before a single byte goes out
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        AdminServerCheck.CheckSocketFolder(Endpoint.SocketPath, StrictServerCheck);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.SocketPath), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>One JSON value per line.</summary>
public static class AdminLines
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AdminJson.Options);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next line as <typeparamref name="T"/>, null at the end of the stream. Throws for a line over the limit.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return line.Length == 0 ? default : JsonSerializer.Deserialize<T>(line.ToArray(), AdminJson.Options);
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            line.Write(buffer, 0, end < 0 ? read : end);
            if (line.Length > AdminClient.MaxLineBytes)
                throw new IOException("The line is too long.");
            if (end >= 0)
                return JsonSerializer.Deserialize<T>(line.ToArray(), AdminJson.Options);
        }
    }
}
