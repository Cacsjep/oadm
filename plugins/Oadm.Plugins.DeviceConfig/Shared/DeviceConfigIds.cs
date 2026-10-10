using System.Globalization;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>Ids, names and texts shared by the server and the client part.</summary>
public static class DeviceConfigIds
{
    public const string PackageId = "oadm.device-config";

    public const string Export = "oadm.device-config.export";

    public const string ExportName = "Export configuration";

    public const string Import = "oadm.device-config.import";

    public const string ImportName = "Import configuration";

    /// <summary>Query of the export task: the device's <c>$export</c> answer, unchanged.</summary>
    public const string ExportQuery = "export";

    /// <summary>AXIS OS that exports and imports its configuration.</summary>
    public static readonly Version MinFirmware = new(12, 5);

    public const string MinFirmwareText = "12.5";

    /// <summary>Largest configuration file OADM reads (a P3265-V exports about 60 KB).</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024;

    /// <summary>Largest ZIP of configuration files.</summary>
    public const long MaxZipBytes = 1024L * 1024 * 1024;

    /// <summary>Most configuration files in one ZIP.</summary>
    public const int MaxZipEntries = 5000;

    /// <summary>Devices the export asks at the same time.</summary>
    public const int ExportParallelism = 8;

    public const string NotAConfigFile = "Not a device configuration file";

    public const string NoModel = "The file does not name the device model";

    public const string FeatureOff = "Configuration export is turned off on this device";

    public const string NoFile = "No file for this device";

    public const string TwoFiles = "Two files for this device";

    public static string OtherModel(string fileModel) => "Other model: the file is for " + ModelNames.Display(fileModel);

    public static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");
}

/// <summary>Model names: the file says "P3265-V", the device grid may say "AXIS P3265-V".</summary>
public static class ModelNames
{
    private const string Brand = "AXIS ";

    /// <summary>Upper case without the brand, for comparing.</summary>
    public static string Normalize(string? model)
    {
        var text = (model ?? string.Empty).Trim();
        if (text.StartsWith(Brand, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Brand.Length..].TrimStart();
        }

        return text.ToUpperInvariant();
    }

    public static bool Same(string? a, string? b)
    {
        var left = Normalize(a);
        return left.Length > 0 && string.Equals(left, Normalize(b), StringComparison.Ordinal);
    }

    /// <summary>"AXIS P3265-V".</summary>
    public static string Display(string? model)
    {
        var text = (model ?? string.Empty).Trim();
        return text.Length == 0 || text.StartsWith(Brand, StringComparison.OrdinalIgnoreCase) ? text : Brand + text;
    }
}

/// <summary>"12.11.77" compared by its first two numbers.</summary>
public static class FirmwareVersions
{
    /// <summary>True when <paramref name="version"/> is at least <see cref="DeviceConfigIds.MinFirmware"/>; false when unknown.</summary>
    public static bool SupportsConfigFiles(string? version) =>
        TryParse(version) is { } parsed && parsed >= DeviceConfigIds.MinFirmware;

    public static Version? TryParse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Trim().Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(new string([.. parts[1].TakeWhile(char.IsAsciiDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        return new Version(major, minor);
    }
}
