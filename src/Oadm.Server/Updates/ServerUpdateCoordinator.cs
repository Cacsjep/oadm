using System.Threading.Channels;

using Oadm.Contracts.Updates;
using Oadm.Core.Auth;
using Oadm.Core.Events;
using Oadm.Core.Tasks;

namespace Oadm.Server.Updates;

/// <summary>What the coordinator knows about this server (tests pass their own).</summary>
public sealed record ServerUpdateEnvironment
{
    /// <summary>"4.8.0" (the informational version without the source revision).</summary>
    public required string ServerVersion { get; init; }

    public required UpdatePlatform Platform { get; init; }

    /// <summary>Installed with the OADM installer (<see cref="Installation.IsServerInstalled"/>).</summary>
    public required bool Installed { get; init; }

    /// <summary>The SQLite connection string of the database (the copy of rule 11).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The data folder (the helper's <c>--data</c>).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Copies the running program into the update folder and returns the copy (rule 3).</summary>
    public required Func<string, string> MakeHelper { get; init; }

    /// <summary>Where the helper asks this server for its version (<c>AuthService.Status</c> on this computer).</summary>
    public required Func<Uri> StatusUrl { get; init; }

    /// <summary>How many sites have a node older than this server.</summary>
    public Func<int> OlderSiteNodes { get; init; } = () => 0;
}

/// <summary>
/// The server update (docs/spec/self-update.md): Start checks the rules, downloads and checks the installer, copies the
/// database, writes the record and starts the helper outside the service; from the start no task and no event rule action
/// starts. The steps reach every client through <see cref="WatchAsync"/>. The record is completed by the server that starts
/// afterwards (<see cref="CompleteOnStartAsync"/>), or by this one when the installer failed without stopping it.
/// </summary>
public sealed partial class ServerUpdateCoordinator(
    UpdateRecordFile records,
    IReleaseFeed feed,
    UpdateDownloader downloader,
    TaskEngine tasks,
    ActionRunner actions,
    AuditLog audit,
    IServerHelperStarter helpers,
    ServerUpdateEnvironment environment,
    UpdateGate gate,
    TimeProvider time,
    ILogger<ServerUpdateCoordinator> logger) : IDisposable
{
    /// <summary>The latest release is read again after this long.</summary>
    public static readonly TimeSpan LatestCacheTime = TimeSpan.FromMinutes(10);

    /// <summary>How often a running record is read while the helper works.</summary>
    public static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A record still running this long after its start failed (the helper never wrote a result): the helper's worst case
    /// (clients, installer limit, new version wait) and a margin.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = ServerUpdateHelper.WorstCase + TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Channel<Contracts.V1.UpdateProgress>> _watchers = [];
    private Contracts.V1.UpdateProgress? _current;
    private (ReleaseOffer? Release, DateTimeOffset At)? _latest;
    private int _following;

    public ServerUpdateEnvironment Environment => environment;

    /// <summary>The running update's last step; null while none runs.</summary>
    public Contracts.V1.UpdateProgress? Current
    {
        get
        {
            lock (_gate)
            {
                return _current?.Clone();
            }
        }
    }

    /// <summary>While set, the server takes no calls but Status and UpdateService (the helper started): the version it updates to.</summary>
    public string? ClosedFor => gate.ClosedFor;

    /// <summary>The open Watch streams (tests).</summary>
    internal int WatcherCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>The record of the last update; null without one.</summary>
    public Task<UpdateRecord?> LastAsync(CancellationToken ct) => records.ReadAsync(ct);

    public string Folder => records.Folder;

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _start.Dispose();
    }

    /// <summary>The latest stable release (cached <see cref="LatestCacheTime"/>); null when GitHub cannot be reached.</summary>
    public async Task<ReleaseOffer?> LatestAsync(CancellationToken ct, bool fresh = false)
    {
        lock (_gate)
        {
            if (!fresh && _latest is { } cached && time.GetUtcNow() - cached.At < LatestCacheTime)
            {
                return cached.Release;
            }
        }

        var release = await feed.LatestAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _latest = (release, time.GetUtcNow());
        }

        return release;
    }

    /// <summary>The steps of the running update (the current one first), then each change, until <paramref name="ct"/>.</summary>
    public async IAsyncEnumerable<Contracts.V1.UpdateProgress> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<Contracts.V1.UpdateProgress>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            if (_current is { } now)
            {
                channel.Writer.TryWrite(now.Clone());
            }

            _watchers.Add(channel);
        }

        try
        {
            await foreach (var progress in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return progress;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove(channel);
            }
        }
    }

    /// <summary>
    /// The reason Install now is refused now (rules 1 to 6 before anything is downloaded), or null. Used by Start and by
    /// the tests of each refusal.
    /// </summary>
    public async Task<string?> RefusalAsync(string version, CancellationToken ct)
    {
        if (Current is not null || await records.ReadAsync(ct).ConfigureAwait(false) is { IsRunning: true })
        {
            return UpdateTexts.Running;
        }

        if (UpdateVersions.IsDevelopmentBuild(environment.ServerVersion))
        {
            return UpdateTexts.DevelopmentBuild;
        }

        if (!environment.Installed)
        {
            return UpdateTexts.NotInstalled;
        }

        if (UpdateVersions.ParseStable(version) is not { } target)
        {
            return UpdateTexts.PreRelease;
        }

        var current = UpdateVersions.Parse(environment.ServerVersion)!;
        if (target == current)
        {
            return UpdateTexts.SameVersion;
        }

        if (target < current)
        {
            return UpdateTexts.Downgrade;
        }

        if (tasks.ActiveCount is var active and > 0)
        {
            return UpdateTexts.TasksRunning(active) + ".";
        }

        return environment.Platform.InstallerName(UpdateApp.Server, target) is null ? UpdateTexts.NoInstaller : null;
    }

    /// <summary>
    /// Install now: checks, downloads, checks the download, copies the database, writes the record and starts the helper.
    /// Throws <see cref="UpdateRefusedException"/> with the reason when it stops before the installer (nothing changed).
    /// </summary>
    public async Task StartAsync(string version, string requestedBy, string clientAddress, CancellationToken ct)
    {
        if (!await _start.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new UpdateRefusedException(UpdateTexts.Running);
        }

        try
        {
            if (await RefusalAsync(version, ct).ConfigureAwait(false) is { } refusal)
            {
                LogRefused(version, refusal);
                throw new UpdateRefusedException(refusal);
            }

            await RunAsync(UpdateVersions.ParseStable(version)!, requestedBy, clientAddress, ct).ConfigureAwait(false);
        }
        finally
        {
            _start.Release();
        }
    }

    private async Task RunAsync(Version target, string requestedBy, string clientAddress, CancellationToken ct)
    {
        var to = UpdateVersions.Plain(target);
        var from = environment.ServerVersion;
        var name = environment.Platform.InstallerName(UpdateApp.Server, target)!;
        Hold(to);
        Publish(new Contracts.V1.UpdateProgress { ToVersion = to, Step = Contracts.V1.UpdateStepKind.UpdateStepDownload });
        try
        {
            // A task that started between the check and the hold is counted now (rule 6).
            await tasks.WaitForStartsAsync(time, ct).ConfigureAwait(false);
            if (tasks.ActiveCount is var active and > 0)
            {
                throw new UpdateRefusedException(UpdateTexts.TasksRunning(active) + ".");
            }

            var latest = await LatestAsync(ct, fresh: true).ConfigureAwait(false) ?? throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
            if (latest.Version != target)
            {
                throw new UpdateRefusedException(UpdateTexts.NotLatest);
            }

            var installer = await downloader.DownloadAsync(latest, name, records.Folder, (step, percent) => Publish(new Contracts.V1.UpdateProgress
            {
                ToVersion = to,
                Step = step == UpdateStep.Download ? Contracts.V1.UpdateStepKind.UpdateStepDownload : Contracts.V1.UpdateStepKind.UpdateStepCheck,
                Percent = percent,
            }), ct, DatabaseBytes()).ConfigureAwait(false);
            DeleteOldInstallers(installer);
            var copy = await DatabaseCopy.CopyAsync(environment.ConnectionString, records.Folder, from, ct).ConfigureAwait(false);
            var record = new UpdateRecord
            {
                From = from,
                To = to,
                StartedUtc = time.GetUtcNow(),
                Step = UpdateRecord.StepInstall,
                Installer = installer,
                InstallerSha256 = await Sha256Async(installer, ct).ConfigureAwait(false),
                DatabaseCopy = copy,
                InstallerLog = environment.Platform.Os == UpdateOs.MacOs ? InstallerCommands.MacInstallLog : Path.Combine(records.Folder, "install.log"),
                RequestedBy = requestedBy,
                ClientAddress = clientAddress,
            };
            await records.WriteAsync(record, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Before the record exists every failure ends the update with nothing changed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var reason = ex is UpdateRefusedException ? ex.Message : ex is OperationCanceledException ? UpdateTexts.Cancelled : UpdateTexts.DownloadFailed;
            LogStopped(to, reason, ex.Message);
            Release(reason);
            if (ex is UpdateRefusedException)
            {
                throw;
            }

            throw new UpdateRefusedException(reason, ex);
        }

        // From here the update goes on even when the caller leaves: the record exists.
        try
        {
            await audit.WriteAsync(UserOf(requestedBy), clientAddress, AuditActions.ServerUpdateStarted, "OADM " + to, "From " + from, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the record and the final audit entry still say what happened.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAuditFailed(ex);
        }

        Publish(new Contracts.V1.UpdateProgress { ToVersion = to, Step = Contracts.V1.UpdateStepKind.UpdateStepInstall });
        gate.Close(to);
        try
        {
            var helper = environment.MakeHelper(records.Folder);
            await helpers.StartAsync(helper, HelperArguments.Of(environment.DataDirectory, to, environment.StatusUrl()).ToList(), CancellationToken.None).ConfigureAwait(false);
            LogHelperStarted(to, helper);
        }
#pragma warning disable CA1031 // Any failure to start the helper ends the update with its record.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHelperFailed(ex, to);
            try
            {
                await records.UpdateRunningAsync(r => r with { Result = UpdateRecord.Failed, Reason = UpdateTexts.HelperFailed, FinishedUtc = time.GetUtcNow() }, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The server never stays closed: the record is completed by the next start at the latest.
            catch (Exception recordFailed)
#pragma warning restore CA1031
            {
                LogRecordFailed(recordFailed);
            }

            try
            {
                await CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // As above: the audit entry is written by the next start.
            catch (Exception completeFailed)
#pragma warning restore CA1031
            {
                LogRecordFailed(completeFailed);
            }
            finally
            {
                Release(UpdateTexts.HelperFailed); // also when the record could not be completed
            }

            throw new UpdateRefusedException(UpdateTexts.HelperFailed, ex);
        }

        StartFollowing();
    }

    /// <summary>The database and its write-ahead log: the copy of rule 11 needs this much more room (rule 13).</summary>
    private long DatabaseBytes()
    {
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(environment.ConnectionString).DataSource;
        static long Size(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;
        return string.IsNullOrEmpty(path) ? 0 : Size(path) + Size(path + "-wal");
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        var stream = File.OpenRead(file);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
    }

    /// <summary>The installers of earlier updates go; one that cannot be deleted now stays (never a reason to stop).</summary>
    private void DeleteOldInstallers(string current)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(records.Folder).Where(f => IsOldInstaller(f, current)))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogOldInstallerKept(old, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOldInstallerKept(records.Folder, ex.Message);
        }
    }

    private static bool IsOldInstaller(string file, string current)
    {
        var name = Path.GetFileName(file);
        return !string.Equals(file, current, StringComparison.Ordinal)
            && (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Server start: completes the record of the last update (the new version that answers is done; a result the helper
    /// wrote is audited) and follows a record that is still running until the helper writes its result, holding tasks and
    /// event rule actions meanwhile. Never fails the start: a problem is logged.
    /// </summary>
    public async Task CompleteOnStartAsync(CancellationToken ct)
    {
        try
        {
            await CompleteOnStartCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogStartRecordFailed(ex);
        }
    }

    private async Task CompleteOnStartCoreAsync(CancellationToken ct)
    {
        var record = await records.ReadAsync(ct).ConfigureAwait(false);
        if (record is null)
        {
            return;
        }

        if (!record.IsRunning)
        {
            HelperCopy.CleanUp(records.Folder);
        }

        if (UpdateVersions.Parse(environment.ServerVersion) is { } v && UpdateVersions.Parse(record.To) == v)
        {
            // The new version runs: done (a note such as "Restart the computer" stays; a give-up is audited again as done).
            // Decided on the record as it is under the lock: a result the helper wrote meanwhile counts.
            await records.ChangeAsync(
                r =>
                {
                    if (r is null)
                    {
                        return null;
                    }

                    var gaveUp = r.Result == UpdateRecord.Failed && r.Reason == UpdateTexts.InstallerDidNotFinish;
                    return r.IsRunning || (r.Result == UpdateRecord.NotBack && !r.Audited) || gaveUp
                        ? r with
                        {
                            Result = UpdateRecord.Done,
                            Reason = r.Result == UpdateRecord.NotBack || gaveUp ? null : r.Reason,
                            FinishedUtc = r.FinishedUtc ?? time.GetUtcNow(),
                            Audited = !gaveUp && r.Audited,
                        }
                        : null;
                },
                ct).ConfigureAwait(false);
        }

        if (await records.ReadAsync(ct).ConfigureAwait(false) is { IsRunning: true } running)
        {
            // The old server runs again while the helper still works (e.g. the MSI rolled back): hold until its result.
            Hold(running.To);
            StartFollowing();
            return;
        }

        await CompleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes the audit entry of a finished record once and lets tasks and calls start again in this process.</summary>
    private async Task CompleteAsync(CancellationToken ct)
    {
        var record = await records.ReadAsync(ct).ConfigureAwait(false);
        if (record is null || record.IsRunning)
        {
            return;
        }

        if (!record.Audited)
        {
            var detail = $"From {record.From} to {record.To}" + (string.IsNullOrEmpty(record.Reason) ? string.Empty : ": " + record.Reason);
            await audit.WriteAsync(
                UserOf(record.RequestedBy),
                record.ClientAddress,
                record.Result == UpdateRecord.Done ? AuditActions.ServerUpdated : AuditActions.ServerUpdateFailed,
                "OADM " + record.To,
                detail,
                ct).ConfigureAwait(false);
            await records.ChangeAsync(r => r is { IsRunning: false } ? r with { Audited = true } : null, ct).ConfigureAwait(false);
            LogCompleted(record.To, record.Result, record.Reason ?? string.Empty);
        }

        // This server still runs: the installer failed without stopping it (or the helper never started).
        Release(record.Result == UpdateRecord.Done ? null : record.Reason ?? UpdateTexts.InstallerFailed);
    }

    private void StartFollowing()
    {
        if (Interlocked.Exchange(ref _following, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FollowAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The server stops.
            }
#pragma warning disable CA1031 // A failure here never stops the server.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFollowFailed(ex);
            }
            finally
            {
                Volatile.Write(ref _following, 0);
                if (!_stop.IsCancellationRequested)
                {
                    // Never left closed: whatever ended the follow, tasks, actions and calls go on.
                    Release(UpdateTexts.InstallerDidNotFinish);
                }
            }
        });
    }

    /// <summary>
    /// Reads the running record until it is finished (a record that cannot be read now, e.g. while the helper replaces it,
    /// is read again next time), or gives up <see cref="GiveUpAfter"/> after its start.
    /// </summary>
    private async Task FollowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var record = await records.ReadAsync(ct).ConfigureAwait(false);
                if (record is { IsRunning: false })
                {
                    await CompleteAsync(ct).ConfigureAwait(false);
                    return;
                }

                if (record is not null && time.GetUtcNow() - record.StartedUtc > GiveUpAfter)
                {
                    // Under the lock: a result the helper wrote meanwhile stays.
                    await records.UpdateRunningAsync(r => r with { Result = UpdateRecord.Failed, Reason = UpdateTexts.InstallerDidNotFinish, FinishedUtc = time.GetUtcNow() }, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFollowFailed(ex); // read again next time
            }

            await Task.Delay(FollowInterval, time, ct).ConfigureAwait(false);
        }
    }

    private int _holding;

    /// <summary>This process holds tasks and event rule actions (and, once the helper started, calls) for an update.</summary>
    internal bool IsHolding => Volatile.Read(ref _holding) == 1;

    private void Hold(string to)
    {
        Volatile.Write(ref _holding, 1);
        gate.Begin(to);
        tasks.HoldStarts(UpdateTexts.Updating(to) + ". Try again after the update.");
        actions.IsHeld = true;
    }

    /// <summary>Tasks, event rule actions and calls start again; every client learns that the update ended (once).</summary>
    private void Release(string? failed)
    {
        if (Interlocked.Exchange(ref _holding, 0) == 0)
        {
            return;
        }

        gate.End();
        tasks.ResumeStarts();
        actions.IsHeld = false;
        string to;
        lock (_gate)
        {
            to = _current?.ToVersion ?? string.Empty;
        }

        Publish(new Contracts.V1.UpdateProgress { ToVersion = to, Finished = true, Failed = failed ?? string.Empty });
    }

    private void Publish(Contracts.V1.UpdateProgress progress)
    {
        lock (_gate)
        {
            _current = progress.Finished ? null : progress;
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(progress.Clone());
            }
        }
    }

    private static string UserOf(string requestedBy) => string.IsNullOrEmpty(requestedBy) ? "system" : requestedBy.Split('@')[0];

    [LoggerMessage(Level = LogLevel.Information, Message = "Server update to {Version} refused: {Reason}")]
    private partial void LogRefused(string version, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server update to {Version} stopped before the installer: {Reason} ({Detail})")]
    private partial void LogStopped(string version, string reason, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server update to {Version}: the helper {Helper} started, the installer runs now")]
    private partial void LogHelperStarted(string version, string helper);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server update to {Version}: the helper could not be started")]
    private partial void LogHelperFailed(Exception ex, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server update to {Version} ended: {Result} {Reason}")]
    private partial void LogCompleted(string version, string result, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Following the server update failed")]
    private partial void LogFollowFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server update: the audit entry of the start could not be written")]
    private partial void LogAuditFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server update: the record of the last update could not be completed at start")]
    private partial void LogStartRecordFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Server update: the record could not be completed after the helper failed to start; the next start completes it")]
    private partial void LogRecordFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server update: the old installer {File} could not be deleted ({Detail})")]
    private partial void LogOldInstallerKept(string file, string detail);
}
