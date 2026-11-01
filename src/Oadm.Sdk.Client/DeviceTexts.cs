namespace Oadm.Sdk.Client;

/// <summary>Device texts shared by the host and plugin pages (one phrase per concept).</summary>
public static class DeviceTexts
{
    /// <summary>Why a user cannot open devices in Web View: the role lacks the right (Web View page, the device list).</summary>
    public const string OpenNotAllowed = "Your role does not allow opening devices.";

    /// <summary>Length of the shortened device id of a removed device.</summary>
    public const int ShortIdLength = 8;

    private const string AxisPrefix = "Axis ";
    private const string AmbarellaPrefix = "Ambarella ";
    private const string Artpec = "Artpec";

    /// <summary>
    /// The SoC text from basicdeviceinfo <c>Soc</c> (the SoC column, the Applications page), short and without the brand
    /// every row would repeat: "Axis Artpec-8" becomes "A8", "Ambarella CV25" becomes "CV25"; other chips stay as the device
    /// reports them. Null for none. The device grid's search also finds the full name ("ARTPEC-8", "Ambarella").
    /// </summary>
    public static string? Soc(string? soc)
    {
        if (string.IsNullOrWhiteSpace(soc))
        {
            return null;
        }

        var text = soc.Trim();
        if (text.StartsWith(AxisPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[AxisPrefix.Length..].TrimStart();
        }

        if (text.StartsWith(AmbarellaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return text[AmbarellaPrefix.Length..].TrimStart();
        }

        return text.StartsWith(Artpec, StringComparison.OrdinalIgnoreCase)
            ? "A" + text[Artpec.Length..].TrimStart('-', ' ')
            : text;
    }

    /// <summary>"Removed device 1a2b3c4d": a device OADM no longer manages.</summary>
    public static string Removed(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var shortId = deviceId.Length > ShortIdLength ? deviceId[..ShortIdLength] : deviceId;
        return "Removed device " + shortId;
    }
}
