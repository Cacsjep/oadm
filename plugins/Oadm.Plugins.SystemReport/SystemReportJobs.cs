using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.SystemReport;

/// <summary>
/// System report jobs: the server reports of the requested devices are downloaded in the background (at most
/// <see cref="Parallelism"/> devices at a time, each into its own file in the job folder), then bundled into one ZIP with a
/// summary. The client polls <see cref="Status"/> (only changed devices) and reads the bundle in chunks. A job and its files
/// are dropped 30 minutes after their last use, on <see cref="Delete"/> and when the plugin stops.
/// </summary>
public sealed partial class SystemReportJobs : IDisposable
{
    /// <summary>Up to this many ids are looked up one by one; more use one list call.</summary>
    private const int FindOneByOneLimit = 16;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly IDeviceRepository _devices;
    private readonly ServerReportDownloader _downloader;
    private readonly string _root;
    private readonly string _version;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ITimer _sweeper;
    private int _running;
    private int _maxRunning;

    /// <param name="root">Folder for the job folders; anything left in it (a crash) is deleted.</param>
    public SystemReportJobs(IDeviceRepository devices, ServerReportDownloader downloader, string root, string oadmVersion, TimeProvider? time = null, ILogger? logger = null, int parallelism = SystemReportPluginInfo.Parallelism)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentException.ThrowIfNullOrEmpty(root);
        _devices = devices;
        _downloader = downloader;
        _root = root;
        _version = oadmVersion;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        Parallelism = Math.Max(1, parallelism);
        DeleteFolder(_root);
        Directory.CreateDirectory(_root);
        _sweeper = _time.CreateTimer(_ => DropExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Devices downloading at the same time (all jobs together).</summary>
    public int Parallelism { get; }

    /// <summary>The most devices that downloaded at the same time so far (tests).</summary>
    public int MaxConcurrentDownloads => Volatile.Read(ref _maxRunning);

    /// <summary>Jobs held right now (tests).</summary>
    public int JobCount => _jobs.Count;

    /// <summary>Validates the request, looks up the devices and starts the job; the status lists every device.</summary>
    public async Task<JobStatus> StartAsync(StartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.DeviceIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Select at least one device.", nameof(request));
        }

        if (ids.Count > SystemReportPluginInfo.MaxDevices)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A system report job holds at most {SystemReportPluginInfo.MaxDevices} devices."), nameof(request));
        }

        DropExpired();
        var found = await FindAsync(ids, ct).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        var items = new Item[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var device = found.GetValueOrDefault(ids[i]);
            items[i] = new Item(device, new DeviceReportStatus
            {
                DeviceId = ids[i],
                Address = device?.Address ?? string.Empty,
                Serial = device?.Serial ?? string.Empty,
                Model = device?.Model,
            });
        }

        var job = new Job(id, folder, items, _time.GetUtcNow(), CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        foreach (var item in items.Where(i => i.Device is null))
        {
            job.Finish(item, null, null, null, "The device is no longer managed");
        }

        job.Worker = Task.Run(() => RunAsync(job), CancellationToken.None);
        _jobs[id] = job;
        LogStarted(id, items.Length);
        return job.ToStatus(0);
    }

    /// <summary>Progress of a job with the devices changed after <paramref name="sinceVersion"/>.</summary>
    public JobStatus Status(string jobId, long sinceVersion) => Get(jobId).ToStatus(sinceVersion);

    /// <summary>The next chunk of a finished bundle.</summary>
    public async Task<ReportChunk> ReadAsync(string jobId, long offset, CancellationToken ct)
    {
        var job = Get(jobId);
        if (job.State != JobStates.Done || job.BundlePath is not { } path)
        {
            throw new InvalidOperationException("The system reports are not ready yet.");
        }

        var total = job.BundleSize;
        if (offset < 0 || offset > total)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset outside the bundle.");
        }

        var length = (int)Math.Min(SystemReportPluginInfo.ChunkBytes, total - offset);
        var buffer = new byte[length];
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
        {
            file.Position = offset;
            await file.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }

        return new ReportChunk
        {
            DataBase64 = Convert.ToBase64String(buffer),
            Offset = offset,
            Total = total,
            Eof = offset + length >= total,
        };
    }

    /// <summary>Cancels a job, forgets it and deletes its files (once the downloads ended).</summary>
    public void Delete(string jobId)
    {
        if (!_jobs.TryRemove(jobId ?? string.Empty, out var job))
        {
            return;
        }

        job.Cancel.Cancel();
        var worker = job.Worker ?? Task.CompletedTask;
        _ = worker.ContinueWith(_ =>
        {
            job.Cancel.Dispose();
            DeleteFolder(job.Folder);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits until a job's background work ended (tests).</summary>
    public Task WaitAsync(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job.Worker ?? Task.CompletedTask : Task.CompletedTask;

    public void Dispose()
    {
        _sweeper.Dispose();
        _shutdown.Cancel();
        var workers = _jobs.Values.Select(j => j.Worker ?? Task.CompletedTask).ToArray();
        try
        {
            // Downloads stop at once on cancellation; wait briefly so the files can be deleted.
            Task.WaitAll(workers, TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        foreach (var job in _jobs.Values)
        {
            job.Cancel.Dispose();
        }

        _jobs.Clear();
        DeleteFolder(_root);
        _shutdown.Dispose();
    }

    private async Task<Dictionary<Guid, IDeviceInfo>> FindAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > FindOneByOneLimit)
        {
            var wanted = ids.ToHashSet();
            return (await _devices.ListAsync(ct).ConfigureAwait(false)).Where(d => wanted.Contains(d.Id)).ToDictionary(d => d.Id);
        }

        var found = new Dictionary<Guid, IDeviceInfo>();
        foreach (var id in ids)
        {
            if (await _devices.FindAsync(id, ct).ConfigureAwait(false) is { } device)
            {
                found[id] = device;
            }
        }

        return found;
    }

    private async Task RunAsync(Job job)
    {
        var ct = job.Cancel.Token;
        try
        {
            var next = -1;
            async Task WorkerAsync()
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < job.Items.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = job.Items[i];
                    if (item.Device is not null)
                    {
                        await DownloadAsync(job, item, i, ct).ConfigureAwait(false);
                    }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, Math.Min(Parallelism, job.Items.Length)).Select(_ => WorkerAsync())).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            job.SetState(JobStates.Packing);
            var bundle = Path.Combine(job.Folder, "bundle.zip");
            var created = _time.GetUtcNow();
            var entries = job.Items.Select(i => new BundleEntry(i.Status, i.ReportPath, i.Mode, i.CapturedUtc)).ToList();
            await ReportBundle.WriteAsync(bundle, entries, created, _version, ct).ConfigureAwait(false);
            foreach (var item in job.Items.Where(i => i.ReportPath is not null))
            {
                ServerReportDownloader.TryDelete(item.ReportPath!);
            }

            job.BundlePath = bundle;
            job.BundleSize = new FileInfo(bundle).Length;
            job.SetState(JobStates.Done);
            LogReady(job.Id, job.Items.Length - job.FailedCount, job.FailedCount, job.BundleSize);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Error = "Cancelled.";
            job.SetState(JobStates.Failed);
        }
#pragma warning disable CA1031 // A failing job is reported to the client, never thrown into the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            job.Error = "The system reports could not be bundled: " + ex.Message;
            job.SetState(JobStates.Failed);
            LogFailed(ex, job.Id);
        }
    }

    private async Task DownloadAsync(Job job, Item item, int index, CancellationToken ct)
    {
        job.Update(item, s => s.State = DeviceReportStates.Downloading);
        var path = Path.Combine(job.Folder, index.ToString(CultureInfo.InvariantCulture) + ".zip");
        var running = Interlocked.Increment(ref _running);
        int max;
        while (running > (max = Volatile.Read(ref _maxRunning)) && Interlocked.CompareExchange(ref _maxRunning, running, max) != max)
        {
        }

        try
        {
            var result = await _downloader.DownloadAsync(item.Device!, path, ct).ConfigureAwait(false);
            var captured = _time.GetUtcNow();
            if (result.IsOk)
            {
                var name = ReportBundle.FileName(item.Status.Address, item.Status.Serial, item.Status.Model, captured);
                job.Finish(item, path, result.Mode, captured, null, result.Bytes, name);
            }
            else
            {
                job.Finish(item, null, result.Mode, null, result.Error);
                LogDeviceFailed(job.Id, item.Status.Address, result.Error!);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private Job Get(string jobId)
    {
        DropExpired();
        if (!_jobs.TryGetValue(jobId ?? string.Empty, out var job))
        {
            throw new KeyNotFoundException("The system report job no longer exists; start it again.");
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

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "System report job {JobId} started for {Devices} devices")]
    private partial void LogStarted(string jobId, int devices);

    [LoggerMessage(Level = LogLevel.Information, Message = "System report job {JobId} ready: {Reports} reports, {Failed} failed, {Bytes} bytes")]
    private partial void LogReady(string jobId, int reports, int failed, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "System report job {JobId}: no report from {Address}: {Error}")]
    private partial void LogDeviceFailed(string jobId, string address, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "System report job {JobId} failed")]
    private partial void LogFailed(Exception ex, string jobId);

    private sealed class Item(IDeviceInfo? device, DeviceReportStatus status)
    {
        public IDeviceInfo? Device { get; } = device;
        public DeviceReportStatus Status { get; } = status;
        public long Version { get; set; }
        public string? ReportPath { get; set; }
        public string? Mode { get; set; }
        public DateTimeOffset? CapturedUtc { get; set; }
    }

    private sealed class Job(string id, string folder, Item[] items, DateTimeOffset created, CancellationTokenSource cancel)
    {
        private readonly Lock _gate = new();
        private long _version = 1;
        private int _finished;
        private int _failed;
        private string _state = JobStates.Running;

        public string Id { get; } = id;
        public string Folder { get; } = folder;
        public Item[] Items { get; } = items;
        public CancellationTokenSource Cancel { get; } = cancel;
        public DateTimeOffset LastUsed { get; set; } = created;
        public Task? Worker { get; set; }
        public string? BundlePath { get; set; }
        public long BundleSize { get; set; }
        public string? Error { get; set; }

        public string State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public int FailedCount
        {
            get
            {
                lock (_gate)
                {
                    return _failed;
                }
            }
        }

        public void SetState(string state)
        {
            lock (_gate)
            {
                _state = state;
                _version++;
            }
        }

        public void Update(Item item, Action<DeviceReportStatus> change)
        {
            lock (_gate)
            {
                change(item.Status);
                item.Version = ++_version;
            }
        }

        public void Finish(Item item, string? path, string? mode, DateTimeOffset? captured, string? error, long size = 0, string? fileName = null)
        {
            lock (_gate)
            {
                item.ReportPath = path;
                item.Mode = mode;
                item.CapturedUtc = captured;
                item.Status.State = error is null ? DeviceReportStates.Done : DeviceReportStates.Failed;
                item.Status.Error = error;
                item.Status.Size = size;
                item.Status.FileName = fileName;
                item.Version = ++_version;
                _finished++;
                if (error is not null)
                {
                    _failed++;
                }
            }
        }

        /// <summary>O(n) over the devices, copies of the changed ones only.</summary>
        public JobStatus ToStatus(long sinceVersion)
        {
            lock (_gate)
            {
                var status = new JobStatus
                {
                    JobId = Id,
                    State = _state,
                    Total = Items.Length,
                    Finished = _finished,
                    Failed = _failed,
                    Size = _state == JobStates.Done ? BundleSize : 0,
                    Error = Error,
                    Version = _version,
                };
                foreach (var item in Items)
                {
                    if (item.Version > sinceVersion || sinceVersion <= 0)
                    {
                        var s = item.Status;
                        status.Devices.Add(new DeviceReportStatus
                        {
                            DeviceId = s.DeviceId,
                            Address = s.Address,
                            Serial = s.Serial,
                            Model = s.Model,
                            State = s.State,
                            Error = s.Error,
                            Size = s.Size,
                            FileName = s.FileName,
                        });
                    }
                }

                return status;
            }
        }
    }
}
