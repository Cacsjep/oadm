using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.MyAxis;

/// <summary>The <see cref="IAxisDownloads"/> of a task the given user started (the task engine asks it per task).</summary>
public interface IAxisDownloadSource
{
    Task<IAxisDownloads> ForUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// The host's downloads from axis.com for tasks (docs/spec/myaxis.md "For plugins"): with the session of the user who
/// started the task, one download per URL at a time (tasks that need the same file wait for it and see its progress), size
/// and SHA-256 checked, kept in <c>&lt;datafolder&gt;/axis-downloads/</c> 7 days after the last use and at most 5 GB in all
/// (the oldest go first, never a file in use). A cached file is checked against its SHA-256 before every use.
/// </summary>
public sealed partial class AxisDownloadService : IAxisDownloadSource, IDisposable
{
    public static readonly TimeSpan DefaultKeepFor = TimeSpan.FromDays(7);
    public const long DefaultMaxBytes = 5L * 1024 * 1024 * 1024;

    private const string TempFolder = "tmp";
    private const int BufferSize = 81920;

    /// <summary>How often one call waits for a download before it gives up.</summary>
    private const int MaxRounds = 4;

    private readonly MyAxisSessionStore _sessions;
    private readonly AxisComClient _web;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _inUse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedDownload> _downloads = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    public AxisDownloadService(string directory, MyAxisSessionStore sessions, AxisComClient web, TimeProvider? time = null, ILogger<AxisDownloadService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AxisDownloadService>.Instance;
    }

    /// <summary>The cache folder (<c>&lt;datafolder&gt;/axis-downloads</c>).</summary>
    public string Directory { get; }

    /// <summary>How long a file stays after its last use (7 days).</summary>
    public TimeSpan KeepFor { get; init; } = DefaultKeepFor;

    /// <summary>The most bytes the cache keeps (5 GB); files in use stay even above it.</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    public async Task<IAxisDownloads> ForUserAsync(Guid userId, CancellationToken ct) =>
        new UserDownloads(this, userId, await _sessions.HasSessionAsync(userId, ct).ConfigureAwait(false));

    /// <summary>
    /// Removes what the cache no longer keeps: partial downloads of no running download, files unused for
    /// <see cref="KeepFor"/>, then the oldest until it holds at most <see cref="MaxBytes"/>; never a file in use. Called at
    /// the server's start and after every download.
    /// </summary>
    public void Sweep()
    {
        lock (_gate)
        {
            try
            {
                SweepLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSweepFailed(ex);
            }
        }
    }

    private void SweepLocked()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        var temp = Path.Combine(Directory, TempFolder);
        if (System.IO.Directory.Exists(temp))
        {
            var active = _downloads.Values.Select(d => d.TempPath).ToHashSet(StringComparer.Ordinal);
            foreach (var file in System.IO.Directory.EnumerateFiles(temp))
            {
                if (!active.Contains(file))
                {
                    TryDeleteFile(file);
                }
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var kept = new List<(string Sha, string Folder, long Length, DateTime LastUse)>();
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
        {
            var sha = Path.GetFileName(folder);
            if (!IsSha256(sha))
            {
                continue;
            }

            var file = System.IO.Directory.EnumerateFiles(folder).FirstOrDefault();
            if (file is null)
            {
                TryDeleteFolder(folder);
                continue;
            }

            var info = new FileInfo(file);
            var inUse = _inUse.ContainsKey(sha);
            if (!inUse && now - info.LastWriteTimeUtc >= KeepFor)
            {
                TryDeleteFolder(folder);
                continue;
            }

            kept.Add((sha, folder, info.Length, info.LastWriteTimeUtc));
        }

        var total = kept.Sum(k => k.Length);
        foreach (var entry in kept.OrderBy(k => k.LastUse))
        {
            if (total <= MaxBytes)
            {
                break;
            }

            if (!_inUse.ContainsKey(entry.Sha) && TryDeleteFolder(entry.Folder))
            {
                total -= entry.Length;
            }
        }
    }

    internal async Task<AxisDownloadedFile> DownloadAsync(UserDownloads user, Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!AxisDownloadUrls.IsAllowed(uri))
        {
            throw new ArgumentException("Only files below /ftp/pub_soft/MPQT/ and /ftp/pub_soft/PACS/ of www.axis.com can be downloaded.", nameof(uri));
        }

        var sha = sha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsSha256(sha))
        {
            throw new ArgumentException("The SHA-256 must be 64 hexadecimal characters.", nameof(sha256));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var session = await _sessions.GetSessionAsync(user.UserId, ct).ConfigureAwait(false);
        if (session is null)
        {
            user.HasSession = false;
            throw new AxisDownloadException(AxisDownloadTexts.SignIn) { NeedsSignIn = true };
        }

        user.HasSession = true;
        var waited = false;
        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (round > MaxRounds)
            {
                // Downloaded, but never found intact afterwards (a disk that changes files): no endless downloads.
                throw new AxisDownloadException(AxisDownloadTexts.Mismatch);
            }

            var cached = await TryCachedAsync(sha, size, fromCache: !waited, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            var key = uri.AbsoluteUri + "|" + sha;
            SharedDownload download;
            lock (_gate)
            {
                if (!_downloads.TryGetValue(key, out download!))
                {
                    download = new SharedDownload(user.UserId, Path.Combine(Directory, TempFolder, Guid.NewGuid().ToString("N") + ".part"), _stopping.Token);
                    _downloads[key] = download;
                    download.Task = RunAsync(key, download, uri, sha, size, session);
                }

                download.Join(progress);
            }

            try
            {
                await download.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (AxisDownloadException ex) when (ex.NeedsSignIn && download.UserId != user.UserId)
            {
                // Another user's session expired; this user's own session tries again.
            }
            catch (AxisDownloadException ex) when (ex.NeedsSignIn)
            {
                user.HasSession = false;
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                // Every task that waited for it was cancelled just before this one joined: start again.
            }
            finally
            {
                download.Leave(progress);
            }

            waited = true;
        }
    }

    /// <summary>The cached file when it is there with the right size and SHA-256 (checked now); a mismatch deletes it.</summary>
    private async Task<AxisDownloadedFile?> TryCachedAsync(string sha, long size, bool fromCache, CancellationToken ct)
    {
        Acquire(sha);
        var keep = false;
        try
        {
            var folder = Path.Combine(Directory, sha);
            var file = System.IO.Directory.Exists(folder) ? System.IO.Directory.EnumerateFiles(folder).FirstOrDefault() : null;
            if (file is null)
            {
                return null;
            }

            if (new FileInfo(file).Length != size || !string.Equals(await HashAsync(file, ct).ConfigureAwait(false), sha, StringComparison.Ordinal))
            {
                LogCacheMismatch(sha);
                TryDeleteFolder(folder);
                return null;
            }

            try
            {
                File.SetLastWriteTimeUtc(file, _time.GetUtcNow().UtcDateTime);
            }
            catch (IOException)
            {
                // Read by another task right now: its use counts too.
            }

            keep = true;
            var released = 0;
            return new AxisDownloadedFile(file, size, fromCache, () =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    Release(sha);
                }

                return ValueTask.CompletedTask;
            });
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        finally
        {
            if (!keep)
            {
                Release(sha);
            }
        }
    }

    private async Task RunAsync(string key, SharedDownload download, Uri uri, string sha, long size, string session)
    {
        await Task.Yield();
        var ct = download.Cancel.Token;
        try
        {
            await DownloadToCacheAsync(download, uri, sha, size, session, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(download.TempPath);
            lock (_gate)
            {
                _downloads.Remove(key);
            }

            download.Dispose();
        }
    }

    private async Task DownloadToCacheAsync(SharedDownload download, Uri uri, string sha, long size, string session, CancellationToken ct)
    {
        var limit = size + (size / 50) + (1024 * 1024);
        string actual;
        long received = 0;
        try
        {
            using var response = await _web.GetAsync(uri, session, ct).ConfigureAwait(false);
            switch (AxisComClient.Classify(response))
            {
                case AxisAnswer.SignInNeeded:
                    LogExpired(download.UserId);
                    await _sessions.ExpireAsync(download.UserId, session, CancellationToken.None).ConfigureAwait(false);
                    throw new AxisDownloadException(AxisDownloadTexts.Expired) { NeedsSignIn = true };
                case AxisAnswer.Failed:
                    throw new AxisDownloadException(AxisDownloadTexts.NotDownloaded(AxisComClient.Reason(response)));
            }

            if (response.Content.Headers.ContentLength is { } announced && announced != size)
            {
                throw new AxisDownloadException(AxisDownloadTexts.Mismatch);
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(download.TempPath)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var target = new FileStream(download.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferSize];
                    while (true)
                    {
                        var read = await _web.ReadWithIdleTimeoutAsync(body, buffer, ct).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        received += read;
                        if (received > limit)
                        {
                            throw new AxisDownloadException(AxisDownloadTexts.Mismatch);
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        download.Report(received);
                    }
                }
            }

            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (HttpRequestException ex)
        {
            LogFailed(ex, uri);
            throw new AxisDownloadException(AxisDownloadTexts.NotDownloaded(ShortReason(ex)), ex);
        }
        catch (TimeoutException ex)
        {
            LogFailed(ex, uri);
            throw new AxisDownloadException(AxisDownloadTexts.NotDownloaded("no answer in time"), ex);
        }
        catch (IOException ex)
        {
            LogFailed(ex, uri);
            throw new AxisDownloadException(AxisDownloadTexts.NotDownloaded("the connection was lost"), ex);
        }

        if (received != size || !string.Equals(actual, sha, StringComparison.Ordinal))
        {
            LogDownloadMismatch(uri, received, size);
            throw new AxisDownloadException(AxisDownloadTexts.Mismatch);
        }

        var folder = Path.Combine(Directory, sha);
        var final = Path.Combine(folder, FileNameOf(uri));
        Acquire(sha);
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            foreach (var other in System.IO.Directory.EnumerateFiles(folder).Where(f => !string.Equals(f, final, StringComparison.Ordinal)))
            {
                TryDeleteFile(other);
            }

            try
            {
                File.Move(download.TempPath, final, overwrite: true);
            }
            catch (IOException) when (File.Exists(final) && new FileInfo(final).Length == size)
            {
                // The same file came by another URL and is read right now: that copy is kept.
            }

            File.SetLastWriteTimeUtc(final, _time.GetUtcNow().UtcDateTime);
            await _sessions.TouchAsync(download.UserId, CancellationToken.None).ConfigureAwait(false);
            Sweep();
        }
        finally
        {
            Release(sha);
        }
    }

    private static string ShortReason(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "axis.com was not found",
        HttpRequestError.ConnectionError => "no connection to axis.com",
        HttpRequestError.SecureConnectionError => "no secure connection to axis.com",
        HttpRequestError.ResponseEnded => "the connection was lost",
        _ => "the connection failed",
    };

    /// <summary>The URL's file name, safe for the file system.</summary>
    private static string FileNameOf(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.Segments[^1]);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c)]).Trim('.', ' ');
        return safe.Length == 0 ? "download.bin" : safe;
    }

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private void Acquire(string sha)
    {
        lock (_gate)
        {
            _inUse[sha] = _inUse.GetValueOrDefault(sha) + 1;
        }
    }

    private void Release(string sha)
    {
        lock (_gate)
        {
            var left = _inUse.GetValueOrDefault(sha) - 1;
            if (left <= 0)
            {
                _inUse.Remove(sha);
            }
            else
            {
                _inUse[sha] = left;
            }
        }
    }

    /// <summary>Files of the cache in use now (tests).</summary>
    internal int InUseCount(string sha)
    {
        lock (_gate)
        {
            return _inUse.GetValueOrDefault(sha);
        }
    }

    private void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(ex, file);
        }
    }

    private bool TryDeleteFolder(string folder)
    {
        try
        {
            System.IO.Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(ex, folder);
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    public override string ToString() => nameof(AxisDownloadService);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MyAxis: axis.com refused the session of user {UserId}; the sign-in has expired")]
    private partial void LogExpired(Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MyAxis: the download of {Uri} failed")]
    private partial void LogFailed(Exception ex, Uri uri);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MyAxis: {Uri} did not match its listed size or SHA-256 ({Received} of {Size} bytes); nothing kept")]
    private partial void LogDownloadMismatch(Uri uri, long received, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MyAxis: the cached file {Sha} no longer matched its SHA-256 and was deleted")]
    private partial void LogCacheMismatch(string sha);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MyAxis: {Path} could not be deleted yet")]
    private partial void LogDeleteFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MyAxis: the download cache could not be cleaned up")]
    private partial void LogSweepFailed(Exception ex);

    /// <summary>One running download and the tasks that wait for it.</summary>
    private sealed class SharedDownload : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<IProgress<long>?> _waiters = [];
        private long _received;

        public SharedDownload(Guid userId, string tempPath, CancellationToken stopping)
        {
            UserId = userId;
            TempPath = tempPath;
            Cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        }

        public Guid UserId { get; }

        public string TempPath { get; }

        public CancellationTokenSource Cancel { get; }

        public Task Task { get; set; } = Task.CompletedTask;

        public void Join(IProgress<long>? progress)
        {
            long received;
            lock (_lock)
            {
                _waiters.Add(progress);
                received = _received;
            }

            if (received > 0)
            {
                progress?.Report(received);
            }
        }

        /// <summary>The last waiter that leaves before the end stops the download.</summary>
        public void Leave(IProgress<long>? progress)
        {
            bool last;
            lock (_lock)
            {
                _waiters.Remove(progress);
                last = _waiters.Count == 0;
            }

            if (last && !Task.IsCompleted)
            {
                try
                {
                    Cancel.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Ended meanwhile.
                }
            }
        }

        public void Report(long received)
        {
            IProgress<long>?[] waiters;
            lock (_lock)
            {
                _received = received;
                waiters = [.. _waiters];
            }

            foreach (var waiter in waiters)
            {
                waiter?.Report(received);
            }
        }

        public void Dispose() => Cancel.Dispose();
    }
}

/// <summary>The downloads of one user's task: <see cref="HasSession"/> follows what the last call learned.</summary>
internal sealed class UserDownloads(AxisDownloadService service, Guid userId, bool hasSession) : IAxisDownloads
{
    private volatile bool _hasSession = hasSession;

    public Guid UserId { get; } = userId;

    public bool HasSession
    {
        get => _hasSession;
        set => _hasSession = value;
    }

    public Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct) =>
        service.DownloadAsync(this, uri, sha256, size, progress, ct);

    public override string ToString() => $"AxisDownloads of {UserId.ToString("D", CultureInfo.InvariantCulture)}";
}
