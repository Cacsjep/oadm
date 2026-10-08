using System.Net;
using System.Net.Sockets;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.AddDevices;

/// <summary>
/// <see cref="IDeviceAutoAdd"/> for core plugins (the DHCP server's "Automatically add Axis devices"), on top of the add
/// page's code: anonymous probe (serial must be the expected one), the anonymous Axis check of
/// <see cref="DiscoveryAuthenticator"/> (no credential before it passed), factory default -> added as Password not set,
/// else the automatic login of <see cref="DiscoveryAuthenticator"/> in a session of its own (credential list only, at most
/// 10 tries, HTTPS first) -> added with the credential that worked (Ok) or without one (Credentials required), stored by
/// <see cref="DeviceAdder"/> like an add from the page and the first full refresh queued. Not an Axis device, another serial
/// or no answer: nothing is added (logged). Added devices and moved ones go to the audit log as user "system".
/// </summary>
public sealed partial class DeviceAutoAddService(
    DeviceRepository devices,
    DeviceAdder adder,
    Core.Vapix.VapixProbe vapixProbe,
    DiscoveryAuthenticator authenticator,
    DeviceAddressService addresses,
    ServerSettingsStore settings,
    DevicePollingService polling,
    AuditLog audit,
    TimeProvider time,
    ILogger<DeviceAutoAddService> logger) : IDeviceAutoAdd
{
    public async Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct)
    {
        var ip = ParseAddress(address);
        var serial = DeviceSerial.Normalize(expectedSerial);
        source = string.IsNullOrWhiteSpace(source) ? "a core plugin" : source.Trim();
        var text = ip.ToString();

        if (await devices.FindBySerialAsync(serial, ct).ConfigureAwait(false) is not null)
        {
            return new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, null, $"{serial} is managed already.");
        }

        var probe = new DiscoveredDevice("auto", serial, ip, null, null, null, DiscoveredDeviceStatus.Unknown, null, DiscoverySources.None, time.GetUtcNow());
        Core.Vapix.VapixProbeResult? probed;
        try
        {
            probed = await vapixProbe.ProbeAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            probed = null;
        }

        if (probed is null)
        {
            return NotAdded(DeviceAutoAddResult.Unreachable, serial, text, source, $"No Axis device answered at {text}.");
        }

        if (!string.Equals(probed.Serial, serial, StringComparison.OrdinalIgnoreCase))
        {
            return NotAdded(DeviceAutoAddResult.SerialMismatch, serial, text, source, $"{text} answers as another device ({probed.Serial}), not {serial}.");
        }

        var found = probe with
        {
            Model = probed.Model,
            FirmwareVersion = probed.FirmwareVersion,
            ProductType = probed.ProductType,
            Scheme = probed.Scheme,
            Status = probed.IsFactoryDefault ? DiscoveredDeviceStatus.PasswordNotSet
                : probed.AuthenticationRequired ? DiscoveredDeviceStatus.CredentialsRequired
                : DiscoveredDeviceStatus.AnonymousAccess,
        };

        // The add page's Axis check, before anything else and without any credential.
        var verdict = await authenticator.CheckAxisAsync(found, ct).ConfigureAwait(false);
        if (verdict.Kind == DiscoveryAuthenticator.AxisVerdictKind.NotAxis)
        {
            return NotAdded(DeviceAutoAddResult.NotAxis, serial, text, source, verdict.Detail ?? "The device did not identify itself as an Axis device. No password was sent.");
        }

        if (verdict.Kind != DiscoveryAuthenticator.AxisVerdictKind.Verified)
        {
            return NotAdded(DeviceAutoAddResult.Unreachable, serial, text, source, verdict.Detail ?? $"No Axis device answered at {text}.");
        }

        Core.Security.DeviceCredentials? credentials = null;
        if (found.Status != DiscoveredDeviceStatus.PasswordNotSet)
        {
            var session = "auto-add:" + Guid.NewGuid().ToString("N");
            try
            {
                authenticator.Ensure(session, found, alreadyManaged: false);
                await authenticator.WhenIdleAsync(session, ct).ConfigureAwait(false);
                var login = authenticator.Get(session, serial);
                if (login.State == DeviceAuthState.Unreachable)
                {
                    return NotAdded(DeviceAutoAddResult.Unreachable, serial, text, source, login.Detail ?? $"No Axis device answered at {text}.");
                }

                credentials = login.State == DeviceAuthState.Authenticated ? authenticator.MatchedCredentials(session, serial) : null;
            }
            finally
            {
                authenticator.Forget(session);
            }
        }

        var server = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        var added = await adder.AddAsync(found, useHostName: false, initialPassword: null, credentials, server.ServerName, ct, probed).ConfigureAwait(false);
        if (added is not { } device)
        {
            return new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, null, $"{serial} is managed already.");
        }

        polling.QueueRefresh([device.Id]);
        var (result, message) = device.Status switch
        {
            SdkDeviceStatus.PasswordNotSet => (DeviceAutoAddResult.AddedPasswordNotSet, "Added; the device has no password yet (factory default)."),
            SdkDeviceStatus.Ok or SdkDeviceStatus.Unknown => (DeviceAutoAddResult.Added, "Added and logged in as " + (credentials?.UserName ?? "-") + "."),
            _ => (DeviceAutoAddResult.AddedCredentialsRequired, "Added; no password of the credential list works on it."),
        };
        LogAdded(serial, text, source, result);
        await audit.WriteAsync(AuditActions.DeviceAddedAutomatically, $"{text} ({serial})", $"{source}: {message}", ct).ConfigureAwait(false);
        return new DeviceAutoAddOutcome(result, device.Id, message);
    }

    public async Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct)
    {
        var ip = ParseAddress(address);
        var normalized = DeviceSerial.Normalize(serial);
        source = string.IsNullOrWhiteSpace(source) ? "a core plugin" : source.Trim();
        var device = await devices.FindBySerialAsync(normalized, ct).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceFollowResult.NotManaged;
        }

        var old = device.Address;
        var result = await addresses.TryRelocateAsync(normalized, ip.ToString(), "new address from the " + source, requireUnreachable: false, ct).ConfigureAwait(false);
        switch (result)
        {
            case DeviceAddressChangeResult.Updated:
                await audit.WriteAsync(AuditActions.DeviceMoved, $"{normalized}", $"{source}: from {old} to {ip}", ct).ConfigureAwait(false);
                return DeviceFollowResult.Moved;
            case DeviceAddressChangeResult.KeptHostName:
                return DeviceFollowResult.KeptHostName;
            case DeviceAddressChangeResult.Unchanged:
                return DeviceFollowResult.Unchanged;
            case DeviceAddressChangeResult.NotManaged:
                return DeviceFollowResult.NotManaged;
            default:
                return DeviceFollowResult.NotVerified;
        }
    }

    private DeviceAutoAddOutcome NotAdded(DeviceAutoAddResult result, string serial, string address, string source, string message)
    {
        LogNotAdded(serial, address, source, result, message);
        return new DeviceAutoAddOutcome(result, null, message);
    }

    private static IPAddress ParseAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (!IPAddress.TryParse(address.Trim().Trim('[', ']'), out var ip) || ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException($"\"{address}\" is not an IP address.", nameof(address));
        }

        return ip;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} at {Address} added automatically ({Source}): {Result}")]
    private partial void LogAdded(string serial, string address, string source, DeviceAutoAddResult result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} at {Address} not added automatically ({Source}): {Result} - {Reason}")]
    private partial void LogNotAdded(string serial, string address, string source, DeviceAutoAddResult result, string reason);
}
