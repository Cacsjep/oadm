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
/// gRPC AddDevicesService, the server side of the add devices page.
/// <para><b>RetryAuth</b> logs in to one discovered device with credentials the technician typed
/// (<see cref="DiscoveryAuthenticator"/>); the automatic login with known credentials runs while
/// the page watches discovery.</para>
/// <para><b>Commit</b> stores each device (serial unique; the entered address of "Add manually",
/// else host name or IP per the server setting <c>Devices.UseHostName</c>), pins the certificate
/// fingerprint from the probe, sets the initial root password on factory-default devices when one
/// is given (per device from <c>initial_passwords</c>, else <c>initial_root_password</c>; over HTTPS
/// when the device offers it), stores credentials (initial password as root, else explicit request
/// credentials, else the credential that worked in the session's login), verifies them with one
/// authenticated call (wrong credentials still add the device, status CredentialsRequired) and
/// finally queues the first full refresh. Adding devices is not a task and never shows up in the
/// task list. <b>Prepare</b> is the legacy wizard step, kept for wire compatibility.</para>
/// </summary>
public sealed partial class AddDevicesGrpcService(
    IDiscoveryBackend discovery,
    DeviceRepository devices,
    ServerSettingsStore settings,
    DevicePollingService polling,
    DiscoveryAuthenticator authenticator,
    DeviceAdder adder,
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
            var probed = await adder.TryProbeAsync(found, token).ConfigureAwait(false);
            var factoryDefault = DeviceAdder.IsFactoryDefault(found, probed);
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

    public override async Task<Proto.DiscoveredDevice> RetryAuth(Proto.RetryAuthRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            throw GrpcGuard.InvalidArgument("Enter a user name and a password.");
        }

        var found = discovery.FindDiscovered(request.SessionId, request.DiscoveredId)
            ?? throw GrpcGuard.NotFound($"Discovered device '{request.DiscoveredId}' is unknown (discovery session '{request.SessionId}').");
        if (await devices.FindBySerialAsync(found.Serial, ct).ConfigureAwait(false) is not null)
        {
            throw GrpcGuard.FailedPrecondition($"The device {found.Serial} has already been added.");
        }

        try
        {
            var result = await authenticator.RetryAsync(request.SessionId, found, request.UserName, request.Password, request.SaveToCredentialList, request.RelatedSessionIds, ct).ConfigureAwait(false);
            return Mappers.ToProto(found, false, 100, result);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    public override async Task<Proto.CommitReply> Commit(Proto.CommitRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var initialPassword = string.IsNullOrEmpty(request.InitialRootPassword) ? null : request.InitialRootPassword;
        try
        {
            foreach (var password in request.InitialPasswords.Values.Where(p => p.Length > 0).Append(initialPassword).OfType<string>())
            {
                VapixClient.ValidatePassword(password);
            }
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }

        var selected = await ResolveAsync(request.SessionId, request.DiscoveredIds, ct).ConfigureAwait(false);
        var serverSettings = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        var serverName = serverSettings.ServerName;
        var useHostName = serverSettings.UseHostName; // CommitRequest.use_host_name is unused
        var added = new ConcurrentDictionary<string, (Guid Id, SdkDeviceStatus Status)>();

        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (found, token) =>
        {
            var deviceCredentials = PickCredentials(request.Credentials, found.DiscoveredId) ?? MatchedCredentials(request.SessionId, found);
            var devicePassword = request.InitialPasswords.TryGetValue(found.DiscoveredId, out var own) && own.Length > 0 ? own : initialPassword;
            var credentialsToStore = deviceCredentials is null ? null : new DeviceCredentials(deviceCredentials.UserName, deviceCredentials.Password);
            var result = await adder.AddAsync(found, useHostName, devicePassword, credentialsToStore, serverName, token).ConfigureAwait(false);
            if (result is { } one)
            {
                added[found.DiscoveredId] = one;
            }
        }).ConfigureAwait(false);

        var reply = new Proto.CommitReply();
        var ids = selected.Where(s => added.ContainsKey(s.DiscoveredId)).Select(s => added[s.DiscoveredId].Id).ToArray();
        reply.DeviceIds.AddRange(ids.Select(i => i.ToString()));
        foreach (var found in selected)
        {
            var ok = added.TryGetValue(found.DiscoveredId, out var one);
            reply.Results.Add(new Proto.CommitResult
            {
                DiscoveredId = found.DiscoveredId,
                DeviceId = ok ? one.Id.ToString() : string.Empty,
                Status = ok ? Mappers.ToProto(one.Status) : Proto.DeviceStatus.Unknown,
            });
        }

        if (ids.Length > 0)
        {
            polling.QueueRefresh(ids);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            authenticator.MarkAdded(request.SessionId, selected.Where(s => added.ContainsKey(s.DiscoveredId)).Select(s => s.Serial));
        }

        LogCommitted(ids.Length, selected.Count);
        return reply;
    }

    /// <summary>The credential that worked in the session's automatic login or RetryAuth (server memory only).</summary>
    private Proto.DeviceCredentials? MatchedCredentials(string sessionId, DiscoveredDevice found)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || authenticator.MatchedCredentials(sessionId, found.Serial) is not { } matched)
        {
            return null;
        }

        return new Proto.DeviceCredentials { DiscoveredId = found.DiscoveredId, UserName = matched.UserName, Password = matched.Password };
    }

    /// <summary>Per-device credentials override the default entry (empty discovered_id).</summary>
    internal static Proto.DeviceCredentials? PickCredentials(IEnumerable<Proto.DeviceCredentials> all, string discoveredId)
    {
        var usable = all.Where(c => !string.IsNullOrWhiteSpace(c.UserName)).ToList();
        return usable.FirstOrDefault(c => string.Equals(c.DiscoveredId, discoveredId, StringComparison.OrdinalIgnoreCase))
            ?? usable.FirstOrDefault(c => string.IsNullOrEmpty(c.DiscoveredId));
    }

    /// <summary>The connection address (see <see cref="DeviceAdder.ChooseAddress"/>).</summary>
    internal static (string Address, string? HostName) ChooseAddress(DiscoveredDevice found, bool useHostName) => DeviceAdder.ChooseAddress(found, useHostName);

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
}
