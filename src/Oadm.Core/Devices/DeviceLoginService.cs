using System.Collections.Concurrent;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>Result of a "Log in" for one managed device.</summary>
/// <param name="Rejected">The device answered and rejected the user name or password.</param>
/// <param name="Message">User facing reason when not <paramref name="Ok"/>.</param>
public sealed record DeviceLoginResult(Guid DeviceId, bool Ok, bool Rejected = false, string? Message = null);

/// <summary>
/// "Log in" for managed devices whose stored credentials are rejected (status Credentials required): tries one
/// credential the technician typed on every given device (stored address, scheme and pinned certificate,
/// authenticated basicdeviceinfo; the serial number must be the record's), with bounded parallelism. The devices that
/// accept it get it stored (one transaction), their cached client is dropped and a full refresh is queued, which sets
/// the status back to Ok through the normal path. The password is never logged.
/// </summary>
public sealed partial class DeviceLoginService
{
    /// <summary>The user facing text of a rejected login (same as the add page).</summary>
    public const string WrongLoginMessage = "The user name or password is wrong.";

    public const string OtherDeviceMessage = "Another device answers at this address.";

    public const string CertificateChangedMessage = DeviceMessages.CertificateChanged;

    public const string NotManagedMessage = DeviceMessages.Removed;

    private readonly DeviceRepository _devices;
    private readonly CredentialStore _credentials;
    private readonly VapixClientFactory _clients;
    private readonly Action<IReadOnlyCollection<Guid>> _queueRefresh;
    private readonly ILogger _logger;
    private readonly Lock _peakLock = new();
    private int _inFlight;

    public DeviceLoginService(DeviceRepository devices, CredentialStore credentials, VapixClientFactory clients, DevicePollingService polling, ILogger<DeviceLoginService>? logger = null)
        : this(devices, credentials, clients, ids => (polling ?? throw new ArgumentNullException(nameof(polling))).QueueRefresh(ids), logger)
    {
    }

    public DeviceLoginService(DeviceRepository devices, CredentialStore credentials, VapixClientFactory clients, Action<IReadOnlyCollection<Guid>> queueRefresh, ILogger<DeviceLoginService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(queueRefresh);
        _devices = devices;
        _credentials = credentials;
        _clients = clients;
        _queueRefresh = queueRefresh;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Devices logged in to at the same time. Default 8.</summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>Timeout of one device's check. Default 15 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Most device checks that ran at the same time since the start (tests).</summary>
    public int PeakParallelism { get; private set; }

    /// <summary>
    /// Tries <paramref name="userName"/> / <paramref name="password"/> on every device of <paramref name="deviceIds"/>
    /// (duplicates once) and stores it for those that accept it. Results in the order of the distinct ids.
    /// </summary>
    public async Task<IReadOnlyList<DeviceLoginResult>> LogInAsync(IReadOnlyCollection<Guid> deviceIds, string userName, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var user = userName.Trim();
        var ids = deviceIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // One read of the device table for the whole selection (5,000 ids: one query, then O(1) lookups).
        var wanted = ids.ToHashSet();
        var rows = (await _devices.ListDevicesAsync(ct).ConfigureAwait(false))
            .Where(d => wanted.Contains(d.Id))
            .ToDictionary(d => d.Id);

        var results = new ConcurrentDictionary<Guid, DeviceLoginResult>();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct },
            async (id, token) =>
            {
                results[id] = rows.TryGetValue(id, out var device)
                    ? await TryOneAsync(device, user, password, token).ConfigureAwait(false)
                    : new DeviceLoginResult(id, false, Message: NotManagedMessage);
            }).ConfigureAwait(false);

        var accepted = ids.Where(id => results[id].Ok).ToList();
        if (accepted.Count > 0)
        {
            var stored = await StoreAsync(accepted, user, password, ct).ConfigureAwait(false);
            foreach (var id in accepted.Where(id => !stored.Contains(id)))
            {
                results[id] = new DeviceLoginResult(id, false, Message: NotManagedMessage);
            }

            foreach (var id in stored)
            {
                _clients.Invalidate(id);
            }

            _queueRefresh(stored);
        }

        var ordered = ids.Select(id => results[id]).ToList();
        var okCount = ordered.Count(r => r.Ok);
        LogLogin(ordered.Count, okCount, user);
        return ordered;
    }

    /// <summary>The stored user name these devices share, or null when they have none or different ones.</summary>
    public async Task<string?> SharedUserNameAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return null;
        }

        var names = await _credentials.ListUserNamesAsync(deviceIds, ct).ConfigureAwait(false);
        var distinct = deviceIds.Distinct().Select(names.GetValueOrDefault).Distinct(StringComparer.Ordinal).ToList();
        return distinct is [{ } single] ? single : null;
    }

    private async Task<HashSet<Guid>> StoreAsync(List<Guid> accepted, string user, string password, CancellationToken ct)
    {
        try
        {
            // One transaction for every device that accepted the login.
            await _credentials.SetManyAsync(accepted, user, password, ct).ConfigureAwait(false);
            return [.. accepted];
        }
        catch (KeyNotFoundException)
        {
            // A device was removed meanwhile: store the others one by one.
            var stored = new HashSet<Guid>();
            foreach (var id in accepted)
            {
                try
                {
                    await _credentials.SetAsync(id, user, password, ct).ConfigureAwait(false);
                    stored.Add(id);
                }
                catch (KeyNotFoundException)
                {
                    // Gone.
                }
            }

            return stored;
        }
    }

    private async Task<DeviceLoginResult> TryOneAsync(Device device, string user, string password, CancellationToken ct)
    {
        if (device.Status == DeviceStatus.CertificateChanged)
        {
            return new DeviceLoginResult(device.Id, false, Message: CertificateChangedMessage);
        }

        var now = Interlocked.Increment(ref _inFlight);
        lock (_peakLock)
        {
            PeakParallelism = Math.Max(PeakParallelism, now);
        }

        try
        {
            using var client = _clients.CreateWithCredentials(device, user, password);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var info = await client.GetBasicDeviceInfoAsync(timeout.Token).ConfigureAwait(false);
            if (!DeviceSerial.TryNormalize(info.SerialNumber, out var serial) || !string.Equals(serial, device.Serial, StringComparison.Ordinal))
            {
                LogOtherDevice(device.Serial, device.Address, info.SerialNumber);
                return new DeviceLoginResult(device.Id, false, Message: OtherDeviceMessage);
            }

            return new DeviceLoginResult(device.Id, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeviceLoginResult(device.Id, false, Message: "Unreachable - the device did not answer in time");
        }
#pragma warning disable CA1031 // Every device error becomes this device's result; the other devices go on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var result = Classify(device.Id, ex);
            LogFailed(device.Serial, device.Address, result.Message ?? ex.Message);
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>The user facing result of a failed check.</summary>
    internal static DeviceLoginResult Classify(Guid deviceId, Exception ex)
    {
        switch (DeviceStatusClassifier.FromException(ex))
        {
            case DeviceStatus.CredentialsRequired:
                return new DeviceLoginResult(deviceId, false, Rejected: true, Message: WrongLoginMessage);
            case DeviceStatus.CertificateChanged:
                return new DeviceLoginResult(deviceId, false, Message: CertificateChangedMessage);
            case DeviceStatus.PasswordNotSet:
                return new DeviceLoginResult(deviceId, false, Message: "The device has no password yet (factory default).");
            case DeviceStatus.Unreachable:
                return new DeviceLoginResult(deviceId, false, Message: "Unreachable - " + Innermost(ex));
            default:
                return new DeviceLoginResult(deviceId, false, Message: "The login could not be checked: " + Innermost(ex));
        }
    }

    private static string Innermost(Exception ex)
    {
        var socket = ex as SocketException;
        for (var e = ex.InnerException; socket is null && e is not null; e = e.InnerException)
        {
            socket = e as SocketException;
        }

        return (socket?.Message ?? ex.Message).TrimEnd('.');
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device login as {UserName}: {Ok} of {Count} device(s) accepted it")]
    private partial void LogLogin(int count, int ok, string userName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device login on {Serial} at {Address} failed: {Reason}")]
    private partial void LogFailed(string serial, string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device login on {Serial}: the device at {Address} has serial number {OtherSerial}")]
    private partial void LogOtherDevice(string serial, string address, string otherSerial);
}
