using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client.Controls;

/// <summary>A row of the device tree (<see cref="DeviceTree"/>): a tag group header or a device under it.</summary>
public abstract class DeviceTreeRow : ObservableObject
{
    /// <summary>A tag header (true) or a device (false).</summary>
    public abstract bool IsGroup { get; }
}

/// <summary>A tag ("No tag" for devices without one); collapsing hides its devices.</summary>
public sealed partial class DeviceTreeGroup(string key, string name) : DeviceTreeRow
{
    public override bool IsGroup => true;

    /// <summary>The tag name, empty for "No tag".</summary>
    public string Key { get; } = key;

    /// <summary>What the header shows.</summary>
    public string Name { get; } = name;

    /// <summary>The group shows its devices.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>A device under one tag group; a device with several tags has one row per tag.</summary>
public sealed partial class DeviceTreeDevice(IDeviceInfo device, string groupKey) : DeviceTreeRow
{
    public override bool IsGroup => false;

    /// <summary>The device.</summary>
    public IDeviceInfo Device { get; } = device;

    /// <summary>The tag of the group this row is under.</summary>
    public string GroupKey { get; } = groupKey;

    /// <summary>Its address.</summary>
    public string Address => Device.Address;

    /// <summary>For the shared device icon: the category icon, colored and explained by the status.</summary>
    public DeviceCategory Category => Device.Category;

    /// <summary>Its status, for the device icon.</summary>
    public DeviceStatus Status => Device.Status;

    /// <summary>The page shows this row as the current one (its tab is active).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>The device is open on the page (it has a tab).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }
}

/// <summary>
/// The rows of a device tree: the devices grouped by tag in tag order (a device with several tags under each, "No tag"
/// last), sorted by address, filtered by a search and an optional <see cref="Filter"/>; collapsed groups hide their devices.
/// One reset per rebuild, O(devices x tags): 5,000 devices are fine.
/// </summary>
public sealed class DeviceTreeModel
{
    /// <summary>The group of devices without a tag, always last.</summary>
    public const string NoTag = "No tag";

    private readonly Dictionary<string, bool> _collapsed = new(StringComparer.Ordinal);
    private List<DeviceTreeDevice> _devices = [];

    /// <summary>Group headers and the devices of expanded groups, for the list.</summary>
    public RangeObservableCollection<DeviceTreeRow> Rows { get; } = [];

    /// <summary>Every device row, also those of collapsed groups.</summary>
    public IReadOnlyList<DeviceTreeDevice> DeviceRows => _devices;

    /// <summary>Only these devices are listed (e.g. devices with video); null lists all.</summary>
    public Func<IDeviceInfo, bool>? Filter { get; init; }

    /// <summary>The number of devices the last rebuild listed, before the search (devices the page can use).</summary>
    public int UsableCount { get; private set; }

    private IReadOnlyList<IDeviceInfo> _source = [];
    private string _search = string.Empty;

    /// <summary>Builds the rows from <paramref name="devices"/> and <paramref name="search"/> in one reset.</summary>
    public void Rebuild(IReadOnlyList<IDeviceInfo> devices, string search)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _source = devices;
        _search = search?.Trim() ?? string.Empty;
        Rebuild();
    }

    /// <summary>Collapses or expands a group (the rows are rebuilt).</summary>
    public void Toggle(DeviceTreeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _collapsed[group.Key] = group.IsExpanded;
        Rebuild();
    }

    private void Rebuild()
    {
        var usable = Filter is null ? _source : _source.Where(Filter).ToList();
        UsableCount = usable.Count;
        var devices = usable
            .Where(d => _search.Length == 0 || Matches(d, _search))
            .OrderBy(d => DeviceOrder.AddressKey(d.Address), StringComparer.Ordinal)
            .ToList();
        var groups = new SortedDictionary<string, List<IDeviceInfo>>(StringComparer.CurrentCultureIgnoreCase);
        var untagged = new List<IDeviceInfo>();
        foreach (var device in devices)
        {
            if (device.Tags.Count == 0)
            {
                untagged.Add(device);
                continue;
            }

            foreach (var tag in device.Tags)
            {
                if (!groups.TryGetValue(tag, out var list))
                {
                    groups[tag] = list = [];
                }

                list.Add(device);
            }
        }

        var rows = new List<DeviceTreeRow>(devices.Count + groups.Count + 1);
        var all = new List<DeviceTreeDevice>(devices.Count);
        void Add(string key, string name, List<IDeviceInfo> members)
        {
            var collapsed = _collapsed.TryGetValue(key, out var c) && c;
            rows.Add(new DeviceTreeGroup(key, name) { IsExpanded = !collapsed });
            foreach (var device in members)
            {
                var row = new DeviceTreeDevice(device, key);
                all.Add(row);
                if (!collapsed)
                {
                    rows.Add(row);
                }
            }
        }

        foreach (var (tag, members) in groups)
        {
            Add(tag, tag, members);
        }

        if (untagged.Count > 0)
        {
            Add(string.Empty, NoTag, untagged);
        }

        _devices = all;
        Rows.ReplaceAll(rows);
    }

    /// <summary>The search matches address, model, MAC address, host name or a tag.</summary>
    public static bool Matches(IDeviceInfo device, string search)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(search);
        return device.Address.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (device.Model?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || device.Serial.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (device.HostName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || device.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
