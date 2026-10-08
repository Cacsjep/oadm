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
/// The scan jobs: one scan at a time over all (or the chosen) managed devices, bounded parallelism, a timeout per request and
/// per device, results streamed to the page (<see cref="HardeningMethods.ResultsTopic"/>) and kept per device and level.
/// Read-only for the devices (<see cref="DeviceFactsReader"/>).
/// </summary>
public sealed partial class HardeningScanService : IAsyncDisposable
{
    public const string ConfigKey = "config";

    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly IPluginSettings? _settings;
    private readonly IPluginEvents? _events;
    private readonly ILogger _logger;
    private readonly HardeningScanOptions _options;
    private readonly DeviceFactsReader _reader;
    private readonly ResultStore _store;
    private readonly Lock _gate = new();
    private readonly List<DeviceResult> _pendingEvents = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Job? _job;
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

    public async Task<HardeningState> GetStateAsync(CancellationToken ct)
    {
        var managed = (await _devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        if (_store.RemoveExcept(managed) > 0)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false); // a removed device's result is dropped
        }

        return new HardeningState
        {
            Catalog = HardeningCatalog.All,
            Columns = HardeningCatalog.ColumnIds,
            Results = [.. _store.All().Select(ResultStore.ToResult)],
            Job = CurrentJob,
        };
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

    /// <summary>The running scan, else the last one; null when none ran.</summary>
    public ScanJobStatus? CurrentJob
    {
        get
        {
            lock (_gate)
            {
                return _job?.Status();
            }
        }
    }

    /// <summary>Starts a scan; while one runs, returns the running one.</summary>
    public async Task<ScanJobStatus> StartAsync(StartScanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_job is { IsRunning: true } running)
            {
                return running.Status();
            }
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

        Job job;
        lock (_gate)
        {
            if (_job is { IsRunning: true } running)
            {
                return running.Status();
            }

            job = new Job(Guid.NewGuid().ToString("N"), request.Level, devices.Count, _options.Time.GetUtcNow(), CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _job?.Cancel.Dispose(); // the previous scan has ended
            _job = job;
        }

        LogScanStarted(_logger, request.Level, devices.Count, _parallelism);
        job.Run = Task.Run(() => RunAsync(job, devices), CancellationToken.None);
        return job.Status();
    }

    public ScanJobStatus? Cancel(string? jobId)
    {
        lock (_gate)
        {
            if (_job is null || (jobId is not null && !string.Equals(jobId, _job.Id, StringComparison.Ordinal)))
            {
                return null;
            }

            if (_job.IsRunning)
            {
                _job.Cancel.Cancel();
            }

            return _job.Status();
        }
    }

    /// <summary>Waits for the running scan to end (tests).</summary>
    public Task WaitAsync()
    {
        lock (_gate)
        {
            return _job?.Run ?? Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Job? job;
        lock (_gate)
        {
            job = _job;
        }

        if (job is { IsRunning: true })
        {
            await job.Cancel.CancelAsync().ConfigureAwait(false);
        }

        if (job?.Run is { } run)
        {
            await run.ConfigureAwait(false);
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
    public async Task<DeviceDetail?> ScanDeviceAsync(IDeviceInfo device, ScanLevel level, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = _options.Time.GetUtcNow();
        if (RefusalFor(device.Status) is { } refusal)
        {
            return Detail(device.Id, level, now, refusal, HardeningChecks.CacheOnly(device, level, refusal, now));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.DeviceTimeout);
        try
        {
            var client = await _vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
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
                ? string.Create(CultureInfo.InvariantCulture, $"Timeout after {_options.DeviceTimeout.TotalSeconds:0.#} s")
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

    private static DeviceDetail Detail(Guid deviceId, ScanLevel level, DateTimeOffset now, string? status, IReadOnlyList<CheckResult> checks) =>
        new() { DeviceId = deviceId, Level = level, ScannedUtc = now, Status = status, Checks = checks };

    private async Task RunAsync(Job job, IReadOnlyList<IDeviceInfo> devices)
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
                var detail = await ScanDeviceAsync(devices[i], job.Level, token).ConfigureAwait(false);
                if (detail is null)
                {
                    return;
                }

                _store.Set(detail);
                lock (_gate)
                {
                    _pendingEvents.Add(ResultStore.ToResult(detail));
                }

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

    private async Task PublishLoopAsync(Job job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.EventInterval, _options.Time, ct).ConfigureAwait(false);
            PublishPending(job);
        }
    }

    private void PublishPending(Job job)
    {
        List<DeviceResult> rows;
        lock (_gate)
        {
            rows = [.. _pendingEvents];
            _pendingEvents.Clear();
        }

        if (_events is not { } events)
        {
            return;
        }

        foreach (var chunk in rows.Chunk(HardeningScanPluginInfo.EventBatchRows))
        {
            PublishResults(events, chunk);
        }

        events.Publish(HardeningMethods.ProgressTopic, HardeningJson.Serialize(job.Status()));
    }

    /// <summary>One event per chunk; a chunk over the event size limit is split in halves.</summary>
    private static void PublishResults(IPluginEvents events, DeviceResult[] rows)
    {
        var json = HardeningJson.Serialize(new ResultsEvent { Results = rows });
        if (json.Length <= IPluginEvents.MaxPayloadLength || rows.Length <= 1)
        {
            events.Publish(HardeningMethods.ResultsTopic, json);
            return;
        }

        var half = rows.Length / 2;
        PublishResults(events, [.. rows.Take(half)]);
        PublishResults(events, [.. rows.Skip(half)]);
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

    private sealed class Job(string id, ScanLevel level, int total, DateTimeOffset started, CancellationTokenSource cancel)
    {
        private int _done;
        private int _failed;
        private volatile string _state = ScanJobStates.Running;
        private DateTimeOffset? _finished;

        public string Id { get; } = id;

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
