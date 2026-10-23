using Oadm.Core.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// A device group (table DeviceGroups, docs/spec/device-scope.md): a named set of rules that limits a user to part of the
/// devices. A device is in the group when a site (or "This server"), a tag or a single serial covers it and its serial is
/// not excepted. Sites and tags keep up: a new device of a chosen site or tag is in the group at once.
/// </summary>
public sealed class DeviceGroup
{
    /// <summary>Most groups one server keeps.</summary>
    public const int MaxGroups = 200;

    /// <summary>Longest name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Longest description.</summary>
    public const int MaxDescriptionLength = 200;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>1..64 characters, trimmed, unique case-insensitive.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Upper invariant of <see cref="Name"/>: the unique index.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Optional, at most 200 characters.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>"This server": every device without a site (devices kept from a removed site keep its id and are not covered).</summary>
    public bool ThisServer { get; set; }

    /// <summary>Sites by id (JSON column). A site that no longer exists covers nothing.</summary>
    public List<Guid> SiteIds { get; set; } = [];

    /// <summary>Tags by definition id (JSON column), so a renamed tag stays in the group; a deleted tag covers nothing.</summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>Single devices by serial number (JSON column, normalized, sorted): stable when a device is removed and added again.</summary>
    public List<string> Serials { get; set; } = [];

    /// <summary>Devices excepted by serial number (JSON column), even when a site, tag or serial covers them.</summary>
    public List<string> ExceptSerials { get; set; } = [];

    /// <summary>Starts at 1 and grows with every change of the rules (name and description changes too).</summary>
    public long Revision { get; set; } = 1;

    public DateTime CreatedUtc { get; set; }

    public DateTime ChangedUtc { get; set; }

    /// <summary>The rules as one value.</summary>
    public DeviceGroupRules Rules => new(ThisServer, SiteIds, TagIds, Serials, ExceptSerials);

    public DeviceGroup Clone()
    {
        var copy = (DeviceGroup)MemberwiseClone();
        copy.SiteIds = [.. SiteIds];
        copy.TagIds = [.. TagIds];
        copy.Serials = [.. Serials];
        copy.ExceptSerials = [.. ExceptSerials];
        return copy;
    }

    public override string ToString() => $"DeviceGroup {Name} ({Id})";
}

/// <summary>The rules of a device group.</summary>
/// <param name="ThisServer">Every device without a site.</param>
/// <param name="SiteIds">Every device of these sites.</param>
/// <param name="TagIds">Every device with one of these tags (definition ids).</param>
/// <param name="Serials">These single devices.</param>
/// <param name="ExceptSerials">Never these devices.</param>
public sealed record DeviceGroupRules(
    bool ThisServer,
    IReadOnlyList<Guid> SiteIds,
    IReadOnlyList<Guid> TagIds,
    IReadOnlyList<string> Serials,
    IReadOnlyList<string> ExceptSerials)
{
    /// <summary>Most sites (<see cref="Sites.Site.MaxSites"/>), tags and serials one group lists.</summary>
    public const int MaxSites = Sites.Site.MaxSites;

    /// <inheritdoc cref="MaxSites"/>
    public const int MaxTags = TagNames.MaxDefinitions;

    /// <inheritdoc cref="MaxSites"/>
    public const int MaxSerials = Sdk.Devices.DeviceLimits.MaxDevices;

    /// <summary>No device at all.</summary>
    public static DeviceGroupRules Empty { get; } = new(false, [], [], [], []);

    /// <summary>
    /// Distinct ids, normalized and distinct serials (sorted), within the limits; a serial both single and excepted stays
    /// excepted. Throws <see cref="DeviceGroupException"/> (InvalidArgument) for a bad serial or too many entries.
    /// </summary>
    public DeviceGroupRules Normalize()
    {
        var sites = SiteIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var tags = TagIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var serials = NormalizeSerials(Serials);
        var except = NormalizeSerials(ExceptSerials);
        if (sites.Count > MaxSites || tags.Count > MaxTags || serials.Count > MaxSerials || except.Count > MaxSerials)
        {
            throw new DeviceGroupException(DeviceGroupError.InvalidArgument, DeviceGroupTexts.TooManyRules);
        }

        return new DeviceGroupRules(ThisServer, sites, tags, serials, except);
    }

    /// <summary>True when the rules cover no device (no site, tag, serial and not "This server").</summary>
    public bool IsEmpty => !ThisServer && SiteIds.Count == 0 && TagIds.Count == 0 && Serials.Count == 0;

    private static List<string> NormalizeSerials(IReadOnlyList<string> serials)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var serial in serials)
        {
            if (!DeviceSerial.TryNormalize(serial, out var normalized))
            {
                throw new DeviceGroupException(DeviceGroupError.InvalidArgument, DeviceGroupTexts.BadSerial(serial));
            }

            set.Add(normalized);
        }

        return [.. set];
    }
}

/// <summary>Why a device group operation failed.</summary>
public enum DeviceGroupError
{
    InvalidArgument,
    NotFound,
    AlreadyExists,
    Precondition,
    ResourceExhausted,
}

/// <summary>A device group operation failed; the message is for the user.</summary>
public sealed class DeviceGroupException : Exception
{
    public DeviceGroupException()
    {
    }

    public DeviceGroupException(string message)
        : base(message)
    {
    }

    public DeviceGroupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DeviceGroupException(DeviceGroupError error, string message)
        : base(message)
    {
        Error = error;
    }

    public DeviceGroupError Error { get; } = DeviceGroupError.InvalidArgument;
}

/// <summary>The user texts of device groups (one phrase per concept, shared by the server, the client and the plugins).</summary>
public static class DeviceGroupTexts
{
    /// <summary>The name of "every device without a site" in the editor, summaries and Your role.</summary>
    public const string ThisServer = "This server";

    /// <summary>The choice "every device" of the user form and owners of shared objects.</summary>
    public const string AllDevices = "All devices";

    /// <summary>A user or an object without devices (its group was deleted).</summary>
    public const string NoDevices = "No devices";

    /// <summary>The refusal of a method only users with all devices may call.</summary>
    public const string AllDevicesOnly = "Only users with all devices can do this.";

    public const string ChooseDevices = "Choose All devices or a device group.";
    public const string UnknownGroup = "The device group does not exist (any more).";
    public const string EnterName = "Enter a name.";
    public const string TooManyRules = "A device group can have at most 100 sites, 500 tags and 30,000 single devices.";
    public const string TooManyGroups = "OADM keeps at most 200 device groups.";
    public const string ChangedMeanwhile = "Another administrator changed this device group meanwhile. Close it and make your changes again.";

    public static string NameTooLong => $"The name must have at most {DeviceGroup.MaxNameLength} characters.";

    public static string DescriptionTooLong => $"The description must have at most {DeviceGroup.MaxDescriptionLength} characters.";

    public static string NameInUse(string name) => $"A device group named {name} exists already.";

    public static string BadSerial(string? serial) => $"{serial} is not a serial number (12 hex digits).";

    /// <summary>The note of a tag or site a group uses, before it is deleted: "Used by device group Building B".</summary>
    public static string UsedBy(IReadOnlyCollection<string> groupNames)
    {
        ArgumentNullException.ThrowIfNull(groupNames);
        return groupNames.Count == 1
            ? "Used by device group " + groupNames.First()
            : "Used by device groups " + string.Join(", ", groupNames);
    }
}
