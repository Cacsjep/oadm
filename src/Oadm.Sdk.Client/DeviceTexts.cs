namespace Oadm.Sdk.Client;

/// <summary>Device texts shared by the host and plugin pages (one phrase per concept).</summary>
public static class DeviceTexts
{
    /// <summary>Length of the shortened device id of a removed device.</summary>
    public const int ShortIdLength = 8;

    /// <summary>"Removed device 1a2b3c4d": a device OADM no longer manages.</summary>
    public static string Removed(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var shortId = deviceId.Length > ShortIdLength ? deviceId[..ShortIdLength] : deviceId;
        return "Removed device " + shortId;
    }
}
