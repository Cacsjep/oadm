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
/// <item>Status poll: every <c>Polling.IntervalSeconds</c> (live-updated from
/// <see cref="ServerSettingsStore.Changed"/>) one basicdeviceinfo call per device with bounded
/// parallelism, updating Status, Model, FirmwareVersion and LastSeenUtc.</item>
/// <item>Full refresh: basicdeviceinfo plus param.cgi network info (DHCP, HTTPS, 802.1X, UPnP
/// name), the HTTPS certificate seen in the TLS handshake (expiry, chain trust, subject, issuer)
/// and the Server column. Queued with <see cref="QueueRefresh"/> (on add, manual Refresh, after a
/// task) or run directly with <see cref="RefreshAsync"/>.</item>
/// <item>Scheduled full refresh: every <c>Polling.FullRefreshMinutes</c> (default 10) per device,
/// checked every <see cref="FullRefreshCheckPeriod"/>, and immediately when a status poll sees a
/// device go from a non-Ok status back to Ok. Both go through the deduplicating refresh queue,
/// so parallelism stays bounded.</item>
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
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<Guid> _refreshQueue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });
    private readonly ConcurrentDictionary<Guid, byte> _pendingRefresh = new();
    private readonly Lock _wakeSync = new();
    private CancellationTokenSource _wake = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFullRefresh = new();
    private readonly DateTimeOffset _startedAt;
    private int _intervalSeconds = ServerSettings.DefaultPollingIntervalSeconds;
    private int _fullRefreshMinutes = ServerSettings.DefaultFullRefreshMinutes;

    public DevicePollingService(
        DeviceRepository devices,
        VapixClientFactory clients,
        ServerSettingsStore settings,
        TimeProvider? timeProvider = null,
        ILogger<DevicePollingService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(settings);
        _devices = devices;
        _clients = clients;
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _startedAt = _time.GetUtcNow();
        _settings.Changed += OnSettingChanged;
    }

    /// <summary>Devices polled at the same time. Default 16.</summary>
    public int MaxParallelism { get; init; } = 16;

    /// <summary>Current poll interval in seconds.</summary>
    public int IntervalSeconds => Volatile.Read(ref _intervalSeconds);

    /// <summary>Current scheduled full refresh interval in minutes.</summary>
    public int FullRefreshMinutes => Volatile.Read(ref _fullRefreshMinutes);

    /// <summary>How often the scheduler looks for devices whose full refresh is due. Default 30 s.</summary>
    public TimeSpan FullRefreshCheckPeriod { get; init; } = TimeSpan.FromSeconds(30);

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
    /// Runs the poll loop and the refresh queue until <paramref name="ct"/> is cancelled. The first
    /// poll runs after one interval (devices get a full refresh when added).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _intervalSeconds, settings.PollingIntervalSeconds);
            Volatile.Write(ref _fullRefreshMinutes, settings.FullRefreshMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSettingsFailed(ex);
        }

        var worker = RunRefreshQueueAsync(ct);
        var poller = RunPollLoopAsync(ct);
        var scheduler = RunFullRefreshScheduleAsync(ct);
        try
        {
            await Task.WhenAll(worker, poller, scheduler).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One status poll of every managed device, with bounded parallelism.</summary>
    public async Task PollAllAsync(CancellationToken ct)
    {
        var devices = await _devices.ListDevicesAsync(ct).ConfigureAwait(false);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(devices, options, async (device, token) =>
        {
            await UpdateDeviceAsync(device, full: false, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a full refresh for every device whose last full refresh (or, when it had none since
    /// the service started, the service start) is at least <see cref="FullRefreshMinutes"/> ago.
    /// Returns the queued device ids.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> QueueDueFullRefreshesAsync(CancellationToken ct)
    {
        var devices = await _devices.ListDevicesAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var interval = TimeSpan.FromMinutes(FullRefreshMinutes);
        var known = devices.Select(d => d.Id).ToHashSet();
        foreach (var gone in _lastFullRefresh.Keys.Where(id => !known.Contains(id)))
        {
            _lastFullRefresh.TryRemove(gone, out _);
        }

        var due = devices
            .Where(d => (_lastFullRefresh.TryGetValue(d.Id, out var last) ? last : _startedAt) + interval <= now)
            .Select(d => d.Id)
            .ToList();
        QueueRefresh(due);
        return due;
    }

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
        return device is null ? null : await UpdateDeviceAsync(device, full: true, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingChanged;
        _refreshQueue.Writer.TryComplete();
        lock (_wakeSync)
        {
            _wake.Dispose();
        }
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CancellationToken wake;
            lock (_wakeSync)
            {
                wake = _wake.Token;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct, wake);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), _time, delayCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // interval changed: start waiting again with the new value
            }

            try
            {
                await PollAllAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogPollFailed(ex);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task RunFullRefreshScheduleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(FullRefreshCheckPeriod, _time, ct).ConfigureAwait(false);
            try
            {
                var queued = await QueueDueFullRefreshesAsync(ct).ConfigureAwait(false);
                if (queued.Count > 0)
                {
                    LogScheduledFullRefresh(queued.Count);
                }
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
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism / 2), CancellationToken = ct };
        await Parallel.ForEachAsync(_refreshQueue.Reader.ReadAllAsync(ct), options, async (id, token) =>
        {
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
        if (full)
        {
            // Stamped at the start so the scheduler does not queue a refresh that is already running.
            _lastFullRefresh[device.Id] = _time.GetUtcNow();
        }

        var observation = await ObserveAsync(device, full, ct).ConfigureAwait(false);
        var updated = await _devices.UpdateAsync(device.Id, d => observation.ApplyTo(d), ct).ConfigureAwait(false);

        if (updated is not null)
        {
            DeviceUpdated?.Invoke(this, updated);
            if (!full && device.Status != DeviceStatus.Ok && updated.Status == DeviceStatus.Ok)
            {
                LogBackOnline(device.Id, device.Status);
                QueueRefresh([device.Id]);
            }
        }

        return updated;
    }

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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Undecryptable credentials or an invalid address: we cannot talk to the device.
            LogClientFailed(ex, device.Id);
            observation.Status = DeviceStatus.Unknown;
            return observation;
        }

        try
        {
            var info = await client.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            observation.Status = DeviceStatus.Ok;
            observation.Model = info.ProdNbr;
            observation.Firmware = info.Version;
            if (!string.IsNullOrWhiteSpace(info.ProdType))
            {
                observation.ProductType = info.ProdType.Trim();
                observation.Category = DeviceCategoryMapper.Map(observation.ProductType, _logger);
            }
            observation.LastSeen = _time.GetUtcNow().UtcDateTime;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            observation.Status = DeviceStatusClassifier.FromException(ex);
            if (observation.Status is DeviceStatus.CredentialsRequired)
            {
                observation.LastSeen = _time.GetUtcNow().UtcDateTime;
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

            if (device.Scheme == DeviceScheme.Http)
            {
                observation.ClearCertificate = true;
            }
            else if (client.ObservedCertificate is { } certificate)
            {
                observation.Certificate = certificate;
                observation.CertificateTrust = certificate.TrustAt(_time.GetUtcNow());
            }
        }

        return observation;
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

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key == SettingKeys.PollingFullRefreshMinutes)
        {
            OnFullRefreshMinutesChanged(e.ValueJson);
            return;
        }

        if (e.Key != SettingKeys.PollingIntervalSeconds)
        {
            return;
        }

        var seconds = ServerSettings.DefaultPollingIntervalSeconds;
        if (e.ValueJson is not null)
        {
            try
            {
                seconds = JsonSerializer.Deserialize<int>(e.ValueJson);
            }
            catch (JsonException)
            {
                return;
            }
        }

        if (seconds < 1 || Interlocked.Exchange(ref _intervalSeconds, seconds) == seconds)
        {
            return;
        }

        LogIntervalChanged(seconds);
        lock (_wakeSync)
        {
            var old = _wake;
            _wake = new CancellationTokenSource();
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposed during shutdown.
            }

            old.Dispose();
        }
    }

    private void OnFullRefreshMinutesChanged(string? valueJson)
    {
        var minutes = ServerSettings.DefaultFullRefreshMinutes;
        if (valueJson is not null)
        {
            try
            {
                minutes = JsonSerializer.Deserialize<int>(valueJson);
            }
            catch (JsonException)
            {
                return;
            }
        }

        if (minutes is >= ServerSettings.MinFullRefreshMinutes and <= ServerSettings.MaxFullRefreshMinutes
            && Interlocked.Exchange(ref _fullRefreshMinutes, minutes) != minutes)
        {
            LogFullRefreshChanged(minutes);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Polling interval changed to {Seconds} s")]
    private partial void LogIntervalChanged(int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Full refresh interval changed to {Minutes} min")]
    private partial void LogFullRefreshChanged(int minutes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scheduled full refresh queued for {Count} devices")]
    private partial void LogScheduledFullRefresh(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduling the periodic full refresh failed")]
    private partial void LogScheduleFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} is back online (was {Previous}), queuing a full refresh")]
    private partial void LogBackOnline(Guid deviceId, DeviceStatus previous);

    /// <summary>What one poll learned; applied to the freshly loaded row so concurrent edits survive.</summary>
    private sealed class Observation
    {
        public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;

        public string? Model { get; set; }

        public string? Firmware { get; set; }

        public DateTime? LastSeen { get; set; }

        public string? Fingerprint { get; set; }

        public NetworkInfo? Network { get; set; }

        public string? ServerName { get; set; }

        public string? ProductType { get; set; }

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
            d.CertFingerprintSha256 ??= Fingerprint;
            d.ServerName = ServerName ?? d.ServerName;
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
