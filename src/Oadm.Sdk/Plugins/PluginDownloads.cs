namespace Oadm.Sdk.Plugins;

/// <summary>
/// A file a core plugin sends to its client page through gRPC <c>PluginService.Download</c>
/// (<see cref="ICorePluginDownloads.OpenDownloadAsync"/>): the host copies <see cref="Content"/> to the client in chunks
/// and disposes the download afterwards, also when the client cancels. Nothing is stored on the server, so a plugin can
/// pass a device answer straight on (e.g. a recording export opened with <c>VapixRequestOptions.StreamResponse</c>).
/// </summary>
public sealed class PluginDownload : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    /// <param name="content">The bytes, read once from the start; disposed with the download.</param>
    /// <param name="fileName">The suggested file name (no folder), e.g. the name the device sent.</param>
    /// <param name="length">The length in bytes when known, else null.</param>
    /// <param name="owner">Disposed after <paramref name="content"/>, e.g. the device response the stream belongs to.</param>
    public PluginDownload(Stream content, string fileName, long? length = null, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Content = content;
        FileName = fileName;
        Length = length is >= 0 ? length : null;
        _owner = owner;
    }

    public Stream Content { get; }

    public string FileName { get; }

    public long? Length { get; }

    /// <summary>
    /// What the audit entry of an audited download says (<see cref="ICorePlugin.IsAudited"/>), e.g. the device and the
    /// time range; null = the method name. Never a secret.
    /// </summary>
    public string? AuditDetail { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
    }
}

/// <summary>
/// Optional for a core plugin: files its page downloads (gRPC <c>PluginService.Download</c>). The host checks the
/// method's permission like an Invoke (<see cref="ICorePlugin.RequiredPermission"/>), audits it when
/// <see cref="ICorePlugin.IsAudited"/> says so (with <see cref="PluginDownload.AuditDetail"/>) and answers like an Invoke
/// for the plugin's exceptions (<see cref="ArgumentException"/> = invalid argument, <see cref="InvalidOperationException"/>
/// = failed precondition with the message).
/// </summary>
public interface ICorePluginDownloads
{
    /// <summary>Opens the file <paramref name="method"/> names for <paramref name="payloadJson"/>.</summary>
    Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct);
}
