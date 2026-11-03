using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using Oadm.Contracts.Updates;
using Oadm.Core.Plugins;
using Oadm.Core.Plugins.AddOns;
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
/// Server start (wiring in <c>OadmServerHost</c>): <see cref="LoadInstalledAsync"/> right after the bundled plugins are loaded,
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
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset At, bool Value)? _reach;

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

    /// <summary>
    /// Server start, right after the bundled plugins are loaded: deletes the folders of removed add-ons and the leftovers of
    /// an install that stopped, takes up folders put in by hand, and loads every installed add-on of this version (no event:
    /// the start reads the registry afterwards). An id a bundled plugin has is not loaded (log line). Add-ons of another
    /// version or without their folder wait for <see cref="FetchAfterUpdateAsync"/>.
    /// </summary>
    public async Task LoadInstalledAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        AddOnFolders.CleanTemporary(Root);
        var list = (await _list.ReadAsync(ct).ConfigureAwait(false)).ToList();
        var changed = false;

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

        // Put in by hand from the release ZIP: installed, of this version.
        foreach (var entry in AddOnCatalog.Entries)
        {
            if (!list.Exists(a => Same(a.Id, entry.Id)) && File.Exists(Path.Combine(FolderOf(entry), PluginLoader.ManifestFileName)))
            {
                list.Add(new InstalledAddOn(entry.Id, _options.ReleaseVersion ?? _options.ServerVersion));
                changed = true;
                LogTakenUp(_logger, entry.Id);
            }
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

            if (installed is null)
            {
                continue;
            }

            if (installed.Removed)
            {
                // Its folder could not be deleted: not loaded, gone at a later start.
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

            if (!folderThere || UpdateVersions.Parse(installed.Version) != UpdateVersions.Parse(release))
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
    /// Never throws but for <paramref name="ct"/>.
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
                    await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, release), CancellationToken.None).ConfigureAwait(false);
                    lock (_sync)
                    {
                        slot.Version = release;
                        slot.Loaded = true;
                        slot.Failed = null;
                    }

                    // The load raised PackageAdded: its stored choice applies (its core plugins start when it is on).
                    await _activation.PackageFollowedAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
                    LogFetched(_logger, entry.Id, release);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // A failed add-on never stops the server.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    lock (_sync)
                    {
                        slot.Failed = AddOnTexts.NotLoadedAfterUpdate(entry.DisplayName, release);
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
            _turn.Release();
        }
    }

    // ----- IAddOnInstaller -----

    public async Task<IReadOnlyList<AddOnStatus>> ListAsync(CancellationToken ct)
    {
        var reach = await CanReachAsync(ct).ConfigureAwait(false);
        return [.. AddOnCatalog.Entries.Select(e => StatusOf(e, reach))];
    }

    public async Task<AddOnStatus> InstallAsync(string id, IProgress<AddOnProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var entry = EntryOf(id);
        var release = _options.ReleaseVersion ?? throw new InvalidOperationException(AddOnTexts.NoRelease);
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var slot = SlotOf(entry);
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
            }

            if (IsBundled(entry) && !slot.Loaded)
            {
                throw new InvalidOperationException(AddOnTexts.PartOfServer(entry.DisplayName));
            }

            if (slot.Loaded)
            {
                // Removed but still loaded (off): installing it again keeps it, nothing is downloaded.
                await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, slot.Version), CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    slot.Removed = false;
                }

                progress.Report(new AddOnProgress(AddOnStep.Install, 100));
                LogKeptAgain(_logger, entry.Id);
                return StatusOf(entry, reach: true);
            }

            bool wasPending;
            lock (_sync)
            {
                wasPending = slot.Pending;
                slot.Pending = false; // waiting for the fetch after an update: this install does it
            }

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

            lock (_sync)
            {
                slot.Version = release;
                slot.Loaded = true;
                slot.Removed = false;
                slot.Failed = null;
            }

            // Installed off; then the registry's readers follow the new package.
            await _activation.TurnOffAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, release), CancellationToken.None).ConfigureAwait(false);
            await _activation.PackageFollowedAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            progress.Report(new AddOnProgress(AddOnStep.Install, 100));
            LogInstalled(_logger, entry.Id, release);
            return StatusOf(entry, reach: true);
        }
        finally
        {
            _turn.Release();
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
            _turn.Release();
        }
    }

    public async Task<AddOnClientPart> GetClientPartAsync(string id, CancellationToken ct)
    {
        var entry = EntryOf(id);
        string version;
        lock (_sync)
        {
            var slot = _slots[entry.Id];
            if (!slot.Loaded || slot.Removed)
            {
                throw new KeyNotFoundException(AddOnTexts.NotInstalled(entry.DisplayName));
            }

            version = slot.Version;
        }

        var files = await AddOnFolders.ReadClientPartAsync(FolderOf(entry), ct).ConfigureAwait(false);
        return new AddOnClientPart(entry.Id, version, files);
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
            await _files.DownloadAsync(release, fileName, zip, new InlineProgress(p => Report(AddOnStep.Download, p, p * 90 / 100)), ct).ConfigureAwait(false);

            Report(AddOnStep.Check, 0, 90);
            await AddOnFolders.UnpackAsync(zip, staged, ct).ConfigureAwait(false);
            AddOnFolders.CheckManifest(staged, entry.Id);
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
            _turn.Release();
            throw new InvalidOperationException(AddOnTexts.Updating);
        }
    }

    private async Task<bool> CanReachAsync(CancellationToken ct)
    {
        if (_options.ReleaseVersion is not { } release)
        {
            return false;
        }

        lock (_sync)
        {
            if (_reach is { } cached && _time.GetUtcNow() - cached.At < AddOnInstallerOptions.ReachCacheTime)
            {
                return cached.Value;
            }
        }

        bool reach;
        try
        {
            reach = await _files.CanReachAsync(release, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            reach = false;
        }

        lock (_sync)
        {
            _reach = (_time.GetUtcNow(), reach);
        }

        return reach;
    }

    private AddOnStatus StatusOf(AddOnEntry entry, bool reach)
    {
        var updating = _update.UpdatingTo is not null;
        lock (_sync)
        {
            var slot = _slots[entry.Id];
            var canInstall = reach && !updating && !slot.Bundled;
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
                return new AddOnStatus(entry, AddOnState.Removed, slot.Version) { Message = AddOnTexts.Removed, CanInstall = canInstall };
            }

            if (slot.Loaded)
            {
                return new AddOnStatus(entry, AddOnState.Installed, slot.Version) { CanInstall = canInstall };
            }

            if (slot.Failed is { } failed)
            {
                return new AddOnStatus(entry, AddOnState.Failed, slot.Version) { Message = failed, CanInstall = canInstall };
            }

            var message = _options.ReleaseVersion is null ? AddOnTexts.NoRelease : reach ? null : AddOnTexts.CannotReach;
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

    public void Dispose() => _turn.Dispose();

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
    }

    /// <summary>Reports on the caller's thread.</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} {Version} installed")]
    private static partial void LogInstalled(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} removed: off now, its folder goes at the next start")]
    private static partial void LogRemoved(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} was installed again before the restart: kept")]
    private static partial void LogKeptAgain(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder of the removed add-on {Id} deleted")]
    private static partial void LogRemovedDeleted(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Folder of the removed add-on {Id} could not be deleted; tried again at the next start")]
    private static partial void LogRemovedKept(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} found in the add-on folder: installed")]
    private static partial void LogTakenUp(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} is not loaded: a plugin that came with the server has the same id")]
    private static partial void LogBundledWins(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} {Version} loaded")]
    private static partial void LogLoaded(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Add-on {Id} could not be loaded (see the plugin errors)")]
    private static partial void LogLoadFailed(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} {Installed} waits for the download of {Release}")]
    private static partial void LogFetchPending(ILogger logger, string id, string installed, string release);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add-on {Id} {Release} downloaded and loaded after the update")]
    private static partial void LogFetched(ILogger logger, string id, string release);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Add-on {Id} {Release} could not be downloaded after the update; not loaded")]
    private static partial void LogFetchFailed(ILogger logger, Exception ex, string id, string release);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Add-on {Id} not installed: {Reason} {Detail}")]
    private static partial void LogInstallFailed(ILogger logger, string id, string reason, string detail);
}
