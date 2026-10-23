using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.HardeningScan.Checks;
using Oadm.Plugins.HardeningScan.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using SnapshotRequests = Oadm.Plugins.SnapshotReport.SnapshotRequests;

namespace Oadm.Plugins.HardeningScan.Scanning;

/// <summary>Timings and limits of the scan (tests shorten them).</summary>
public sealed class HardeningScanOptions
{
    /// <summary>Devices scanned at the same time; the plugin setting <c>config.parallelism</c> (1..64) overrides it.</summary>
    public int Parallelism { get; set; } = HardeningScanPluginInfo.DefaultParallelism;

    public TimeSpan RequestTimeout { get; set; } = HardeningScanPluginInfo.RequestTimeout;

    public TimeSpan DeviceTimeout { get; set; } = HardeningScanPluginInfo.DeviceTimeout;

    /// <summary>Changed results are written to the plugin setting at most this often (and at the end of a scan).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Progress and changed rows are published at most this often.</summary>
    public TimeSpan EventInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public int MaxStoredDevices { get; set; } = HardeningScanPluginInfo.MaxStoredDevices;

    public TimeProvider Time { get; set; } = TimeProvider.System;
}

/// <summary>
/// The scan jobs: one scan at a time per device scope over all (or the chosen) devices of that scope, bounded parallelism, a
/// timeout per request and per device, results streamed to the pages of the scope (<see cref="HardeningMethods.ResultsTopic"/>)
/// and kept per device and level. Read-only for the devices (<see cref="DeviceFactsReader"/>). Device groups (SDK 0.4.0): the
/// methods with an <see cref="ICallContext"/> work on the user's view (a device outside a group answers like an unknown one,
/// <see cref="DevicesNotAvailableException"/>), a scan is a job of its user (<see cref="IPluginJobs"/>: only that user can
/// stop it) that reads through the user's view; the methods without one use the plugin's context (in-process callers).
/// </summary>
public sealed partial class HardeningScanService : IAsyncDisposable
{
    public const string ConfigKey = "config";

    /// <summary>The answer to Stop for a scan id the user does not have (ended, or another user's).</summary>
    public const string NotYours = "The scan has ended or was started by another user.";

    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly IPluginSettings? _settings;
    private readonly IPluginEvents? _events;
    private readonly ILogger _logger;
    private readonly HardeningScanOptions _options;
    private readonly DeviceFactsReader _reader;
    private readonly ResultStore _store;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private Task _flushLoop = Task.CompletedTask;
    private int _parallelism;

    public HardeningScanService(IDeviceRepository devices, IVapixClientFactory vapix, IPluginSettings? settings, IPluginEvents? events, ILogger? logger = null, HardeningScanOptions? options = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));
        _settings = settings;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new HardeningScanOptions();
        _reader = new DeviceFactsReader(_options.RequestTimeout, _options.Time);
        _store = new ResultStore(_options.MaxStoredDevices);
        _parallelism = Math.Clamp(_options.Parallelism, HardeningScanPluginInfo.MinParallelism, HardeningScanPluginInfo.MaxParallelism);
    }

    /// <summary>Devices scanned at the same time.</summary>
    public int Parallelism => _parallelism;

    /// <summary>The stored results (tests).</summary>
    public ResultStore Store => _store;

    /// <summary>Reads the stored results and the configuration, then starts the background writer.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_settings is not null)
        {
            _store.Load(await _settings.GetAsync(ResultStore.SettingKey, ct).ConfigureAwait(false));
            if (await _settings.GetAsync(ConfigKey, ct).ConfigureAwait(false) is { } config)
            {
                try
                {
                    if (JsonSerializer.Deserialize<ScanConfig>(config, HardeningJson.Options) is { Parallelism: > 0 } stored)
                    {
                        _parallelism = Math.Clamp(stored.Parallelism, HardeningScanPluginInfo.MinParallelism, HardeningScanPluginInfo.MaxParallelism);
                    }
                }
                catch (JsonException)
                {
                    // keep the default
                }
            }
        }

        _flushLoop = Task.Run(() => FlushLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    public Task<HardeningState> GetStateAsync(CancellationToken ct) => GetStateAsync(_devices, ICallContext.AllDevicesScopeKey, all: true, ct);

    /// <summary>The results of the devices of the user's view and the scan of the user's device scope.</summary>
    public Task<HardeningState> GetStateAsync(ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return GetStateAsync(caller.Devices, caller.DeviceScopeKey, caller.HasAllDevices, ct);
    }

    private async Task<HardeningState> GetStateAsync(IDeviceRepository devices, string scope, bool all, CancellationToken ct)
    {
        var managed = (await devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();

        // Only a view of every device can tell a removed device; a group's view keeps the results of the others.
        if (all && _store.RemoveExcept(managed) > 0)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false); // a removed device's result is dropped
        }

        return new HardeningState
        {
            Catalog = HardeningCatalog.All,
            Columns = HardeningCatalog.ColumnIds,
            Results = [.. _store.All().Where(d => all || managed.Contains(d.DeviceId)).Select(ResultStore.ToResult)],
            Job = JobOf(scope),
        };
    }

    /// <summary>The stored details of devices of the user's view; for a user with a device group every id must be in it.</summary>
    public async Task<DetailReply> GetDetailAsync(ICallContext caller, DetailRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        _ = await SelectAsync(caller, request.DeviceIds, ct).ConfigureAwait(false);
        return GetDetail(request);
    }

    public DetailReply GetDetail(DetailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = new List<DeviceDetail>();
        foreach (var id in request.DeviceIds.Distinct())
        {
            foreach (var level in new[] { ScanLevel.Basic, ScanLevel.Extended })
            {
                if (_store.Get(id, level) is { } detail)
                {
                    details.Add(detail);
                }
            }
        }

        return new DetailReply { Devices = details };
    }

    /// <summary>The running scan of every device (the scope of users with all devices), else the last one; null when none ran.</summary>
    public ScanJobStatus? CurrentJob => JobOf(ICallContext.AllDevicesScopeKey);

    /// <summary>The running scan of a device scope, else its last one; null when none ran.</summary>
    public ScanJobStatus? JobOf(string scope)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(scope)?.Status();
        }
    }

    /// <summary>Starts a scan of the plugin's devices (in-process callers); while one runs, returns the running one.</summary>
    public async Task<ScanJobStatus> StartAsync(StartScanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string scope = ICallContext.AllDevicesScopeKey;
        if (JobOf(scope) is { State: ScanJobStates.Running } running)
        {
            return running;
        }

        var all = await _devices.ListAsync(ct).ConfigureAwait(false);
        IReadOnlyList<IDeviceInfo> devices;
        if (request.DeviceIds.Count == 0)
        {
            devices = all;
        }
        else
        {
            var wanted = request.DeviceIds.ToHashSet();
            devices = [.. all.Where(d => wanted.Contains(d.Id))];
        }

        return Begin(scope, request.Level, devices, job => Task.Run(() => RunAsync(job, devices, _vapix), CancellationToken.None), null);
    }

    /// <summary>
    /// Starts a scan of the user's devices (all of the user's view for an empty list; a user with a device group: every id
    /// must be in the view) as a job of the user, reading through the user's view. While a scan of the user's device scope
    /// runs, returns that one.
    /// </summary>
    public async Task<ScanJobStatus> StartAsync(ICallContext caller, IPluginJobs jobs, StartScanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(request);
        var scope = caller.DeviceScopeKey;
        if (JobOf(scope) is { State: ScanJobStates.Running } running)
        {
            return running;
        }

        var devices = await SelectAsync(caller, request.DeviceIds, ct).ConfigureAwait(false);
        return Begin(scope, request.Level, devices, null, job =>
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owned = jobs.Start(caller, "scan", async (owned, token) =>
            {
                await ready.Task.ConfigureAwait(false);
                using (token.Register(job.Cancel.Cancel))
                {
                    await RunAsync(job, devices, owned.Owner.Vapix, caller.HasAllDevices ? null : owned.Owner.Devices).ConfigureAwait(false);
                }
            });
            return (owned, ready);
        });
    }

    /// <summary>Stops the scan of the plugin's devices (in-process callers); null when <paramref name="jobId"/> is not it.</summary>
    public ScanJobStatus? Cancel(string? jobId)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(ICallContext.AllDevicesScopeKey);
            if (job is null || (jobId is not null && !string.Equals(jobId, job.Id, StringComparison.Ordinal)))
            {
                return null;
            }

            return Cancel(job);
        }
    }

    /// <summary>
    /// Stops the user's scan (<paramref name="jobId"/>, or the scan of the user's scope when null). A scan id the user does
    /// not have, also another user's, answers <see cref="NotYours"/> (<see cref="KeyNotFoundException"/>).
    /// </summary>
    public ScanJobStatus Cancel(ICallContext caller, IPluginJobs jobs, string? jobId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(caller.DeviceScopeKey);
            jobId ??= job?.Id;
            if (job is null || jobId is null || !string.Equals(jobId, job.Id, StringComparison.Ordinal) || jobs.Find(caller, jobId) is null)
            {
                throw new KeyNotFoundException(NotYours);
            }

            return Cancel(job);
        }
    }

    /// <summary>Waits for the running scans to end (tests).</summary>
    public Task WaitAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_jobs.Values.Select(j => j.Run ?? Task.CompletedTask));
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Job> jobs;
        lock (_gate)
        {
            jobs = [.. _jobs.Values];
        }

        foreach (var job in jobs)
        {
            job.Owned?.Cancel();
            if (job.IsRunning)
            {
                await job.Cancel.CancelAsync().ConfigureAwait(false);
            }

            if (job.Run is { } run)
            {
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flushLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    /// <summary>Scans one device (never throws for device problems); null when the scan was cancelled meanwhile.</summary>
    public Task<DeviceDetail?> ScanDeviceAsync(IDeviceInfo device, ScanLevel level, CancellationToken ct) => ScanDeviceAsync(_vapix, device, level, ct);

    /// <summary>Scans one device with the clients of <paramref name="vapix"/> (a user's view or the plugin's context).</summary>
    public async Task<DeviceDetail?> ScanDeviceAsync(IVapixClientFactory vapix, IDeviceInfo device, ScanLevel level, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(device);
        var now = _options.Time.GetUtcNow();
        if (RefusalFor(device) is { } refusal)
        {
            return Detail(device.Id, level, now, refusal, HardeningChecks.CacheOnly(device, level, refusal, now));
        }

        using var deadline = new CancellationTokenSource(_options.DeviceTimeout, _options.Time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var client = await vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            var facts = await _reader.ReadAsync(client, device, level, timeout.Token).ConfigureAwait(false);
            var status = facts.Params.Error is { } error && DeviceFactsReader.IsDeviceLevel(error) ? error : null;
            return Detail(device.Id, level, now, status, HardeningChecks.Evaluate(facts, level));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
#pragma warning disable CA1031 // A device problem becomes the row status; the scan goes on with the next device.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var text = ex is OperationCanceledException
                ? DeviceMessages.Timeout(_options.DeviceTimeout)
                : DeviceFactsReader.ErrorText(ex, _options.RequestTimeout);
            return Detail(device.Id, level, now, text, HardeningChecks.CacheOnly(device, level, text, now));
        }
    }

    /// <summary>
    /// The text for a device status that is not contacted (the existing status texts of the snapshot report), null when the
    /// device may be read. Unreachable devices are not contacted either.
    /// </summary>
    public static string? RefusalFor(DeviceStatus status) => status == DeviceStatus.Unreachable
        ? "Unreachable - the device did not answer the last status check"
        : SnapshotRequests.StatusError(status);


    /// <summary>The text of a status that is not contacted for <paramref name="device"/>: "Site offline: Building B" while its site is offline (nothing is sent).</summary>
    public static string? RefusalFor(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.SiteOffline ? Oadm.Sdk.Sites.SiteTexts.SiteOffline(device.SiteName) : RefusalFor(device.Status);
    }

    private static DeviceDetail Detail(Guid deviceId, ScanLevel level, DateTimeOffset now, string? status, IReadOnlyList<CheckResult> checks) =>
        new() { DeviceId = deviceId, Level = level, ScannedUtc = now, Status = status, Checks = checks };

    /// <summary>
    /// The devices of the user's view: all for an empty list, else those of <paramref name="deviceIds"/> (a user with a
    /// device group: any id outside the view refuses the whole request, <see cref="DevicesNotAvailableException"/>).
    /// </summary>
    private static async Task<IReadOnlyList<IDeviceInfo>> SelectAsync(ICallContext caller, IReadOnlyList<Guid> deviceIds, CancellationToken ct)
    {
        var all = await caller.Devices.ListAsync(ct).ConfigureAwait(false);
        if (deviceIds.Count == 0)
        {
            return all;
        }

        var wanted = deviceIds.ToHashSet();
        List<IDeviceInfo> devices = [.. all.Where(d => wanted.Contains(d.Id))];
        if (!caller.HasAllDevices && devices.Count < wanted.Count)
        {
            throw new DevicesNotAvailableException(wanted.Count - devices.Count);
        }

        return devices;
    }

    /// <summary>
    /// Starts a scan of <paramref name="scope"/> unless one runs (that one is returned): with <paramref name="run"/> as its
    /// own task (in-process) or as the user's job (<paramref name="owned"/>).
    /// </summary>
    private ScanJobStatus Begin(string scope, ScanLevel level, IReadOnlyList<IDeviceInfo> devices, Func<Job, Task>? run, Func<Job, (IPluginJob Job, TaskCompletionSource Ready)>? owned)
    {
        Job job;
        TaskCompletionSource? ready = null;
        lock (_gate)
        {
            if (_jobs.GetValueOrDefault(scope) is { IsRunning: true } running)
            {
                return running.Status();
            }

            job = new Job(scope, level, devices.Count, _options.Time.GetUtcNow(), CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            if (owned is not null)
            {
                (job.Owned, ready) = owned(job);
                job.Run = job.Owned.Completion;
            }

            if (_jobs.GetValueOrDefault(scope) is { } previous)
            {
                previous.Cancel.Dispose(); // the previous scan of the scope has ended
            }

            _jobs[scope] = job;
        }

        LogScanStarted(_logger, level, devices.Count, _parallelism);
        if (run is not null)
        {
            job.Run = run(job);
        }

        ready?.SetResult();
        return job.Status();
    }

    private static ScanJobStatus Cancel(Job job)
    {
        if (job.IsRunning)
        {
            job.Cancel.Cancel();
        }

        return job.Status();
    }

    /// <summary>
    /// Scans <paramref name="devices"/>. With <paramref name="view"/> (a group user's scan) a device that left the user's devices
    /// during the scan gets no row: its result is neither stored nor published (also not the error of the refused read).
    /// </summary>
    private async Task RunAsync(Job job, IReadOnlyList<IDeviceInfo> devices, IVapixClientFactory vapix, IDeviceRepository? view = null)
    {
        var token = job.Cancel.Token;
        using var publish = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var publisher = Task.Run(() => PublishLoopAsync(job, publish.Token), CancellationToken.None);
        var next = -1;

        async Task WorkerAsync()
        {
            int i;
            while (!token.IsCancellationRequested && (i = Interlocked.Increment(ref next)) < devices.Count)
            {
                var detail = await ScanDeviceAsync(vapix, devices[i], job.Level, token).ConfigureAwait(false);
                if (detail is null)
                {
                    return;
                }

                if (view is not null && !await StillInViewAsync(view, devices[i].Id, token).ConfigureAwait(false))
                {
                    job.Completed(failed: true);
                    continue;
                }

                _store.Set(detail);
                job.Pending(ResultStore.ToResult(detail));

                job.Completed(detail.Status is not null);
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, Math.Min(_parallelism, devices.Count))).Select(_ => WorkerAsync())).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A scan never crashes the server.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogScanFailed(_logger, ex);
        }

        job.Finish(token.IsCancellationRequested, _options.Time.GetUtcNow());
        await publish.CancelAsync().ConfigureAwait(false);
        try
        {
            await publisher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        PublishPending(job);
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        var status = job.Status();
        LogScanEnded(_logger, status.State, status.Done, status.Total, status.Failed);
    }

    private static async Task<bool> StillInViewAsync(IDeviceRepository view, Guid id, CancellationToken ct)
    {
        try
        {
            return await view.FindAsync(id, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is DevicesNotAvailableException or DeviceAccessRefusedException)
        {
            return false;
        }
    }

    private async Task PublishLoopAsync(Job job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.EventInterval, _options.Time, ct).ConfigureAwait(false);
            PublishPending(job);
        }
    }

    /// <summary>
    /// The new rows and the progress go to the pages of the scan's device scope (rows of a group's scan also to the pages of
    /// users with all devices, which show every result).
    /// </summary>
    private void PublishPending(Job job)
    {
        var rows = job.TakePending();
        if (_events is not { } events)
        {
            return;
        }

        foreach (var chunk in rows.Chunk(HardeningScanPluginInfo.EventBatchRows))
        {
            PublishResults(events, chunk, job.Scope);
        }

        events.Publish(HardeningMethods.ProgressTopic, HardeningJson.Serialize(job.Status()), PluginEventAudience.DeviceScope(job.Scope));
    }

    /// <summary>One event per chunk; a chunk over the event size limit is split in halves.</summary>
    private static void PublishResults(IPluginEvents events, DeviceResult[] rows, string scope)
    {
        var json = HardeningJson.Serialize(new ResultsEvent { Results = rows });
        if (json.Length <= IPluginEvents.MaxPayloadLength || rows.Length <= 1)
        {
            events.Publish(HardeningMethods.ResultsTopic, json, PluginEventAudience.DeviceScope(scope));
            if (!string.Equals(scope, ICallContext.AllDevicesScopeKey, StringComparison.OrdinalIgnoreCase))
            {
                events.Publish(HardeningMethods.ResultsTopic, json, PluginEventAudience.AllDevices);
            }

            return;
        }

        var half = rows.Length / 2;
        PublishResults(events, [.. rows.Take(half)], scope);
        PublishResults(events, [.. rows.Skip(half)], scope);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.FlushInterval, _options.Time, ct).ConfigureAwait(false);
            await FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_settings is null || !_store.IsDirty)
        {
            return;
        }

        try
        {
            await _settings.SetAsync(ResultStore.SettingKey, _store.Serialize(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Saving is retried with the next change; the results stay in memory.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSaveFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Hardening scan ({Level}) of {Count} devices started, {Parallelism} at a time")]
    private static partial void LogScanStarted(ILogger logger, ScanLevel level, int count, int parallelism);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hardening scan {State}: {Done} of {Total} devices, {Failed} could not be scanned")]
    private static partial void LogScanEnded(ILogger logger, string state, int done, int total, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hardening scan failed")]
    private static partial void LogScanFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hardening scan: the results could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex);

    private sealed class ScanConfig
    {
        public int Parallelism { get; init; }
    }

    private sealed class Job(string scope, ScanLevel level, int total, DateTimeOffset started, CancellationTokenSource cancel)
    {
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly Lock _gate = new();
        private readonly List<DeviceResult> _pending = [];
        private int _done;
        private int _failed;
        private volatile string _state = ScanJobStates.Running;
        private DateTimeOffset? _finished;

        /// <summary>The user's job id (a scan through the call), else an id of its own.</summary>
        public string Id => Owned?.Id ?? _id;

        /// <summary>The device scope the scan belongs to.</summary>
        public string Scope { get; } = scope;

        /// <summary>The user's job of a scan started through the call (null for in-process scans).</summary>
        public IPluginJob? Owned { get; set; }

        public void Pending(DeviceResult row)
        {
            lock (_gate)
            {
                _pending.Add(row);
            }
        }

        public List<DeviceResult> TakePending()
        {
            lock (_gate)
            {
                List<DeviceResult> rows = [.. _pending];
                _pending.Clear();
                return rows;
            }
        }

        public ScanLevel Level { get; } = level;

        public CancellationTokenSource Cancel { get; } = cancel;

        public Task? Run { get; set; }

        public bool IsRunning => _state == ScanJobStates.Running;

        public void Completed(bool failed)
        {
            Interlocked.Increment(ref _done);
            if (failed)
            {
                Interlocked.Increment(ref _failed);
            }
        }

        public void Finish(bool cancelled, DateTimeOffset now)
        {
            _finished = now;
            _state = cancelled ? ScanJobStates.Cancelled : ScanJobStates.Done;
        }

        public ScanJobStatus Status() => new()
        {
            JobId = Id,
            Level = Level,
            State = _state,
            Done = Volatile.Read(ref _done),
            Total = total,
            Failed = Volatile.Read(ref _failed),
            StartedUtc = started,
            FinishedUtc = _finished,
        };
    }
}
