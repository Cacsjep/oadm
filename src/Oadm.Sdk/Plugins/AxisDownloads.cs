namespace Oadm.Sdk.Plugins;

/// <summary>
/// Files from www.axis.com that need the MyAxis sign-in of the user who started the task (SDK 0.9.0,
/// docs/spec/myaxis.md "For plugins"): AXIS OS images below <c>/ftp/pub_soft/MPQT/</c> and <c>/ftp/pub_soft/PACS/</c>. The
/// host downloads them with that user's session, which never reaches the plugin, checks size and SHA-256 and keeps them
/// in its cache for 7 days. Tasks that need the same file share one download.
/// </summary>
public interface IAxisDownloads
{
    /// <summary>Whether the user who started the task is signed in to MyAxis.</summary>
    bool HasSession { get; }

    /// <summary>
    /// The file at <paramref name="uri"/>, from the cache or downloaded now. <paramref name="sha256"/> is the hex SHA-256 and
    /// <paramref name="size"/> the size in bytes as listed on axis.com. <paramref name="progress"/> gets the bytes received
    /// so far (also while another task's download of the same file runs). Throws <see cref="ArgumentException"/> for a URL
    /// outside the two download folders and <see cref="AxisDownloadException"/> with the message for the user when the
    /// download fails (no or expired sign-in, network, size or checksum). The file stays until the returned handle is
    /// disposed.
    /// </summary>
    Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>A downloaded file of <see cref="IAxisDownloads"/>; dispose it when the task no longer needs it.</summary>
public sealed class AxisDownloadedFile : IAsyncDisposable
{
    private readonly Func<ValueTask>? _release;
    private int _disposed;

    /// <param name="path">The full path of the file on the server; read only.</param>
    /// <param name="length">Its size in bytes.</param>
    /// <param name="fromCache">True when no download was needed.</param>
    /// <param name="release">Called once when the handle is disposed.</param>
    public AxisDownloadedFile(string path, long length, bool fromCache, Func<ValueTask>? release = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        Length = length;
        FromCache = fromCache;
        _release = release;
    }

    /// <summary>The full path of the file; open it for reading only.</summary>
    public string Path { get; }

    /// <summary>The size in bytes.</summary>
    public long Length { get; }

    /// <summary>True when the file came from the cache.</summary>
    public bool FromCache { get; }

    /// <summary>Opens the file for reading.</summary>
    public Stream OpenRead() => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>Lets the host delete the file once no task uses it and its time in the cache is over.</summary>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 && _release is not null ? _release() : ValueTask.CompletedTask;
}

/// <summary>A download of <see cref="IAxisDownloads"/> failed; <see cref="Exception.Message"/> is for the user.</summary>
public sealed class AxisDownloadException : Exception
{
    /// <summary>The download failed.</summary>
    public AxisDownloadException()
    {
    }

    /// <summary>The download failed, with the message for the user.</summary>
    public AxisDownloadException(string message)
        : base(message)
    {
    }

    /// <summary>The download failed, with the message for the user and the cause.</summary>
    public AxisDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>True when the user must sign in to MyAxis (again).</summary>
    public bool NeedsSignIn { get; init; }
}
