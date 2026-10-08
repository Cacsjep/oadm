using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Uploads;

/// <summary>
/// Files uploaded by client dialogs (firmware images, ACAP packages) in <c>&lt;datafolder&gt;/uploads</c>:
/// <c>&lt;id&gt;.bin</c> with the content and <c>&lt;id&gt;.json</c> with name, size, SHA-256 and upload time.
/// While an upload is written it lives in <c>&lt;id&gt;.part</c>; the metadata file is written last, so a
/// file without metadata is never visible. Size limit <c>Uploads.MaxMegabytes</c>, removed after
/// <c>Uploads.RetentionHours</c> by <see cref="DeleteExpiredAsync"/>. Ids are 32 lower-case hex chars;
/// anything else is rejected, so an id can never escape the folder.
/// All uploads together use at most <see cref="QuotaBytes"/> (10 GB, user decision) and at least
/// <see cref="MinFreeDiskBytes"/> (1 GB) of the disk stay free: a new upload first removes the oldest finished
/// uploads until it fits, otherwise it is refused (<see cref="UploadRejectedException.TooLarge"/>, RESOURCE_EXHAUSTED).
/// </summary>
public sealed partial class UploadStore : IUploadedFiles
{
    private const string ContentExtension = ".bin";
    private const string MetadataExtension = ".json";
    private const string PartialExtension = ".part";
    private const int MaxNameLength = 255;

    /// <summary>Disk space all uploads together may use (10 GB, user decision).</summary>
    public const long DefaultQuotaBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>Free disk space that must remain after an upload (1 GB).</summary>
    public const long MinFreeDiskBytes = 1L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ServerSettingsStore? _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _quotaGate = new();
    private readonly Dictionary<string, long> _reserved = new(StringComparer.Ordinal);

    public UploadStore(OadmPaths paths, ServerSettingsStore? settings = null, TimeProvider? timeProvider = null, ILogger<UploadStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory = Path.Combine(paths.DataDirectory, "uploads");
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        FreeDiskSpace = ReadFreeDiskSpace;
    }

    /// <summary><c>&lt;datafolder&gt;/uploads</c>.</summary>
    public string Directory { get; }

    /// <summary>Overrides the <c>Uploads.MaxMegabytes</c> setting (tests).</summary>
    public long? MaxBytesOverride { get; set; }

    /// <summary>Disk space all uploads together may use; default <see cref="DefaultQuotaBytes"/>.</summary>
    public long QuotaBytes { get; set; } = DefaultQuotaBytes;

    /// <summary>Free bytes on the upload disk (tests replace it); default: the drive of <see cref="Directory"/>.</summary>
    public Func<long?> FreeDiskSpace { get; set; }

    public static bool IsValidId(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Current size limit in bytes.</summary>
    public async Task<long> GetMaxBytesAsync(CancellationToken ct)
    {
        if (MaxBytesOverride is { } overridden)
        {
            return overridden;
        }

        var megabytes = await ReadIntSettingAsync(SettingKeys.UploadsMaxMegabytes, ServerSettings.DefaultUploadsMaxMegabytes, ct).ConfigureAwait(false);
        return megabytes * 1024L * 1024L;
    }

    /// <summary>Current retention period.</summary>
    public async Task<TimeSpan> GetRetentionAsync(CancellationToken ct) =>
        TimeSpan.FromHours(await ReadIntSettingAsync(SettingKeys.UploadsRetentionHours, ServerSettings.DefaultUploadsRetentionHours, ct).ConfigureAwait(false));

    /// <summary>Starts an upload. Throws <see cref="UploadRejectedException"/> for an empty name or a size over the limit.</summary>
    public async Task<UploadWriter> BeginAsync(string name, long size, CancellationToken ct)
    {
        var safeName = SanitizeName(name);
        if (size < 0)
        {
            throw new UploadRejectedException("The file size must not be negative.");
        }

        var max = await GetMaxBytesAsync(ct).ConfigureAwait(false);
        if (size > max)
        {
            throw new UploadRejectedException(string.Create(CultureInfo.InvariantCulture,
                $"The file is larger than the upload limit of {max / (1024 * 1024)} MB.")) { TooLarge = true };
        }

        System.IO.Directory.CreateDirectory(Directory);
        var id = Guid.NewGuid().ToString("N");
        Reserve(id, size);
        try
        {
            return new UploadWriter(this, id, safeName, size, Path.Combine(Directory, id + PartialExtension));
        }
        catch
        {
            Release(id);
            throw;
        }
    }

    public async Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct)
    {
        var meta = await ReadMetadataAsync(fileId, ct).ConfigureAwait(false);
        return meta is null || !File.Exists(ContentPath(fileId)) ? null : meta.ToUploadedFile();
    }

    public async Task<Stream> OpenReadAsync(string fileId, CancellationToken ct)
    {
        if (await FindAsync(fileId, ct).ConfigureAwait(false) is null)
        {
            throw new FileNotFoundException($"Uploaded file {fileId} does not exist (it may have expired).", fileId);
        }

        return new FileStream(ContentPath(fileId), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81_920, useAsync: true);
    }

    /// <summary>Deletes an upload. Returns false when it does not exist.</summary>
    public Task<bool> DeleteAsync(string fileId, CancellationToken ct)
    {
        if (!IsValidId(fileId))
        {
            return Task.FromResult(false);
        }

        var existed = File.Exists(MetadataPath(fileId)) || File.Exists(ContentPath(fileId));
        TryDelete(MetadataPath(fileId));
        TryDelete(ContentPath(fileId));
        TryDelete(Path.Combine(Directory, fileId + PartialExtension));
        return Task.FromResult(existed);
    }

    /// <summary>Deletes uploads (and abandoned partial files) older than the retention period. Returns the number removed.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow().UtcDateTime - await GetRetentionAsync(ct).ConfigureAwait(false);
        var removed = 0;
        foreach (var file in System.IO.Directory.GetFiles(Directory))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id))
            {
                continue;
            }

            DateTime created;
            try
            {
                created = File.GetLastWriteTimeUtc(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (created >= cutoff)
            {
                continue;
            }

            var extension = Path.GetExtension(file);
            if (extension == MetadataExtension || extension == PartialExtension
                || (extension == ContentExtension && !File.Exists(MetadataPath(id))))
            {
                if (extension == MetadataExtension)
                {
                    TryDelete(ContentPath(id));
                    removed++;
                    LogExpired(id);
                }

                TryDelete(file);
            }
        }

        return removed;
    }

    internal async Task<UploadedFile> CommitAsync(string id, string name, long size, string sha256, string partialPath, CancellationToken ct)
    {
        File.Move(partialPath, ContentPath(id), overwrite: true);
        var meta = new Metadata(id, name, size, sha256, _time.GetUtcNow().UtcDateTime);
        var tmp = MetadataPath(id) + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(meta, JsonOptions), ct).ConfigureAwait(false);
        File.Move(tmp, MetadataPath(id), overwrite: true);
        LogStored(id, size);
        return meta.ToUploadedFile();
    }

    /// <summary>
    /// Makes room for an upload of <paramref name="size"/> bytes and reserves it until the writer is disposed:
    /// removes the oldest finished uploads while the quota or the free disk space would be exceeded.
    /// </summary>
    /// <exception cref="UploadRejectedException">Still no room after removing every finished upload.</exception>
    private void Reserve(string id, long size)
    {
        lock (_quotaGate)
        {
            var finished = ListFinished();
            var used = finished.Sum(f => f.Size) + _reserved.Values.Sum();
            var freeAtStart = FreeDiskSpace();
            long freed = 0;
            var next = 0;
            bool QuotaOk() => used + size <= QuotaBytes;
            bool DiskOk() => freeAtStart is not { } free || free + freed - size >= MinFreeDiskBytes;
            while ((!QuotaOk() || !DiskOk()) && next < finished.Count)
            {
                var oldest = finished[next++];
                TryDelete(MetadataPath(oldest.Id));
                TryDelete(ContentPath(oldest.Id));
                used -= oldest.Size;
                freed += oldest.Size;
                LogRemovedForRoom(oldest.Id, oldest.Size);
            }

            if (!QuotaOk())
            {
                throw new UploadRejectedException(string.Create(CultureInfo.InvariantCulture,
                    $"There is no room for this upload: all uploads together may use {QuotaBytes / (1024 * 1024)} MB and the uploads still in progress need the rest. Try again later."))
                { TooLarge = true };
            }

            if (!DiskOk())
            {
                throw new UploadRejectedException("There is not enough free disk space on the server for this upload: at least 1 GB must stay free.") { TooLarge = true };
            }

            _reserved[id] = size;
        }
    }

    /// <summary>Ends the reservation of an upload (finished uploads count by their file from now on).</summary>
    internal void Release(string id)
    {
        lock (_quotaGate)
        {
            _reserved.Remove(id);
        }
    }

    /// <summary>Finished uploads (content + metadata), oldest first.</summary>
    private List<(string Id, long Size, DateTime CreatedUtc)> ListFinished()
    {
        var result = new List<(string Id, long Size, DateTime CreatedUtc)>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return result;
        }

        foreach (var file in System.IO.Directory.GetFiles(Directory, "*" + ContentExtension))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id) || !File.Exists(MetadataPath(id)))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                result.Add((id, info.Length, info.LastWriteTimeUtc));
            }
            catch (IOException)
            {
            }
        }

        result.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        return result;
    }

    private long? ReadFreeDiskSpace()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(Directory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null; // unknown (e.g. a network share): only the quota applies
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeName(string name)
    {
        var fileName = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/')[^1]).Trim();
        fileName = new string([.. fileName.Where(c => !char.IsControl(c))]);
        if (fileName.Length == 0)
        {
            throw new UploadRejectedException("The file name must not be empty.");
        }

        return fileName.Length > MaxNameLength ? fileName[..MaxNameLength] : fileName;
    }

    private async Task<Metadata?> ReadMetadataAsync(string fileId, CancellationToken ct)
    {
        if (!IsValidId(fileId) || !File.Exists(MetadataPath(fileId)))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(MetadataPath(fileId), ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Metadata>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task<int> ReadIntSettingAsync(string key, int fallback, CancellationToken ct)
    {
        if (_settings is null)
        {
            return fallback;
        }

        try
        {
            return await _settings.GetAsync<int?>(key, ct).ConfigureAwait(false) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private string ContentPath(string id) => Path.Combine(Directory, id + ContentExtension);

    private string MetadataPath(string id) => Path.Combine(Directory, id + MetadataExtension);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {FileId} stored ({Size} bytes)")]
    private partial void LogStored(string fileId, long size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {FileId} ({Size} bytes) removed to make room for a new upload")]
    private partial void LogRemovedForRoom(string fileId, long size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {FileId} expired and was deleted")]
    private partial void LogExpired(string fileId);

    private sealed record Metadata(string Id, string Name, long Size, string Sha256, DateTime CreatedUtc)
    {
        public UploadedFile ToUploadedFile() => new(Id, Name, Size, Sha256);
    }
}

/// <summary>
/// Writes one upload: chunks are appended and hashed (SHA-256) on the fly. <see cref="CompleteAsync"/>
/// checks the declared size and publishes the file; disposing without completing deletes it.
/// </summary>
public sealed class UploadWriter : IAsyncDisposable
{
    private readonly UploadStore _store;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly string _partialPath;
    private long _written;
    private bool _completed;
    private bool _disposed;

    internal UploadWriter(UploadStore store, string id, string name, long declaredSize, string partialPath)
    {
        _store = store;
        Id = id;
        Name = name;
        DeclaredSize = declaredSize;
        _partialPath = partialPath;
        _stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
    }

    public string Id { get; }

    public string Name { get; }

    public long DeclaredSize { get; }

    public long Written => _written;

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_written + data.Length > DeclaredSize)
        {
            throw new UploadRejectedException("The upload is larger than the announced file size.");
        }

        _hash.AppendData(data.Span);
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        _written += data.Length;
    }

    public async Task<UploadedFile> CompleteAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_written != DeclaredSize)
        {
            throw new UploadRejectedException(string.Create(CultureInfo.InvariantCulture,
                $"The upload is incomplete ({_written} of {DeclaredSize} bytes)."));
        }

        await _stream.FlushAsync(ct).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        var sha = Convert.ToHexString(_hash.GetHashAndReset());
        var file = await _store.CommitAsync(Id, Name, _written, sha, _partialPath, ct).ConfigureAwait(false);
        _completed = true;
        return file;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _hash.Dispose();
        _store.Release(Id);
        if (!_completed)
        {
            UploadStore.TryDelete(_partialPath);
        }
    }
}

/// <summary>An upload was refused (size limit, size mismatch, empty name). The message is meant for the user.</summary>
public sealed class UploadRejectedException : Exception
{
    public UploadRejectedException()
    {
    }

    public UploadRejectedException(string message)
        : base(message)
    {
    }

    public UploadRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file exceeds <c>Uploads.MaxMegabytes</c>, the upload quota or the free disk space (RESOURCE_EXHAUSTED).</summary>
    public bool TooLarge { get; init; }
}

/// <summary>Used when no upload store is configured (tests, tools).</summary>
public sealed class NoUploadedFiles : IUploadedFiles
{
    public static readonly NoUploadedFiles Instance = new();

    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) => Task.FromResult<UploadedFile?>(null);

    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
        throw new FileNotFoundException("No uploaded files are available.", fileId);
}
