using System.Globalization;
using System.Text;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Devices;

/// <summary>
/// The device list as CSV (Export devices): every device grid column as text, a header row, invariant
/// formats (ISO date for the certificate end), RFC 4180 quoting, UTF-8 with BOM so Excel reads it, and
/// formula-safe cells (<see cref="Csv.Field"/>). The client has no passwords, so none can leak. O(n).
/// The device import (add page) reads this format back.
/// </summary>
public static class DeviceListCsv
{
    public const string AddressColumn = "Address";

    /// <summary>The device's tags, "Building A; PTZ" (the import reads it back).</summary>
    public const string TagsColumn = "Tags";

    public static IReadOnlyList<string> Columns { get; } =
    [
        "MAC address", "Status", AddressColumn, TagsColumn, "Host name", "Model", "Firmware", "SoC", "Category", "Product type",
        "DHCP", "HTTPS", "Certificate expires", "Certificate", "IEEE 802.1X",
    ];

    /// <summary>"oadm-devices-2026-10-08.csv".</summary>
    public static string DefaultFileName(DateTime localNow) =>
        "oadm-devices-" + localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";

    /// <summary>The CSV text (header + one record per device).</summary>
    public static string Write(IEnumerable<DeviceRowViewModel> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var sb = new StringBuilder(256);
        Csv.AppendRecord(sb, Columns);
        foreach (DeviceRowViewModel d in devices)
        {
            Csv.AppendRecord(sb,
            [
                d.Serial,
                d.StatusText,
                d.DisplayAddress,
                d.TagsText,
                d.HostName,
                d.Model,
                d.FirmwareVersion,
                d.Soc,
                DeviceCategoryInfo.ToText(d.Category),
                d.ProductType,
                d.DhcpText,
                d.HttpsText,
                d.CertNotAfterUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.CertTrust.Text,
                d.Dot1xText,
            ]);
        }

        return sb.ToString();
    }

    /// <summary>The file content: UTF-8 with BOM.</summary>
    public static byte[] ToBytes(IEnumerable<DeviceRowViewModel> devices)
    {
        string text = Write(devices);
        byte[] bom = Encoding.UTF8.GetPreamble();
        byte[] data = new byte[bom.Length + Encoding.UTF8.GetByteCount(text)];
        bom.CopyTo(data, 0);
        Encoding.UTF8.GetBytes(text, 0, text.Length, data, bom.Length);
        return data;
    }
}
