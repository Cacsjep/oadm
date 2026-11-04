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
    /// returns how many parts (dialogs, pages, toolbar entries) it brought. The assemblies load on the thread pool; the parts
    /// join the registry on the calling (UI) thread. The package counts as loaded afterwards.
    /// </summary>
    /// <exception cref="AddOnPartException">No dll brought a part and one could not be loaded: nothing joined the registry and a later sync tries again.</exception>
    Task<int> LoadAddOnAsync(string packageId, string folder);
}

/// <summary>
/// The client side of add-ons (docs/spec/add-ons.md rule 6): for every add-on the server has installed, the client part
/// of the server's version is taken from the cache of this server (checked against the digest the server lists) or fetched
/// (<c>GetAddOnClientPart</c>, each file checked against its SHA-256) and loaded into its own load context.
/// <see cref="PluginPackageStore"/> runs it after it announced the plugin list and announces the list again when parts
/// came, so the pages follow the add-on as they follow Turn on: on every connect and after an install. Add-ons the server
/// no longer has are marked; their files go at the client's next start. A newer version while the older one is loaded is
/// stored and loads at the next start (<see cref="RestartNeeded"/>). Errors are logged, never thrown: the client runs
/// without the add-on's pages.
/// </summary>
public sealed partial class AddOnClientSync(
    IOadmApi api,
    AddOnClientCache cache,
    IAddOnPartLoader loader,
    ILogger<AddOnClientSync> logger,
    Func<string?>? serverKey = null) : IDisposable
{
    /// <summary>The longest one sync may take (a large client part on a slow link).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The cache folder of a server without a pinned certificate (fake mode, plain HTTP in development).</summary>
    internal const string UnpinnedServer = "unpinned";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _loadedVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restartNeeded = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    /// <summary>Raised on the calling thread when a sync starts or ends (<see cref="IsSyncing"/>, <see cref="RestartNeeded"/>).</summary>
    public event EventHandler? StateChanged;

    /// <summary>A sync runs: the client parts are coming from the server.</summary>
    public bool IsSyncing => Volatile.Read(ref _running) > 0;

    /// <summary>The last sync saw an add-on that the server is installing (the client asks again later).</summary>
    public bool AnyInstalling { get; private set; }

    /// <summary>Add-ons whose newer version is stored and loads at the next start of the client.</summary>
    public IReadOnlyCollection<string> RestartNeeded => _restartNeeded;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Brings the client parts of the installed add-ons; returns how many parts were loaded. The files are checked and
    /// written and the assemblies loaded on the thread pool; the parts join the registry on the calling (UI) thread.
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        Interlocked.Increment(ref _running);
        StateChanged?.Invoke(this, EventArgs.Empty);
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
                AnyInstalling = false;
                return 0; // a server without add-ons
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogListFailed(logger, ex.Message);
                return 0;
            }

            AnyInstalling = list.AddOns.Any(a => a.State == AddOnState.AddOnInstalling);
            string server = ServerKey();
            int loaded = 0;
            try
            {
                foreach (AddOnInfo addOn in list.AddOns)
                {
                    loaded += await SyncOneAsync(server, addOn, timeout.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogListFailed(logger, "timed out");
            }

            return loaded;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string ServerKey() => CacheKey(serverKey?.Invoke());

    /// <summary>The cache folder of a pinned fingerprint ("3F:A2:..."): its hex digits in lower case, no colons (never in a folder name).</summary>
    internal static string CacheKey(string? fingerprint)
    {
        string key = (fingerprint ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal);
        return key.Length == 0 ? UnpinnedServer : key.ToLowerInvariant();
    }

    private async Task<int> SyncOneAsync(string server, AddOnInfo addOn, CancellationToken ct)
    {
        switch (addOn.State)
        {
            case AddOnState.AddOnNotInstalled:
            case AddOnState.AddOnRemoved:
                cache.MarkRemoved(server, addOn.Id);
                return 0;
            case AddOnState.AddOnInstalled:
                break;
            default:
                return 0; // installing, or not loaded on the server
        }

        cache.Unmark(server, addOn.Id);
        bool loadedHere = _loadedVersions.TryGetValue(addOn.Id, out string? loadedVersion);
        if (loader.HasPackage(addOn.Id) && !loadedHere)
        {
            return 0; // bundled or from a plugin folder (a checkout): that copy stays
        }

        if (loadedHere && string.Equals(loadedVersion, addOn.Version, StringComparison.Ordinal))
        {
            return 0; // loaded by an earlier sync
        }

        try
        {
            string folder = await FolderAsync(server, addOn, ct).ConfigureAwait(true);
            if (folder.Length == 0)
            {
                return 0; // an add-on without a client part
            }

            cache.SetCurrent(server, addOn.Id, addOn.Version);
            if (loadedHere)
            {
                // The server has a newer version than the one loaded now: it loads at the next start.
                if (_restartNeeded.Add(addOn.Id))
                {
                    LogRestartNeeded(logger, addOn.Id, loadedVersion!, addOn.Version);
                }

                return 0;
            }

            int parts = await loader.LoadAddOnAsync(addOn.Id, folder).ConfigureAwait(true);
            _loadedVersions[addOn.Id] = addOn.Version;
            LogLoaded(logger, addOn.Id, addOn.Version, parts);
            return parts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPartFailed(logger, ex, addOn.Id, addOn.Version);
            return 0;
        }
    }

    /// <summary>The cached folder of this version (still matching the server's digest) or the part fetched now; empty without files.</summary>
    private async Task<string> FolderAsync(string server, AddOnInfo addOn, CancellationToken ct)
    {
        string? folder = await Task.Run(() => cache.Find(server, addOn.Id, addOn.Version, addOn.ClientDigest), ct).ConfigureAwait(true);
        if (folder is not null)
        {
            return folder;
        }

        AddOnClientPart part = await api.GetAddOnClientPartAsync(addOn.Id, ct).ConfigureAwait(true);
        if (part.Files.Count == 0)
        {
            return string.Empty;
        }

        return await Task.Run(() => cache.Store(server, addOn.Id, part), ct).ConfigureAwait(true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The optional plugins could not be read: {Message}")]
    private static partial void LogListFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client part of optional plugin {Id} V{Version} loaded ({Parts} parts)")]
    private static partial void LogLoaded(ILogger logger, string id, string version, int parts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id}: V{Newer} is stored and loads at the next start of the client (V{Version} runs now)")]
    private static partial void LogRestartNeeded(ILogger logger, string id, string version, string newer);

    [LoggerMessage(Level = LogLevel.Error, Message = "The client part of optional plugin {Id} V{Version} could not be loaded")]
    private static partial void LogPartFailed(ILogger logger, Exception ex, string id, string version);
}
