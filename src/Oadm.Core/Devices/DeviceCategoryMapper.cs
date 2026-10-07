using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>
/// The one place that maps basicdeviceinfo <c>ProdType</c> to <see cref="DeviceCategory"/>.
/// Axis uses many free-text product types ("Dome Camera" on AXIS P3265-V, "Network Camera",
/// "PTZ Network Camera", "Network Speaker", "Security Radar", "Network I/O Relay Module",
/// "Network Door Controller", "Network Video Door Station", "Network Video Encoder", ...), so the
/// table matches keywords, first rule wins. Order matters: intercoms and encoders mention "Video"
/// but no "Camera"; "Radar-Video Fusion Camera" is a camera. Empty means Unknown, anything else
/// unmatched is Other and logged once per distinct string.
/// </summary>
public static partial class DeviceCategoryMapper
{
    /// <summary>Keyword rules (case-insensitive substring), evaluated in order.</summary>
    public static IReadOnlyList<(string Keyword, DeviceCategory Category)> Rules { get; } =
    [
        ("intercom", DeviceCategory.Intercom),
        ("door station", DeviceCategory.Intercom),
        ("door controller", DeviceCategory.DoorController),
        ("access control", DeviceCategory.DoorController),
        ("encoder", DeviceCategory.Encoder),
        ("video server", DeviceCategory.Encoder),
        ("camera", DeviceCategory.Camera),
        ("radar", DeviceCategory.Radar),
        ("speaker", DeviceCategory.Speaker),
        ("horn", DeviceCategory.Speaker),
        ("audio", DeviceCategory.Audio),
        ("microphone", DeviceCategory.Audio),
        ("amplifier", DeviceCategory.Audio),
        ("i/o", DeviceCategory.IoModule),
        ("io module", DeviceCategory.IoModule),
        ("relay module", DeviceCategory.IoModule),
    ];

    private static readonly ConcurrentDictionary<string, byte> LoggedUnknown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a ProdType. Null or blank is Unknown, no matching rule is Other.</summary>
    public static DeviceCategory Map(string? productType)
    {
        if (string.IsNullOrWhiteSpace(productType))
        {
            return DeviceCategory.Unknown;
        }

        foreach (var (keyword, category) in Rules)
        {
            if (productType.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return DeviceCategory.Other;
    }

    /// <summary><see cref="Map"/>, logging an unmatched ProdType once per process so the table can be extended.</summary>
    public static DeviceCategory Map(string? productType, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var category = Map(productType);
        if (category == DeviceCategory.Other)
        {
            var trimmed = productType!.Trim();
            if (LoggedUnknown.TryAdd(trimmed, 0))
            {
                LogUnknownProductType(logger, trimmed);
            }
        }

        return category;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unknown Axis ProdType '{ProductType}', shown as category Other")]
    private static partial void LogUnknownProductType(ILogger logger, string productType);
}
