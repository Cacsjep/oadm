namespace Oadm.Plugins.Radius.Tests.Support;

/// <summary>Hex dumps as the RFCs print them ("01 00 00 38 ...").</summary>
public static class Hex
{
    public static byte[] Bytes(string dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        return Convert.FromHexString(string.Concat(dump.Where(char.IsAsciiHexDigit)));
    }
}
