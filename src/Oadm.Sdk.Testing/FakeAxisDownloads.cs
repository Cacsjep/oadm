using System.Collections.Concurrent;
using System.Security.Cryptography;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// <see cref="IAxisDownloads"/> for plugin tests (SDK 0.9.0): the files axis.com serves are in <see cref="Files"/>, keyed by
/// URL. Like the host it refuses URLs outside <c>https://www.axis.com/ftp/pub_soft/MPQT/</c> and <c>/PACS/</c>
/// (<see cref="ArgumentException"/>), throws <see cref="AxisDownloadException"/> without a session, after <see cref="Expire"/>,
/// for an unknown URL and for a size or SHA-256 that does not match the file. Every request is in <see cref="Requests"/>;
/// files are written to a temp folder and deleted with <see cref="Dispose"/>.
/// </summary>
public sealed class FakeAxisDownloads : IAxisDownloads, IDisposable
{
    private static readonly string[] Folders = ["/ftp/pub_soft/MPQT/", "/ftp/pub_soft/PACS/"];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-fake-axis-downloads", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<Uri> _requests = new();
    private int _open;

    /// <inheritdoc />
    public bool HasSession { get; set; } = true;

    /// <summary>The files on axis.com by URL.</summary>
    public ConcurrentDictionary<Uri, byte[]> Files { get; } = new();

    /// <summary>When set, every download fails with this exception (e.g. a network error).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every <see cref="DownloadAsync"/> call that passed the URL check, in order.</summary>
    public IReadOnlyList<Uri> Requests => [.. _requests];

    /// <summary>Handles returned and not disposed yet.</summary>
    public int OpenHandles => Volatile.Read(ref _open);

    /// <summary>Adds a file and returns its hex SHA-256 (as axis.com lists it).</summary>
    public string Add(Uri uri, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(content);
        Files[uri] = content;
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>The sign-in expires: the next download fails with <see cref="AxisDownloadTexts.Expired"/>.</summary>
    public void Expire() => _expired = true;

    private volatile bool _expired;

    /// <inheritdoc />
    public async Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "www.axis.com", StringComparison.OrdinalIgnoreCase)
            || uri.OriginalString.Contains("..", StringComparison.Ordinal) || uri.Query.Length > 0
            || !Folders.Any(f => uri.AbsolutePath.StartsWith(f, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Only files below /ftp/pub_soft/MPQT/ and /ftp/pub_soft/PACS/ of www.axis.com can be downloaded.", nameof(uri));
        }

        ct.ThrowIfCancellationRequested();
        _requests.Enqueue(uri);
        if (_expired)
        {
            HasSession = false;
            _expired = false;
            throw new AxisDownloadException(AxisDownloadTexts.Expired) { NeedsSignIn = true };
        }

        if (!HasSession)
        {
            throw new AxisDownloadException(AxisDownloadTexts.SignIn) { NeedsSignIn = true };
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (!Files.TryGetValue(uri, out var content))
        {
            throw new AxisDownloadException(AxisDownloadTexts.NotDownloaded("axis.com answered 404"));
        }

        if (content.LongLength != size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new AxisDownloadException(AxisDownloadTexts.Mismatch);
        }

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(uri.AbsolutePath));
        await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);
        progress?.Report(content.LongLength);
        Interlocked.Increment(ref _open);
        return new AxisDownloadedFile(path, content.LongLength, fromCache: false, () =>
        {
            Interlocked.Decrement(ref _open);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Deletes the written files.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test still reads a file.
        }
    }
}
