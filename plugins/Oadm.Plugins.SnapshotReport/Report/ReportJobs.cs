using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.SnapshotReport.Report;

/// <summary>
/// PDF reports built in the background: fresh snapshots at report size for the selected tiles (bounded by the
/// <see cref="SnapshotService"/>), then the PDF. The page polls the status and reads the finished file in
/// chunks (a report with many cameras is larger than one gRPC message). A finished report stays in memory only until it
/// was downloaded: the read of its last chunk drops it at once. One never downloaded (page closed, client gone) is dropped
/// <see cref="Lifetime"/> after it ended (a timer). One user builds at most <see cref="MaxRunningPerUser"/> at once; all
/// finished reports together stay within <see cref="DefaultMaxFinishedBytes"/> (a guard: the oldest finished ones are
/// dropped first, never the one that just finished). Device groups (SDK 0.4.0): a report of a user is a job of that user
/// (<see cref="IPluginJobs"/>): it takes snapshots through the user's view, and its id answers only that user (another
/// user's id like an unknown one).
/// </summary>
public sealed partial class ReportJobs : IDisposable
{
    /// <summary>Snapshots per report: a site with 5,000 cameras fits in one report.</summary>
    public const int MaxItems = 5000;

    /// <summary>Reports with more snapshots than this take them at <see cref="LargeReportMaxWidth"/> at most (memory bound).</summary>
    public const int LargeReportItems = 200;

    /// <summary>Reports with more snapshots than this take them at <see cref="HugeReportMaxWidth"/> at most (memory bound).</summary>
    public const int HugeReportItems = 1000;

    /// <summary>1280x720 still fills the 17 cm picture area at about 190 dpi.</summary>
    public const int LargeReportMaxWidth = 1280;

    /// <summary>960x540: about 140 dpi on paper, a quarter of the full-HD bytes.</summary>
    public const int HugeReportMaxWidth = 960;

    /// <summary>A report nobody downloaded is dropped this long after it ended (finished or failed).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How often reports past their <see cref="Lifetime"/> are dropped.</summary>
    public static readonly TimeSpan ExpiryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Reports one user builds at the same time (each holds its snapshots in memory until the PDF is built).</summary>
    public const int MaxRunningPerUser = 2;

    /// <summary>The PDFs of all finished reports together (memory bound): beyond it the oldest finished ones are dropped.</summary>
    public const long DefaultMaxFinishedBytes = 1L << 30;

    private readonly SnapshotService _snapshots;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ITimer _expiry;
    private readonly long _maxFinishedBytes;
    private readonly Lock _finishing = new();

    /// <summary>Per user: the reports that may build at once (<see cref="MaxRunningPerUser"/>); a further one waits its turn.</summary>
    private readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> _turns = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="maxFinishedBytes">The byte budget of all finished reports (tests pass a small one); default <see cref="DefaultMaxFinishedBytes"/>.</param>
    public ReportJobs(SnapshotService snapshots, TimeProvider? time = null, ILogger? logger = null, long? maxFinishedBytes = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        _snapshots = snapshots;
        _maxFinishedBytes = maxFinishedBytes ?? DefaultMaxFinishedBytes;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _expiry = _time.CreateTimer(_ => DropExpired(), null, ExpiryInterval, ExpiryInterval);
    }

    /// <summary>The text for a report id the user does not have (also another user's).</summary>
    public const string NotFound = "The report no longer exists; create it again.";

    /// <summary>The status of a third report of one user while two are built: it starts when one of them ended.</summary>
    public const string WaitingForEarlier = "Wait for your earlier reports to finish";

    /// <summary>Reports in memory.</summary>
    public int Count => _jobs.Count;

    /// <summary>Validates the request and starts the report with the plugin's devices (in-process callers); returns its first status.</summary>
    public ReportJobStatus Start(ReportRequest request, string defaultVersion)
    {
        Validate(request);
        DropExpired();
        var job = new Job(Guid.NewGuid().ToString("N"), string.Empty, request.Items.Count, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), null);
        _jobs[job.Id] = job;
        job.Message = "Read video sources";
        var access = new SnapshotService.DeviceAccess(_snapshots.DefaultDevices, _snapshots.DefaultVapix, Strict: false);
        var version = VersionOf(request, defaultVersion);
        job.Worker = Task.Run(async () =>
        {
            try
            {
                await RunAsync(job, access, request, version).ConfigureAwait(false);
                await job.Released.Task.ConfigureAwait(false); // until Delete or the expiry
            }
            finally
            {
                Forget(job);
            }
        });
        return job.ToStatus();
    }

    /// <summary>
    /// Validates the request and starts the report as a job of <paramref name="caller"/> (<paramref name="jobs"/>): every item
    /// must be a device of the user's view (a user with a device group: else <see cref="Sdk.Devices.DevicesNotAvailableException"/>,
    /// nothing runs); the snapshots are taken through the user's view. Returns the first status.
    /// </summary>
    public async Task<ReportJobStatus> StartAsync(ICallContext caller, IPluginJobs jobs, ReportRequest request, string defaultVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        Validate(request);
        await SnapshotService.RequireAsync(caller, [.. request.Items.Select(i => i.DeviceId).Distinct()], ct).ConfigureAwait(false);
        DropExpired();
        var version = VersionOf(request, defaultVersion);
        var ready = new TaskCompletionSource<Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = _turns.GetOrAdd(caller.UserName, _ => new Lazy<SemaphoreSlim>(() => new SemaphoreSlim(MaxRunningPerUser))).Value;
        var owned = jobs.Start(caller, "report", async (owned, token) =>
        {
            var job = await ready.Task.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using (token.Register(job.Cancel.Cancel))
                {
                    await RunAsync(job, SnapshotService.DeviceAccess.Of(owned.Owner), request, version, turns).ConfigureAwait(false);

                    // The job lasts as long as the report: the host's end of it (a logout, other devices for the user)
                    // drops the finished report too, and Delete or the expiry ends the job.
                    await job.Released.Task.WaitAsync(token).ConfigureAwait(false);
                }
            }
            finally
            {
                Forget(job);
            }
        });
        try
        {
            var job = new Job(owned.Id, caller.UserName, request.Items.Count, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), owned)
            {
                Message = "Read video sources",
            };
            _jobs[job.Id] = job;
            ready.SetResult(job);
            return job.ToStatus();
        }
        catch (Exception ex)
        {
            // Never a job that waits forever and holds the user's place.
            ready.TrySetException(ex);
            owned.Cancel();
            throw;
        }
    }

    public ReportJobStatus Status(string jobId) => Get(jobId).ToStatus();

    /// <summary>The status of the user's report; another user's id answers like an unknown one.</summary>
    public ReportJobStatus Status(ICallContext caller, IPluginJobs jobs, string jobId) => Owned(caller, jobs, jobId).ToStatus();

    /// <summary>The next chunk of the user's finished report; the last chunk drops the report.</summary>
    public ReportChunk Read(ICallContext caller, IPluginJobs jobs, string jobId, long offset) => Read(Owned(caller, jobs, jobId).Id, offset);

    /// <summary>Cancels the user's report and forgets it; another user's id answers like an unknown one.</summary>
    public void Delete(ICallContext caller, IPluginJobs jobs, string jobId) => Delete(Owned(caller, jobs, jobId).Id);

    /// <summary>The next chunk of a finished report. The last chunk drops the report: a second download answers <see cref="NotFound"/>.</summary>
    public ReportChunk Read(string jobId, long offset)
    {
        var job = Get(jobId);
        var pdf = job.Pdf ?? throw new InvalidOperationException("The report is not ready yet.");
        if (offset < 0 || offset > pdf.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset outside the report.");
        }

        var length = (int)Math.Min(SnapshotReportPluginInfo.ReportChunkBytes, pdf.Length - offset);
        var chunk = new ReportChunk
        {
            DataBase64 = Convert.ToBase64String(pdf, (int)offset, length),
            Offset = offset,
            Total = pdf.Length,
            Eof = offset + length >= pdf.Length,
        };
        if (chunk.Eof)
        {
            Delete(job.Id); // downloaded: the job ends and its memory is freed
        }

        return chunk;
    }

    /// <summary>Cancels a running report and forgets it.</summary>
    public void Delete(string jobId)
    {
        if (_jobs.TryRemove(jobId ?? string.Empty, out var job))
        {
            End(job);
        }
    }

    /// <summary>
    /// Ends a report's work: released (a finished one) and cancelled (a running one), the user's job too. Cancelled before
    /// anything is disposed; the work disposes its token source when it ended (<see cref="Forget"/>).
    /// </summary>
    private static void End(Job job)
    {
        job.Released.TrySetResult();
        job.Owned?.Cancel();
        try
        {
            job.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The work ended meanwhile.
        }
    }

    /// <summary>The work of a report ended: it is forgotten (unless already) and its token source disposed.</summary>
    private void Forget(Job job)
    {
        _jobs.TryRemove(new KeyValuePair<string, Job>(job.Id, job));
        job.Released.TrySetResult();
        job.Cancel.Dispose();
    }

    /// <summary>Builds the PDF from tiles and snapshots that are already there (tests, fake data).</summary>
    public static RenderedReport Build(ReportRequest request, IReadOnlyList<ReportEntry> entries, string version, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var data = new ReportData(request.Site, request.Technician, request.Date == default ? DateOnly.FromDateTime(nowUtc.ToLocalTime()) : request.Date, version, nowUtc, entries);
        return ReportDocument.Render(data);
    }

    public void Dispose()
    {
        _expiry.Dispose();
        _shutdown.Cancel(); // every report's token first; their work disposes its own source when it ended
        foreach (var job in _jobs.Values)
        {
            End(job);
        }

        _jobs.Clear();
        _shutdown.Dispose();

        // Never disposed: a report that still ends releases its turn (a few semaphores, one per user who made a report).
        _turns.Clear();
    }

    /// <summary>
    /// Phase 1 of a report: the tiles and fresh snapshots of every item, in request order. Devices and sources are
    /// read once for all items (one device list, cached sources), snapshots are taken by
    /// <see cref="SnapshotReportPluginInfo.Parallelism"/> workers at <see cref="SnapshotSize"/>.
    /// <paramref name="onTaken"/> gets the number of finished items and whether the last one succeeded.
    /// </summary>
    public Task<ReportEntry[]> CollectAsync(ReportRequest request, Action<int, bool>? onTaken, CancellationToken ct) =>
        CollectAsync(new SnapshotService.DeviceAccess(_snapshots.DefaultDevices, _snapshots.DefaultVapix, Strict: false), request, onTaken, ct);

    /// <summary><see cref="CollectAsync(ReportRequest, Action{int, bool}?, CancellationToken)"/> through the user's view.</summary>
    public Task<ReportEntry[]> CollectAsync(ICallContext caller, ReportRequest request, Action<int, bool>? onTaken, CancellationToken ct) =>
        CollectAsync(SnapshotService.DeviceAccess.Of(caller), request, onTaken, ct);

    private async Task<ReportEntry[]> CollectAsync(SnapshotService.DeviceAccess access, ReportRequest request, Action<int, bool>? onTaken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.Items.Select(i => i.DeviceId).ToHashSet();

        // Sources the page just read are reused (no second source request per device for the report).
        var listed = await _snapshots.ListSourcesAsync(access, new ListSourcesRequest { DeviceIds = [.. ids] }, refreshSources: false, ct).ConfigureAwait(false);
        var tiles = new Dictionary<(Guid, int), SnapshotTile>();
        var byDevice = new Dictionary<Guid, SnapshotTile>();
        foreach (var t in listed.Tiles)
        {
            tiles.TryAdd((t.Device.DeviceId, t.Camera), t);
            byDevice.TryAdd(t.Device.DeviceId, t);
        }

        var devices = (await SnapshotService.GetDevicesAsync(access, ids, ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
        var (maxWidth, maxHeight) = SnapshotSize(request);
        var entries = new ReportEntry[request.Items.Count];
        var done = 0;
        await _snapshots.ForEachBoundedAsync(entries.Length, async index =>
        {
            var item = request.Items[index];
            var tile = Resolve(item, tiles, byDevice, devices);
            var snapshot = tile.Error is { } error && tile.Device.DeviceId == Guid.Empty
                ? new CapturedSnapshot(null, 0, 0, null, error)
                : await _snapshots.TakeAsync(access.Vapix, devices.GetValueOrDefault(item.DeviceId), item.Camera, maxWidth, maxHeight, ct).ConfigureAwait(false);
            entries[index] = new ReportEntry(tile, snapshot);
            onTaken?.Invoke(Interlocked.Increment(ref done), snapshot.IsOk);
        }, ct).ConfigureAwait(false);
        return entries;
    }

    private static void Validate(ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("Select at least one snapshot for the report.", nameof(request));
        }

        if (request.Items.Count > MaxItems)
        {
            throw new ArgumentException($"A report holds at most {MaxItems} snapshots.", nameof(request));
        }
    }

    private static string VersionOf(ReportRequest request, string defaultVersion) =>
        string.IsNullOrWhiteSpace(request.OadmVersion) ? defaultVersion : request.OadmVersion!;

    /// <summary>The report of <paramref name="jobId"/> when it is the user's (<see cref="IPluginJobs.Find"/>), else <see cref="NotFound"/>.</summary>
    private Job Owned(ICallContext caller, IPluginJobs jobs, string jobId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        if (string.IsNullOrEmpty(jobId) || jobs.Find(caller, jobId) is null)
        {
            throw new KeyNotFoundException(NotFound);
        }

        return Get(jobId);
    }

    /// <param name="turns">The user's reports that may build at once: this one waits its turn (null: in-process, no limit).</param>
    private async Task RunAsync(Job job, SnapshotService.DeviceAccess access, ReportRequest request, string version, SemaphoreSlim? turns = null)
    {
        var ct = job.Cancel.Token;
        var turn = false;
        try
        {
            if (turns is not null)
            {
                if (!turns.Wait(0))
                {
                    job.Message = WaitingForEarlier;
                    await turns.WaitAsync(ct).ConfigureAwait(false);
                }

                turn = true;
            }

            var total = request.Items.Count;
            job.Message = string.Create(CultureInfo.InvariantCulture, $"Take snapshot 1 of {total}");
            var entries = await CollectAsync(access, request, (finished, ok) =>
            {
                job.Done = finished;
                if (!ok)
                {
                    Interlocked.Increment(ref job.FailedCount);
                }

                job.Message = finished < total
                    ? string.Create(CultureInfo.InvariantCulture, $"Take snapshot {finished + 1} of {total}")
                    : "Build PDF";
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            job.Message = "Build PDF";
            var report = await Task.Run(() => Build(request, entries, version, _time.GetUtcNow().UtcDateTime), ct).ConfigureAwait(false);
            var pdf = report.Pdf;
            job.Pages = report.Pages;
            job.Message = "Report ready";
            lock (_finishing)
            {
                job.FinishedAt = _time.GetUtcNow();
                job.Pdf = pdf;
                job.State = ReportJobStates.Done;
            }

            LogReportReady(job.Id, entries.Length, pdf.Length);
            KeepWithinLimits(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.FinishedAt = _time.GetUtcNow();
            job.State = ReportJobStates.Failed;
            job.Error = "Cancelled.";
        }
#pragma warning disable CA1031 // A failing report is reported to the page, never thrown into the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            job.FinishedAt = _time.GetUtcNow();
            job.State = ReportJobStates.Failed;
            job.Error = FailureText(ex);
            LogReportFailed(ex, job.Id);
        }
        finally
        {
            if (turn)
            {
                turns!.Release(); // the PDF is built: the snapshots are no longer held, the next report may start
            }
        }
    }

    /// <summary>
    /// A report finished: while all finished PDFs together exceed the byte budget, the oldest finished ones of any user are
    /// dropped (never <paramref name="finished"/>). A guard only: a report is dropped once downloaded.
    /// </summary>
    private void KeepWithinLimits(Job finished)
    {
        List<Job> drop = [];
        lock (_finishing)
        {
            var done = _jobs.Values.Where(j => j.Pdf is not null && j.FinishedAt is not null).OrderBy(j => j.FinishedAt).ToList();
            var bytes = done.Sum(j => (long)j.Pdf!.Length);
            foreach (var old in done)
            {
                if (bytes <= _maxFinishedBytes)
                {
                    break;
                }

                if (!ReferenceEquals(old, finished))
                {
                    drop.Add(old);
                    bytes -= old.Pdf!.Length;
                }
            }
        }

        foreach (var old in drop)
        {
            LogReportDropped(old.Id, old.Pdf?.Length ?? 0);
            Delete(old.Id);
        }
    }

    /// <summary>The page's text for a failed report: never empty (a missing assembly once came with no message).</summary>
    internal static string FailureText(Exception ex) => ex switch
    {
        FileNotFoundException { FileName: { Length: > 0 } file } => "A file the report needs is missing: " + file,
        _ when !string.IsNullOrWhiteSpace(ex.Message) => ex.Message,
        _ => ex.GetType().Name,
    };

    /// <summary>
    /// Snapshot size of a report: the requested size, capped for large reports so that thousands of snapshots stay
    /// within memory (the job holds every JPEG until the PDF is built).
    /// </summary>
    public static (int Width, int Height) SnapshotSize(ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cap = request.Items.Count > HugeReportItems ? HugeReportMaxWidth
            : request.Items.Count > LargeReportItems ? LargeReportMaxWidth
            : int.MaxValue;
        if (request.MaxWidth <= cap)
        {
            return (request.MaxWidth, request.MaxHeight);
        }

        return (cap, (int)Math.Round((double)request.MaxHeight * cap / request.MaxWidth));
    }

    private static SnapshotTile Resolve(ReportItem item, Dictionary<(Guid, int), SnapshotTile> tiles, Dictionary<Guid, SnapshotTile> byDevice, Dictionary<Guid, IDeviceInfo> managed)
    {
        if (tiles.TryGetValue((item.DeviceId, item.Camera), out var tile))
        {
            return tile;
        }

        if (byDevice.TryGetValue(item.DeviceId, out var device))
        {
            // Listing failed or the source disappeared: keep the device facts.
            return new SnapshotTile
            {
                Device = device.Device,
                Camera = item.Camera,
                SourceCount = device.SourceCount,
                SourceLabel = device.SourceCount > 1 ? string.Create(CultureInfo.InvariantCulture, $"Camera {item.Camera}") : null,
                Title = device.SourceCount > 1 ? string.Create(CultureInfo.InvariantCulture, $"{device.Device.Address} - Camera {item.Camera}") : device.Device.Address,
                Error = device.Error,
            };
        }

        if (managed.TryGetValue(item.DeviceId, out var found))
        {
            // Managed and in the user's devices, but no video (a speaker, an I/O module): the listing has no source of it.
            return new SnapshotTile
            {
                Device = SnapshotService.ToFacts(found),
                Camera = item.Camera,
                Title = found.Address,
                Error = found.HasVideo ? null : SnapshotService.NoVideo,
            };
        }

        // Not in the user's devices now (removed, or outside the user's device group): the one phrase for both, so the
        // report never tells them apart (docs/spec/device-scope.md, one phrase for a device the user cannot reach).
        return new SnapshotTile
        {
            Device = new DeviceFacts { DeviceId = Guid.Empty, Address = item.DeviceId.ToString("N")[..8] },
            Camera = item.Camera,
            Title = "Device " + item.DeviceId.ToString("N")[..8],
            Error = Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1),
        };
    }

    private Job Get(string jobId)
    {
        DropExpired();
        if (!_jobs.TryGetValue(jobId ?? string.Empty, out var job))
        {
            throw new KeyNotFoundException(NotFound);
        }

        return job;
    }

    /// <summary>Drops the reports that ended more than <see cref="Lifetime"/> ago (never downloaded); running ones stay.</summary>
    private void DropExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } ended && now - ended > Lifetime)
            {
                Delete(id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot report {JobId} ready: {Snapshots} snapshots, {Bytes} bytes")]
    private partial void LogReportReady(string jobId, int snapshots, int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot report {JobId} dropped ({Bytes} bytes) to make room for newer reports")]
    private partial void LogReportDropped(string jobId, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot report {JobId} failed")]
    private partial void LogReportFailed(Exception ex, string jobId);

    private sealed class Job(string id, string owner, int total, CancellationTokenSource cancel, IPluginJob? owned)
    {
        public string Id { get; } = id;

        /// <summary>The user of the report ("" for in-process reports).</summary>
        public string Owner { get; } = owner;

        /// <summary>When the report ended: the PDF was ready or it failed (null while it is built).</summary>
        public DateTimeOffset? FinishedAt { get; set; }

        /// <summary>The user's job of a report started through the call (null for in-process reports).</summary>
        public IPluginJob? Owned { get; } = owned;
        public int Total { get; } = total;
        public CancellationTokenSource Cancel { get; } = cancel;

        /// <summary>Set when the report is dropped (downloaded, Delete, the expiry): its work ends.</summary>
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Worker { get; set; }

        public volatile string State = ReportJobStates.Running;
        public volatile int Done;
        public int FailedCount;
        public volatile string? Message;
        public volatile string? Error;
        public volatile byte[]? Pdf;
        public volatile int Pages;

        public ReportJobStatus ToStatus() => new()
        {
            JobId = Id,
            State = State,
            Done = Done,
            Total = Total,
            Message = Message,
            Error = Error,
            Size = Pdf?.Length ?? 0,
            Pages = Pages,
            Failed = Volatile.Read(ref FailedCount),
        };
    }
}
