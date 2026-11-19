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
    ILogger<DeviceAutoAddService> logger,
    Core.Sites.ISiteStates? sites = null,
    Core.DeviceGroups.DeviceViewFactory? views = null,
    Core.Security.CredentialListStore? credentialList = null,
    DeviceFirstPasswordService? firstPasswords = null,
    DeviceTagStore? tagStore = null,
    FactoryNewDeviceNotices? factoryNew = null,
    DeviceLookOptions? lookOptions = null) : IDeviceAutoAdd
{
    private long _lastLimitWarningTicks = DateTimeOffset.MinValue.UtcTicks;
    private readonly DeviceLookOptions _look = lookOptions ?? new DeviceLookOptions();

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
                authenticator.AllowCredentialList(session); // the server itself adds the device: the credential list as before
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
        (Guid Id, SdkDeviceStatus Status)? added;
        try
        {
            added = await adder.AddAsync(found, useHostName: false, initialPassword: null, credentials, server.ServerName, DeviceScope.All, ct, probed).ConfigureAwait(false);
        }
        catch (DeviceLimitException)
        {
            var now = time.GetUtcNow().UtcTicks;
            var last = Interlocked.Read(ref _lastLimitWarningTicks);
            if (now - last >= TimeSpan.TicksPerHour && Interlocked.CompareExchange(ref _lastLimitWarningTicks, now, last) == last)
            {
                LogLimitReached(DeviceLimits.MaxDevices);
            }

            return NotAdded(DeviceAutoAddResult.LimitReached, serial, text, source, DeviceLimits.TooManyDevicesText);
        }

        if (added is not { } device)
        {
            return new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, null, $"{serial} is managed already.");
        }

        polling.QueueRefresh([device.Id]);
        var (result, message) = device.Status switch
        {
            SdkDeviceStatus.PasswordNotSet => (DeviceAutoAddResult.AddedPasswordNotSet, "Added; the device has no password yet (factory default)."),
            SdkDeviceStatus.Ok or SdkDeviceStatus.Unknown => (DeviceAutoAddResult.Added, "Added and logged in as " + (credentials?.UserName ?? "-") + "."),
            SdkDeviceStatus.SiteOffline => (DeviceAutoAddResult.Added, "Added; " + Sdk.Sites.SiteTexts.SiteOffline(null) + "."),
            _ => (DeviceAutoAddResult.AddedCredentialsRequired, "Added; no password of the credential list works on it."),
        };
        LogAdded(serial, text, source, result);
        await audit.WriteAsync(AuditActions.DeviceAddedAutomatically, $"{text} ({serial})", $"{source}: {message}", ct).ConfigureAwait(false);
        await MembershipAudit.AddedAsync(views, audit, "system", string.Empty, [device.Id], source, ct, logger).ConfigureAwait(false);
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
        var oldRoute = Core.Sites.DeviceConnections.RouteOf(device);

        // A lease of the server's DHCP server: the device is on the server's own network now (also when it was at a site).
        var result = await addresses.TryRelocateAsync(normalized, ip.ToString(), Core.Sites.DeviceRoute.Local, "new address from the " + source,
            requireUnreachable: false, ct).ConfigureAwait(false);
        switch (result)
        {
            case DeviceAddressChangeResult.Updated:
                await audit.WriteAsync(AuditActions.DeviceMoved, $"{normalized}",
                    $"{source}: {Devices.DeviceMoveText.Of(oldRoute, old, Core.Sites.DeviceRoute.Local, ip.ToString(), sites)}", ct).ConfigureAwait(false);
                await MembershipAudit.MovedAsync(views, audit, device, ct, logger).ConfigureAwait(false);
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

    /// <summary>
    /// What answers at the address without a password (Provisioning): the anonymous probe and the add page's Axis check, always
    /// at the device itself, also for a managed one (its record may be old: a camera reset to factory default is new again).
    /// A camera that is still starting is asked again every <see cref="DeviceLookOptions.Retry"/> until it says, at most
    /// <see cref="DeviceLookOptions.StartingTimeout"/>; then <see cref="DeviceLookResult.Starting"/>, never "has a password".
    /// No credential is sent.
    /// </summary>
    public async Task<DeviceLook> LookAsync(string address, string expectedSerial, CancellationToken ct)
    {
        var ip = ParseAddress(address);
        var serial = DeviceSerial.Normalize(expectedSerial);
        var managed = await devices.FindBySerialAsync(serial, ct).ConfigureAwait(false);
        var clock = _look.Time ?? time;
        var end = clock.GetUtcNow() + _look.StartingTimeout;
        while (true)
        {
            var (verdict, probed, message) = await CheckAsync(ip, serial, ct).ConfigureAwait(false);
            if (verdict != DeviceLookResult.Starting || clock.GetUtcNow() >= end)
            {
                return new DeviceLook(verdict, managed?.Id, probed?.Model ?? managed?.Model, message);
            }

            await Task.Delay(_look.Retry, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Devices stored as Password not set and managed ones that turned Password not set (<see cref="FactoryNewDeviceNotices"/>).</summary>
    public IDisposable WatchFactoryNew(Action<FactoryNewDevice> found) =>
        factoryNew?.Watch(found) ?? throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

    /// <summary>
    /// The first root password from a credential list entry (Provisioning's Set password): a managed device in Password not set
    /// through the context menu's "Set password" (stored as its login, refresh queued), any other through the same checks at
    /// its address (serial and needsetup right before writing). The password never leaves the server.
    /// </summary>
    public async Task<DeviceFirstPasswordOutcome> SetFirstPasswordAsync(string address, string expectedSerial, Guid credentialId, string source, CancellationToken ct)
    {
        var ip = ParseAddress(address);
        var serial = DeviceSerial.Normalize(expectedSerial);
        source = string.IsNullOrWhiteSpace(source) ? "a core plugin" : source.Trim();
        if (credentialList is null || firstPasswords is null)
        {
            throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);
        }

        if (await credentialList.RevealAsync(credentialId, ct).ConfigureAwait(false) is not { } credential)
        {
            return new DeviceFirstPasswordOutcome(false, DeviceFirstSetupTexts.CredentialGone);
        }

        if (!string.Equals(credential.UserName, "root", StringComparison.Ordinal))
        {
            return new DeviceFirstPasswordOutcome(false, DeviceFirstSetupTexts.RootOnly);
        }

        DeviceFirstPasswordResult result;
        if (await devices.FindBySerialAsync(serial, ct).ConfigureAwait(false) is { } managed)
        {
            result = (await firstPasswords.SetAsync([managed.Id], credential.Password, ct).ConfigureAwait(false))[0];
        }
        else
        {
            var found = new DiscoveredDevice("first-password", serial, ip, null, null, null, DiscoveredDeviceStatus.PasswordNotSet, null, DiscoverySources.None, time.GetUtcNow());
            result = await firstPasswords.SetUnmanagedAsync(found, credential.Password, ct).ConfigureAwait(false);
        }

        var message = result.Ok ? "Password set." : result.Message ?? "The camera did not take the password.";
        var text = ip.ToString();
        LogFirstPassword(serial, text, source, result.Ok);
        if (result.Ok)
        {
            await audit.WriteAsync(AuditActions.FirstPasswordSet, $"{text} ({serial})", $"{source}: {message}", ct).ConfigureAwait(false);
        }

        return new DeviceFirstPasswordOutcome(result.Ok, message);
    }

    /// <summary>
    /// Adds the device logged in with one credential list entry (Provisioning's Add to OADM), after the probe and the Axis
    /// check, for all devices, with the tags; a managed device keeps its record and gets the tags.
    /// </summary>
    public async Task<DeviceAutoAddOutcome> AddWithLoginAsync(string address, string expectedSerial, Guid credentialId, IReadOnlyList<string> tags, string source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var ip = ParseAddress(address);
        var serial = DeviceSerial.Normalize(expectedSerial);
        source = string.IsNullOrWhiteSpace(source) ? "a core plugin" : source.Trim();
        var text = ip.ToString();
        if (credentialList is null)
        {
            throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);
        }

        if (await devices.FindBySerialAsync(serial, ct).ConfigureAwait(false) is { } managed)
        {
            var tagged = await TagAsync(managed.Id, serial, tags, ct).ConfigureAwait(false);
            return new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, managed.Id, "OADM manages this camera already." + tagged);
        }

        if (await credentialList.RevealAsync(credentialId, ct).ConfigureAwait(false) is not { } credential)
        {
            return new DeviceAutoAddOutcome(DeviceAutoAddResult.Unreachable, null, DeviceFirstSetupTexts.CredentialGone);
        }

        var (verdict, probed, message) = await CheckAsync(ip, serial, ct).ConfigureAwait(false);
        switch (verdict)
        {
            case DeviceLookResult.Unreachable:
                return NotAdded(DeviceAutoAddResult.Unreachable, serial, text, source, message);
            case DeviceLookResult.NotAxis:
                return NotAdded(DeviceAutoAddResult.NotAxis, serial, text, source, message);
            case DeviceLookResult.SerialMismatch:
                return NotAdded(DeviceAutoAddResult.SerialMismatch, serial, text, source, message);
        }

        var found = FoundOf(ip, serial, probed!);
        var server = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        (Guid Id, SdkDeviceStatus Status)? added;
        try
        {
            added = await adder.AddAsync(found, useHostName: false, initialPassword: null,
                new Core.Security.DeviceCredentials(credential.UserName, credential.Password), server.ServerName, DeviceScope.All, ct, probed).ConfigureAwait(false);
        }
        catch (DeviceLimitException)
        {
            return NotAdded(DeviceAutoAddResult.LimitReached, serial, text, source, DeviceLimits.TooManyDevicesText);
        }

        if (added is not { } device)
        {
            return new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, null, "OADM manages this camera already.");
        }

        polling.QueueRefresh([device.Id]);
        var tagText = await TagAsync(device.Id, serial, tags, ct).ConfigureAwait(false);
        var (result, addedText) = device.Status switch
        {
            SdkDeviceStatus.PasswordNotSet => (DeviceAutoAddResult.AddedPasswordNotSet, "Added; the camera has no password yet."),
            SdkDeviceStatus.Ok or SdkDeviceStatus.Unknown => (DeviceAutoAddResult.Added, "Added and logged in as " + credential.UserName + "."),
            _ => (DeviceAutoAddResult.AddedCredentialsRequired, "Added; the password does not work on it."),
        };
        LogAdded(serial, text, source, result);
        await audit.WriteAsync(AuditActions.DeviceAddedAutomatically, $"{text} ({serial})", $"{source}: {addedText}", ct).ConfigureAwait(false);
        await MembershipAudit.AddedAsync(views, audit, "system", string.Empty, [device.Id], source, ct, logger).ConfigureAwait(false);
        return new DeviceAutoAddOutcome(result, device.Id, addedText + tagText);
    }

    /// <summary>The credential list without passwords.</summary>
    public async Task<IReadOnlyList<DeviceCredentialName>> ListCredentialsAsync(CancellationToken ct)
    {
        if (credentialList is null)
        {
            throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);
        }

        return [.. (await credentialList.ListAsync(ct).ConfigureAwait(false)).Select(c => new DeviceCredentialName(c.Id, c.UserName, AddedOf(c.CreatedUtc)))];
    }

    /// <summary>Adds an entry to the credential list (an equal one is reused).</summary>
    public async Task<DeviceCredentialName> AddCredentialAsync(string userName, string password, CancellationToken ct)
    {
        if (credentialList is null)
        {
            throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);
        }

        var item = await credentialList.AddAsync(userName, password, ct).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.CredentialAdded, item.UserName, "a core plugin added it", ct).ConfigureAwait(false);
        return new DeviceCredentialName(item.Id, item.UserName, AddedOf(item.CreatedUtc));
    }

    /// <summary>The stored creation time (UTC, read back without its kind) as an offset.</summary>
    private static DateTimeOffset AddedOf(DateTime createdUtc) => new(DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc));

    private DiscoveredDevice FoundOf(IPAddress ip, string serial, Core.Vapix.VapixProbeResult probed) =>
        new DiscoveredDevice("auto", serial, ip, null, null, null, DiscoveredDeviceStatus.Unknown, null, DiscoverySources.None, time.GetUtcNow()) with
        {
            Model = probed.Model,
            FirmwareVersion = probed.FirmwareVersion,
            ProductType = probed.ProductType,
            Scheme = probed.Scheme,
            Status = probed.IsFactoryDefault ? DiscoveredDeviceStatus.PasswordNotSet
                : probed.AuthenticationRequired ? DiscoveredDeviceStatus.CredentialsRequired
                : DiscoveredDeviceStatus.AnonymousAccess,
        };

    /// <summary>The anonymous probe and the Axis check: what answers at the address. No credential is sent.</summary>
    private async Task<(DeviceLookResult Result, Core.Vapix.VapixProbeResult? Probed, string Message)> CheckAsync(IPAddress ip, string serial, CancellationToken ct)
    {
        var text = ip.ToString();
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
            return (DeviceLookResult.Unreachable, null, $"No Axis device answered at {text}.");
        }

        if (!string.Equals(probed.Serial, serial, StringComparison.OrdinalIgnoreCase))
        {
            return (DeviceLookResult.SerialMismatch, probed, $"{text} answers as another device ({probed.Serial}), not {serial}.");
        }

        var verdict = await authenticator.CheckAxisAsync(FoundOf(ip, serial, probed), ct).ConfigureAwait(false);
        if (verdict.Kind == DiscoveryAuthenticator.AxisVerdictKind.NotAxis)
        {
            return (DeviceLookResult.NotAxis, probed, verdict.Detail ?? "The device did not identify itself as an Axis device. No password was sent.");
        }

        if (verdict.Kind != DiscoveryAuthenticator.AxisVerdictKind.Verified)
        {
            return (DeviceLookResult.Unreachable, probed, verdict.Detail ?? $"No Axis device answered at {text}.");
        }

        return probed.IsFactoryDefault ? (DeviceLookResult.FactoryNew, probed, "The camera has no password yet.")
            : probed.IsStarting ? (DeviceLookResult.Starting, probed, StillStarting)
            : (DeviceLookResult.HasPassword, probed, "The camera has a password already.");
    }

    /// <summary>Adds the tags to the device for all devices; " Tags not set: ..." when they could not be set, else empty.</summary>
    private async Task<string> TagAsync(Guid deviceId, string serial, IReadOnlyList<string> tags, CancellationToken ct)
    {
        if (tags.Count == 0)
        {
            return string.Empty;
        }

        if (tagStore is null || views is null)
        {
            return " Tags not set: this server cannot set tags.";
        }

        try
        {
            var viewed = await views.System.GetAsync([deviceId], ct).ConfigureAwait(false);
            await tagStore.SetDeviceTagsAsync(viewed, [.. tags], [], ct).ConfigureAwait(false);
            return string.Empty;
        }
        catch (Exception ex) when (ex is TagNameException or TagLimitException or TagNeedsAllDevicesException or DevicesNotAvailableException)
        {
            LogTagsFailed(serial, ex.Message);
            return " Tags not set: " + ex.Message.TrimEnd('.') + ".";
        }
    }

    /// <summary>The look of a camera that was still starting after the wait.</summary>
    public const string StillStarting = "The camera did not finish starting within 2 minutes.";

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

    [LoggerMessage(Level = LogLevel.Information, Message = "First password of {Serial} at {Address} ({Source}): {Ok}")]
    private partial void LogFirstPassword(string serial, string address, string source, bool ok);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tags of {Serial} not set: {Reason}")]
    private partial void LogTagsFailed(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Devices are not added automatically: OADM manages {Max} devices already")]
    private partial void LogLimitReached(int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} at {Address} added automatically ({Source}): {Result}")]
    private partial void LogAdded(string serial, string address, string source, DeviceAutoAddResult result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} at {Address} not added automatically ({Source}): {Result} - {Reason}")]
    private partial void LogNotAdded(string serial, string address, string source, DeviceAutoAddResult result, string reason);
}

/// <summary>How long <see cref="DeviceAutoAddService.LookAsync"/> asks a camera that is still starting (tests pass a fake clock).</summary>
public sealed record DeviceLookOptions
{
    /// <summary>Longest wait for a starting camera to say whether it needs setup (2 minutes).</summary>
    public TimeSpan StartingTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The pause between two asks.</summary>
    public TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The clock; null: the server's.</summary>
    public TimeProvider? Time { get; init; }
}
