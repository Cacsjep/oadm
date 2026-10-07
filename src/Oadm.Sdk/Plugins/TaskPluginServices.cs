using Microsoft.Extensions.Logging;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// Optional for task plugins whose dialog must read the current device state before the user
/// decides (existing users, current network settings, installed ACAP applications, firmware
/// status). The client dialog calls it through <c>ITaskDialogContext.QueryAsync</c>; it runs on the
/// server with the stored credentials. Queries must be read-only: never change the device here.
/// </summary>
public interface ITaskPluginQuery
{
    Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct);
}

public interface ITaskQueryContext
{
    /// <summary>Pre-authenticated for the queried device.</summary>
    IVapixClient Vapix { get; }

    ILogger Logger { get; }
}

/// <summary>
/// Files the user uploaded from the client (firmware images, ACAP .eap packages). The dialog
/// uploads with <c>ITaskDialogContext.UploadAsync</c> and puts the returned id into the payload;
/// the task reads the file here. Uploads are kept in the server data folder and removed by the
/// server after a retention period, never by plugins.
/// </summary>
public interface IUploadedFiles
{
    Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct);

    Task<Stream> OpenReadAsync(string fileId, CancellationToken ct);
}

/// <summary>An uploaded file. <see cref="Sha256"/> is upper-case hex.</summary>
public sealed record UploadedFile(string Id, string Name, long Size, string Sha256);
