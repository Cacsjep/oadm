using System.Globalization;

namespace Oadm.Core.Devices;

/// <summary>The fixed tag palette (theme brushes <c>Oadm.Tag.&lt;Name&gt;Brush</c> in the client). None = undefined tag (grey).</summary>
public enum TagColor
{
    None = 0,
    Blue = 1,
    Green = 2,
    Teal = 3,
    Amber = 4,
    Orange = 5,
    Red = 6,
    Pink = 7,
    Violet = 8,
}

/// <summary>A tag definition (table TagDefinitions): a name devices carry in <see cref="Device.Tags"/> and its color.</summary>
public sealed class TagDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name, 1..<see cref="TagNames.MaxLength"/> characters, trimmed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="TagNames.Normalize"/> of the name; unique (names are unique case-insensitive).</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public TagColor Color { get; set; } = TagColor.Blue;

    public DateTime CreatedUtc { get; set; }

    public TagDefinition Clone() => (TagDefinition)MemberwiseClone();
}

/// <summary>A tag as listed: a definition with its device count, or a name used on devices without a definition.</summary>
public sealed record TagSummary(Guid? Id, string Name, TagColor Color, int DeviceCount, bool Defined);

/// <summary>Result of a rename / recolor.</summary>
public sealed record TagUpdateResult(TagDefinition Tag, string OldName, TagColor OldColor, int DevicesChanged);

/// <summary>
/// Result of <see cref="DeviceTagStore.SetDeviceTagsAsync"/>: how many devices changed, the definitions created and every
/// changed device before and after (the membership audit of device groups).
/// </summary>
public sealed record SetDeviceTagsResult(int DevicesChanged, IReadOnlyList<TagDefinition> Created)
{
    /// <summary>The changed devices before and after the write.</summary>
    public IReadOnlyList<(Sdk.Devices.IDeviceInfo? Before, Sdk.Devices.IDeviceInfo? After)> Changes { get; init; } = [];
}

/// <summary>Result of <see cref="DeviceTagStore.DeleteAsync"/>: the deleted name and every changed device before and after.</summary>
public sealed record TagDeleteResult(string Name, int DevicesChanged, IReadOnlyList<(Sdk.Devices.IDeviceInfo? Before, Sdk.Devices.IDeviceInfo? After)> Changes);

/// <summary>
/// A user with a device group tried to add or remove a tag only users with all devices may add or remove on devices: a tag
/// a device group uses (<see cref="TagInUseByGroupException"/>) or one a plugin protects (<see cref="ProtectedTagException"/>,
/// docs/spec/tags.md). Nothing was changed.
/// </summary>
public abstract class TagNeedsAllDevicesException(string name, string message) : InvalidOperationException(message)
{
    public string Name { get; } = name;
}

/// <summary>
/// A user with a device group tried to add or remove a tag a device group uses: that would move devices between groups
/// (docs/spec/device-scope.md, Decisions: Tags). Nothing was changed.
/// </summary>
public sealed class TagInUseByGroupException(string name)
    : TagNeedsAllDevicesException(name, $"A device group uses the tag {name}. Only users with all devices can add or remove it. Nothing was changed.");

/// <summary>
/// A user with a device group tried to add or remove a tag a plugin protects (<see cref="ProtectedTagRegistry"/>, e.g. a tag
/// of the 802.1X server's VLAN rules); the message is the plugin's refusal. Nothing was changed.
/// </summary>
public sealed class ProtectedTagException(string name, string message) : TagNeedsAllDevicesException(name, message);

/// <summary>
/// A rename or delete of a tag a running plugin protects (<see cref="ProtectedTagRegistry.ChangeRefusalFor"/>): the plugin's
/// settings name the tag, so every user, administrators too, changes them first. The message is the plugin's text. Nothing was changed.
/// </summary>
public sealed class ProtectedTagChangeException(string name, string message) : InvalidOperationException(message)
{
    public string Name { get; } = name;
}

/// <summary>A tag name that cannot be used; the message says why (shown below the name field).</summary>
public sealed class TagNameException(string message) : ArgumentException(message);

/// <summary>Another tag already has this name (case-insensitive).</summary>
public sealed class TagConflictException(string name)
    : InvalidOperationException($"A tag named \"{name}\" already exists.")
{
    public string Name { get; } = name;
}

/// <summary>A limit of the tag feature (definitions, tags per device) would be exceeded.</summary>
public sealed class TagLimitException(string message) : InvalidOperationException(message);

/// <summary>Rules for tag names. Pure functions.</summary>
public static class TagNames
{
    public const int MaxLength = 64;

    /// <summary>Most tag definitions of a server.</summary>
    public const int MaxDefinitions = 500;

    /// <summary>Most tags of one device.</summary>
    public const int MaxTagsPerDevice = 20;

    /// <summary>Names compare case-insensitive everywhere (uniqueness, matching on devices).</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Why <paramref name="name"/> cannot be a tag name (after trimming), null when it can.</summary>
    public static string? Problem(string? name)
    {
        string text = name?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter a tag name.";
        }

        if (text.Length > MaxLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"A tag name has at most {MaxLength} characters.");
        }

        if (text.Contains(';', StringComparison.Ordinal))
        {
            return "A tag name cannot contain \";\".";
        }

        if (text.Any(char.IsControl))
        {
            return "A tag name cannot contain control characters.";
        }

        return null;
    }

    /// <summary>The trimmed name.</summary>
    /// <exception cref="TagNameException">The name cannot be used.</exception>
    public static string Clean(string? name) =>
        Problem(name) is { } problem ? throw new TagNameException(problem) : name!.Trim();

    /// <summary>Key of the unique index: trimmed, upper case (invariant).</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToUpperInvariant();
    }

    /// <summary>The color of the n-th definition created without a color: the palette in order, then again.</summary>
    public static TagColor DefaultColor(int existingDefinitions) => (TagColor)((Math.Max(0, existingDefinitions) % 8) + 1);

    /// <summary>Distinct (case-insensitive, first spelling wins) and sorted by name: how devices store their tags.</summary>
    public static List<string> Canonical(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var seen = new HashSet<string>(Comparer);
        var list = new List<string>();
        foreach (string tag in tags)
        {
            string text = tag.Trim();
            if (text.Length > 0 && seen.Add(text))
            {
                list.Add(text);
            }
        }

        list.Sort(Comparer);
        return list;
    }

    /// <summary>"+Building A -PTZ": the audit text of a tag change.</summary>
    public static string ChangeText(IEnumerable<string> added, IEnumerable<string> removed) =>
        string.Join(' ', added.Select(a => "+" + a).Concat(removed.Select(r => "-" + r)));
}
