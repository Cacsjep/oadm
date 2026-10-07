using System.Collections.Concurrent;
using System.Net;

using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Core.Vapix;
using Oadm.Server.Common;
using Oadm.Server.Discovery;
using Oadm.Server.Mapping;
using Oadm.Server.Tasks;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.AddDevices;

/// <summary>
/// gRPC AddDevicesService, the server side of the ADM add wizard.
/// <para><b>Prepare</b> probes every selected device anonymously (<see cref="VapixProbe"/>) and
/// classifies it: factory default needs the initial password, everything else needs credentials.
/// Already managed serials are left out.</para>
/// <para><b>Commit</b> stores each device (serial unique, host name or IP as address), pins the
/// certificate fingerprint from the probe, sets the initial root password on factory-default
/// devices when one is given (over HTTPS when the device offers it), stores credentials (initial
/// password as root, else the per-device entry, else the entry with an empty discovered_id),
/// verifies them with one authenticated call (wrong credentials still add the device, status
/// CredentialsRequired) and finally starts the "Add devices" task, which runs the first full refresh.</para>
/// </summary>
public sealed partial class AddDevicesGrpcService(
    IDiscoveryBackend discovery,
    DeviceRepository devices,
    CredentialStore credentials,
    VapixClientFactory clients,
    IVapixConnector connector,
    VapixProbe probe,
    ServerSettingsStore settings,
    TaskEngine engine,
    ILogger<AddDevicesGrpcService> logger) : Proto.AddDevicesService.AddDevicesServiceBase
{
    /// <summary>Devices handled at the same time during Prepare and Commit.</summary>
    public const int Parallelism = 8;

    public override async Task<Proto.AddPlan> Prepare(Proto.PrepareRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var selected = await ResolveAsync(request.SessionId, request.DiscoveredIds, ct).ConfigureAwait(false);
        var items = new ConcurrentDictionary<string, Proto.AddPlanItem>();
        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (found, token) =>
        {
            var probed = await TryProbeAsync(found, token).ConfigureAwait(false);
            var factoryDefault = IsFactoryDefault(found, probed);
            items[found.DiscoveredId] = new Proto.AddPlanItem
            {
                DiscoveredId = found.DiscoveredId,
                Serial = found.Serial,
                Address = found.Address.ToString(),
                HostName = found.HostName ?? string.Empty,
                Model = probed?.Model ?? found.Model ?? string.Empty,
                NeedsInitialPassword = factoryDefault,
                NeedsCredentials = !factoryDefault,
            };
        }).ConfigureAwait(false);

        var plan = new Proto.AddPlan();
        plan.Items.AddRange(selected.Select(s => items[s.DiscoveredId]));
        return plan;
    }

    public override async Task<Proto.CommitReply> Commit(Proto.CommitRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var initialPassword = string.IsNullOrEmpty(request.InitialRootPassword) ? null : request.InitialRootPassword;
        if (initialPassword is not null)
        {
            try
            {
                VapixClient.ValidatePassword(initialPassword);
            }
            catch (ArgumentException ex)
            {
                throw GrpcGuard.InvalidArgument(ex.Message);
            }
        }

        var selected = await ResolveAsync(request.SessionId, request.DiscoveredIds, ct).ConfigureAwait(false);
        var serverName = (await settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName;
        var added = new ConcurrentDictionary<string, Guid>();

        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (found, token) =>
        {
            var deviceCredentials = PickCredentials(request.Credentials, found.DiscoveredId);
            var id = await AddOneAsync(found, request.UseHostName, initialPassword, deviceCredentials, serverName, token).ConfigureAwait(false);
            if (id is { } deviceId)
            {
                added[found.DiscoveredId] = deviceId;
            }
        }).ConfigureAwait(false);

        var reply = new Proto.CommitReply();
        var ids = selected.Where(s => added.ContainsKey(s.DiscoveredId)).Select(s => added[s.DiscoveredId]).ToArray();
        reply.DeviceIds.AddRange(ids.Select(i => i.ToString()));
        if (ids.Length > 0)
        {
            var taskId = await engine.RunAsync(AddDevicesTaskPlugin.PluginId, ids, null, GrpcGuard.Owner(context), ct).ConfigureAwait(false);
            reply.TaskId = taskId.ToString();
        }

        LogCommitted(ids.Length, selected.Count);
        return reply;
    }

    /// <summary>Per-device credentials override the default entry (empty discovered_id).</summary>
    internal static Proto.DeviceCredentials? PickCredentials(IEnumerable<Proto.DeviceCredentials> all, string discoveredId)
    {
        var usable = all.Where(c => !string.IsNullOrWhiteSpace(c.UserName)).ToList();
        return usable.FirstOrDefault(c => string.Equals(c.DiscoveredId, discoveredId, StringComparison.OrdinalIgnoreCase))
            ?? usable.FirstOrDefault(c => string.IsNullOrEmpty(c.DiscoveredId));
    }

    /// <summary>The connection address: the host name when asked for and known, else the IP.</summary>
    internal static (string Address, string? HostName) ChooseAddress(DiscoveredDevice found, bool useHostName)
    {
        var hostName = found.HostName;
        if (!string.IsNullOrWhiteSpace(hostName) && !hostName.Contains('.', StringComparison.Ordinal))
        {
            hostName += ".local"; // mDNS names come without the domain
        }

        var address = useHostName && !string.IsNullOrWhiteSpace(hostName) ? hostName : found.Address.ToString();
        return (address, string.IsNullOrWhiteSpace(hostName) ? null : hostName);
    }

    private async Task<Guid?> AddOneAsync(
        DiscoveredDevice found,
        bool useHostName,
        string? initialPassword,
        Proto.DeviceCredentials? deviceCredentials,
        string serverName,
        CancellationToken ct)
    {
        if (await devices.FindBySerialAsync(found.Serial, ct).ConfigureAwait(false) is not null)
        {
            LogSkippedManaged(found.Serial);
            return null;
        }

        var probed = await TryProbeAsync(found, ct).ConfigureAwait(false);
        var (address, hostName) = ChooseAddress(found, useHostName);
        var scheme = probed?.Scheme ?? found.Scheme ?? Uri.UriSchemeHttps;
        var factoryDefault = IsFactoryDefault(found, probed);

        Device device;
        try
        {
            device = await devices.AddAsync(
                new Device
                {
                    Serial = found.Serial,
                    Address = address,
                    UseHostName = useHostName,
                    HostName = hostName,
                    Model = probed?.Model ?? found.Model,
                    FirmwareVersion = probed?.FirmwareVersion ?? found.FirmwareVersion,
                    ProductType = probed?.ProductType ?? found.ProductType,
                    Category = DeviceCategoryMapper.Map(probed?.ProductType ?? found.ProductType),
                    Scheme = Mappers.ToDeviceScheme(scheme),
                    CertFingerprintSha256 = scheme == Uri.UriSchemeHttps ? probed?.CertificateFingerprint : null,
                    ServerName = serverName,
                    Status = probed is null ? SdkDeviceStatus.Unreachable : SdkDeviceStatus.Unknown,
                },
                ct).ConfigureAwait(false);
        }
        catch (DuplicateDeviceException)
        {
            LogSkippedManaged(found.Serial);
            return null;
        }

        SdkDeviceStatus status;
        if (factoryDefault)
        {
            status = initialPassword is null
                ? SdkDeviceStatus.PasswordNotSet
                : await SetInitialPasswordAsync(device, initialPassword, ct).ConfigureAwait(false);
        }
        else if (deviceCredentials is not null)
        {
            await credentials.SetAsync(device.Id, deviceCredentials.UserName.Trim(), deviceCredentials.Password, ct).ConfigureAwait(false);
            status = await VerifyAsync(device.Id, ct).ConfigureAwait(false);
        }
        else
        {
            status = probed is null ? SdkDeviceStatus.Unreachable : SdkDeviceStatus.CredentialsRequired;
        }

        await devices.UpdateAsync(device.Id, d => d.Status = status, ct).ConfigureAwait(false);
        LogAdded(device.Serial, device.Address, status);
        return device.Id;
    }

    private async Task<SdkDeviceStatus> SetInitialPasswordAsync(Device device, string password, CancellationToken ct)
    {
        var scheme = Mappers.ToSchemeString(device.Scheme);
        try
        {
            using (var anonymous = connector.Connect(new VapixConnectionOptions
            {
                Address = device.Address,
                Scheme = scheme,
                PinnedCertificateFingerprint = device.CertFingerprintSha256,
            }))
            {
                // HTTPS when the device offers it (the probe tries it first); plain HTTP only as a fallback.
                await anonymous.SetInitialRootPasswordAsync(password, ct, allowPlainHttp: scheme == Uri.UriSchemeHttp).ConfigureAwait(false);
            }

            await credentials.SetAsync(device.Id, "root", password, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogInitialPasswordFailed(device.Serial, ex.Message);
            var status = DeviceStatusClassifier.FromException(ex);
            return status is SdkDeviceStatus.Unreachable or SdkDeviceStatus.CertificateChanged ? status : SdkDeviceStatus.PasswordNotSet;
        }

        return await VerifyAsync(device.Id, ct).ConfigureAwait(false);
    }

    /// <summary>One authenticated call with the stored credentials.</summary>
    private async Task<SdkDeviceStatus> VerifyAsync(Guid deviceId, CancellationToken ct)
    {
        try
        {
            var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
            await client.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            return SdkDeviceStatus.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DeviceStatusClassifier.FromException(ex);
        }
    }

    private async Task<VapixProbeResult?> TryProbeAsync(DiscoveredDevice found, CancellationToken ct)
    {
        try
        {
            var result = await probe.ProbeAsync(found.Address.ToString(), ct).ConfigureAwait(false);
            if (result is not null && !string.Equals(result.Serial, found.Serial, StringComparison.OrdinalIgnoreCase))
            {
                LogSerialMismatch(found.Address.ToString(), found.Serial, result.Serial);
                return null;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogProbeFailed(found.Address.ToString(), ex.Message);
            return null;
        }
    }

    private static bool IsFactoryDefault(DiscoveredDevice found, VapixProbeResult? probed) =>
        probed?.IsFactoryDefault ?? found.Status == DiscoveredDeviceStatus.PasswordNotSet;

    /// <summary>Selected discovered devices that are known and not managed yet, in request order.</summary>
    private async Task<List<DiscoveredDevice>> ResolveAsync(string sessionId, IEnumerable<string> discoveredIds, CancellationToken ct)
    {
        var result = new List<DiscoveredDevice>();
        foreach (var id in discoveredIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = discovery.FindDiscovered(sessionId, id);
            if (found is null)
            {
                throw GrpcGuard.NotFound($"Discovered device '{id}' is unknown (discovery session '{sessionId}').");
            }

            if (Equals(found.Address, IPAddress.None) || await devices.FindBySerialAsync(found.Serial, ct).ConfigureAwait(false) is not null)
            {
                continue;
            }

            result.Add(found);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Add devices: {Added} of {Selected} device(s) added")]
    private partial void LogCommitted(int added, int selected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Added device {Serial} at {Address}: {Status}")]
    private partial void LogAdded(string serial, string address, SdkDeviceStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} is already managed, skipped")]
    private partial void LogSkippedManaged(string serial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting the initial password on {Serial} failed: {Reason}")]
    private partial void LogInitialPasswordFailed(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probe of {Address} failed: {Reason}")]
    private partial void LogProbeFailed(string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Address {Address} now answers as {Actual}, expected {Expected}")]
    private partial void LogSerialMismatch(string address, string expected, string actual);
}
