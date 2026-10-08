using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.V1;

namespace Oadm.Client.Tags;

/// <summary>
/// One tag name as the client shows it: the name, its palette color (Unspecified = grey: no definition) and the device
/// count of the server's last list. One instance per name (case-insensitive) for the whole client, so a recolor
/// reaches every chip, group header and dialog row through bindings without touching the device rows.
/// </summary>
public sealed partial class TagInfo : ObservableObject
{
    internal TagInfo(string name)
    {
        Name = name;
    }

    [ObservableProperty] public partial string Name { get; private set; }

    /// <summary>Palette color; <see cref="TagColor.Unspecified"/> for a name without a definition (neutral grey).</summary>
    [ObservableProperty] public partial TagColor Color { get; private set; }

    [ObservableProperty] public partial bool IsDefined { get; private set; }

    /// <summary>Devices with this tag in the server's last list (the dialog counts the client mirror itself).</summary>
    [ObservableProperty] public partial int DeviceCount { get; private set; }

    /// <summary>A tag outside any store (rows built without a <see cref="TagStore"/>, tests).</summary>
    public static TagInfo Detached(string name) => new(name);

    internal void Update(string name, TagColor color, bool defined, int deviceCount)
    {
        Name = name;
        Color = color;
        IsDefined = defined;
        DeviceCount = deviceCount;
    }

    public override string ToString() => Name;
}

/// <summary>
/// The client's tag definitions (TagService.Watch): every tag the server lists, sorted by name, and one shared
/// <see cref="TagInfo"/> per name for the device rows. Names compare case-insensitive. UI thread only.
/// </summary>
public sealed class TagStore
{
    private readonly Dictionary<string, TagInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tags of the server's last list (definitions, then names only used on devices), sorted by name.</summary>
    public RangeObservableCollection<TagInfo> Tags { get; } = [];

    /// <summary>Raised after every <see cref="Reset"/> (definitions created, renamed, recolored or deleted).</summary>
    public event EventHandler? Changed;

    /// <summary>The server sent its list at least once.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The shared tag of a name; a name the server did not list yet is grey until it does.</summary>
    public TagInfo Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out TagInfo? tag))
        {
            tag = new TagInfo(name);
            _byName[name] = tag;
        }

        return tag;
    }

    public TagInfo? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Takes the server's list: updates the shared tags in place; names no longer listed turn grey.</summary>
    public void Reset(IEnumerable<DeviceTag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var listed = new List<TagInfo>();
        var seen = new HashSet<TagInfo>();
        foreach (DeviceTag tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Name))
            {
                continue;
            }

            TagInfo info = Resolve(tag.Name);
            if (seen.Add(info))
            {
                info.Update(tag.Name, tag.Defined ? tag.Color : TagColor.Unspecified, tag.Defined, tag.DeviceCount);
                listed.Add(info);
            }
        }

        foreach (TagInfo info in _byName.Values.Where(i => !seen.Contains(i)))
        {
            info.Update(info.Name, TagColor.Unspecified, defined: false, deviceCount: 0);
        }

        listed.Sort(Compare);
        Tags.ReplaceAll(listed);
        IsLoaded = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A tag the dialog just created or changed, before the server's next list arrives.</summary>
    public TagInfo Apply(DeviceTag tag, string? previousName = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        TagInfo info = Resolve(tag.Name);
        info.Update(tag.Name, tag.Defined ? tag.Color : TagColor.Unspecified, tag.Defined, info.DeviceCount);
        if (previousName is not null && !string.Equals(previousName, tag.Name, StringComparison.OrdinalIgnoreCase)
            && _byName.TryGetValue(previousName, out TagInfo? old))
        {
            old.Update(old.Name, TagColor.Unspecified, defined: false, deviceCount: 0);
            Tags.Remove(old);
        }

        if (!Tags.Contains(info))
        {
            List<TagInfo> list = [.. Tags, info];
            list.Sort(Compare);
            Tags.ReplaceAll(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    /// <summary>A tag the dialog just deleted.</summary>
    public void Forget(string name)
    {
        if (_byName.TryGetValue(name, out TagInfo? info))
        {
            info.Update(info.Name, TagColor.Unspecified, defined: false, deviceCount: 0);
            Tags.Remove(info);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Tag name order everywhere (dialog, chips, group headers): culture-aware, case-insensitive.</summary>
    public static int Compare(TagInfo? a, TagInfo? b) => StringComparer.CurrentCultureIgnoreCase.Compare(a?.Name, b?.Name);

    /// <summary>"42 devices" / "1 device".</summary>
    public static string DevicesText(int count) =>
        count == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} devices");
}
