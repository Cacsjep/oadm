using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>Loads the client part of an add-on into the client's plugin registry (implemented by <see cref="ClientPluginLoader"/>).</summary>
public interface IAddOnPartLoader
{
    /// <summary>A part of this package is loaded already (bundled, from a plugin folder or an earlier sync).</summary>
    bool HasPackage(string packageId);

    /// <summary>
    /// Loads every <c>*.Client.dll</c> of <paramref name="folder"/> into its own load context, like a plugin folder, and
    /// returns how many parts (dialogs, pages, toolbar entries) it brought. The package counts as loaded afterwards.
    /// </summary>
    int LoadAddOn(string packageId, string folder);
}

/// <summary>
/// The client side of add-ons (docs/spec/add-ons.md rule 6): for every add-on the server has installed, the client part
/// of the server's version is taken from the cache or fetched (<c>GetAddOnClientPart</c>, each file checked against its
/// SHA-256) and loaded into its own load context. <see cref="PluginPackageStore"/> runs it before it announces a new
/// plugin list, so the pages follow the add-on as they follow Turn on: on every connect and after an install. Add-ons the
/// server no longer has are marked; their files go at the client's next start. Errors are logged, never thrown: the
/// client runs without the add-on's pages.
/// </summary>
public sealed partial class AddOnClientSync(IOadmApi api, AddOnClientCache cache, IAddOnPartLoader loader, ILogger<AddOnClientSync> logger) : IDisposable
{
    /// <summary>The longest a sync may hold up the plugin list (a large client part on a slow link).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised on the calling thread when a sync loaded new client parts.</summary>
    public event EventHandler? PartsLoaded;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Brings the client parts of the installed add-ons; returns how many parts were loaded. Runs the loading on the calling
    /// (UI) thread; the files are checked and written on the thread pool.
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            AddOnList list;
            try
            {
                list = await api.ListAddOnsAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                return 0; // a server without add-ons
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogListFailed(logger, ex.Message);
                return 0;
            }

            int loaded = 0;
            try
            {
                foreach (AddOnInfo addOn in list.AddOns)
                {
                    loaded += await SyncOneAsync(addOn, timeout.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogListFailed(logger, "timed out");
            }

            if (loaded > 0)
            {
                PartsLoaded?.Invoke(this, EventArgs.Empty);
            }

            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> SyncOneAsync(AddOnInfo addOn, CancellationToken ct)
    {
        switch (addOn.State)
        {
            case AddOnState.AddOnNotInstalled:
            case AddOnState.AddOnRemoved:
                cache.MarkRemoved(addOn.Id);
                return 0;
            case AddOnState.AddOnInstalled:
                break;
            default:
                return 0; // installing, or not loaded on the server
        }

        cache.Unmark(addOn.Id);
        if (loader.HasPackage(addOn.Id))
        {
            return 0; // bundled, from a plugin folder (a checkout) or an earlier sync: that copy stays
        }

        try
        {
            string? folder = cache.Find(addOn.Id, addOn.Version);
            if (folder is null)
            {
                AddOnClientPart part = await api.GetAddOnClientPartAsync(addOn.Id, ct).ConfigureAwait(true);
                if (part.Files.Count == 0)
                {
                    return 0; // an add-on without a client part
                }

                folder = await Task.Run(() => cache.Store(addOn.Id, part), ct).ConfigureAwait(true);
            }

            int parts = loader.LoadAddOn(addOn.Id, folder);
            LogLoaded(logger, addOn.Id, addOn.Version, parts);
            return parts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPartFailed(logger, ex, addOn.Id, addOn.Version);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The add-ons could not be read: {Message}")]
    private static partial void LogListFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client part of add-on {Id} V{Version} loaded ({Parts} parts)")]
    private static partial void LogLoaded(ILogger logger, string id, string version, int parts);

    [LoggerMessage(Level = LogLevel.Error, Message = "The client part of add-on {Id} V{Version} could not be loaded")]
    private static partial void LogPartFailed(ILogger logger, Exception ex, string id, string version);
}
