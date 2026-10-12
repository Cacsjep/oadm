using Oadm.Contracts.V1;

namespace Oadm.Client.Devices;

/// <summary>Icon key (theme geometry <c>Icon.device.*</c>) and display text of a device category.</summary>
public static class DeviceCategoryInfo
{
    /// <summary>The shared mapping of <see cref="Oadm.Sdk.Client.Controls.DeviceIcon.IconKey"/>.</summary>
    public static string ToIconKey(DeviceCategory category) => Oadm.Sdk.Client.Controls.DeviceIcon.IconKey(ToSdk(category));

    /// <summary>The shared text of <see cref="Oadm.Sdk.Client.Controls.DeviceIcon.CategoryText"/>.</summary>
    public static string ToText(DeviceCategory category) => Oadm.Sdk.Client.Controls.DeviceIcon.CategoryText(ToSdk(category));

    /// <summary>Tooltip of the icon: category, plus the raw ProdType when it adds information ("Camera (Dome Camera)").</summary>
    public static string ToTooltip(DeviceCategory category, string? productType)
    {
        string text = ToText(category);
        return string.IsNullOrWhiteSpace(productType) || string.Equals(productType, text, StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text} ({productType})";
    }

    public static Oadm.Sdk.Devices.DeviceCategory ToSdk(DeviceCategory category) => category switch
    {
        DeviceCategory.Camera => Oadm.Sdk.Devices.DeviceCategory.Camera,
        DeviceCategory.Encoder => Oadm.Sdk.Devices.DeviceCategory.Encoder,
        DeviceCategory.Speaker => Oadm.Sdk.Devices.DeviceCategory.Speaker,
        DeviceCategory.Audio => Oadm.Sdk.Devices.DeviceCategory.Audio,
        DeviceCategory.Intercom => Oadm.Sdk.Devices.DeviceCategory.Intercom,
        DeviceCategory.Radar => Oadm.Sdk.Devices.DeviceCategory.Radar,
        DeviceCategory.IoModule => Oadm.Sdk.Devices.DeviceCategory.IoModule,
        DeviceCategory.DoorController => Oadm.Sdk.Devices.DeviceCategory.DoorController,
        DeviceCategory.Other => Oadm.Sdk.Devices.DeviceCategory.Other,
        _ => Oadm.Sdk.Devices.DeviceCategory.Unknown,
    };
}
