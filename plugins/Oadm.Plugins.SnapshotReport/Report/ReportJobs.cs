using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Oadm.Plugins.SnapshotReport.Report;

/// <summary>
/// PDF reports built in the background: fresh snapshots at report size for the selected tiles (bounded by the
/// <see cref="SnapshotService"/>), then the PDF. The page polls the status and reads the finished file in
/// chunks (a report with many cameras is larger than one gRPC message). Reports are kept in memory and
/// dropped 30 minutes after their last use.
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

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly SnapshotService _snapshots;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public ReportJobs(SnapshotService snapshots, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        _snapshots = snapshots;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Validates the request and starts the report; returns its first status.</summary>
    public ReportJobStatus Start(ReportRequest request, string defaultVersion)
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

        DropExpired();
        var job = new Job(Guid.NewGuid().ToString("N"), request.Items.Count, _time.GetUtcNow(), CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        _jobs[job.Id] = job;
        job.Message = "Read video sources";
        job.Worker = Task.Run(() => RunAsync(job, request, string.IsNullOrWhiteSpace(request.OadmVersion) ? defaultVersion : request.OadmVersion!));
        return job.ToStatus();
    }

    public ReportJobStatus Status(string jobId) => Get(jobId).ToStatus();

    /// <summary>The next chunk of a finished report.</summary>
    public ReportChunk Read(string jobId, long offset)
    {
        var job = Get(jobId);
        var pdf = job.Pdf ?? throw new InvalidOperationException("The report is not ready yet.");
        if (offset < 0 || offset > pdf.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset outside the report.");
        }

        var length = (int)Math.Min(SnapshotReportPluginInfo.ReportChunkBytes, pdf.Length - offset);
        return new ReportChunk
        {
            DataBase64 = Convert.ToBase64String(pdf, (int)offset, length),
            Offset = offset,
            Total = pdf.Length,
            Eof = offset + length >= pdf.Length,
        };
    }

    /// <summary>Cancels a running report and forgets it.</summary>
    public void Delete(string jobId)
    {
        if (_jobs.TryRemove(jobId ?? string.Empty, out var job))
        {
            job.Cancel.Cancel();
            job.Cancel.Dispose();
        }
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
        _shutdown.Cancel();
        foreach (var job in _jobs.Values)
        {
            job.Cancel.Dispose();
        }

        _jobs.Clear();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Phase 1 of a report: the tiles and fresh snapshots of every item, in request order. Devices and sources are
    /// read once for all items (one device list, cached sources), snapshots are taken by
    /// <see cref="SnapshotReportPluginInfo.Parallelism"/> workers at <see cref="SnapshotSize"/>.
    /// <paramref name="onTaken"/> gets the number of finished items and whether the last one succeeded.
    /// </summary>
    public async Task<ReportEntry[]> CollectAsync(ReportRequest request, Action<int, bool>? onTaken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.Items.Select(i => i.DeviceId).ToHashSet();

        // Sources the page just read are reused (no second source request per device for the report).
        var listed = await _snapshots.ListSourcesAsync(new ListSourcesRequest { DeviceIds = [.. ids] }, ct, refreshSources: false).ConfigureAwait(false);
        var tiles = new Dictionary<(Guid, int), SnapshotTile>();
        var byDevice = new Dictionary<Guid, SnapshotTile>();
        foreach (var t in listed.Tiles)
        {
            tiles.TryAdd((t.Device.DeviceId, t.Camera), t);
            byDevice.TryAdd(t.Device.DeviceId, t);
        }

        var devices = (await _snapshots.GetVideoDevicesAsync(ids, ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
        var (maxWidth, maxHeight) = SnapshotSize(request);
        var entries = new ReportEntry[request.Items.Count];
        var done = 0;
        await _snapshots.ForEachBoundedAsync(entries.Length, async index =>
        {
            var item = request.Items[index];
            var tile = Resolve(item, tiles, byDevice);
            var snapshot = tile.Error is { } error && tile.Device.DeviceId == Guid.Empty
                ? new CapturedSnapshot(null, 0, 0, null, error)
                : await _snapshots.TakeAsync(devices.GetValueOrDefault(item.DeviceId), item.Camera, maxWidth, maxHeight, ct).ConfigureAwait(false);
            entries[index] = new ReportEntry(tile, snapshot);
            onTaken?.Invoke(Interlocked.Increment(ref done), snapshot.IsOk);
        }, ct).ConfigureAwait(false);
        return entries;
    }

    private async Task RunAsync(Job job, ReportRequest request, string version)
    {
        var ct = job.Cancel.Token;
        try
        {
            var total = request.Items.Count;
            job.Message = string.Create(CultureInfo.InvariantCulture, $"Take snapshot 1 of {total}");
            var entries = await CollectAsync(request, (finished, ok) =>
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
            job.Pdf = pdf;
            job.Message = "Report ready";
            job.State = ReportJobStates.Done;
            LogReportReady(job.Id, entries.Length, pdf.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.State = ReportJobStates.Failed;
            job.Error = "Cancelled.";
        }
#pragma warning disable CA1031 // A failing report is reported to the page, never thrown into the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            job.State = ReportJobStates.Failed;
            job.Error = FailureText(ex);
            LogReportFailed(ex, job.Id);
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

    private static SnapshotTile Resolve(ReportItem item, Dictionary<(Guid, int), SnapshotTile> tiles, Dictionary<Guid, SnapshotTile> byDevice)
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

        return new SnapshotTile
        {
            Device = new DeviceFacts { DeviceId = Guid.Empty, Address = item.DeviceId.ToString("N")[..8] },
            Camera = item.Camera,
            Title = "Removed device " + item.DeviceId.ToString("N")[..8],
            Error = "The device was removed from OADM or has no video.",
        };
    }

    private Job Get(string jobId)
    {
        DropExpired();
        if (!_jobs.TryGetValue(jobId ?? string.Empty, out var job))
        {
            throw new KeyNotFoundException("The report no longer exists; create it again.");
        }

        job.LastUsed = _time.GetUtcNow();
        return job;
    }

    private void DropExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, job) in _jobs)
        {
            if (now - job.LastUsed > Lifetime)
            {
                Delete(id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot report {JobId} ready: {Snapshots} snapshots, {Bytes} bytes")]
    private partial void LogReportReady(string jobId, int snapshots, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot report {JobId} failed")]
    private partial void LogReportFailed(Exception ex, string jobId);

    private sealed class Job(string id, int total, DateTimeOffset created, CancellationTokenSource cancel)
    {
        public string Id { get; } = id;
        public int Total { get; } = total;
        public CancellationTokenSource Cancel { get; } = cancel;
        public DateTimeOffset LastUsed { get; set; } = created;
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
