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

    /// <summary>Several files were chosen and none is for this product: the device is skipped.</summary>
    NoFileForModel = 8,

    /// <summary>AXIS OS 13 or later and an installed application that is unsigned or does not declare it: the device would refuse.</summary>
    AppsBlock = 9,
}

public sealed record FirmwareCheck(FirmwareVerdict Verdict, string Message)
{
    /// <summary>
    /// An upgrade off the recommended path (<see cref="AxisOsUpgradePath"/>): the versions to install first. Advice only,
    /// the upgrade still runs; newer AXIS OS refuses it. Null when on the path.
    /// </summary>
    public string? PathAdvice { get; init; }

    /// <summary>
    /// An upgrade that leaves the device's LTS track (<see cref="AxisOsUpgradePath.Note"/>): on the path only from the
    /// latest version of that track, which OADM does not know. A note, not a warning. Null otherwise.
    /// </summary>
    public string? PathNote { get; init; }

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
    /// <summary>The downgrade checkbox of the dialog as the refusals name it (its label without the hint in brackets).</summary>
    public const string AllowDowngradeName = "Allow downgrade to an older AXIS OS version";

    /// <param name="apps">The device's installed applications (<see cref="InstalledAppRules"/>); null when not read.</param>
    public static FirmwareCheck Evaluate(string? deviceModel, string? deviceVersion, FirmwareImageInfo image, FactoryDefaultMode mode, bool allowDowngrade,
        IReadOnlyList<InstalledApp>? apps = null)
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
                    $"The firmware version could not be compared, so a downgrade cannot be ruled out. Turn on \"{AllowDowngradeName}\" to use a factory default with this file.");
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
            if (apps is not null && InstalledAppRules.Blocking(apps, target) is { Count: > 0 } blocking)
            {
                return new(FirmwareVerdict.AppsBlock, $"{current} → {target}. {InstalledAppRules.Describe(blocking)}");
            }

            // Off the recommended path: advice only (a factory default lifts the path).
            var advice = mode == FactoryDefaultMode.None ? AxisOsUpgradePath.Advice(current, target) : null;
            var note = mode == FactoryDefaultMode.None ? AxisOsUpgradePath.Note(current, target) : null;
            var extra = advice ?? note;
            return new(FirmwareVerdict.Upgrade, extra is null ? $"{current} → {target}" : $"{current} → {target}. {extra}") { PathAdvice = advice, PathNote = note };
        }

        var across = target.Major != current.Major ? " (older major version)" : string.Empty;
        if (!allowDowngrade)
        {
            return new(FirmwareVerdict.DowngradeBlocked, $"Downgrade {current} → {target}{across} is not allowed. Turn on \"{AllowDowngradeName}\".");
        }

        if (mode == FactoryDefaultMode.None)
        {
            return new(FirmwareVerdict.DowngradeBlocked, $"Downgrade {current} → {target}{across} requires a factory default (AXIS OS refuses downgrades that keep the settings).");
        }

        return new(FirmwareVerdict.Downgrade, $"Downgrade {current} → {target}{across} with {mode.ToString().ToLowerInvariant()} factory default.");
    }

    /// <summary>Message of <see cref="FirmwareVerdict.NoFileForModel"/>.</summary>
    public const string NoFileForModel = "No file for this model.";

    /// <summary>
    /// The file of several for one device: the file for its product, else a file whose product is not known from its name
    /// (the device checks it). One file is always that file. Index -1: none of the files is for this device.
    /// </summary>
    public static (int Index, FirmwareCheck Check) Choose(string? deviceModel, string? deviceVersion, IReadOnlyList<FirmwareImageInfo> images, FactoryDefaultMode mode, bool allowDowngrade,
        IReadOnlyList<InstalledApp>? apps = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 1)
        {
            return (0, Evaluate(deviceModel, deviceVersion, images[0], mode, allowDowngrade, apps));
        }

        var unnamed = -1;
        for (var i = 0; i < images.Count; i++)
        {
            if (images[i].IsRejected)
            {
                continue;
            }

            if (images[i].Product is { } product)
            {
                if (!string.IsNullOrWhiteSpace(deviceModel) && ProductMatches(product, deviceModel))
                {
                    return (i, Evaluate(deviceModel, deviceVersion, images[i], mode, allowDowngrade, apps));
                }
            }
            else if (unnamed < 0)
            {
                unnamed = i;
            }
        }

        return unnamed >= 0
            ? (unnamed, Evaluate(deviceModel, deviceVersion, images[unnamed], mode, allowDowngrade, apps))
            : (-1, new FirmwareCheck(FirmwareVerdict.NoFileForModel, NoFileForModel));
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
