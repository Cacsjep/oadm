namespace Oadm.Plugins.Firmware;

/// <summary>Outcome of checking one firmware file against one device.</summary>
public enum FirmwareVerdict
{
    /// <summary>Newer version for this product: will be installed.</summary>
    Upgrade = 0,

    /// <summary>Product or version not known from the file: the device validates the image (signature, product) before installing.</summary>
    DeviceValidates = 1,

    /// <summary>Same version: nothing is installed, the device finishes as Done with warning "already up to date".</summary>
    AlreadyUpToDate = 2,

    /// <summary>Older version, allowed by the user and combined with a factory default.</summary>
    Downgrade = 3,

    /// <summary>Older version and "allow downgrade" is off, or no factory default selected.</summary>
    DowngradeBlocked = 4,

    /// <summary>The file is for another product.</summary>
    WrongProduct = 5,

    /// <summary>The file is not an AXIS OS image.</summary>
    InvalidFile = 6,

    /// <summary>Unknown version combined with a factory default but without "allow downgrade": refused, a downgrade cannot be ruled out.</summary>
    UnknownVersionRefused = 7,
}

public sealed record FirmwareCheck(FirmwareVerdict Verdict, string Message)
{
    /// <summary>The upgrade request is sent to the device.</summary>
    public bool WillInstall => Verdict is FirmwareVerdict.Upgrade or FirmwareVerdict.DeviceValidates or FirmwareVerdict.Downgrade;

    /// <summary>The device is left alone without an error (already up to date).</summary>
    public bool IsNoOp => Verdict is FirmwareVerdict.AlreadyUpToDate;
}

/// <summary>
/// The one place that decides whether a firmware file may be installed on a device. Used by the
/// dialog (preview) and by the task (enforced again on the server before anything is written).
/// </summary>
public static class FirmwareCompatibility
{
    public static FirmwareCheck Evaluate(string? deviceModel, string? deviceVersion, FirmwareImageInfo image, FactoryDefaultMode mode, bool allowDowngrade)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.RejectReason is { } reason)
        {
            return new(FirmwareVerdict.InvalidFile, reason);
        }

        if (image.Product is { } product && !string.IsNullOrWhiteSpace(deviceModel) && !ProductMatches(product, deviceModel))
        {
            return new(FirmwareVerdict.WrongProduct, $"The file is for {product}, the device is {NormalizeProduct(deviceModel)}.");
        }

        var current = AxisOsVersion.TryParse(deviceVersion);
        if (image.Version is not { } target || current is null)
        {
            if (mode != FactoryDefaultMode.None && !allowDowngrade)
            {
                return new(FirmwareVerdict.UnknownVersionRefused,
                    "The firmware version could not be compared, so a downgrade cannot be ruled out. Enable \"Allow downgrade\" to use a factory default with this file.");
            }

            return new(FirmwareVerdict.DeviceValidates, "Version not known from the file name; the device checks the image before installing it.");
        }

        var cmp = target.CompareTo(current);
        if (cmp == 0)
        {
            return new(FirmwareVerdict.AlreadyUpToDate, $"Already up to date ({current}).");
        }

        if (cmp > 0)
        {
            return new(FirmwareVerdict.Upgrade, $"{current} → {target}");
        }

        var across = target.Major != current.Major ? " (older major version)" : string.Empty;
        if (!allowDowngrade)
        {
            return new(FirmwareVerdict.DowngradeBlocked, $"Downgrade {current} → {target}{across} is not allowed. Enable \"Allow downgrade\".");
        }

        if (mode == FactoryDefaultMode.None)
        {
            return new(FirmwareVerdict.DowngradeBlocked, $"Downgrade {current} → {target}{across} requires a factory default (AXIS OS refuses downgrades that keep the settings).");
        }

        return new(FirmwareVerdict.Downgrade, $"Downgrade {current} → {target}{across} with {mode.ToString().ToLowerInvariant()} factory default.");
    }

    /// <summary>"AXIS P3265-V" and "p3265-v" match "P3265-V".</summary>
    public static bool ProductMatches(string fileProduct, string deviceModel) =>
        string.Equals(NormalizeProduct(fileProduct), NormalizeProduct(deviceModel), StringComparison.OrdinalIgnoreCase);

    public static string NormalizeProduct(string product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var p = product.Trim();
        if (p.StartsWith("AXIS ", StringComparison.OrdinalIgnoreCase))
        {
            p = p[5..].Trim();
        }

        return p.ToUpperInvariant();
    }
}
