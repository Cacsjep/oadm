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

    /// <summary>A version from axis.com was chosen without a MyAxis sign-in (dialog only).</summary>
    SignInNeeded = 10,

    /// <summary>The chosen version from axis.com is no longer listed (dialog only).</summary>
    NotListed = 11,
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
    public const string AllowDowngradeName = "Allow downgrade";

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
                    $"The AXIS OS version could not be compared, so a downgrade cannot be ruled out. Turn on \"{AllowDowngradeName}\" to use a factory default with this file.");
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

    /// <summary>
    /// The version from axis.com of <paramref name="online"/> that names no devices for a device (README "Firmware online"):
    /// the entry for its product and its AXIS OS major, else the entry for its product made for devices of unknown version;
    /// -1 when none.
    /// </summary>
    public static int FindOnline(string? deviceModel, string? deviceVersion, IReadOnlyList<FirmwareOnline> online)
    {
        ArgumentNullException.ThrowIfNull(online);
        var major = AxisOsVersion.TryParse(deviceVersion)?.Major;
        var unknown = -1;
        for (var i = 0; i < online.Count; i++)
        {
            if (online[i].Devices is not null || !OnlineFirmware.Matches(online[i].Product, deviceModel))
            {
                continue;
            }

            if (online[i].FromMajor == major)
            {
                return i;
            }

            if (online[i].FromMajor is null && unknown < 0)
            {
                unknown = i;
            }
        }

        return unknown;
    }

    /// <summary>
    /// The firmware for one device of a run (the task, and the tests that hold the dialog to it), README "Payload":
    /// <list type="number">
    /// <item>a version from axis.com that names the device (its product must be the device's model);</item>
    /// <item>a chosen file that names the device;</item>
    /// <item>a version from axis.com for its product and AXIS OS major (<see cref="FindOnline"/>);</item>
    /// <item>a chosen file that names no device, by its product as in <see cref="Choose"/> (next to other choices a single
    /// such file is only for its own product).</item>
    /// </list>
    /// <c>Online</c> or <c>File</c> is the index taken (into <paramref name="online"/> or <paramref name="files"/>), the
    /// other -1; both -1: no file for this model.
    /// </summary>
    /// <remarks>
    /// <paramref name="runIndex"/>: the run's <see cref="FirmwareRunIndex"/> of the same <paramref name="online"/> and
    /// <paramref name="files"/>, built once per run (the task's payload cache), so a device costs O(1); without it one is
    /// built for this call.
    /// </remarks>
    public static (int Online, int File, FirmwareCheck Check) ChooseFor(Guid deviceId, string? deviceModel, string? deviceVersion,
        IReadOnlyList<FirmwareOnline> online, IReadOnlyList<FirmwareImageInfo> onlineImages, IReadOnlyList<FirmwareFile> files,
        IReadOnlyList<FirmwareImageInfo> fileImages, FactoryDefaultMode mode, bool allowDowngrade, IReadOnlyList<InstalledApp>? apps = null,
        FirmwareRunIndex? runIndex = null)
    {
        ArgumentNullException.ThrowIfNull(online);
        ArgumentNullException.ThrowIfNull(onlineImages);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(fileImages);
        var run = runIndex ?? new FirmwareRunIndex(online, files);
        if (run.OnlineNaming(deviceId) is var named and >= 0)
        {
            return (named, -1, EvaluateOnline(online[named], onlineImages[named], deviceModel, deviceVersion, mode, allowDowngrade, apps));
        }

        if (run.FileNaming(deviceId) is var namedFile and >= 0)
        {
            return (-1, namedFile, Evaluate(deviceModel, deviceVersion, fileImages[namedFile], mode, allowDowngrade, apps));
        }

        var index = run.OnlineFor(deviceModel, deviceVersion);
        if (index >= 0)
        {
            return (index, -1, Evaluate(deviceModel, deviceVersion, onlineImages[index], mode, allowDowngrade, apps));
        }

        var byProduct = run.FilesByProduct;

        if (byProduct.Count == 0)
        {
            return (-1, -1, new FirmwareCheck(FirmwareVerdict.NoFileForModel, NoFileForModel));
        }

        // The old payload (only files by product): one file is for every device; next to other choices only for its product.
        var alone = byProduct.Count == 1 && online.Count == 0 && files.Count == 1;
        var images = byProduct.Select(i => fileImages[i]).ToList();
        var (file, check) = Choose(deviceModel, deviceVersion, alone || images.Count > 1 ? images : [images[0], NoImage], mode, allowDowngrade, apps);
        return (-1, file < 0 ? -1 : byProduct[file], check);
    }

    /// <summary>A version from axis.com for a device: refused when it is for another product, else the decision table.</summary>
    public static FirmwareCheck EvaluateOnline(FirmwareOnline entry, FirmwareImageInfo image, string? deviceModel, string? deviceVersion,
        FactoryDefaultMode mode, bool allowDowngrade, IReadOnlyList<InstalledApp>? apps = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !string.IsNullOrWhiteSpace(deviceModel) && !OnlineFirmware.Matches(entry.Product, deviceModel)
            ? new FirmwareCheck(FirmwareVerdict.WrongProduct, $"The file is for {entry.Product}, the device is {NormalizeProduct(deviceModel)}.")
            : Evaluate(deviceModel, deviceVersion, image, mode, allowDowngrade, apps);
    }

    /// <summary>A rejected placeholder: makes <see cref="Choose"/> match a single file by product.</summary>
    private static readonly FirmwareImageInfo NoImage = new(string.Empty, 0, null, null, "none");

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

/// <summary>
/// The choices of one run's payload indexed once (README "Payload"): which entry names a device (the first one, as the
/// payload lists them), the version from axis.com of each product and AXIS OS major (<see cref="FirmwareCompatibility.FindOnline"/>)
/// and the files by product. Each lookup is O(1), so a run of 100,000 devices costs the server no per-device scan.
/// Read only after construction: thread safe.
/// </summary>
public sealed class FirmwareRunIndex
{
    private readonly Dictionary<Guid, int> _online = [];
    private readonly Dictionary<Guid, int> _files = [];
    private readonly Dictionary<(string Product, int? Major), int> _byProduct = [];

    public FirmwareRunIndex(IReadOnlyList<FirmwareOnline> online, IReadOnlyList<FirmwareFile> files)
    {
        ArgumentNullException.ThrowIfNull(online);
        ArgumentNullException.ThrowIfNull(files);
        for (var i = 0; i < online.Count; i++)
        {
            if (online[i].Devices is { } devices)
            {
                foreach (var id in devices)
                {
                    _online.TryAdd(id, i);
                }
            }
            else if (OnlineFirmware.Normalize(online[i].Product) is { Length: > 0 } product)
            {
                _byProduct.TryAdd((product, online[i].FromMajor), i);
            }
        }

        var byProduct = new List<int>();
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Devices is { } devices)
            {
                foreach (var id in devices)
                {
                    _files.TryAdd(id, i);
                }
            }
            else
            {
                byProduct.Add(i);
            }
        }

        FilesByProduct = byProduct;
    }

    /// <summary>The first version from axis.com that names the device; -1 when none.</summary>
    public int OnlineNaming(Guid deviceId) => _online.GetValueOrDefault(deviceId, -1);

    /// <summary>The first file that names the device; -1 when none.</summary>
    public int FileNaming(Guid deviceId) => _files.GetValueOrDefault(deviceId, -1);

    /// <summary>The files that name no device, in payload order.</summary>
    public IReadOnlyList<int> FilesByProduct { get; }

    /// <summary>
    /// <see cref="FirmwareCompatibility.FindOnline"/> in O(1): the entry of the device's product and AXIS OS major, else the
    /// one of its product for devices of unknown version; -1 when none.
    /// </summary>
    public int OnlineFor(string? deviceModel, string? deviceVersion)
    {
        var product = OnlineFirmware.Normalize(deviceModel);
        if (product.Length == 0)
        {
            return -1;
        }

        var major = AxisOsVersion.TryParse(deviceVersion)?.Major;
        return major is not null && _byProduct.TryGetValue((product, major), out var exact) ? exact
            : _byProduct.TryGetValue((product, null), out var unknown) ? unknown
            : -1;
    }
}
