using Oadm.Contracts.V1;

namespace Oadm.Client.Devices;

/// <summary>Visual category of a status pill. Colors live in the theme (pill classes ok, warn, error, neutral, accent).</summary>
public enum PillKind
{
    Neutral,
    Ok,
    Warning,
    Error,
    Accent,
}

public static class DeviceStatusInfo
{
    public static string ToText(DeviceStatus status) => status switch
    {
        DeviceStatus.Ok => "OK",
        DeviceStatus.Unreachable => "Unreachable",
        DeviceStatus.CredentialsRequired => "Credentials required",
        DeviceStatus.PasswordNotSet => "Password not set",
        DeviceStatus.CertificateChanged => "Certificate changed",
        _ => "Unknown",
    };

    public static PillKind ToKind(DeviceStatus status) => status switch
    {
        DeviceStatus.Ok => PillKind.Ok,
        DeviceStatus.Unreachable => PillKind.Error,
        DeviceStatus.CredentialsRequired or DeviceStatus.PasswordNotSet or DeviceStatus.CertificateChanged => PillKind.Warning,
        _ => PillKind.Neutral,
    };

    public static Oadm.Sdk.Devices.DeviceStatus ToSdk(DeviceStatus status) => status switch
    {
        DeviceStatus.Ok => Oadm.Sdk.Devices.DeviceStatus.Ok,
        DeviceStatus.Unreachable => Oadm.Sdk.Devices.DeviceStatus.Unreachable,
        DeviceStatus.CredentialsRequired => Oadm.Sdk.Devices.DeviceStatus.CredentialsRequired,
        DeviceStatus.PasswordNotSet => Oadm.Sdk.Devices.DeviceStatus.PasswordNotSet,
        DeviceStatus.CertificateChanged => Oadm.Sdk.Devices.DeviceStatus.CertificateChanged,
        _ => Oadm.Sdk.Devices.DeviceStatus.Unknown,
    };
}
