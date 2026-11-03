using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Settings;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>
/// Keeps the device table current.
/// <list type="bullet">
/// <item>Status poll: rounds of one basicdeviceinfo call per device (<see cref="StatusPollTimeout"/>, 3 s) with at most
/// <see cref="MaxParallelism"/> (200) at once (<see cref="PollRoundAsync"/>), each followed by <see cref="RoundPause"/>
/// (60 s) before the next one starts, so a round never overlaps the next and a large installation simply polls a little
/// less often; no setting. Updating Status, Model, FirmwareVersion and LastSeenUtc; a LastSeenUtc-only change is written in
/// batches of <see cref="LastSeenBatchSize"/> (one transaction each) and not published. A device that stops answering is asked once more at the end of the round before it turns Unreachable,
/// except in an outage (more than <see cref="OutageShare"/> of the round failed). A new firmware or model queues a full
/// refresh.</item>
/// <item>Full refresh: basicdeviceinfo plus param.cgi network info (DHCP, HTTPS, 802.1X, UPnP
/// name), the HTTPS certificate seen in the TLS handshake (expiry, chain trust, subject, issuer)
/// and the Server column. Queued with <see cref="QueueRefresh"/> (on add, manual Refresh, after a
/// task) or run directly with <see cref="RefreshAsync"/>.</item>
/// <item>Full refresh rounds like the status poll: a round over every device that is Ok (others are watched by the status
/// poll) and was not refreshed within the last <see cref="FullRefreshPause"/> (a task, back online or new firmware refreshed
/// it already), then <see cref="FullRefreshPause"/> (10 min) before the next one; no setting. Immediately as well when a
/// status poll sees a device go from a non-Ok status back to Ok or a new firmware. Rounds and the queue share one limit:
/// at most <see cref="RefreshParallelism"/> (64) full refreshes at once.</item>
/// </list>
/// Status comes from <see cref="DeviceStatusClassifier"/>; a pinned certificate mismatch becomes
/// CertificateChanged; a 401 on a device whose systemready says needsetup becomes PasswordNotSet.
/// Never throws from the background loops.
/// </summary>
public sealed partial class DevicePollingService : IDisposable
{
    private readonly DeviceRepository _devices;
    private readonly VapixClientFactory _clients;
    private readonly ServerSettingsStore _settings;
    private readonly Sites.ISiteStates? _sites;
    private readonly DeviceStatusNotes? _notes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<Guid> _refreshQueue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });
    private readonly ConcurrentDictionary<Guid, byte> _pendingRefresh = new();
    private readonly Lock _lastSeenSync = new();
    private List<(Guid Id, DateTime LastSeenUtc)> _lastSeen = [];
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFullRefresh = new();
    private SemaphoreSlim? _fullGate;
    private bool _mostDevicesDown;

    public DevicePollingService(
        DeviceRepository devices,
        VapixClientFactory clients,
        ServerSettingsStore settings,
        TimeProvider? timeProvider = null,
        ILogger<DevicePollingService>? logger = null,
        Sites.ISiteStates? sites = null,
        DeviceStatusNotes? notes = null)
    {
        _notes = notes;
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(settings);
        _devices = devices;
        _clients = clients;
        _settings = settings;
        _sites = sites;
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Status polls at the same time (default 200): a pool, the next device starts as soon as one poll ends, so a slow or
    /// dead device never holds the others (measured: about 1,000 polls per second).
    /// </summary>
    public int MaxParallelism { get; init; } = 200;

    /// <summary>The pause after a round before the next one starts. 60 s; no setting.</summary>
    public TimeSpan RoundPause { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Polls that change nothing but LastSeenUtc are written together, this many in one transaction (default 500): one
    /// transaction per poll was the ceiling of a round at 50,000 devices.
    /// </summary>
    public int LastSeenBatchSize { get; init; } = 500;

    /// <summary>The last finished status round (the support report); null before the first one.</summary>
    public PollRoundInfo? LastRound { get; private set; }

    /// <summary>Full refreshes at the same time. Default 64.</summary>
    public int RefreshParallelism { get; init; } = 64;

    /// <summary>Timeout of the status poll's request. Default 3 s; every other request keeps the client's timeout.</summary>
    public TimeSpan StatusPollTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Share of a round that may stop answering before it counts as an outage (default 10 %): then the devices are not
    /// asked a second time, they turn Unreachable at once (a second try would double the round).
    /// </summary>
    public double OutageShare { get; init; } = 0.10;

    /// <summary>An outage also needs at least this many failed devices (default 10): a small site always gets the second try.</summary>
    public int OutageMinimum { get; init; } = 10;

    /// <summary>The pause after a full refresh round before the next one starts. 10 min; no setting.</summary>
    public TimeSpan FullRefreshPause { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The last finished full refresh round (the support report); null before the first one.</summary>
    public PollRoundInfo? LastFullRound { get; private set; }

    /// <summary>Raised after a device was polled or refreshed (also when the device did not answer).</summary>
    public event EventHandler<Device>? DeviceUpdated;

    /// <summary>Queues a full refresh. Devices already waiting in the queue are not queued twice.</summary>
    public void QueueRefresh(IEnumerable<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        foreach (var id in deviceIds)
        {
            if (_pendingRefresh.TryAdd(id, 0))
            {
                _refreshQueue.Writer.TryWrite(id);
            }
        }
    }

    /// <summary>
    /// Runs the status rounds, the full refresh rounds and the refresh queue until <paramref name="ct"/> is cancelled. The
    /// first status round runs after one pause, the first full refresh round after one full pause (devices get a full
    /// refresh when added).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var worker = RunRefreshQueueAsync(ct);
        var poller = RunPollLoopAsync(ct);
        var scheduler = RunFullRefreshLoopAsync(ct);
        try
        {
            await Task.WhenAll(worker, poller, scheduler).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One status poll of every managed device (<see cref="PollRoundAsync"/>).</summary>
    public Task PollAllAsync(CancellationToken ct) => PollRoundAsync(ct);

    /// <summary>
    /// One status poll of every managed device: a pool of at most <see cref="MaxParallelism"/> polls, the next device
    /// starts as soon as one poll ends (a slow or unreachable device never adds parallel requests). Each device row is read
    /// right before its poll, so a row changed during the round is polled with its current address and credentials. A poll
    /// that changes nothing but LastSeenUtc is collected and written with others in one transaction.
    /// </summary>
    public async Task PollRoundAsync(CancellationToken ct)
    {
        var ids = await _devices.ListDeviceIdsAsync(ct).ConfigureAwait(false);
        var start = _time.GetUtcNow();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct };
        var missed = new ConcurrentBag<(Device Device, Observation Observation)>();
        var polled = 0;
        var failed = 0;
        await Parallel.ForEachAsync(ids, options, async (id, token) =>
        {
            var device = await _devices.GetAsync(id, token).ConfigureAwait(false);
            if (device is null || device.Status == DeviceStatus.SiteOffline)
            {
                return; // the site is offline: not contacted, the stored status stays
            }

            var observation = await ObserveAsync(device, full: false, token).ConfigureAwait(false);
            if (observation.SiteProblem)
            {
                return; // the site could not carry the poll: not a device result, not part of the round
            }

            Interlocked.Increment(ref polled);
            if (observation.Status != DeviceStatus.Unreachable)
            {
                if (OnlyLastSeen(device, observation) is { } seen)
                {
                    await CollectLastSeenAsync(device.Id, seen, token).ConfigureAwait(false);
                }
                else
                {
                    await ApplyAsync(device, observation, full: false, token).ConfigureAwait(false);
                }

                return;
            }

            Interlocked.Increment(ref failed);
            if (device.Status == DeviceStatus.Unreachable)
            {
                await ApplyAsync(device, observation, full: false, token).ConfigureAwait(false);
            }
            else
            {
                missed.Add((device, observation)); // asked once more at the end of the round
            }
        }).ConfigureAwait(false);
        await FlushLastSeenAsync(ct).ConfigureAwait(false);

        if (!missed.IsEmpty)
        {
            if (missed.Count >= OutageMinimum && missed.Count > polled * OutageShare)
            {
                // An outage: no second try, the devices turn Unreachable now.
                await Parallel.ForEachAsync(missed, options, async (miss, token) =>
                    await ApplyAsync(miss.Device, miss.Observation, full: false, token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            else
            {
                await Parallel.ForEachAsync(missed, options, async (miss, token) =>
                    await PollAsync(miss.Device.Id, token).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        NoteRound(polled, failed);
        LastRound = new PollRoundInfo(ids.Count, _time.GetUtcNow() - start, _time.GetUtcNow());
    }

    /// <summary>
    /// The new LastSeenUtc when <paramref name="observation"/> changes nothing else of <paramref name="device"/> (status,
    /// model, firmware, clock, certificate pin, the status note); null when it changes more (then it is written and
    /// published at once).
    /// </summary>
    private DateTime? OnlyLastSeen(Device device, Observation observation)
    {
        if (observation.LastSeen is not { } seen
            || (_notes?.Get(device.Id) ?? null) != (observation.Status == DeviceStatus.CredentialsRequired ? observation.Note : null))
        {
            return null;
        }

        var after = device.Clone();
        observation.ApplyTo(after);
        return after.Status == device.Status
            && after.Model == device.Model
            && after.FirmwareVersion == device.FirmwareVersion
            && after.CertFingerprintSha256 == device.CertFingerprintSha256
            && after.ServerName == device.ServerName
            && after.Soc == device.Soc
            && after.Architecture == device.Architecture
            && after.ClockOffsetMs == device.ClockOffsetMs
            && after.NtpState == device.NtpState
            && after.ProductType == device.ProductType
            && after.Category == device.Category
            && after.DhcpEnabled == device.DhcpEnabled
            && after.HttpsEnabled == device.HttpsEnabled
            && after.Dot1xEnabled == device.Dot1xEnabled
            && after.UpnpFriendlyName == device.UpnpFriendlyName
            && ReferenceEquals(after.Apis, device.Apis)
            && after.CertNotAfterUtc == device.CertNotAfterUtc
            && after.CertTrust == device.CertTrust
            && after.CertSubject == device.CertSubject
            && after.CertIssuer == device.CertIssuer
            && after.CertNameMatches == device.CertNameMatches
                ? seen
                : null;
    }

    /// <summary>Collects a LastSeenUtc-only change; a full batch is written at once (one transaction).</summary>
    private async Task CollectLastSeenAsync(Guid id, DateTime seen, CancellationToken ct)
    {
        List<(Guid Id, DateTime LastSeenUtc)>? full = null;
        lock (_lastSeenSync)
        {
            _lastSeen.Add((id, seen));
            if (_lastSeen.Count >= Math.Max(1, LastSeenBatchSize))
            {
                full = _lastSeen;
                _lastSeen = [];
            }
        }

        if (full is not null)
        {
            await _devices.SetLastSeenManyAsync(full, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the collected LastSeenUtc changes (the end of a round).</summary>
    private async Task FlushLastSeenAsync(CancellationToken ct)
    {
        List<(Guid Id, DateTime LastSeenUtc)> rest;
        lock (_lastSeenSync)
        {
            rest = _lastSeen;
            _lastSeen = [];
        }

        if (rest.Count > 0)
        {
            await _devices.SetLastSeenManyAsync(rest, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One warning when most devices of a round did not answer, one information when they answer again.</summary>
    private void NoteRound(int polled, int failed)
    {
        if (polled < 10)
        {
            return;
        }

        var most = failed * 2 > polled;
        if (most == _mostDevicesDown)
        {
            return;
        }

        _mostDevicesDown = most;
        if (most)
        {
            LogMostDevicesDown(failed, polled);
        }
        else
        {
            LogDevicesAnswerAgain(polled - failed, polled);
        }
    }

    /// <summary>
    /// One full refresh round: every device that is Ok and was not refreshed within the last <see cref="FullRefreshPause"/>,
    /// in a pool with the queue's limit (<see cref="RefreshParallelism"/> together). Returns how many were refreshed.
    /// </summary>
    public async Task<int> FullRefreshRoundAsync(CancellationToken ct)
    {
        var devices = await _devices.ListIdsWithStatusAsync(ct).ConfigureAwait(false);
        var start = _time.GetUtcNow();
        var known = devices.Select(d => d.Id).ToHashSet();
        foreach (var gone in _lastFullRefresh.Keys.Where(id => !known.Contains(id)))
        {
            _lastFullRefresh.TryRemove(gone, out _);
        }

        var due = devices
            .Where(d => d.Status == DeviceStatus.Ok
                && !_pendingRefresh.ContainsKey(d.Id)
                && !(_lastFullRefresh.TryGetValue(d.Id, out var last) && start - last < FullRefreshPause))
            .Select(d => d.Id)
            .ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, RefreshParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(due, options, async (id, token) =>
        {
            try
            {
                await RefreshAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                LogRefreshFailed(ex, id);
            }
        }).ConfigureAwait(false);
        LastFullRound = new PollRoundInfo(due.Count, _time.GetUtcNow() - start, _time.GetUtcNow());
        return due.Count;
    }

    /// <summary>Whether a full refresh of the device waits in the queue (diagnostics and tests).</summary>
    public bool IsRefreshQueued(Guid deviceId) => _pendingRefresh.ContainsKey(deviceId);

    /// <summary>Start time of the last full refresh of a device since the service started, null when none.</summary>
    public DateTimeOffset? LastFullRefresh(Guid deviceId) =>
        _lastFullRefresh.TryGetValue(deviceId, out var last) ? last : null;

    /// <summary>Lightweight status poll of one device. Returns the updated row, null when the device is gone.</summary>
    public async Task<Device?> PollAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false);
        return device is null ? null : await UpdateDeviceAsync(device, full: false, ct).ConfigureAwait(false);
    }

    /// <summary>Full refresh of one device. Returns the updated row, null when the device is gone.</summary>
    public async Task<Device?> RefreshAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            return null;
        }

        // Rounds and the queue together: at most RefreshParallelism full refreshes at once.
        var gate = FullGate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await UpdateDeviceAsync(device, full: true, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim FullGate
    {
        get
        {
            if (Volatile.Read(ref _fullGate) is { } gate)
            {
                return gate;
            }

            var created = new SemaphoreSlim(Math.Max(1, RefreshParallelism));
            var existing = Interlocked.CompareExchange(ref _fullGate, created, null);
            if (existing is not null)
            {
                created.Dispose();
                return existing;
            }

            return created;
        }
    }

    public void Dispose()
    {
        _refreshQueue.Writer.TryComplete();
        _fullGate?.Dispose();
    }

    /// <summary>
    /// Round, then <see cref="RoundPause"/>, then the next round: a round never overlaps the next, and the time between two
    /// polls of a device is the round's length plus the pause (about 61 s for 100 devices, about 90 s for 50,000).
    /// </summary>
    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(RoundPause, _time, ct).ConfigureAwait(false);
            try
            {
                await PollRoundAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogPollFailed(ex);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Full refresh round, then <see cref="FullRefreshPause"/>, then the next round (like the status rounds).</summary>
    private async Task RunFullRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(FullRefreshPause, _time, ct).ConfigureAwait(false);
            try
            {
                var refreshed = await FullRefreshRoundAsync(ct).ConfigureAwait(false);
                LogFullRound(refreshed, LastFullRound?.Duration.TotalSeconds ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogScheduleFailed(ex);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task RunRefreshQueueAsync(CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, RefreshParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(_refreshQueue.Reader.ReadAllAsync(ct), options, async (id, token) =>
        {
            // Stamp before the device leaves the pending set: otherwise a round could see it neither fresh nor pending
            // in the gap until UpdateDeviceAsync stamps it, and refresh it a second time.
            _lastFullRefresh[id] = _time.GetUtcNow();
            _pendingRefresh.TryRemove(id, out _);
            try
            {
                await RefreshAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                LogRefreshFailed(ex, id);
            }
        }).ConfigureAwait(false);
    }

    private async Task<Device?> UpdateDeviceAsync(Device device, bool full, CancellationToken ct)
    {
        if (device.Status == DeviceStatus.SiteOffline)
        {
            return device; // not contacted; a full refresh follows when the site is back after a longer time
        }

        if (full)
        {
            // Stamped at the start so a round does not start a refresh that is already running.
            _lastFullRefresh[device.Id] = _time.GetUtcNow();
        }

        var observation = await ObserveAsync(device, full, ct).ConfigureAwait(false);
        return observation.SiteProblem ? device : await ApplyAsync(device, observation, full, ct).ConfigureAwait(false);
    }

    private async Task<Device?> ApplyAsync(Device device, Observation observation, bool full, CancellationToken ct)
    {
        // The note first, so a watcher that maps the status change already reads it.
        var noteChanged = _notes?.Set(device.Id, observation.Status == DeviceStatus.CredentialsRequired ? observation.Note : null) ?? false;
        var updated = await _devices.UpdateAsync(device.Id, d => observation.ApplyTo(d), ct).ConfigureAwait(false);
        if (updated is not null && noteChanged)
        {
            // The status may stay Credentials required: the watchers still get the device with its new note (as stored).
            await _devices.PublishStoredAsync([device.Id]).ConfigureAwait(false);
        }

        if (updated is not null)
        {
            Oadm.Core.Auth.CallerContext.Raise(DeviceUpdated, this, updated);
            if (!full && device.Status != DeviceStatus.Ok && updated.Status == DeviceStatus.Ok)
            {
                LogBackOnline(device.Id, device.Status);
                QueueRefresh([device.Id]);
            }
            else if (!full && updated.Status == DeviceStatus.Ok && (Changed(device.FirmwareVersion, observation.Firmware) || Changed(device.Model, observation.Model)))
            {
                // A new firmware or model brings another API list: read it now, not at the next scheduled refresh.
                LogFirmwareChanged(device.Id, device.FirmwareVersion ?? "-", observation.Firmware ?? "-");
                QueueRefresh([device.Id]);
            }
        }

        return updated;
    }

    private static bool Changed(string? stored, string? seen) =>
        !string.IsNullOrEmpty(stored) && !string.IsNullOrEmpty(seen) && !string.Equals(stored, seen, StringComparison.Ordinal);

    private async Task<Observation> ObserveAsync(Device device, bool full, CancellationToken ct)
    {
        var observation = new Observation();
        if (full)
        {
            try
            {
                observation.ServerName = (await _settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSettingsFailed(ex);
            }
        }

        VapixClient client;
        try
        {
            client = await _clients.GetClientAsync(device, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Sites.SiteErrors.Find(ex) is not null)
        {
            observation.SiteProblem = true;
            return observation;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Undecryptable credentials or an invalid address: we cannot talk to the device.
            LogClientFailed(ex, device.Id);
            observation.Status = DeviceStatus.Unknown;
            return observation;
        }

        try
        {
            var timed = await client.GetTimedBasicDeviceInfoAsync(full ? null : StatusPollTimeout, _time, ct).ConfigureAwait(false);
            var info = timed.Info;
            observation.Status = DeviceStatus.Ok;
            observation.ClockOffsetMs = timed.OffsetMs; // the Date header of an Ok answer; none keeps the last value
            observation.Model = info.ProdNbr;
            observation.Firmware = info.Version;
            if (!string.IsNullOrWhiteSpace(info.ProdType))
            {
                observation.ProductType = info.ProdType.Trim();
                observation.Category = DeviceCategoryMapper.Map(observation.ProductType, _logger);
            }

            if (!string.IsNullOrWhiteSpace(info.Soc))
            {
                observation.Soc = info.Soc.Trim();
            }

            if (!string.IsNullOrWhiteSpace(info.Architecture))
            {
                observation.Architecture = info.Architecture.Trim();
            }
            observation.LastSeen = _time.GetUtcNow().UtcDateTime;
        }
        catch (Exception ex) when (Sites.SiteErrors.Find(ex) is { } site)
        {
            // A site error (offline, busy, port not allowed) is never a device error: the status stays.
            LogSiteProblem(device.Id, site.Message);
            observation.SiteProblem = true;
            return observation;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            observation.Status = DeviceStatusClassifier.FromException(ex);
            if (observation.Status is DeviceStatus.Unreachable && device.SiteId is { } siteId && _sites is not null
                && !await _sites.LinkAnswersAsync(siteId, ct).ConfigureAwait(false))
            {
                // No answer through a site whose node does not answer either (the link dropped, the session ended): a site
                // problem, never "Unreachable" for every device of the site before it counts as offline.
                LogSiteProblem(device.Id, "the site node does not answer: " + ex.Message);
                observation.SiteProblem = true;
                return observation;
            }

            if (observation.Status is DeviceStatus.CredentialsRequired)
            {
                observation.LastSeen = _time.GetUtcNow().UtcDateTime;
                if (VapixAuthenticationException.Find(ex) is { BasicOnlyOverHttp: true })
                {
                    // The password was not sent: the device offers only Basic over HTTP and the server setting is off.
                    observation.Note = Sdk.Devices.DeviceMessages.BasicOnlyOverHttp;
                }

                if (await IsFactoryDefaultAsync(client, ct).ConfigureAwait(false))
                {
                    observation.Status = DeviceStatus.PasswordNotSet;
                }
            }
            else if (observation.Status is DeviceStatus.CertificateChanged)
            {
                observation.LastSeen = _time.GetUtcNow().UtcDateTime;
            }

            LogDeviceNotOk(device.Id, device.Address, observation.Status, ex.Message);
        }

        if (device.CertFingerprintSha256 is null && observation.Status == DeviceStatus.Ok && client.CertificateFingerprint is { } fingerprint)
        {
            observation.Fingerprint = fingerprint; // trust on first use
        }
        else if (device.CertFingerprintSha256 is not null && observation.Status == DeviceStatus.Ok && client.RenewedCertificateFingerprint is { } renewed)
        {
            observation.RenewedFingerprint = renewed; // an OADM CA renewed it (EST, PKI): pinned again, no Certificate changed
            LogCertificateRenewed(device.Id, device.Address);
        }

        if (full && observation.Status == DeviceStatus.Ok)
        {
            try
            {
                observation.Network = await client.GetNetworkInfoAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogNetworkInfoFailed(device.Id, ex.Message);
            }

            try
            {
                observation.Apis = await client.GetApiListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogApiListFailed(device.Id, ex.Message);
            }

            observation.NtpState = await ReadNtpStateAsync(client, device, observation.Apis ?? device.Apis, ct).ConfigureAwait(false);

            if (device.Scheme == DeviceScheme.Http)
            {
                // Reached over HTTP (e.g. a port forward of port 80 only): no TLS handshake to observe, so the web server's
                // certificate is read over VAPIX instead (cert API, AXIS OS 11.11+); nothing when HTTPS is off or unreadable.
                var certificate = observation.Network?.HttpsEnabled == false ? null : await ReadWebServerCertificateAsync(client, device.Id, ct).ConfigureAwait(false);
                if (certificate is not null)
                {
                    observation.Certificate = certificate;
                    observation.CertificateTrust = certificate.TrustAt(_time.GetUtcNow());
                }
                else
                {
                    observation.ClearCertificate = true;
                }
            }
            else if (client.ObservedCertificate is { } certificate)
            {
                observation.Certificate = certificate;
                observation.CertificateTrust = certificate.TrustAt(_time.GetUtcNow());
            }
        }

        return observation;
    }

    private async Task<CertificateInfo?> ReadWebServerCertificateAsync(VapixClient client, Guid deviceId, CancellationToken ct)
    {
        try
        {
            return await WebServerCertificateReader.ReadAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogWebServerCertificateFailed(deviceId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// getNTPInfo when the API list has <c>ntp</c> 1.x; Unknown without it or when the read fails (never fails the refresh).
    /// </summary>
    private async Task<NtpState> ReadNtpStateAsync(VapixClient client, Device device, IReadOnlyList<Sdk.Vapix.DeviceApi> apis, CancellationToken ct)
    {
        if (Sdk.Vapix.DeviceApiExtensions.FindApi(apis, ClockHealth.NtpApiId, 1) is not { } api)
        {
            return NtpState.Unknown;
        }

        try
        {
            return await client.GetNtpStateAsync(api.Version, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogNtpInfoFailed(device.Id, ex.Message);
            return NtpState.Unknown;
        }
    }

    private static async Task<bool> IsFactoryDefaultAsync(VapixClient client, CancellationToken ct)
    {
        try
        {
            var ready = await client.GetSystemReadyAsync(ct).ConfigureAwait(false);
            return ready.NeedSetup == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} ({Address}) has a certificate an OADM CA renewed; it is pinned now")]
    private partial void LogCertificateRenewed(Guid deviceId, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading server settings failed")]
    private partial void LogSettingsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Device status poll failed")]
    private partial void LogPollFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Full refresh of device {DeviceId} failed")]
    private partial void LogRefreshFailed(Exception ex, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot create a VAPIX client for device {DeviceId}")]
    private partial void LogClientFailed(Exception ex, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId} at {Address}: {Status} ({Reason})")]
    private partial void LogDeviceNotOk(Guid deviceId, string address, DeviceStatus status, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId}: network parameters not readable ({Reason})")]
    private partial void LogNetworkInfoFailed(Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId}: API list not readable ({Reason})")]
    private partial void LogApiListFailed(Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId}: NTP state not readable ({Reason})")]
    private partial void LogNtpInfoFailed(Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId}: web server certificate not readable over HTTP ({Reason})")]
    private partial void LogWebServerCertificateFailed(Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Full refresh round: {Count} devices in {Seconds:0.0} s")]
    private partial void LogFullRound(int count, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "A full refresh round failed")]
    private partial void LogScheduleFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} has a new AXIS OS version or model ({Old} -> {New}), queuing a full refresh")]
    private partial void LogFirmwareChanged(Guid deviceId, string old, string @new);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Most devices do not answer ({Failed} of {Polled}). Check the server's network.")]
    private partial void LogMostDevicesDown(int failed, int polled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Devices answer again ({Answering} of {Polled})")]
    private partial void LogDevicesAnswerAgain(int answering, int polled);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId} not polled: {Problem}")]
    private partial void LogSiteProblem(Guid deviceId, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} is back online (was {Previous}), queuing a full refresh")]
    private partial void LogBackOnline(Guid deviceId, DeviceStatus previous);

    /// <summary>What one poll learned; applied to the freshly loaded row so concurrent edits survive.</summary>
    private sealed class Observation
    {
        public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;

        /// <summary>The site could not carry the request: nothing is applied, the device keeps its status.</summary>
        public bool SiteProblem { get; set; }

        /// <summary>Why Credentials required (<see cref="DeviceStatusNotes"/>); null for a plain refusal.</summary>
        public string? Note { get; set; }

        public string? Model { get; set; }

        public string? Firmware { get; set; }

        public DateTime? LastSeen { get; set; }

        public string? Fingerprint { get; set; }

        /// <summary>A certificate an OADM CA renewed: replaces the pinned fingerprint.</summary>
        public string? RenewedFingerprint { get; set; }

        public NetworkInfo? Network { get; set; }

        /// <summary>apidiscovery list; null when not fetched (status poll) or the request failed.</summary>
        public IReadOnlyList<Sdk.Vapix.DeviceApi>? Apis { get; set; }

        public string? ServerName { get; set; }

        public string? ProductType { get; set; }

        public string? Soc { get; set; }

        public string? Architecture { get; set; }

        /// <summary>From the Date header of an Ok answer; null keeps the stored value.</summary>
        public int? ClockOffsetMs { get; set; }

        /// <summary>From getNTPInfo (full refresh of an Ok device only); null keeps the stored value.</summary>
        public NtpState? NtpState { get; set; }

        public DeviceCategory Category { get; set; }

        public CertificateInfo? Certificate { get; set; }

        public CertificateTrust CertificateTrust { get; set; }

        /// <summary>The device is HTTP only: drop any certificate info from an earlier HTTPS time.</summary>
        public bool ClearCertificate { get; set; }

        public void ApplyTo(Device d)
        {
            d.Status = Status;
            d.Model = Model ?? d.Model;
            d.FirmwareVersion = Firmware ?? d.FirmwareVersion;
            d.LastSeenUtc = LastSeen ?? d.LastSeenUtc;
            d.CertFingerprintSha256 = RenewedFingerprint ?? d.CertFingerprintSha256 ?? Fingerprint;
            d.ServerName = ServerName ?? d.ServerName;
            d.Soc = Soc ?? d.Soc;
            d.Architecture = Architecture ?? d.Architecture;
            d.ClockOffsetMs = ClockHealth.Next(d.ClockOffsetMs, ClockOffsetMs);
            d.NtpState = NtpState ?? d.NtpState;
            if (ProductType is not null)
            {
                d.ProductType = ProductType;
                d.Category = Category;
            }
            if (Network is { } n)
            {
                d.DhcpEnabled = n.DhcpEnabled ?? d.DhcpEnabled;
                d.HttpsEnabled = n.HttpsEnabled ?? d.HttpsEnabled;
                d.Dot1xEnabled = n.Dot1xEnabled ?? d.Dot1xEnabled;
                d.UpnpFriendlyName = n.UpnpFriendlyName ?? d.UpnpFriendlyName;
            }

            if (Apis is { } apis)
            {
                d.Apis = apis;
            }

            if (Certificate is { } c)
            {
                d.CertNotAfterUtc = c.NotAfterUtc;
                d.CertTrust = CertificateTrust;
                d.CertSubject = c.Subject;
                d.CertIssuer = c.Issuer;
                d.CertNameMatches = c.NameMatches;
            }
            else if (ClearCertificate)
            {
                d.CertNotAfterUtc = null;
                d.CertTrust = CertificateTrust.Unknown;
                d.CertSubject = null;
                d.CertIssuer = null;
                d.CertNameMatches = null;
            }
        }
    }
}

/// <summary>A finished status round: how many devices, how long it took, when it ended (the support report).</summary>
public sealed record PollRoundInfo(int Devices, TimeSpan Duration, DateTimeOffset FinishedAt);
