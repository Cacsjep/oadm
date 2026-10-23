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
    /// <summary>
    /// Answers <paramref name="method"/> for the dialog with JSON. Read only: never change the device. Throw
    /// <see cref="ArgumentException"/> for an unknown method or bad input; the message reaches the dialog.
    /// </summary>
    Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// <see cref="QueryAsync(ITaskQueryContext, IDeviceInfo, string, string?, CancellationToken)"/> with the user's call
    /// (SDK 0.4.0): read other devices than <paramref name="device"/> only through <paramref name="caller"/> (the user's
    /// view). The host calls this one; the default forwards to the old one. <paramref name="device"/> is always in the
    /// user's view (the host checked it).
    /// </summary>
    Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        QueryAsync(ctx, device, method, payloadJson, ct);
}

/// <summary>What the server gives a query (<see cref="ITaskPluginQuery.QueryAsync(ITaskQueryContext, IDeviceInfo, string, string?, CancellationToken)"/>).</summary>
public interface ITaskQueryContext
{
    /// <summary>Pre-authenticated for the queried device.</summary>
    IVapixClient Vapix { get; }

    /// <summary>The server log.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Read-only view of all managed devices (e.g. to flag an address another managed device already uses).
    /// Null when the host does not offer it.
    /// </summary>
    IDeviceRepository? Devices => null;

    /// <summary>
    /// Network checks towards the queried device, routed like it (through its site or on the server's own network), e.g.
    /// whether a new address is in use. Null on hosts without it.
    /// </summary>
    Sites.IDeviceNetwork? Network => null;
}

/// <summary>
/// Files the user uploaded from the client (firmware images, ACAP .eap packages). The dialog
/// uploads with <c>ITaskDialogContext.UploadAsync</c> and puts the returned id into the payload;
/// the task reads the file here. Uploads are kept in the server data folder and removed by the
/// server after a retention period, never by plugins.
/// </summary>
public interface IUploadedFiles
{
    /// <summary>The uploaded file with this id; null when it is gone (uploads are deleted after a day).</summary>
    Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct);

    /// <summary>Opens the file for reading; the caller disposes the stream.</summary>
    Task<Stream> OpenReadAsync(string fileId, CancellationToken ct);
}

/// <summary>An uploaded file. <see cref="Sha256"/> is upper-case hex.</summary>
/// <param name="Id">The id to put into the payload.</param>
/// <param name="Name">The file name as the user chose it.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">SHA-256 of the content, upper case hex.</param>
public sealed record UploadedFile(string Id, string Name, long Size, string Sha256);
