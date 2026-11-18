using System.Collections.Concurrent;
using System.Security.Cryptography;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// Uploaded files in memory (<see cref="IUploadedFiles"/>): <see cref="Add"/> puts a file in and answers its id, as an upload
/// link or the assistant's Attach file would. For <see cref="FakeCallContext.Uploads"/>, a task's <c>UploadedFiles</c> and a core
/// plugin's own files (<see cref="FakeCoreContext.Uploads"/>, SDK 0.15.0; <see cref="Remove"/> lets one expire).
/// </summary>
public sealed class FakeUploadedFiles : IUploadedFiles, IPluginUploads
{
    private readonly ConcurrentDictionary<string, (UploadedFile File, byte[] Content)> _files = new(StringComparer.Ordinal);

    /// <summary>Adds a file and answers it (a 32 character lower case hex id, the SHA-256 in upper case hex).</summary>
    public UploadedFile Add(string name, byte[] content)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);
        var file = new UploadedFile(Guid.NewGuid().ToString("N"), name, content.Length, Convert.ToHexString(SHA256.HashData(content)));
        _files[file.Id] = (file, content.ToArray());
        return file;
    }

    /// <summary>Removes a file, as the server does after a day.</summary>
    public bool Remove(string fileId) => _files.TryRemove(fileId, out _);

    /// <summary>The files added so far.</summary>
    public int Count => _files.Count;

    /// <inheritdoc />
    public async Task<UploadedFile> AddAsync(string fileName, Stream content, long size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct).ConfigureAwait(false);
        if (copy.Length != size)
        {
            throw new InvalidOperationException("The upload is incomplete.");
        }

        return Add(fileName, copy.ToArray());
    }

    /// <inheritdoc />
    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(fileId is not null && _files.TryGetValue(fileId, out var entry) ? entry.File : null);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
        fileId is not null && _files.TryGetValue(fileId, out var entry)
            ? Task.FromResult<Stream>(new MemoryStream(entry.Content, writable: false))
            : throw new FileNotFoundException("No uploaded file has this id.");
}
