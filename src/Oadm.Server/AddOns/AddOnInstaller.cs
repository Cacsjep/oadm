using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using Oadm.Contracts;
using Oadm.Contracts.Updates;
using Oadm.Core.Plugins;
using Oadm.Server.Updates;

namespace Oadm.Server.AddOns;

/// <summary>Where and for which version the installer works.</summary>
/// <param name="DataDirectory">The server's data folder; add-ons live in <c>addons/</c> below it.</param>
/// <param name="ServerVersion">The server's exact version ("4.11.0", "4.11.0-rc.1", "0.1.0-dev").</param>
/// <param name="RuntimeId">The server's platform ("win-x64", ...; <see cref="CurrentRuntimeId"/>).</param>
public sealed record AddOnInstallerOptions(string DataDirectory, string ServerVersion, string RuntimeId)
{
    /// <summary>The largest ZIP a client may send.</summary>
    public long MaxZipBytes { get; init; } = AddOnFolders.MaxZipBytes;

    /// <summary>The most bytes one ZIP may unpack to.</summary>
    public long MaxUnpackedBytes { get; init; } = AddOnFolders.MaxUnpackedBytes;

    /// <summary>The most files one ZIP may hold.</summary>
    public int MaxEntries { get; init; } = AddOnFolders.MaxEntries;

    /// <summary>An upload that sends nothing for this long ends, so a client that stands still never holds up the others.</summary>
    public TimeSpan UploadIdleTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Deletes a folder; false when it cannot go now (tests: a folder that stays on every system).</summary>
    public Func<string, bool> DeleteFolder { get; init; } = AddOnFolders.TryDelete;

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
/// Installs and removes the add-ons of this OADM version (docs/spec/add-ons.md "Server"): takes the ZIP a client sends into
/// <c>addons/.upload/</c> (<see cref="InstallFileAsync"/>; never in memory), refuses every file but the genuine one
/// (<see cref="IAddOnHashes"/>) before anything is unpacked, unpacks it into a new folder, checks its plugin.json and version,
/// swaps it into <c>addons/&lt;id&gt;/</c>, loads it (<see cref="PluginLoader.LoadPackage"/>, off; the load raises the registry's
/// <see cref="PluginRegistry.PackageAdded"/>) and reports Installed once the registry's readers follow it
/// (<see cref="IAddOnActivation.PackageFollowedAsync"/>). Remove turns the add-on off at once and deletes its folder at the
/// next start. One install or remove at a time, none while a self update runs; any failure leaves the state as it was.
/// <para>
/// Server start (wiring in <c>OadmServerHost</c>): <see cref="LoadInstalledAsync(bool, CancellationToken)"/> right after the
/// bundled plugins are loaded. An add-on whose folder does not name this exact version, or has no folder (after an update or a
/// restore), stays unloaded and its row asks to install it again; OADM runs as usual.
/// </para>
/// </summary>
public sealed partial class AddOnInstaller : IAddOnInstaller, IDisposable
{
    private readonly AddOnInstallerOptions _options;
    private readonly IAddOnHashes _hashes;
    private readonly IAddOnListStore _list;
    private readonly IAddOnActivation _activation;
    private readonly PluginLoader _loader;
    private readonly UpdateGate _update;
    private readonly IDiskSpace _disk;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly SemaphoreSlim _partRead = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _partCallers = new(StringComparer.Ordinal);

    public AddOnInstaller(
        AddOnInstallerOptions options,
        IAddOnHashes hashes,
        IAddOnListStore list,
        IAddOnActivation activation,
        PluginLoader loader,
        UpdateGate update,
        ILogger<AddOnInstaller> logger,
        TimeProvider? time = null,
        IDiskSpace? disk = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _hashes = hashes;
        _list = list;
        _activation = activation;
        _loader = loader;
        _update = update;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _disk = disk ?? new SystemDiskSpace();
        Root = AddOnFolders.Root(options.DataDirectory);
        foreach (var entry in AddOnCatalog.Entries)
        {
            _slots[entry.Id] = new Slot();
        }
    }

    /// <summary><c>&lt;datafolder&gt;/addons</c>.</summary>
    public string Root { get; }

    private string UploadFolder => Path.Combine(Root, AddOnFolders.UploadFolderName);

    private string FolderOf(AddOnEntry entry) => Path.Combine(Root, entry.Id);

    // ----- Server start -----

    /// <summary>The start of a server whose data folder was not fixed at this start (tests, a console run).</summary>
    public Task LoadInstalledAsync(CancellationToken ct) => LoadInstalledAsync(foldersTrusted: true, ct);

    /// <summary>
    /// Server start, right after the bundled plugins are loaded: deletes the folders of removed add-ons, of ids this version
    /// does not offer and the leftovers of an install that stopped, takes up folders put in by hand, and loads every installed
    /// add-on whose folder names this exact version (no event: the start reads the registry afterwards). An id a bundled
    /// plugin has is not loaded (log line). Any other add-on stays unloaded with <see cref="AddOnTexts.InstallAgain"/>.
    /// Without <paramref name="foldersTrusted"/> (the service had to fix the data folder's permissions at this start, a folder
    /// there could come from anyone) no add-on folder is used: each is deleted, a listed add-on is installed again.
    /// </summary>
    public async Task LoadInstalledAsync(bool foldersTrusted, CancellationToken ct)
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

        // Put in by hand from the release ZIP: on the list with the version its file names, loaded only when that is this one.
        foreach (var entry in AddOnCatalog.Entries)
        {
            if (list.Exists(a => Same(a.Id, entry.Id)) || !File.Exists(Path.Combine(FolderOf(entry), PluginLoader.ManifestFileName)))
            {
                continue;
            }

            if (!foldersTrusted)
            {
                LogNotTakenUp(_logger, entry.Id);
                if (!_options.DeleteFolder(FolderOf(entry)))
                {
                    list.Add(new InstalledAddOn(entry.Id, string.Empty, Removed: true)); // gone at a later start, never taken up
                    changed = true;
                }

                continue;
            }

            var version = AddOnFolders.ReadVersion(FolderOf(entry)) ?? string.Empty;
            list.Add(new InstalledAddOn(entry.Id, version));
            changed = true;
            LogTakenUp(_logger, entry.Id, version);
        }

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

            if (!foldersTrusted && Directory.Exists(FolderOf(entry)))
            {
                LogNotTrusted(_logger, entry.Id);
                if (!_options.DeleteFolder(FolderOf(entry)))
                {
                    list[list.IndexOf(installed)] = installed with { Removed = true };
                    changed = true;
                    continue;
                }
            }

            // The exact version, read from the folder itself: the folder of "4.11.0-rc.1" is not the add-on of 4.11.0.
            var folderVersion = File.Exists(Path.Combine(FolderOf(entry), PluginLoader.ManifestFileName)) ? AddOnFolders.ReadVersion(FolderOf(entry)) : null;
            if (!string.Equals(folderVersion, _options.ServerVersion, StringComparison.Ordinal))
            {
                lock (_sync)
                {
                    slot.Version = installed.Version;
                    slot.Failed = AddOnTexts.InstallAgain;
                }

                LogOtherVersion(_logger, entry.Id, folderVersion ?? "none", _options.ServerVersion);
                continue;
            }

            if (!string.Equals(installed.Version, folderVersion, StringComparison.Ordinal))
            {
                list[list.IndexOf(installed)] = installed with { Version = _options.ServerVersion };
                changed = true;
            }

            LoadAtStart(entry, slot, _options.ServerVersion);
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

    /// <summary>Raised after an install or a remove: <see cref="NotLoaded"/> may have changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The names of the add-ons that are installed but not loaded now (<see cref="AddOnState.Failed"/>).</summary>
    public IReadOnlyList<string> NotLoaded
    {
        get
        {
            lock (_sync)
            {
                return [.. AddOnCatalog.Entries.Where(e => _slots[e.Id] is { Loaded: false, Failed: not null }).Select(e => e.DisplayName)];
            }
        }
    }

    // ----- IAddOnInstaller -----

    public Task<IReadOnlyList<AddOnStatus>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AddOnStatus>>([.. AddOnCatalog.Entries.Select(StatusOf)]);

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
            string loadedVersion;
            lock (_sync)
            {
                RefuseWhenInPlace(entry, slot);
                if (!slot.Loaded)
                {
                    throw new InvalidOperationException(AddOnTexts.OlderClient);
                }

                loadedVersion = slot.Version;
            }

            // Removed but still loaded (off): taking it back keeps it, no file is needed.
            await SaveAsync(entry.Id, new InstalledAddOn(entry.Id, loadedVersion), CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                slot.Removed = false;
            }

            progress.Report(new AddOnProgress(AddOnStep.Install, 100));
            LogKeptAgain(_logger, entry.Id);
            return StatusOf(entry);
        }
        finally
        {
            Leave();
        }
    }

    public async Task<AddOnStatus> InstallFileAsync(AddOnUpload upload, IProgress<AddOnProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(progress);
        var entry = EntryOf(upload.Id);
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var slot = SlotOf(entry);
            lock (_sync)
            {
                RefuseWhenInPlace(entry, slot);
                if (slot.Loaded)
                {
                    throw new InvalidOperationException(AddOnTexts.Removed); // loaded: Install takes it back, without a file
                }
            }

            var expected = _hashes.Find(entry.Id) ?? throw new InvalidOperationException(AddOnTexts.NoHash);
            var version = _options.ServerVersion;

            // A first install starts off; Install again after an update keeps the administrator's choice.
            var firstInstall = !_activation.HasChoice(entry.Id);
            try
            {
                var old = await StageAsync(entry, upload, expected, slot, progress, ct).ConfigureAwait(false);
                progress.Report(new AddOnProgress(AddOnStep.Install, 60));
                LoadNow(entry, old);
            }
            finally
            {
                lock (_sync)
                {
                    slot.Installing = null;
                }
            }

            MarkLoaded(slot, version);
            if (firstInstall)
            {
                await _activation.TurnOffAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            }

            // The registry's readers follow the new package.
            await RecordAsync(entry, version, CancellationToken.None).ConfigureAwait(false);
            progress.Report(new AddOnProgress(AddOnStep.Install, 100));
            LogInstalled(_logger, entry.Id, version);
            return StatusOf(entry);
        }
        finally
        {
            Leave();
        }
    }

    private static void RefuseWhenInPlace(AddOnEntry entry, Slot slot)
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
                // Off, so a later install starts off (rule 5). Failed and never loaded: nothing holds the folder, it goes now. One
                // that stays is marked, so no start takes it up again.
                await _activation.TurnOffAsync(entry.Id, ct).ConfigureAwait(false);
                var deleted = _options.DeleteFolder(FolderOf(entry));
                await SaveAsync(entry.Id, deleted ? null : new InstalledAddOn(entry.Id, version, Removed: true), CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    slot.Failed = null;
                    slot.Version = string.Empty;
                }

                if (deleted)
                {
                    LogRemovedDeleted(_logger, entry.Id);
                }
                else
                {
                    LogRemovedKept(_logger, entry.Id);
                }

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
            return StatusOf(entry);
        }
        finally
        {
            Leave();
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
    /// Receive, check, unpack and swap into <c>addons/&lt;id&gt;/</c> (not loaded). Any failure leaves no file, no new folder and
    /// the old folder as it was; <see cref="AddOnException"/> says why. Nothing is unpacked before the hash holds.
    /// </summary>
    /// <returns>Where the old folder waits until the new one loaded; null without one.</returns>
    private async Task<string?> StageAsync(AddOnEntry entry, AddOnUpload upload, string expected, Slot slot, IProgress<AddOnProgress> progress, CancellationToken ct)
    {
        void Report(AddOnStep step, int percent, int overall)
        {
            lock (_sync)
            {
                slot.Installing = overall;
            }

            progress.Report(new AddOnProgress(step, percent));
        }

        var zip = Path.Combine(UploadFolder, $"{entry.Id}-{Guid.NewGuid():N}.zip");
        var staged = Path.Combine(Root, $".new-{entry.Id}-{Guid.NewGuid():N}");
        try
        {
            CheckFileName(entry, upload.FileName);
            Directory.CreateDirectory(UploadFolder);
            Report(AddOnStep.Upload, 0, 0);
            await ReceiveAsync(upload, zip, expected, percent => Report(AddOnStep.Upload, percent, percent * 90 / 100), ct).ConfigureAwait(false);

            Report(AddOnStep.Check, 0, 90);
            // The upload is in and its hash holds: from here the install runs to its end, whoever has gone, and is audited.
            await AddOnFolders.UnpackAsync(zip, staged, _options.MaxUnpackedBytes, _options.MaxEntries, CancellationToken.None).ConfigureAwait(false);
            AddOnFolders.CheckManifest(staged, entry);
            AddOnFolders.CheckVersion(staged, _options.ServerVersion);
            Report(AddOnStep.Check, 100, 95);

            Report(AddOnStep.Install, 0, 95);
            try
            {
                return AddOnFolders.SwapIn(staged, FolderOf(entry), Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AddOnException(AddOnTexts.CouldNotInstall, ex);
            }
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

    /// <summary>
    /// A file whose name says another plugin, version or system is refused with that reason before it is sent. A renamed file
    /// is left to the hash, which decides either way.
    /// </summary>
    private void CheckFileName(AddOnEntry entry, string fileName)
    {
        if (AddOnCatalog.ParseReleaseFileName(fileName) is not { } name)
        {
            return;
        }

        if (!Same(name.Id, entry.Id))
        {
            throw new AddOnException(AddOnTexts.WrongPlugin(entry.DisplayName));
        }

        if (!string.Equals(name.Version, _options.ServerVersion, StringComparison.Ordinal))
        {
            throw new AddOnException(AddOnTexts.WrongVersion(_options.ServerVersion));
        }

        if (!string.Equals(name.RuntimeId, _options.RuntimeId, StringComparison.Ordinal))
        {
            throw new AddOnException(AddOnTexts.WrongSystem(_options.RuntimeId));
        }
    }

    /// <summary>
    /// Writes the chunks to <paramref name="zip"/> and hashes them on the way, so the ZIP is never held in memory. A size over
    /// the limit, more bytes than announced or another hash: <see cref="AddOnTexts.WrongFile"/>; fewer bytes or a client that
    /// stands still for <see cref="AddOnInstallerOptions.UploadIdleTimeout"/>: <see cref="AddOnTexts.UploadStopped"/>.
    /// </summary>
    private async Task ReceiveAsync(AddOnUpload upload, string zip, string expected, Action<int> report, CancellationToken ct)
    {
        if (upload.Size <= 0 || upload.Size > _options.MaxZipBytes)
        {
            throw new AddOnException(AddOnTexts.WrongFile);
        }

        // The file, its unpacked folder and the old folder until the swap: three times its size, like self update.
        if (_disk.AvailableBytes(Root) is { } free && free / 3 < upload.Size)
        {
            throw new AddOnException(AddOnTexts.NotEnoughSpace);
        }

        using var idle = new CancellationTokenSource(_options.UploadIdleTimeout, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        var percent = -1;
        try
        {
            var file = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await foreach (var chunk in upload.Data.WithCancellation(either.Token).ConfigureAwait(false))
                {
                    idle.CancelAfter(_options.UploadIdleTimeout);
                    received += chunk.Length;
                    if (received > upload.Size)
                    {
                        throw new AddOnException(AddOnTexts.WrongFile);
                    }

                    hash.AppendData(chunk.Span);
                    await file.WriteAsync(chunk, either.Token).ConfigureAwait(false);
                    var now = (int)(received * 100 / upload.Size);
                    if (now != percent)
                    {
                        percent = now;
                        report(now);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AddOnException(AddOnTexts.UploadStopped);
        }

        if (received != upload.Size)
        {
            throw new AddOnException(AddOnTexts.UploadStopped);
        }

        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new AddOnException(AddOnTexts.WrongFile);
        }
    }

    /// <summary>Loads the swapped-in folder; a refusal brings the old folder back and says so (<see cref="AddOnException"/>).</summary>
    private void LoadNow(AddOnEntry entry, string? old)
    {
        var folder = FolderOf(entry);
        if (_loader.LoadPackage(folder) is not null)
        {
            AddOnFolders.DropOld(old);
            return;
        }

        LogLoadFailed(_logger, entry.Id);
        try
        {
            AddOnFolders.Restore(old, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogRestoreFailed(_logger, ex, entry.Id);
        }

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

        if (!_update.TryBeginAddOnWork())
        {
            ReleaseQuietly(_turn);
            throw new InvalidOperationException(AddOnTexts.Updating);
        }
    }

    private void Leave()
    {
        _update.EndAddOnWork();
        ReleaseQuietly(_turn);
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Telling the event rules never changes the result of an install.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStateNotReported(_logger, ex);
        }
    }

    private AddOnStatus StatusOf(AddOnEntry entry)
    {
        var updating = _update.UpdatingTo is not null;
        var hash = _hashes.Find(entry.Id) is not null;
        var url = hash ? AddOnCatalog.DownloadUrl(entry.Id, _options.ServerVersion, _options.RuntimeId) : null;
        lock (_sync)
        {
            var slot = _slots[entry.Id];
            var canInstall = hash && !updating && !slot.Bundled;
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
                // Install keeps the loaded add-on: no file.
                return new AddOnStatus(entry, AddOnState.Removed, slot.Version) { Message = AddOnTexts.Removed, CanInstall = !updating };
            }

            if (slot.Loaded)
            {
                return new AddOnStatus(entry, AddOnState.Installed, slot.Version) { CanInstall = canInstall };
            }

            if (slot.Failed is { } failed)
            {
                return new AddOnStatus(entry, AddOnState.Failed, slot.Version) { Message = failed, CanInstall = canInstall, DownloadUrl = url };
            }

            return new AddOnStatus(entry, AddOnState.NotInstalled) { Message = hash ? null : AddOnTexts.NoHash, CanInstall = canInstall, DownloadUrl = url };
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
            // Emptied at the next start (.upload).
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

        public int? Installing { get; set; }

        /// <summary>Why it is not loaded; stays until an install succeeds or it is removed.</summary>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} is not loaded and its folder goes: the data folder's permissions were fixed at this start. Install it again")]
    private static partial void LogNotTrusted(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} is not loaded: a plugin that came with the server has the same id")]
    private static partial void LogBundledWins(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The event rules could not be told of the optional plugins' state")]
    private static partial void LogStateNotReported(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} {Version} loaded")]
    private static partial void LogLoaded(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} could not be loaded (see the plugin errors)")]
    private static partial void LogLoadFailed(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} is not loaded: its folder is of version {Folder}, this server is {Server}. Install it again on the Plugins page")]
    private static partial void LogOtherVersion(ILogger logger, string id, string folder, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The previous folder of optional plugin {Id} could not be put back")]
    private static partial void LogRestoreFailed(ILogger logger, Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin {Id} not installed: {Reason} {Detail}")]
    private static partial void LogInstallFailed(ILogger logger, string id, string reason, string detail);
}
