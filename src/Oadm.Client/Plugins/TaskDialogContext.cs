using System.Globalization;

using Oadm.Client.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Plugins;

/// <summary>
/// Server access handed to a task plugin dialog: queries go to <c>TaskService.Query</c> for the
/// dialog's plugin, uploads to <c>FileService.Upload</c>. gRPC errors surface as RpcException whose
/// Status.Detail is the message for the user.
/// </summary>
public sealed class TaskDialogContext : ITaskDialogContext
{
    private readonly IOadmApi _api;

    public TaskDialogContext(IOadmApi api, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _api = api;
        PluginId = pluginId;
    }

    public string PluginId { get; }

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return _api.QueryTaskPluginAsync(PluginId, deviceId.ToString(), method, payloadJson, ct);
    }

    public async Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var info = await _api.UploadFileAsync(localPath, progress, ct).ConfigureAwait(false);
        return new UploadedFile(info.Id, info.Name, info.Size, info.Sha256.ToUpper(CultureInfo.InvariantCulture));
    }
}
