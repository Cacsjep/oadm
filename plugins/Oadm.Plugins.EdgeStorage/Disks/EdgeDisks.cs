using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage.Disks;

/// <summary>
/// One disk of <c>disks/list.cgi?diskid=all</c> (VAPIX Edge storage, disk management API): the SD card (<c>SD_DISK</c>) or a
/// network share. Sizes in kB as the camera sends them. Verified on AXIS OS 12.11 (P3265-V, Q1615 Mk III): a camera without an
/// SD card and without a network share lists both with status "disconnected"; an unmounted card is "connected" with size 0.
/// Part of Edge storage; Edge Recording Export and Hardening scan link it as source.
/// </summary>
public sealed record EdgeDisk(
    string Id,
    string? Group = null,
    string? Status = null,
    long? TotalKb = null,
    long? FreeKb = null,
    int? CleanupMaxAgeDays = null,
    string? CleanupPolicy = null,
    bool? Locked = null,
    bool? Full = null,
    bool? ReadOnly = null,
    string? FileSystem = null,
    bool? EncryptionEnabled = null,
    bool? Encrypted = null)
{
    public const string SdCardId = "SD_DISK";

    public const string NetworkShareId = "NetworkShare";

    public bool IsSdCard => Id.StartsWith(SdCardId, StringComparison.OrdinalIgnoreCase);

    public bool IsNetworkShare => string.Equals(Id, NetworkShareId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Mounted and in use (status OK).</summary>
    public bool IsConnected => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>A card in the slot or a share that is set up: every status but "disconnected".</summary>
    public bool IsPresent => !string.Equals(Status, "disconnected", StringComparison.OrdinalIgnoreCase);

    /// <summary>Present but not mounted (status "connected"): after a format or an unmount, or a card the camera does not use.</summary>
    public bool IsUnmounted => string.Equals(Status, "connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>"SD card", "Network share" or the disk id.</summary>
    public string Label => IsSdCard ? "SD card" : IsNetworkShare ? "Network share" : Id;

    /// <summary>
    /// What is wrong in plain words ("failed", "is not in use"), or null when the disk is fine: a disk that failed, is
    /// encrypted with an unknown passphrase, is not in use, read-only or locked. A full disk is normal (the camera removes the
    /// oldest recordings).
    /// </summary>
    public string? Problem => Status?.ToLowerInvariant() switch
    {
        "failed" => "failed",
        "wrong passphrase" or "no passphrase" => "is encrypted and cannot be opened",
        "not encrypted" => "is not encrypted as the camera requires",
        "connected" => "is not in use",
        _ when ReadOnly == true => "is read-only",
        _ when Locked == true => "is locked",
        _ => null,
    };

    /// <summary>"The SD card failed", or null.</summary>
    public string? ProblemText => Problem is { } problem ? "The " + InSentence + " " + problem : null;

    /// <summary>The label inside a sentence: "SD card", "network share".</summary>
    public string InSentence => IsSdCard ? "SD card" : IsNetworkShare ? "network share" : "disk " + Id;
}

/// <summary>The disk list request and its parser (XML through <see cref="DeviceXml"/>, names without namespace).</summary>
public static class EdgeDisks
{
    /// <summary>Every disk with its state (Admin and Operator on the device).</summary>
    public const string ListUri = "axis-cgi/disks/list.cgi?diskid=all";

    public const string NoDisk = "No SD card or network share";

    public static IReadOnlyList<EdgeDisk> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        try
        {
            return [.. DeviceXml.Parse(xml.Trim()).Descendants().Where(e => e.Name.LocalName == "disk").Select(d => new EdgeDisk(
                Attr(d, "diskid") ?? string.Empty,
                Attr(d, "group"),
                Attr(d, "status"),
                Long(Attr(d, "totalsize")),
                Long(Attr(d, "freesize")),
                Long(Attr(d, "cleanupmaxage")) is { } age && age <= int.MaxValue ? (int)age : null,
                Attr(d, "cleanuppolicy"),
                Bool(Attr(d, "locked")),
                Bool(Attr(d, "full")),
                Bool(Attr(d, "readonly")),
                Attr(d, "filesystem"),
                Bool(Attr(d, "encryptionenabled") ?? Attr(d, "diskencryptionenabled")),
                Bool(Attr(d, "diskencrypted"))))
                .Where(d => d.Id.Length > 0)];
        }
        catch (XmlException ex)
        {
            throw new FormatException("disks/list.cgi: the device sent an unreadable answer.", ex);
        }
    }

    /// <summary>
    /// The first problem of the present disks in plain words, SD card before network share ("The SD card failed"), or null
    /// when they are fine; <see cref="NoDisk"/> when none is present.
    /// </summary>
    public static string? FirstProblem(IReadOnlyList<EdgeDisk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var present = disks.Where(d => d.IsPresent).OrderBy(d => d.IsSdCard ? 0 : 1).ToList();
        return present.Count == 0 ? NoDisk : present.Select(d => d.ProblemText).FirstOrDefault(p => p is not null);
    }

    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value.Trim();

    private static bool? Bool(string? value) => value?.ToUpperInvariant() switch
    {
        "YES" or "TRUE" or "1" => true,
        "NO" or "FALSE" or "0" => false,
        _ => null,
    };

    private static long? Long(string? value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}
