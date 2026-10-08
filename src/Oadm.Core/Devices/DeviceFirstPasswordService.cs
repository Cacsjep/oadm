using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>Result of "Set password" for one managed device.</summary>
/// <param name="Message">User facing reason when not <paramref name="Ok"/>.</param>
public sealed record DeviceFirstPasswordResult(Guid DeviceId, bool Ok, string? Message = null);

/// <summary>The passphrase policy a factory-default device reports in systemready ("none", "length", "complex").</summary>
/// <param name="Policy">Null when it could not be read.</param>
public sealed record DevicePassphrasePolicy(Guid DeviceId, string? Policy);

/// <summary>
/// "Set password" (device context menu) for managed devices in factory default (status Password not set): sets the first
/// root password exactly like the add page (pwdgrp.cgi add, user root, POST body only, HTTPS when the device offers it)
/// with bounded parallelism. Device safety: right before writing, the device at the stored address must identify itself
/// anonymously with the record's serial number and report <c>needsetup=yes</c> in systemready; otherwise nothing is sent.
/// The devices that took the password get it stored as their credential (one transaction), their cached client dropped
/// and a full refresh queued. The password is never logged.
/// </summary>
public sealed partial class DeviceFirstPasswordService
{
    public const string AlreadySetMessage = "The device already has a password. Nothing was changed.";

    private readonly DeviceRepository _devices;
    private readonly CredentialStore _credentials;
    private readonly VapixClientFactory _clients;
    private readonly IVapixConnector _connector;
    private readonly Action<IReadOnlyCollection<Guid>> _queueRefresh;
    private readonly ILogger _logger;
    private readonly Lock _peakLock = new();
    private int _inFlight;

    public DeviceFirstPasswordService(DeviceRepository devices, CredentialStore credentials, VapixClientFactory clients, IVapixConnector connector, DevicePollingService polling, ILogger<DeviceFirstPasswordService>? logger = null)
        : this(devices, credentials, clients, connector, ids => (polling ?? throw new ArgumentNullException(nameof(polling))).QueueRefresh(ids), logger)
    {
    }

    public DeviceFirstPasswordService(DeviceRepository devices, CredentialStore credentials, VapixClientFactory clients, IVapixConnector connector, Action<IReadOnlyCollection<Guid>> queueRefresh, ILogger<DeviceFirstPasswordService>? logger = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _queueRefresh = queueRefresh ?? throw new ArgumentNullException(nameof(queueRefresh));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Devices handled at the same time. Default 8.</summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>Timeout of one request. Default 15 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Most devices handled at the same time since the start (tests).</summary>
    public int PeakParallelism { get; private set; }

    /// <summary>
    /// Sets <paramref name="password"/> as the first root password on every device of <paramref name="deviceIds"/>
    /// (duplicates once). Results in the order of the distinct ids.
    /// </summary>
    /// <exception cref="ArgumentException">The password is not 1-64 printable ASCII characters.</exception>
    public async Task<IReadOnlyList<DeviceFirstPasswordResult>> SetAsync(IReadOnlyCollection<Guid> deviceIds, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        VapixClient.ValidatePassword(password);
        var ids = deviceIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await RowsAsync(ids, ct).ConfigureAwait(false);
        var results = new ConcurrentDictionary<Guid, DeviceFirstPasswordResult>();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct },
            async (id, token) =>
            {
                results[id] = rows.TryGetValue(id, out var device)
                    ? await SetOneAsync(device, password, token).ConfigureAwait(false)
                    : new DeviceFirstPasswordResult(id, false, DeviceLoginService.NotManagedMessage);
            }).ConfigureAwait(false);

        var accepted = ids.Where(id => results[id].Ok).ToList();
        if (accepted.Count > 0)
        {
            try
            {
                await _credentials.SetManyAsync(accepted, "root", password, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                foreach (var id in accepted)
                {
                    try
                    {
                        await _credentials.SetAsync(id, "root", password, ct).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException)
                    {
                        // Removed meanwhile: the device has its password, OADM no longer manages it.
                    }
                }
            }

            foreach (var id in accepted)
            {
                _clients.Invalidate(id);
            }

            _queueRefresh(accepted);
        }

        var ordered = ids.Select(id => results[id]).ToList();
        LogSet(ordered.Count, accepted.Count);
        return ordered;
    }

    /// <summary>The passphrase policy of every device (anonymous systemready, bounded parallelism); null where unreadable.</summary>
    public async Task<IReadOnlyList<DevicePassphrasePolicy>> ReadPoliciesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var ids = deviceIds.Distinct().ToList();
        var rows = await RowsAsync(ids, ct).ConfigureAwait(false);
        var results = new ConcurrentDictionary<Guid, string?>();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct },
            async (id, token) =>
            {
                string? policy = null;
                if (rows.TryGetValue(id, out var device) && device.Status != DeviceStatus.CertificateChanged)
                {
                    foreach (var (scheme, pin) in Schemes(device))
                    {
                        try
                        {
                            using var client = Anonymous(device, scheme, pin);
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(Timeout);
                            var ready = await client.GetSystemReadyAsync(timeout.Token).ConfigureAwait(false);
                            policy = string.IsNullOrWhiteSpace(ready.PassphrasePolicy) ? null : ready.PassphrasePolicy.Trim().ToLowerInvariant();
                            break;
                        }
#pragma warning disable CA1031 // Only a hint: the device checks the password itself.
                        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
#pragma warning restore CA1031
                        {
                            LogPolicyFailed(device.Serial, ex.Message);
                        }
                    }
                }

                results[id] = policy;
            }).ConfigureAwait(false);
        return [.. ids.Select(id => new DevicePassphrasePolicy(id, results.GetValueOrDefault(id)))];
    }

    private async Task<Dictionary<Guid, Device>> RowsAsync(List<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet();
        return (await _devices.ListDevicesAsync(ct).ConfigureAwait(false))
            .Where(d => wanted.Contains(d.Id))
            .ToDictionary(d => d.Id);
    }

    /// <summary>HTTPS when the device offers it: a device stored with HTTPS keeps its pin; one stored with HTTP is asked over HTTPS first.</summary>
    private static IEnumerable<(string Scheme, string? Pin)> Schemes(Device device) =>
        device.Scheme == DeviceScheme.Https
            ? [(Uri.UriSchemeHttps, device.CertFingerprintSha256)]
            : [(Uri.UriSchemeHttps, null), (Uri.UriSchemeHttp, null)];

    private VapixClient Anonymous(Device device, string scheme, string? pin) => _connector.Connect(new VapixConnectionOptions
    {
        Address = device.Address,
        Scheme = scheme,
        PinnedCertificateFingerprint = pin,
        Timeout = Timeout,
    });

    private async Task<DeviceFirstPasswordResult> SetOneAsync(Device device, string password, CancellationToken ct)
    {
        if (device.Status == DeviceStatus.CertificateChanged)
        {
            return new DeviceFirstPasswordResult(device.Id, false, DeviceLoginService.CertificateChangedMessage);
        }

        var now = Interlocked.Increment(ref _inFlight);
        lock (_peakLock)
        {
            PeakParallelism = Math.Max(PeakParallelism, now);
        }

        try
        {
            Exception? last = null;
            foreach (var (scheme, pin) in Schemes(device))
            {
                using var client = Anonymous(device, scheme, pin);
                SystemReadyInfo ready;
                try
                {
                    // Same device? The anonymous Axis answer must carry the record's serial number.
                    var properties = await client.GetUnrestrictedPropertiesAsync(ct).ConfigureAwait(false);
                    if (!DeviceSerial.TryNormalize(properties.GetValueOrDefault("SerialNumber"), out var serial) || serial != device.Serial)
                    {
                        LogOtherDevice(device.Serial, device.Address);
                        return new DeviceFirstPasswordResult(device.Id, false, DeviceLoginService.OtherDeviceMessage + " Nothing was changed.");
                    }

                    ready = await client.GetSystemReadyAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransport(ex, ct))
                {
                    last = ex;
                    continue; // e.g. HTTPS closed: try HTTP
                }

                if (ready.NeedSetup != true)
                {
                    LogAlreadySet(device.Serial, device.Address);
                    return new DeviceFirstPasswordResult(device.Id, false, AlreadySetMessage);
                }

                try
                {
                    await client.SetInitialRootPasswordAsync(password, ct, allowPlainHttp: scheme == Uri.UriSchemeHttp).ConfigureAwait(false);
                }
                catch (VapixException ex)
                {
                    var reason = ex.Message.Replace("pwdgrp.cgi refused the initial password: ", string.Empty, StringComparison.Ordinal);
                    LogRefused(device.Serial, reason);
                    return new DeviceFirstPasswordResult(device.Id, false, "The device rejected the password: " + reason.TrimEnd('.') + ".");
                }
                catch (InvalidOperationException)
                {
                    // systemready changed between the two reads: SetInitialRootPasswordAsync checks again and sends nothing.
                    return new DeviceFirstPasswordResult(device.Id, false, AlreadySetMessage);
                }

                LogPasswordSet(device.Serial, device.Address, scheme);
                return new DeviceFirstPasswordResult(device.Id, true);
            }

            var result = new DeviceFirstPasswordResult(
                device.Id,
                false,
                last is null ? "Unreachable - the device did not answer" : DeviceLoginService.Classify(device.Id, last).Message);
            LogFailed(device.Serial, device.Address, result.Message ?? "-");
            return result;
        }
#pragma warning disable CA1031 // Every device error becomes this device's result; the other devices go on.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            var message = DeviceLoginService.Classify(device.Id, ex).Message;
            LogFailed(device.Serial, device.Address, message ?? ex.Message);
            return new DeviceFirstPasswordResult(device.Id, false, message);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static bool IsTransport(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException or TimeoutException or CertificateChangedException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set password: {Ok} of {Count} device(s) got their first password")]
    private partial void LogSet(int count, int ok);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set password on {Serial} at {Address} over {Scheme}: done")]
    private partial void LogPasswordSet(string serial, string address, string scheme);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set password on {Serial} at {Address}: the device already has a password, nothing sent")]
    private partial void LogAlreadySet(string serial, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Set password on {Serial}: the device at {Address} has another serial number, nothing sent")]
    private partial void LogOtherDevice(string serial, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set password on {Serial}: the device refused the password: {Reason}")]
    private partial void LogRefused(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set password on {Serial} at {Address} failed: {Reason}")]
    private partial void LogFailed(string serial, string address, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Passphrase policy of {Serial} not read: {Reason}")]
    private partial void LogPolicyFailed(string serial, string reason);
}
