using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using Oadm.Contracts;
using Oadm.Contracts.Updates;
using Oadm.Core.Plugins;
using Oadm.Server.Updates;

namespace Oadm.Server.AddOns;

/// <summary>Where and for which release the installer works.</summary>
/// <param name="DataDirectory">The server's data folder; add-ons live in <c>addons/</c> below it.</param>
/// <param name="ServerVersion">The server's version ("4.11.0"; a development build or a pre-release downloads nothing).</param>
/// <param name="RuntimeId">The server's platform ("win-x64", ...; <see cref="CurrentRuntimeId"/>).</param>
public sealed record AddOnInstallerOptions(string DataDirectory, string ServerVersion, string RuntimeId)
{
    /// <summary>How long the answer of <see cref="IAddOnFiles.CanReachAsync"/> is kept for the Plugins page.</summary>
    public static readonly TimeSpan ReachCacheTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The release whose add-ons this server installs ("4.11.0"); null for a development build or a pre-release (no add-ons to
    /// download, folders put in by hand load as they are).
    /// </summary>
    public string? ReleaseVersion =>
        !UpdateVersions.IsDevelopmentBuild(ServerVersion) && UpdateVersions.ParseStable(ServerVersion) is { } v ? UpdateVersions.Plain(v) : null;

    /// <summary>The runtime id of the add-on ZIPs for this process: win, linux or osx with x64 or arm64.</summary>
    public static string CurrentRuntimeId()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }
}

/// <summary>
/// Installs and removes the add-ons of this OADM version (docs/spec/add-ons.md "Server"): downloads the release file into
/// <c>addons/.download/</c> (<see cref="IAddOnFiles"/>: own release only, SHA-256 checked), unpacks it into a new folder,
/// checks its plugin.json, swaps it into <c>addons/&lt;id&gt;/</c>, loads it (<see cref="PluginLoader.LoadPackage"/>, off; the
/// load raises the registry's <see cref="PluginRegistry.PackageAdded"/>) and reports Installed once the registry's readers
/// follow it (<see cref="IAddOnActivation.PackageFollowedAsync"/>). Remove turns the add-on off at once and deletes its folder at the next start. One install or
/// remove at a time, none while a self update runs; any failure leaves nothing behind.
/// <para>
/// Server start (wiring in <c>OadmServerHost</c>): <see cref="LoadInstalledAsync(bool, CancellationToken)"/> right after the bundled plugins are loaded,
/// <see cref="FetchAfterUpdateAsync"/> in the background once the start is over (<see cref="PluginRegistry.MarkStarted"/>: the
/// stored plugin choices applied, the licenses read; an add-on of an older version, after a self update or a restore, is
/// downloaded again before it loads and is followed like one installed now).
/// </para>
/// </summary>
public sealed partial class AddOnInstaller : IAddOnInstaller, IDisposable
{
    private readonly AddOnInstallerOptions _options;
    private readonly IAddOnFiles _files;
    private readonly IAddOnListStore _list;
    private readonly IAddOnActivation _activation;
    private readonly PluginLoader _loader;
    private readonly UpdateGate _update;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly SemaphoreSlim _partRead = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _partCallers = new(StringComparer.Ordinal);
    private (DateTimeOffset At, bool Value)? _reach;
    private Task<bool>? _reachRun;

    public AddOnInstaller(
        AddOnInstallerOptions options,
        IAddOnFiles files,
        IAddOnListStore list,
        IAddOnActivation activation,
        PluginLoader loader,
        UpdateGate update,
        ILogger<AddOnInstaller> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _files = files;
        _list = list;
        _activation = activation;
        _loader = loader;
        _update = update;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Root = AddOnFolders.Root(options.DataDirectory);
        foreach (var entry in AddOnCatalog.Entries)
        {
            _slots[entry.Id] = new Slot();
        }
    }

    /// <summary><c>&lt;datafolder&gt;/addons</c>.</summary>
    public string Root { get; }

    private string DownloadFolder => Path.Combine(Root, AddOnFolders.DownloadFolderName);

    private string FolderOf(AddOnEntry entry) => Path.Combine(Root, entry.Id);

    // ----- Server start -----

    /// <summary>The start of a server whose data folder was not fixed at this start (tests, a console run).</summary>
    public Task LoadInstalledAsync(CancellationToken ct) => LoadInstalledAsync(takeUpFolders: true, ct);

    /// <summary>
    /// Server start, right after the bundled plugins are loaded: deletes the folders of removed add-ons, of ids this version
    /// does not offer and the leftovers of an install that stopped, takes up folders put in by hand (only with
    /// <paramref name="takeUpFolders"/>: not when the service had to fix the data folder's permissions at this start, a folder
    /// put there before could come from anyone), and loads every installed add-on of this version (no event: the start reads
    /// the registry afterwards). An id a bundled plugin has is not loaded (log line). Add-ons of another version or without
    /// their folder wait for <see cref="FetchAfterUpdateAsync"/>.
    /// </summary>
    public async Task LoadInstalledAsync(bool takeUpFolders, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        AddOnFolders.CleanTemporary(Root);
        var list = (await _list.ReadAsync(ct).ConfigureAwait(false)).ToList();
        var changed = list.RemoveAll(a => !AddOnCatalog.Contains(a.Id)) > 0;

        // Folders of ids this version does not offer (an add-on an older release had, a folder put in by mistake).
        foreach (var folder in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith('.') || AddOnCatalog.Contains(name))
            {
                continue;
            }

            if (AddOnFolders.TryDelete(folder))
            {
                LogUnknownDeleted(_logger, name);
            }
            else
            {
                LogUnknownKept(_logger, name);
            }
        }

        // Removed: the folder goes now (no load context holds it yet).
        foreach (var removed in list.Where(a => a.Removed).ToList())
        {
            if (AddOnCatalog.Find(removed.Id) is not { } entry || AddOnFolders.TryDelete(FolderOf(entry)))
            {
                list.Remove(removed);
                changed = true;
                LogRemovedDeleted(_logger, removed.Id);
            }
            else
            {
                LogRemovedKept(_logger, removed.Id);
            }
        }

        // Put in by hand from the release ZIP: installed, of the release its version file names.
        foreach (var entry in AddOnCatalog.Entries)
        {
            if (list.Exists(a => Same(a.Id, entry.Id)) || !File.Exists(Path.Combine(FolderOf(entry), PluginLoader.ManifestFileName)))
            {
                continue;
            }

            if (!takeUpFolders)
            {
                LogNotTakenUp(_logger, entry.Id);
                continue;
            }

            var version = AddOnFolders.ReadVersion(FolderOf(entry)) ?? (_options.ReleaseVersion is null ? _options.ServerVersion : string.Empty);
            list.Add(new InstalledAddOn(entry.Id, version));
            changed = true;
            LogTakenUp(_logger, entry.Id, version);
        }

        var release = _options.ReleaseVersion;
        foreach (var entry in AddOnCatalog.Entries)
        {
            var slot = SlotOf(entry);
            var installed = list.Find(a => Same(a.Id, entry.Id));
            if (IsBundled(entry))
            {
                lock (_sync)
                {
                    slot.Bundled = true;
                }

                if (installed is not null)
                {
                    LogBundledWins(_logger, entry.Id);
                }

                continue;
            }

            if (installed is null || installed.Removed)
            {
                // Removed: its folder could not be deleted; not loaded, gone at a later start.
                continue;
            }

            var folderThere = File.Exists(Path.Combine(FolderOf(entry), PluginLoader.ManifestFileName));
            if (release is null)
            {
                // A development build or a pre-release: the folder as it is, nothing to download.
                if (folderThere)
                {
                    LoadAtStart(entry, slot, installed.Version);
                }
                else
                {
                    list.Remove(installed);
                    changed = true;
                }

                continue;
            }

            // The exact release: the folder of a pre-release ("4.11.0-rc.1") is not the add-on of 4.11.0.
            if (!folderThere || !string.Equals(installed.Version, release, StringComparison.Ordinal))
            {
                lock (_sync)
                {
                    slot.Version = installed.Version;
                    slot.Pending = true;
                    slot.Installing = 0;
                }

                LogFetchPending(_logger, entry.Id, installed.Version, release);
                continue;
            }

            LoadAtStart(entry, slot, installed.Version);
        }

        if (changed)
        {
            await _list.WriteAsync(list, ct).ConfigureAwait(false);
        }
    }

    private void LoadAtStart(AddOnEntry entry, Slot slot, string version)
    {
        var loaded = _loader.LoadPackage(FolderOf(entry)) is not null;
        lock (_sync)
        {
            slot.Version = version;
            slot.Loaded = loaded;
            slot.Failed = loaded ? null : AddOnTexts.CouldNotLoad(entry.DisplayName);
        }

        if (loaded)
        {
            LogLoaded(_logger, entry.Id, version);
        }
        else
        {
            LogLoadFailed(_logger, entry.Id);
        }
    }

    /// <summary>
    /// After the start (the stored plugin choices applied): downloads the add-on of this version for every installed add-on
    /// of another version or without its folder (after a self update or a restore), loads it and lets it follow its choice.
    /// One that cannot be downloaded stays unloaded with <see cref="AddOnTexts.NotLoadedAfterUpdate"/>; OADM runs on.
    /// Never throws but for <paramref name="ct"/>; a server that stops meanwhile ends it with a log line.
    /// </summary>
    public async Task FetchAfterUpdateAsync(CancellationToken ct)
    {
        var release = _options.ReleaseVersion;
        List<AddOnEntry> pending;
        lock (_sync)
        {
            pending = [.. AddOnCatalog.Entries.Where(e => _slots[e.Id].Pending)];
        }

        if (pending.Count == 0 || release is null)
        {
            return;
        }

        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        }
        catch (ObjectDisposedException)
        {
            LogFetchStopped(_logger);
            return;
        }

        using (linked)
        {
            try
            {
                await FetchPendingAsync(pending, release, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && linked.IsCancellationRequested))
            {
                LogFetchStopped(_logger); // the installer was disposed: the server stops, the next start tries again
            }
        }
    }

    private async Task FetchPendingAsync(List<AddOnEntry> pending, string release, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in pending)
            {
                var slot = SlotOf(entry);
                lock (_sync)
                {
                    if (!slot.Pending)
                    {
                        continue; // installed meanwhile from the Plugins page
                    }
                }

                try
                {
                    var folder = await FetchAsync(entry, release, slot, null, ct).ConfigureAwait(false);
                    LoadNow(entry, folder);
                    MarkLoaded(slot, release);
                    LogFetched(_logger, entry.Id, release);

                    // The load raised PackageAdded: its stored choice applies (its core plugins start when it is on).
                    await RecordAsync(entry, release, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // A failed add-on never stops the server.
                catch (Exception ex) when (ex is not ObjectDisposedException)
#pragma warning restore CA1031
                {
                    lock (_sync)
                    {
                        if (!slot.Loaded)
                        {
                            slot.Failed = AddOnTexts.NotLoadedAfterUpdate(entry.DisplayName, release);
                        }
                    }

                    LogFetchFailed(_logger, ex, entry.Id, release);
                }
                finally
                {
                    lock (_sync)
                    {
                        slot.Pending = false;
                        slot.Installing = null;
                    }
                }
            }
        }
        finally
        {
            ReleaseQuietly(_turn);
        }
    }

    // ----- IAddOnInstaller -----

    /// <summary>Every add-on with its state; checks whether the server reaches the release (the Plugins page's card).</summary>
    public Task<IReadOnlyList<AddOnStatus>> ListAsync(CancellationToken ct) => ListAsync(checkReach: true, ct);

    public async Task<IReadOnlyList<AddOnStatus>> ListAsync(bool checkReach, CancellationToken ct)
    {
        var reach = checkReach ? await CanReachAsync(ct).ConfigureAwait(false) : KnownReach();
        return [.. AddOnCatalog.Entries.Select(e => StatusOf(e, reach))];
    }

    public bool IsRemoved(string packageId)
    {
        lock (_sync)
        {
            return _slots.TryGetValue(packageId ?? string.Empty, out var slot) && slot.Loaded && slot.Removed;
        }
    }

    public async Task<AddOnStatus> InstallAsync(string id, IProgress<AddOnProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var entry = EntryOf(id);
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var slot = SlotOf(entry);
            bool keep;
            string loadedVersion;
            lock (_sync)
            {
                if (slot.Bundled)
                {
                    throw new InvalidOperationException(AddOnTexts.PartOfServer(entry.DisplayName));
                }

                if (slot.Loaded && !slot.Removed)
                {
                    throw new InvalidOperationException(AddOnTexts.AlreadyInstalled(entry.DisplayName));
                }

                keep = slot.Loaded;
                loadedVersion = slot.Version;
            }

            if (keep)
            {
                // Removed but still loaded (off): installing it again keeps it, nothing is downloaded (also in a development
                // build or a pre-release).
                await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, loadedVersion), CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    slot.Removed = false;
                }

                progress.Report(new AddOnProgress(AddOnStep.Install, 100));
                LogKeptAgain(_logger, entry.Id);
                return StatusOf(entry, reach: true);
            }

            var release = _options.ReleaseVersion ?? throw new InvalidOperationException(AddOnTexts.NoRelease);
            bool wasPending;
            lock (_sync)
            {
                wasPending = slot.Pending;
                slot.Pending = false; // waiting for the fetch after an update: this install does it
            }

            // A first install starts off; Install again after a failed fetch keeps the administrator's choice.
            var firstInstall = !_activation.HasChoice(entry.Id);
            try
            {
                var folder = await FetchAsync(entry, release, slot, progress, ct).ConfigureAwait(false);
                progress.Report(new AddOnProgress(AddOnStep.Install, 60));
                LoadNow(entry, folder);
            }
            catch when (wasPending)
            {
                lock (_sync)
                {
                    slot.Failed = AddOnTexts.NotLoadedAfterUpdate(entry.DisplayName, release);
                }

                throw;
            }
            finally
            {
                lock (_sync)
                {
                    slot.Installing = null;
                }
            }

            MarkLoaded(slot, release);
            if (firstInstall)
            {
                await _activation.TurnOffAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            }

            // The registry's readers follow the new package.
            await RecordAsync(entry, release, CancellationToken.None).ConfigureAwait(false);
            progress.Report(new AddOnProgress(AddOnStep.Install, 100));
            LogInstalled(_logger, entry.Id, release);
            return StatusOf(entry, reach: true);
        }
        finally
        {
            ReleaseQuietly(_turn);
        }
    }

    public async Task<AddOnStatus> RemoveAsync(string id, CancellationToken ct)
    {
        var entry = EntryOf(id);
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var slot = SlotOf(entry);
            bool loaded;
            string version;
            lock (_sync)
            {
                if (slot.Bundled)
                {
                    throw new InvalidOperationException(AddOnTexts.PartOfServer(entry.DisplayName));
                }

                if (slot.Removed || (!slot.Loaded && slot.Failed is null))
                {
                    throw new InvalidOperationException(AddOnTexts.NotInstalled(entry.DisplayName));
                }

                loaded = slot.Loaded;
                version = slot.Version;
            }

            if (!loaded)
            {
                // Failed and never loaded: nothing holds the folder, it goes now.
                AddOnFolders.TryDelete(FolderOf(entry));
                await SaveAsync(entry.Id, null, CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    slot.Failed = null;
                    slot.Pending = false;
                    slot.Version = string.Empty;
                }

                LogRemovedDeleted(_logger, entry.Id);
                return new AddOnStatus(entry, AddOnState.NotInstalled, version);
            }

            // Off at once, still loaded (never an unload at run time); the folder goes at the next start.
            await _activation.TurnOffAsync(entry.Id, ct).ConfigureAwait(false);
            await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, version, Removed: true), CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                slot.Removed = true;
            }

            LogRemoved(_logger, entry.Id);
            return StatusOf(entry, reach: true);
        }
        finally
        {
            ReleaseQuietly(_turn);
        }
    }

    /// <summary>The client part for a caller without a session (tests).</summary>
    public Task<AddOnClientPart> GetClientPartAsync(string id, CancellationToken ct) => GetClientPartAsync(id, string.Empty, ct);

    public async Task<AddOnClientPart> GetClientPartAsync(string id, string caller, CancellationToken ct)
    {
        var entry = EntryOf(id);
        caller ??= string.Empty;
        string version;
        lock (_sync)
        {
            var slot = _slots[entry.Id];
            if (!slot.Loaded || slot.Removed)
            {
                throw new KeyNotFoundException(AddOnTexts.NotInstalled(entry.DisplayName));
            }

            if (slot.ClientPart is { } cached && string.Equals(cached.Version, slot.Version, StringComparison.Ordinal))
            {
                return cached;
            }

            if (caller.Length > 0 && !_partCallers.Add(caller))
            {
                throw new InvalidOperationException(AddOnTexts.ClientPartBusy);
            }

            version = slot.Version;
        }

        try
        {
            // One read at a time: the first caller reads and hashes the files, the others take its result.
            await _partRead.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_slots[entry.Id].ClientPart is { } done && string.Equals(done.Version, version, StringComparison.Ordinal))
                    {
                        return done;
                    }
                }

                var files = await AddOnFolders.ReadClientPartAsync(FolderOf(entry), ct).ConfigureAwait(false);
                var part = new AddOnClientPart(entry.Id, version, files);
                lock (_sync)
                {
                    var slot = _slots[entry.Id];
                    if (string.Equals(slot.Version, version, StringComparison.Ordinal))
                    {
                        slot.ClientPart = part;
                    }
                }

                return part;
            }
            finally
            {
                ReleaseQuietly(_partRead);
            }
        }
        finally
        {
            if (caller.Length > 0)
            {
                lock (_sync)
                {
                    _partCallers.Remove(caller);
                }
            }
        }
    }

    // ----- Steps -----

    /// <summary>
    /// Download, check, unpack and swap into <c>addons/&lt;id&gt;/</c> (not loaded). Any failure leaves no download, no new
    /// folder and the old folder as it was; <see cref="AddOnException"/> says why.
    /// </summary>
    private async Task<string> FetchAsync(AddOnEntry entry, string release, Slot slot, IProgress<AddOnProgress>? progress, CancellationToken ct)
    {
        void Report(AddOnStep step, int percent, int overall)
        {
            lock (_sync)
            {
                slot.Installing = overall;
            }

            progress?.Report(new AddOnProgress(step, percent));
        }

        var fileName = AddOnCatalog.ReleaseFileName(entry.Id, release, _options.RuntimeId);
        var zip = Path.Combine(DownloadFolder, fileName);
        var staged = Path.Combine(Root, $".new-{entry.Id}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            Report(AddOnStep.Download, 0, 0);
            await _files.DownloadAsync(release, fileName, zip, new SyncProgress<int>(p => Report(AddOnStep.Download, p, p * 90 / 100)), ct).ConfigureAwait(false);

            Report(AddOnStep.Check, 0, 90);
            await AddOnFolders.UnpackAsync(zip, staged, ct).ConfigureAwait(false);
            AddOnFolders.CheckManifest(staged, entry.Id);
            AddOnFolders.CheckVersion(staged, release);
            Report(AddOnStep.Check, 100, 95);

            Report(AddOnStep.Install, 0, 95);
            try
            {
                AddOnFolders.SwapIn(staged, FolderOf(entry), Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AddOnException(AddOnTexts.CouldNotInstall, ex);
            }

            return FolderOf(entry);
        }
        catch (AddOnException ex)
        {
            LogInstallFailed(_logger, entry.Id, ex.Message, ex.InnerException?.Message ?? string.Empty);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogInstallFailed(_logger, entry.Id, AddOnTexts.CouldNotInstall, ex.Message);
            throw new AddOnException(AddOnTexts.CouldNotInstall, ex);
        }
        finally
        {
            TryDeleteFile(zip);
            AddOnFolders.TryDelete(staged);
        }
    }

    /// <summary>Loads a swapped-in folder; a refusal deletes it and says so (<see cref="AddOnException"/>).</summary>
    private void LoadNow(AddOnEntry entry, string folder)
    {
        if (_loader.LoadPackage(folder) is not null)
        {
            return;
        }

        LogLoadFailed(_logger, entry.Id);
        AddOnFolders.TryDelete(folder);
        throw new AddOnException(AddOnTexts.CouldNotLoad(entry.DisplayName));
    }

    /// <summary>The package is in the registry now: the state says so whatever follows (storing it, the follow).</summary>
    private void MarkLoaded(Slot slot, string version)
    {
        lock (_sync)
        {
            slot.Version = version;
            slot.Loaded = true;
            slot.Removed = false;
            slot.Failed = null;
            slot.ClientPart = null;
        }
    }

    /// <summary>Stores the loaded add-on in the list and waits until the registry's readers follow it.</summary>
    private async Task RecordAsync(AddOnEntry entry, string version, CancellationToken ct)
    {
        await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, version), ct).ConfigureAwait(false);
        await _activation.PackageFollowedAsync(entry.Id, ct).ConfigureAwait(false);
    }

    private async Task SaveAsync(string id, InstalledAddOn? entry, CancellationToken ct)
    {
        var list = (await _list.ReadAsync(ct).ConfigureAwait(false)).Where(a => !Same(a.Id, id)).ToList();
        if (entry is not null)
        {
            list.Add(entry);
        }

        await _list.WriteAsync(list, ct).ConfigureAwait(false);
    }

    private async Task EnterAsync()
    {
        if (_update.UpdatingTo is not null)
        {
            throw new InvalidOperationException(AddOnTexts.Updating);
        }

        if (!await _turn.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            throw new InvalidOperationException(AddOnTexts.Busy);
        }

        if (_update.UpdatingTo is not null)
        {
            ReleaseQuietly(_turn);
            throw new InvalidOperationException(AddOnTexts.Updating);
        }
    }

    /// <summary>
    /// Whether the server reaches the release, kept for <see cref="AddOnInstallerOptions.ReachCacheTime"/>. One check at a
    /// time: callers meanwhile wait for the same answer.
    /// </summary>
    private async Task<bool> CanReachAsync(CancellationToken ct)
    {
        if (_options.ReleaseVersion is not { } release)
        {
            return false;
        }

        Task<bool> run;
        lock (_sync)
        {
            if (_reach is { } cached && _time.GetUtcNow() - cached.At < AddOnInstallerOptions.ReachCacheTime)
            {
                return cached.Value;
            }

            if (_reachRun is null || _reachRun.IsCompleted)
            {
                _reachRun = Task.Run(() => CheckReachAsync(release), CancellationToken.None);
            }

            run = _reachRun;
        }

        return await run.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> CheckReachAsync(string release)
    {
        bool reach;
        try
        {
            reach = await _files.CanReachAsync(release, _stopping.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure is "cannot reach".
        catch (Exception)
#pragma warning restore CA1031
        {
            reach = false;
        }

        lock (_sync)
        {
            _reach = (_time.GetUtcNow(), reach);
        }

        return reach;
    }

    /// <summary>The last answer of the reach check without a new one (a client's sync of its client parts); true before the first.</summary>
    private bool KnownReach()
    {
        if (_options.ReleaseVersion is null)
        {
            return false;
        }

        lock (_sync)
        {
            return _reach?.Value ?? true;
        }
    }

    private AddOnStatus StatusOf(AddOnEntry entry, bool reach)
    {
        var updating = _update.UpdatingTo is not null;
        var release = _options.ReleaseVersion is not null;
        lock (_sync)
        {
            var slot = _slots[entry.Id];
            var canInstall = reach && release && !updating && !slot.Bundled;
            if (slot.Installing is { } percent)
            {
                return new AddOnStatus(entry, AddOnState.Installing, slot.Version) { Percent = percent, CanInstall = false };
            }

            if (slot.Bundled)
            {
                return new AddOnStatus(entry, AddOnState.Installed, _options.ServerVersion) { CanInstall = false };
            }

            if (slot.Loaded && slot.Removed)
            {
                // Install keeps the loaded add-on: nothing to download.
                return new AddOnStatus(entry, AddOnState.Removed, slot.Version) { Message = AddOnTexts.Removed, CanInstall = !updating };
            }

            if (slot.Loaded)
            {
                return new AddOnStatus(entry, AddOnState.Installed, slot.Version) { CanInstall = canInstall };
            }

            if (slot.Failed is { } failed)
            {
                return new AddOnStatus(entry, AddOnState.Failed, slot.Version) { Message = failed, CanInstall = canInstall };
            }

            var message = !release ? AddOnTexts.NoRelease : reach ? null : AddOnTexts.CannotReach;
            return new AddOnStatus(entry, AddOnState.NotInstalled) { Message = message, CanInstall = canInstall };
        }
    }

    /// <summary>A plugin package with this id came from another folder (bundled, development): it wins.</summary>
    private bool IsBundled(AddOnEntry entry)
    {
        var own = Path.GetFullPath(FolderOf(entry));
        return _loader.Packages.Any(p => Same(p.Manifest.Id, entry.Id)
            && !string.Equals(Path.GetFullPath(p.Directory), own, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static AddOnEntry EntryOf(string id) =>
        AddOnCatalog.Find(id ?? string.Empty) ?? throw new KeyNotFoundException(AddOnTexts.Unknown(id ?? string.Empty));

    private Slot SlotOf(AddOnEntry entry)
    {
        lock (_sync)
        {
            return _slots[entry.Id];
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Emptied at the next start (.download).
        }
    }

    /// <summary>Releases a gate that a stopping server may have disposed already.</summary>
    private static void ReleaseQuietly(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The server stops.
        }
    }

    public void Dispose()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return; // disposed before
        }

        _stopping.Dispose();
        _turn.Dispose();
        _partRead.Dispose();
    }

    /// <summary>The state of one add-on in this process; read and written under <see cref="_sync"/>.</summary>
    private sealed class Slot
    {
        public string Version { get; set; } = string.Empty;

        public bool Loaded { get; set; }

        public bool Removed { get; set; }

        public bool Bundled { get; set; }

        public bool Pending { get; set; }

        public int? Installing { get; set; }

        public string? Failed { get; set; }

        /// <summary>The client part of <see cref="Version"/>, read and hashed once.</summary>
        public AddOnClientPart? ClientPart { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} {Version} installed")]
    private static partial void LogInstalled(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} removed: off now, its folder goes at the next start")]
    private static partial void LogRemoved(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} was installed again before the restart: kept")]
    private static partial void LogKeptAgain(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder of the removed optional plugin {Id} deleted")]
    private static partial void LogRemovedDeleted(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Folder of the removed optional plugin {Id} could not be deleted; tried again at the next start")]
    private static partial void LogRemovedKept(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder {Name} of the optional plugins is no optional plugin of this version: deleted")]
    private static partial void LogUnknownDeleted(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Folder {Name} of the optional plugins is no optional plugin of this version and could not be deleted; tried again at the next start")]
    private static partial void LogUnknownKept(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} found in the folder of the optional plugins: installed ({Version})")]
    private static partial void LogTakenUp(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} found in the folder of the optional plugins is not loaded: the data folder's permissions were reset at this start. Install it on the Plugins page")]
    private static partial void LogNotTakenUp(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} is not loaded: a plugin that came with the server has the same id")]
    private static partial void LogBundledWins(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} {Version} loaded")]
    private static partial void LogLoaded(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} could not be loaded (see the plugin errors)")]
    private static partial void LogLoadFailed(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} {Installed} waits for the download of {Release}")]
    private static partial void LogFetchPending(ILogger logger, string id, string installed, string release);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} {Release} downloaded and loaded after the update")]
    private static partial void LogFetched(ILogger logger, string id, string release);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} {Release} could not be downloaded or followed after the update")]
    private static partial void LogFetchFailed(ILogger logger, Exception ex, string id, string release);

    [LoggerMessage(Level = LogLevel.Information, Message = "The download of the optional plugins stopped with the server; the next start tries again")]
    private static partial void LogFetchStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} not installed: {Reason} {Detail}")]
    private static partial void LogInstallFailed(ILogger logger, string id, string reason, string detail);
}
