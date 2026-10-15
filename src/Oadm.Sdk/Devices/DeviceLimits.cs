namespace Oadm.Sdk.Devices;

/// <summary>How many devices one OADM server manages, and the text when an add would go over it.</summary>
public static class DeviceLimits
{
    /// <summary>The most devices one server manages (30,000). Adds beyond it are refused; plugins size their runs by it.</summary>
    public const int MaxDevices = 30_000;

    /// <summary>The text shown when an add would take the server over <see cref="MaxDevices"/>.</summary>
    public const string TooManyDevicesText = "OADM manages at most 30,000 devices. Remove devices first, or add fewer.";
}
