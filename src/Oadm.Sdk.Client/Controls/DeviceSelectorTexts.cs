using System.Globalization;

namespace Oadm.Sdk.Client.Controls;

/// <summary>Short texts of a device choice for the "Selected devices: ..." buttons (event rules, device triggers).</summary>
public static class DeviceSelectorTexts
{
    /// <summary>"10.0.0.48", "3 devices", "Tag Building A", "Tags A, B and 3 devices".</summary>
    public static string Brief(int devices, IReadOnlyList<string> tags, string? singleAddress = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var tagText = tags.Count == 0 ? null : (tags.Count == 1 ? "Tag " : "Tags ") + string.Join(", ", tags);
        var deviceText = devices switch
        {
            0 => null,
            1 when !string.IsNullOrEmpty(singleAddress) => singleAddress,
            _ => string.Create(CultureInfo.CurrentCulture, $"{devices:N0} {(devices == 1 ? "device" : "devices")}"),
        };
        return tagText is not null && deviceText is not null ? $"{tagText} and {deviceText}" : tagText ?? deviceText ?? "None";
    }
}
