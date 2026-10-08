using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tags;

/// <summary>
/// A group of the device grid in group mode: one tag (header "● Building A · 42 devices" in the tag color) or the
/// devices without a tag ("No tag", last). One instance per tag, reused across rebuilds, so the grid's group
/// descriptions keep matching.
/// </summary>
public sealed partial class TagGroupKey : ObservableObject
{
    public const string NoTagName = "No tag";

    internal TagGroupKey(TagInfo? tag)
    {
        Tag = tag;
    }

    /// <summary>The tag; null for "No tag".</summary>
    public TagInfo? Tag { get; }

    public bool IsNoTag => Tag is null;

    public string Name => Tag?.Name ?? NoTagName;

    /// <summary>Devices in the group (each device once per group).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial int Count { get; internal set; }

    /// <summary>"Building A · 42 devices".</summary>
    public string HeaderText => $"{Name} · {TagStore.DevicesText(Count)}";

    /// <summary>Color of the dot and the text; Unspecified (grey) for "No tag" and undefined tags.</summary>
    public TagColor Color => Tag?.Color ?? TagColor.Unspecified;

    internal void NotifyTagChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(Color));
    }

    public override string ToString() => HeaderText;
}

/// <summary>A device under one of its tags (group mode). A device with three tags is three of these.</summary>
public sealed class DeviceTagRow(DeviceRowViewModel row, TagGroupKey group) : IDeviceGridItem
{
    public DeviceRowViewModel Row { get; } = row;

    public TagGroupKey Group { get; } = group;

    public override string ToString() => $"{Row.DisplayAddress} in {Group.Name}";
}

/// <summary>
/// Group mode of the device grid: the shown devices as (device x tag) rows, a device with several tags under every one
/// of them, devices without tags under "No tag". Rebuilt in O(devices x tags) with one collection reset, only when the
/// shown devices, a device's tags or the tag names changed; status and other updates of a row reach the grid through
/// bindings on the shared <see cref="DeviceRowViewModel"/>. Row objects are reused across rebuilds. UI thread only.
/// </summary>
public sealed class DeviceTagGrouping
{
    private readonly Dictionary<TagInfo, TagGroupKey> _keys = [];
    private readonly TagGroupKey _noTag = new(null);
    private readonly Dictionary<(DeviceRowViewModel Row, TagGroupKey Group), DeviceTagRow> _rows = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _tagsAtBuild = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceRowViewModel> _source = [];
    private IReadOnlyList<TagGroupKey> _groups = [];

    /// <summary>The grid items of group mode, in group order (tag name, "No tag" last), devices in source order within a group.</summary>
    public RangeObservableCollection<DeviceTagRow> Rows { get; } = [];

    /// <summary>The groups that have devices, in display order (the grid's group keys).</summary>
    public IReadOnlyList<TagGroupKey> Groups => _groups;

    /// <summary>Raised after a rebuild changed which groups exist or their order (the view sets new group keys).</summary>
    public event EventHandler? GroupsChanged;

    /// <summary>Builds the rows for the shown devices.</summary>
    public void Rebuild(IReadOnlyList<DeviceRowViewModel> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _source = devices;
        _tagsAtBuild.Clear();
        var members = new Dictionary<TagGroupKey, List<DeviceTagRow>>();
        var used = new HashSet<(DeviceRowViewModel, TagGroupKey)>();
        foreach (DeviceRowViewModel device in devices)
        {
            _tagsAtBuild[device.Id] = device.Tags;
            if (device.TagChips.Count == 0)
            {
                Add(device, _noTag);
                continue;
            }

            foreach (TagInfo tag in device.TagChips)
            {
                if (!_keys.TryGetValue(tag, out TagGroupKey? key))
                {
                    key = new TagGroupKey(tag);
                    _keys[tag] = key;
                }

                Add(device, key);
            }
        }

        // Rows of devices or tags that are gone are not kept.
        if (_rows.Count > used.Count)
        {
            foreach ((DeviceRowViewModel, TagGroupKey) stale in _rows.Keys.Where(k => !used.Contains(k)).ToList())
            {
                _rows.Remove(stale);
            }
        }

        List<TagGroupKey> groups = members.Keys.Where(k => !k.IsNoTag).ToList();
        groups.Sort((a, b) => TagStore.Compare(a.Tag, b.Tag));
        if (members.ContainsKey(_noTag))
        {
            groups.Add(_noTag);
        }

        var rows = new List<DeviceTagRow>(used.Count);
        foreach (TagGroupKey group in groups)
        {
            List<DeviceTagRow> list = members[group];
            group.Count = list.Count;
            rows.AddRange(list);
        }

        bool groupsChanged = !groups.SequenceEqual(_groups);
        _groups = groups;
        Rows.ReplaceAll(rows);
        if (groupsChanged)
        {
            GroupsChanged?.Invoke(this, EventArgs.Empty);
        }

        void Add(DeviceRowViewModel device, TagGroupKey key)
        {
            if (!used.Add((device, key)))
            {
                return;
            }

            if (!_rows.TryGetValue((device, key), out DeviceTagRow? row))
            {
                row = new DeviceTagRow(device, key);
                _rows[(device, key)] = row;
            }

            if (!members.TryGetValue(key, out List<DeviceTagRow>? list))
            {
                list = [];
                members[key] = list;
            }

            list.Add(row);
        }
    }

    /// <summary>
    /// Whether any of these devices has other tags than at the last rebuild (O(changed ids)): only then the groups change.
    /// </summary>
    public bool TagsChanged(IEnumerable<string> deviceIds, Func<string, DeviceRowViewModel?> find)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(find);
        foreach (string id in deviceIds)
        {
            DeviceRowViewModel? row = find(id);
            if (_tagsAtBuild.TryGetValue(id, out IReadOnlyList<string>? tags) && row is not null && !ReferenceEquals(tags, row.Tags))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tag names or colors changed: the headers follow, the order may change.</summary>
    public void OnTagsRenamed()
    {
        foreach (TagGroupKey key in _keys.Values)
        {
            key.NotifyTagChanged();
        }

        Rebuild(_source);
    }

    /// <summary>The distinct devices of grid items (a device selected under two tags counts once), in first-seen order.</summary>
    public static List<DeviceRowViewModel> DistinctDevices(IEnumerable<object?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var seen = new HashSet<DeviceRowViewModel>();
        var result = new List<DeviceRowViewModel>();
        foreach (object? item in items)
        {
            if (item is IDeviceGridItem { Row: { } row } && seen.Add(row))
            {
                result.Add(row);
            }
        }

        return result;
    }
}
