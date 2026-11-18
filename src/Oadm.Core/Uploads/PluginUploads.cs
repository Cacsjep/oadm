using Oadm.Sdk.Plugins;

namespace Oadm.Core.Uploads;

/// <summary>
/// A core plugin's own files as system uploads (<see cref="ICorePluginContext.Uploads"/>, SDK 0.15.0): the tasks the plugin
/// starts read them by id like any upload; they are deleted after the retention like the others.
/// </summary>
internal sealed class PluginUploads(UploadStore store) : IPluginUploads
{
    private const int ChunkSize = 81_920;

    public async Task<UploadedFile> AddAsync(string fileName, Stream content, long size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            await using var writer = await store.BeginAsync(fileName, size, null, ct).ConfigureAwait(false);
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return await writer.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (UploadRejectedException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) => store.FindAsync(fileId, ct);
}
