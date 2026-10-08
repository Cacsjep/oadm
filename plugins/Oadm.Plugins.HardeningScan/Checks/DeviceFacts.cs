using Oadm.Plugins.Acap;
using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.HardeningScan.Device;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Users;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.HardeningScan.Checks;

/// <summary>One read of a scan: its value, the error text of a failed read, or why it was not read (not available).</summary>
public sealed class Fact<T>
{
    private readonly T? _value;

    internal Fact(T? value, string? error, string? missing)
    {
        _value = value;
        Error = error;
        Missing = missing;
    }

    public bool IsOk => Error is null && Missing is null;

    /// <summary>The value; only valid when <see cref="IsOk"/>.</summary>
    public T Value => IsOk ? _value! : throw new InvalidOperationException("The read did not succeed.");

    /// <summary>The read failed ("Timeout after 15 s", "Unauthorized - HTTP 401").</summary>
    public string? Error { get; }

    /// <summary>Not read: the reason ("Not available on this firmware").</summary>
    public string? Missing { get; }
}

/// <summary>Factories of <see cref="Fact{T}"/>.</summary>
public static class Fact
{
    /// <summary>A read that was not planned (other level) or whose API the device does not have.</summary>
    public static Fact<T> NotAvailable<T>(string reason) => new(default, null, reason);

    public static Fact<T> Ok<T>(T value) => new(value, null, null);

    public static Fact<T> Failed<T>(string error) => new(default, error, null);
}

/// <summary>Everything one scan read from (or knows about) one device; the input of <see cref="HardeningChecks"/>.</summary>
public sealed class DeviceFacts
{
    public const string NotRead = "Not read at this level";

    public required IDeviceInfo Device { get; init; }

    /// <summary>Time of the scan (certificate expiry).</summary>
    public DateTimeOffset Now { get; init; }

    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];

    public Fact<ParamList> Params { get; init; } = Fact.NotAvailable<ParamList>(NotRead);

    public Fact<IReadOnlyList<DeviceUser>> Users { get; init; } = Fact.NotAvailable<IReadOnlyList<DeviceUser>>(NotRead);

    /// <summary>user-management v2 passphrase policy ("none", "length", "complex"); an empty string when the device sent none.</summary>
    public Fact<string> PasswordPolicy { get; init; } = Fact.NotAvailable<string>(NotRead);

    public Fact<CurrentTimeSettings> Time { get; init; } = Fact.NotAvailable<CurrentTimeSettings>(NotRead);

    public Fact<IReadOnlyList<DiskInfo>> Disks { get; init; } = Fact.NotAvailable<IReadOnlyList<DiskInfo>>(NotRead);

    public Fact<IReadOnlyList<InstalledApplication>> Applications { get; init; } = Fact.NotAvailable<IReadOnlyList<InstalledApplication>>(NotRead);

    /// <summary>config.cgi AllowUnsigned (AXIS OS 11.2+); null when the device has no such setting.</summary>
    public bool? AllowUnsigned { get; init; }

    public Fact<FirewallInfo> Firewall { get; init; } = Fact.NotAvailable<FirewallInfo>(NotRead);

    /// <summary>LLDP activated (only mentioned in the discovery detail).</summary>
    public bool? LldpActivated { get; init; }

    public Fact<SnmpInfo> Snmp { get; init; } = Fact.NotAvailable<SnmpInfo>(NotRead);

    /// <summary>The OpenID provider metadata URL; an empty string when none is configured.</summary>
    public Fact<string> OidcProvider { get; init; } = Fact.NotAvailable<string>(NotRead);

    public Fact<WebServerTlsConfiguration> WebServerTls { get; init; } = Fact.NotAvailable<WebServerTlsConfiguration>(NotRead);
}
