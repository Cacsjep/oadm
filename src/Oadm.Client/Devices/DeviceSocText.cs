namespace Oadm.Client.Devices;

/// <summary>
/// The SoC column text from basicdeviceinfo <c>Soc</c>: "Axis Artpec-8" becomes "ARTPEC-8" (Axis' own spelling, without
/// the brand every row would repeat); other chips stay as the device reports them ("Ambarella CV25").
/// </summary>
public static class DeviceSocText
{
    private const string AxisPrefix = "Axis ";
    private const string Artpec = "Artpec";

    public static string? ToText(string? soc)
    {
        if (string.IsNullOrWhiteSpace(soc))
        {
            return null;
        }

        string text = soc.Trim();
        if (text.StartsWith(AxisPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[AxisPrefix.Length..].TrimStart();
        }

        return text.StartsWith(Artpec, StringComparison.OrdinalIgnoreCase)
            ? "ARTPEC" + text[Artpec.Length..]
            : text;
    }
}
