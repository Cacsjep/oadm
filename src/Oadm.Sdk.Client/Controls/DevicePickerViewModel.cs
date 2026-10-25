using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client.Controls;

/// <summary>What a <see cref="DeviceChoice"/> means.</summary>
public enum DeviceChoiceKind
{
    /// <summary>The devices in <see cref="DeviceChoice.DeviceIds"/> and every device with one of <see cref="DeviceChoice.Tags"/>.</summary>
    Chosen = 0,

    /// <summary>Every managed device, also devices added later.</summary>
    All = 1,

    /// <summary>The device of the event (event rules: "the same device", "the device of the event").</summary>
    EventDevice = 2,
}

/// <summary>The result of the <see cref="DevicePickerWindow"/>: all devices, the device of the event, or chosen devices and tags.</summary>
/// <param name="Kind">All devices, the device of the event, or chosen devices and tags.</param>
/// <param name="DeviceIds">The chosen devices.</param>
/// <param name="Tags">The chosen tags: every device with one of them.</param>
public sealed record DeviceChoice(DeviceChoiceKind Kind, IReadOnlyList<Guid> DeviceIds, IReadOnlyList<string> Tags)
{
    /// <summary>Every managed device, also devices added later.</summary>
    public static DeviceChoice AllDevices { get; } = new(DeviceChoiceKind.All, [], []);

    /// <summary>The device of the event.</summary>
    public static DeviceChoice EventDevice { get; } = new(DeviceChoiceKind.EventDevice, [], []);

    /// <summary>These devices and the devices with these tags.</summary>
    public static DeviceChoice Devices(IEnumerable<Guid> ids, IEnumerable<string>? tags = null) => new(DeviceChoiceKind.Chosen, [.. ids], [.. tags ?? []]);

    /// <summary>A chosen set without devices and tags.</summary>
    public bool IsEmpty => Kind == DeviceChoiceKind.Chosen && DeviceIds.Count == 0 && Tags.Count == 0;
}

/// <summary>What the picker offers besides single devices (event rules: everything; a plain device list: nothing).</summary>
public sealed record DevicePickerOptions
{
    /// <summary>Offer "All devices".</summary>
    public bool AllowAll { get; init; }

    /// <summary>Offer the tags of the devices (a tag means every device with it, also devices tagged later).</summary>
    public bool AllowTags { get; init; }

    /// <summary>Offer the device of the event with this text ("The device of the event", "The same device"); null = not offered.</summary>
    public string? EventDeviceText { get; init; }

    /// <summary>
    /// With <see cref="AllowTags"/>: tag names offered even when no device has them yet (e.g. the tag definitions of the
    /// server), for devices tagged later. The tags of the devices and of the initial choice are always offered.
    /// </summary>
    public IReadOnlyList<string> TagNames { get; init; } = [];

    /// <summary>
    /// Exactly one device ("Use configuration from: Device"): checking a device unchecks the one before; Select all and
    /// Select none are not offered. Not combined with all devices, tags or the device of the event.
    /// </summary>
    public bool SingleDevice { get; init; }

    /// <summary>
    /// The device group editor: sites and tags on the left (with their device counts), the devices they cover checked and
    /// locked with the site or tag, single devices tickable, covered devices can be excepted. Not combined with the
    /// other options; the result is <see cref="DevicePickerViewModel.GroupChoice"/>.
    /// </summary>
    public DeviceGroupPickerOptions? Group { get; init; }

    /// <summary>Every option: all devices, tags (plus <paramref name="tagNames"/>) and, with <paramref name="eventDeviceText"/>, the device of the event.</summary>
    public static DevicePickerOptions Full(string? eventDeviceText = null, IEnumerable<string>? tagNames = null) =>
        new() { AllowAll = true, AllowTags = true, EventDeviceText = eventDeviceText, TagNames = [.. tagNames ?? []] };
}

/// <summary>A site offered by the device group editor: <see cref="Id"/> null = "This server" (every device without a site).</summary>
/// <param name="Id">The site's id; null for the devices without a site.</param>
/// <param name="Name">"Building B", "This server".</param>
public sealed record DevicePickerSite(Guid? Id, string Name);

/// <summary>A tag offered by the device group editor, by the id of its definition (a renamed tag stays in the group).</summary>
/// <param name="Id">The id of the tag definition.</param>
/// <param name="Name">The tag name.</param>
public sealed record DevicePickerTag(string Id, string Name);

/// <summary>What the device group editor offers (<see cref="DevicePickerOptions.Group"/>): the sites and the defined tags.</summary>
public sealed record DeviceGroupPickerOptions
{
    /// <summary>The sites, "This server" first (<see cref="DevicePickerSite.Id"/> null).</summary>
    public IReadOnlyList<DevicePickerSite> Sites { get; init; } = [];

    /// <summary>The tag definitions.</summary>
    public IReadOnlyList<DevicePickerTag> Tags { get; init; } = [];
}

/// <summary>
/// The rules of a device group as the editor shows them: a device is in the group when "This server", a site, a tag or its
/// serial covers it and its serial is not excepted.
/// </summary>
/// <param name="ThisServer">Every device without a site.</param>
/// <param name="SiteIds">The chosen sites.</param>
/// <param name="TagIds">The chosen tags by the id of their definition.</param>
/// <param name="Serials">Single devices by serial number.</param>
/// <param name="ExceptSerials">Devices never in the group, by serial number.</param>
public sealed record DeviceGroupChoice(bool ThisServer, IReadOnlyList<Guid> SiteIds, IReadOnlyList<string> TagIds, IReadOnlyList<string> Serials, IReadOnlyList<string> ExceptSerials)
{
    /// <summary>A group without rules: no devices.</summary>
    public static DeviceGroupChoice Empty { get; } = new(false, [], [], [], []);

    /// <summary>No site, tag or single device is chosen.</summary>
    public bool IsEmpty => !ThisServer && SiteIds.Count == 0 && TagIds.Count == 0 && Serials.Count == 0;
}

/// <summary>One device in the <see cref="DevicePickerWindow"/>: check box, address, model, and why it cannot be chosen.</summary>
public sealed partial class DevicePickItem : ObservableObject
{
    private readonly Action<DevicePickItem, bool> _changed;
    private bool _silent;

    internal DevicePickItem(IDeviceInfo device, string? unavailableReason, Action<DevicePickItem, bool> changed)
    {
        _changed = changed;
        Device = device;
        Model = device.Model?.Replace("AXIS ", string.Empty, StringComparison.Ordinal) ?? string.Empty;
        UnavailableReason = string.IsNullOrWhiteSpace(unavailableReason) ? null : unavailableReason;
        SearchText = device.Address + " " + Model + " " + device.Serial + " " + device.HostName + " " + device.SiteName + " " + string.Join(' ', device.Tags);
    }

    /// <summary>The device.</summary>
    public IDeviceInfo Device { get; }

    /// <summary>Its id.</summary>
    public Guid Id => Device.Id;

    /// <summary>Its address.</summary>
    public string Address => Device.Address;

    /// <summary>Its model without "AXIS ".</summary>
    public string Model { get; }

    /// <summary>Why the device cannot be chosen (shown greyed with this text), or null.</summary>
    public string? UnavailableReason { get; }

    /// <summary>It can be chosen.</summary>
    public bool IsAvailable => UnavailableReason is null;

    internal string SearchText { get; }

    /// <summary>Chosen by itself (not through a tag).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chosen))]
    public partial bool IsChecked { get; set; }

    /// <summary>The chosen tag that includes this device ("Tag Building A"; the group editor also "Site Building B", "This server"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chosen), nameof(CanToggle), nameof(Note), nameof(CanExcept), nameof(CanInclude), nameof(IsLeftOut))]
    public partial string? CoveredBy { get; internal set; }

    /// <summary>
    /// Device group editor: the device is excepted from the group (never in it, even when a site, a tag or its own tick
    /// covers it, like on the server). Shown while a site or tag covers the device (<see cref="IsLeftOut"/>); kept also when
    /// none does now, so the device stays out when one covers it again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chosen), nameof(Note), nameof(CanExcept), nameof(CanInclude), nameof(IsLeftOut))]
    public partial bool IsExcepted { get; private set; }

    /// <summary>A site or tag covers the device and it is excepted.</summary>
    public bool IsLeftOut => IsExcepted && CoveredBy is not null;

    /// <summary>Device group editor: "Except" is offered (a site or tag covers the device).</summary>
    public bool CanExcept => ExceptChanged is not null && CoveredBy is not null && !IsExcepted;

    /// <summary>Device group editor: "Include" is offered (the device is excepted).</summary>
    public bool CanInclude => ExceptChanged is not null && IsLeftOut;

    /// <summary>Set by the group editor: called after <see cref="IsExcepted"/> changed.</summary>
    internal Action<DevicePickItem>? ExceptChanged { get; set; }

    /// <summary>Excepts the device from the group (its site or tag stays).</summary>
    [RelayCommand]
    private void Except() => SetExcepted(true);

    /// <summary>Takes the device back into the group.</summary>
    [RelayCommand]
    private void Include() => SetExcepted(false);

    internal void SetExcepted(bool value, bool notify = true)
    {
        if (IsExcepted == value || ExceptChanged is null)
        {
            return;
        }

        IsExcepted = value;
        if (notify)
        {
            ExceptChanged(this);
        }
    }

    /// <summary>
    /// The check box: chosen by itself or through a tag (a device of a chosen tag cannot be unchosen alone). In the device
    /// group editor an excepted device is never chosen; ticking it by itself takes it back into the group.
    /// </summary>
    public bool Chosen
    {
        get => (IsChecked || CoveredBy is not null) && !IsExcepted;
        set
        {
            if (CoveredBy is null)
            {
                if (value && IsExcepted)
                {
                    SetExcepted(false);
                }

                IsChecked = value;
            }
        }
    }

    /// <summary>The check box can be clicked.</summary>
    public bool CanToggle => IsAvailable && CoveredBy is null;

    /// <summary>The text right of the model: why it cannot be chosen, the tag that includes it, or that it is excepted from it.</summary>
    public string? Note => UnavailableReason ?? (IsLeftOut ? "Excepted from " + CoveredBy : CoveredBy);

    internal void Set(bool value)
    {
        _silent = true;
        IsChecked = value;
        _silent = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_silent)
        {
            _changed(this, value);
        }
    }
}

/// <summary>One tag in the picker: check box, name, how many devices carry it.</summary>
public sealed partial class TagPickItem(string name, int deviceCount, Action changed) : ObservableObject
{
    /// <summary>The tag name.</summary>
    public string Name { get; } = name;

    /// <summary>How many devices carry the tag ("1,204").</summary>
    public string CountText { get; } = string.Create(CultureInfo.CurrentCulture, $"{deviceCount:N0}");

    /// <summary>The id of the tag's definition (device group editor); null elsewhere.</summary>
    public string? Id { get; init; }

    /// <summary>The tag is chosen.</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>One site in the device group editor: check box, name ("This server" for the devices without a site), device count.</summary>
public sealed partial class SitePickItem(Guid? id, string name, int deviceCount, Action changed) : ObservableObject
{
    /// <summary>The site's id; null for "This server".</summary>
    public Guid? Id { get; } = id;

    /// <summary>"Building B", "This server".</summary>
    public string Name { get; } = name;

    /// <summary>How many devices are at the site ("1,204").</summary>
    public string CountText { get; } = string.Create(CultureInfo.CurrentCulture, $"{deviceCount:N0}");

    /// <summary>The site is chosen.</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// The shared device picker (<see cref="DevicePickerWindow"/>): every managed device in address order, a search over
/// address, model, MAC address, host name and tags, Select all / Select none for the devices the search shows, and the
/// count. With <see cref="DevicePickerOptions"/> it also offers "All devices", the tags (a tag stands for every device with
/// it; its devices show checked with the tag's name) and the device of the event; the result is <see cref="Choice"/>.
/// Built for thousands of devices: the list is replaced in one step, the count is kept per change, select all, none and
/// a tag change are one pass.
/// </summary>
public sealed partial class DevicePickerViewModel : ObservableObject
{
    private readonly List<DevicePickItem> _all;
    private readonly List<string> _otherSerials = [];
    private readonly List<string> _otherExcepts = [];
    private int _chosen;
    private bool _ready;

    /// <param name="devices">Every device that can be shown.</param>
    /// <param name="selected">The devices chosen when the picker opens.</param>
    /// <param name="unavailableReason">Why a device cannot be chosen (null = it can); such devices are greyed.</param>
    public DevicePickerViewModel(IEnumerable<IDeviceInfo> devices, IEnumerable<Guid>? selected = null, Func<IDeviceInfo, string?>? unavailableReason = null)
        : this(devices, DeviceChoice.Devices(selected ?? []), new DevicePickerOptions(), unavailableReason)
    {
    }

    /// <param name="devices">Every device that can be shown.</param>
    /// <param name="initial">The choice when the picker opens.</param>
    /// <param name="options">All devices, tags, the device of the event.</param>
    /// <param name="unavailableReason">Why a device cannot be chosen (null = it can); such devices are greyed.</param>
    public DevicePickerViewModel(IEnumerable<IDeviceInfo> devices, DeviceChoice initial, DevicePickerOptions options, Func<IDeviceInfo, string?>? unavailableReason = null)
        : this(devices, initial, options, unavailableReason, null)
    {
    }

    /// <summary>
    /// The device group editor (<see cref="DevicePickerOptions.Group"/>): sites and tags on the left, the devices they
    /// cover checked and locked, single devices tickable, covered devices can be excepted; the result is <see cref="GroupChoice"/>.
    /// Serials of the group that no listed device has (removed devices) are kept as they are.
    /// </summary>
    /// <param name="devices">Every device of the server.</param>
    /// <param name="initial">The rules of the group when the editor opens.</param>
    /// <param name="group">The sites ("This server" first) and the defined tags.</param>
    public DevicePickerViewModel(IEnumerable<IDeviceInfo> devices, DeviceGroupChoice initial, DeviceGroupPickerOptions group)
        : this(devices, DeviceChoice.Devices([]), new DevicePickerOptions { Group = group ?? throw new ArgumentNullException(nameof(group)) }, null,
            initial ?? throw new ArgumentNullException(nameof(initial)))
    {
    }

    private DevicePickerViewModel(IEnumerable<IDeviceInfo> devices, DeviceChoice initial, DevicePickerOptions options, Func<IDeviceInfo, string?>? unavailableReason,
        DeviceGroupChoice? groupInitial)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(initial);
        Options = options ?? throw new ArgumentNullException(nameof(options));
        var chosen = initial.DeviceIds.ToHashSet();
        _all = [.. devices
            .Select(d => new DevicePickItem(d, unavailableReason?.Invoke(d), OnChanged))
            .OrderBy(i => i.IsAvailable ? 0 : 1)
            .ThenBy(i => AddressKey(i.Address), StringComparer.Ordinal)];
        foreach (var item in _all.Where(i => chosen.Contains(i.Id)))
        {
            item.Set(true);
            _chosen++;
        }

        if (options.Group is { } group)
        {
            (Sites, Tags) = GroupLists(group, groupInitial ?? DeviceGroupChoice.Empty);
        }
        else if (options.AllowTags)
        {
            var wanted = initial.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Device counts per tag, then the tags no device has yet: a chosen tag must survive OK (it also stands for
            // devices tagged later), and the defined tags can be chosen before any device carries them.
            var counts = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in _all.SelectMany(i => i.Device.Tags))
            {
                counts[tag] = counts.TryGetValue(tag, out var known) ? (known.Name, known.Count + 1) : (tag, 1);
            }

            foreach (var tag in options.TagNames.Concat(initial.Tags).Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                counts.TryAdd(tag, (tag, 0));
            }

            Tags = [.. counts.Values
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new TagPickItem(t.Name, t.Count, OnTagsChanged) { IsChecked = wanted.Contains(t.Name) })];
        }
        else
        {
            Tags = [];
        }

        Sites ??= [];
        Kind = initial.Kind switch
        {
            DeviceChoiceKind.All when options.AllowAll => DeviceChoiceKind.All,
            DeviceChoiceKind.EventDevice when options.EventDeviceText is not null => DeviceChoiceKind.EventDevice,
            _ => DeviceChoiceKind.Chosen,
        };
        Visible.ReplaceAll(_all);
        _ready = true;
        ApplyTags();
    }

    /// <summary>What the picker offers.</summary>
    public DevicePickerOptions Options { get; }

    /// <summary>Whether the left column (event device, all devices, tags; the group editor's sites and tags) is shown.</summary>
    public bool HasChoices => Options.AllowAll || Options.AllowTags || Options.EventDeviceText is not null || IsGroup;

    /// <summary>The device group editor (<see cref="DevicePickerOptions.Group"/>).</summary>
    public bool IsGroup => Options.Group is not null;

    /// <summary>There are tags to choose.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>Tags are allowed but no device has one (the group editor: no tag is defined).</summary>
    public bool ShowNoTags => (Options.AllowTags || IsGroup) && Tags.Count == 0;

    /// <summary>The tags to choose.</summary>
    public IReadOnlyList<TagPickItem> Tags { get; }

    /// <summary>The device group editor's sites, "This server" first; empty elsewhere.</summary>
    public IReadOnlyList<SitePickItem> Sites { get; }

    /// <summary>
    /// The device group editor's result: "This server", the chosen sites, tags (definition ids), single devices and
    /// excepted devices by serial number (also those no chosen site or tag covers now, and those of devices not listed).
    /// </summary>
    public DeviceGroupChoice GroupChoice => new(
        Sites.Any(s => s.Id is null && s.IsChecked),
        [.. Sites.Where(s => s.IsChecked && s.Id is not null).Select(s => s.Id!.Value)],
        [.. Tags.Where(t => t.IsChecked && t.Id is not null).Select(t => t.Id!)],
        [.. _all.Where(i => i.IsChecked).Select(i => SerialKey(i.Device.Serial)).Concat(_otherSerials).Distinct(StringComparer.Ordinal)],
        [.. _all.Where(i => i.IsExcepted).Select(i => SerialKey(i.Device.Serial)).Concat(_otherExcepts).Distinct(StringComparer.Ordinal)]);

    /// <summary>How many devices the group has with the choice now ("412" of "412 of 4,812 devices").</summary>
    public int CoveredCount { get; private set; }

    /// <summary>Every device of the list.</summary>
    public int TotalCount => _all.Count;

    /// <summary>The radio choice of the left column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChosen), nameof(IsAll), nameof(IsEventDevice))]
    public partial DeviceChoiceKind Kind { get; set; }

    /// <summary>Radio "Chosen devices and tags": the tags and the device list can be used.</summary>
    public bool IsChosen
    {
        get => Kind == DeviceChoiceKind.Chosen;
        set
        {
            if (value)
            {
                Kind = DeviceChoiceKind.Chosen;
            }
        }
    }

    /// <summary>Radio "All devices".</summary>
    public bool IsAll
    {
        get => Kind == DeviceChoiceKind.All;
        set
        {
            if (value)
            {
                Kind = DeviceChoiceKind.All;
            }
        }
    }

    /// <summary>Radio "The device of the event".</summary>
    public bool IsEventDevice
    {
        get => Kind == DeviceChoiceKind.EventDevice;
        set
        {
            if (value)
            {
                Kind = DeviceChoiceKind.EventDevice;
            }
        }
    }

    /// <summary>The devices the search shows.</summary>
    public RangeObservableCollection<DevicePickItem> Visible { get; } = [];

    /// <summary>The search over address, model, MAC address, host name and tags.</summary>
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>"12 of 4,812 devices chosen".</summary>
    [ObservableProperty] public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The list shows nothing.</summary>
    public bool IsEmpty => Visible.Count == 0;

    /// <summary>A refusal of the whole choice (e.g. the server refused a device group's rules), shown directly below the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    /// <summary><see cref="ErrorText"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>What the empty list says.</summary>
    public string EmptyText => _all.Count == 0 ? "No devices yet. Add devices on the Devices page." : "No device matches the search.";

    /// <summary>The devices chosen one by one, in list order (not those of a chosen tag).</summary>
    public IReadOnlyList<Guid> SelectedIds => [.. _all.Where(i => i.IsChecked).Select(i => i.Id)];

    /// <summary>How many devices are chosen.</summary>
    public int ChosenCount => _chosen;

    /// <summary>The result: all devices, the device of the event, or the devices and tags chosen.</summary>
    public DeviceChoice Choice => Kind switch
    {
        DeviceChoiceKind.All => DeviceChoice.AllDevices,
        DeviceChoiceKind.EventDevice => DeviceChoice.EventDevice,
        _ => DeviceChoice.Devices(SelectedIds, Tags.Where(t => t.IsChecked).Select(t => t.Name)),
    };

    /// <summary>Chooses every device the search shows that can be chosen.</summary>
    [RelayCommand]
    private void SelectAll() => SetVisible(true);

    /// <summary>Unchooses every device the search shows.</summary>
    [RelayCommand]
    private void SelectNone() => SetVisible(false);

    partial void OnSearchTextChanged(string value)
    {
        var text = value.Trim();
        Visible.ReplaceAll(text.Length == 0 ? _all : _all.Where(i => i.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList());
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnKindChanged(DeviceChoiceKind value)
    {
        Kind = value == DeviceChoiceKind.All && !Options.AllowAll || value == DeviceChoiceKind.EventDevice && Options.EventDeviceText is null
            ? DeviceChoiceKind.Chosen
            : value;
        UpdateSummary();
    }

    private void SetVisible(bool value)
    {
        if (value && Options.SingleDevice)
        {
            return;
        }

        foreach (var item in Visible)
        {
            if (item.IsChecked != value && (item.IsAvailable || !value))
            {
                if (value)
                {
                    item.SetExcepted(false, notify: false); // like ticking it: chosen takes it back, never in both lists
                }

                item.Set(value);
                _chosen += value ? 1 : -1;
            }
        }

        UpdateSummary();
    }

    private void OnChanged(DevicePickItem item, bool value)
    {
        if (value && !item.IsAvailable)
        {
            item.Set(false);
            return;
        }

        if (value && Options.SingleDevice)
        {
            foreach (var other in _all.Where(i => i.IsChecked && !ReferenceEquals(i, item)))
            {
                other.Set(false);
                _chosen--;
            }
        }

        _chosen += value ? 1 : -1;
        UpdateSummary();
    }

    private void OnTagsChanged()
    {
        if (_ready)
        {
            ApplyTags();
        }
    }

    /// <summary>Marks the devices of the chosen tags (one pass over all devices).</summary>
    private void ApplyTags()
    {
        if (IsGroup)
        {
            ApplyGroup();
            return;
        }

        var chosen = Tags.Where(t => t.IsChecked).Select(t => t.Name).ToList();
        foreach (var item in _all)
        {
            var tag = chosen.Count == 0 ? null : chosen.FirstOrDefault(t => item.Device.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
            item.CoveredBy = tag is null ? null : "Tag " + tag;
        }

        UpdateSummary();
    }

    /// <summary>
    /// The group editor: marks the devices of the chosen sites ("This server" = no site) and tags, one pass over all
    /// devices with hash lookups.
    /// </summary>
    private void ApplyGroup()
    {
        string? thisServer = null;
        var sites = new Dictionary<Guid, string>();
        foreach (var site in Sites.Where(s => s.IsChecked))
        {
            if (site.Id is { } id)
            {
                sites[id] = "Site " + site.Name;
            }
            else
            {
                thisServer = site.Name;
            }
        }

        var tags = Tags.Where(t => t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            var device = item.Device;
            string? by = device.SiteId is { } siteId ? sites.GetValueOrDefault(siteId) : thisServer;
            if (by is null && tags.Count > 0)
            {
                foreach (var tag in device.Tags)
                {
                    if (tags.Contains(tag))
                    {
                        by = "Tag " + tag;
                        break;
                    }
                }
            }

            item.CoveredBy = by;
        }

        UpdateSummary();
    }

    /// <summary>The group editor's sites and tags with their device counts; marks the group's single and excepted devices.</summary>
    private (IReadOnlyList<SitePickItem> Sites, IReadOnlyList<TagPickItem> Tags) GroupLists(DeviceGroupPickerOptions group, DeviceGroupChoice initial)
    {
        var serials = initial.Serials.Select(SerialKey).ToHashSet(StringComparer.Ordinal);
        var excepts = initial.ExceptSerials.Select(SerialKey).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var siteCounts = new Dictionary<Guid, int>();
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int withoutSite = 0;
        foreach (var item in _all)
        {
            item.ExceptChanged = OnExceptChanged;
            var serial = SerialKey(item.Device.Serial);
            seen.Add(serial);
            if (serials.Contains(serial) && !item.IsChecked)
            {
                item.Set(true);
                _chosen++;
            }

            if (excepts.Contains(serial))
            {
                item.SetExcepted(true, notify: false);
            }

            if (item.Device.SiteId is { } siteId)
            {
                siteCounts[siteId] = siteCounts.GetValueOrDefault(siteId) + 1;
            }
            else
            {
                withoutSite++;
            }

            foreach (var tag in item.Device.Tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
            }
        }

        _otherSerials.AddRange(serials.Where(s => !seen.Contains(s)));
        _otherExcepts.AddRange(excepts.Where(s => !seen.Contains(s)));
        var chosenSites = initial.SiteIds.ToHashSet();
        var chosenTags = initial.TagIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<SitePickItem> sites = [.. group.Sites.Select(s => new SitePickItem(s.Id, s.Name, s.Id is { } id ? siteCounts.GetValueOrDefault(id) : withoutSite, OnTagsChanged)
        {
            IsChecked = s.Id is { } chosen ? chosenSites.Contains(chosen) : initial.ThisServer,
        })];
        IReadOnlyList<TagPickItem> tags = [.. group.Tags
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new TagPickItem(t.Name, tagCounts.GetValueOrDefault(t.Name), OnTagsChanged) { Id = t.Id, IsChecked = chosenTags.Contains(t.Id) })];
        return (sites, tags);
    }

    private void OnExceptChanged(DevicePickItem item) => UpdateSummary();

    /// <summary>A serial number as the server keeps it: 12 hex digits, upper case, no separators.</summary>
    private static string SerialKey(string serial) => new([.. serial.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant)]);

    private void UpdateSummary()
    {
        var tags = Tags.Count(t => t.IsChecked);
        var total = _all.Count(i => i.Chosen);
        CoveredCount = total;
        if (IsGroup)
        {
            Summary = string.Create(CultureInfo.CurrentCulture, $"{total:N0} of {_all.Count:N0} {(_all.Count == 1 ? "device" : "devices")}");
            OnPropertyChanged(nameof(ChosenCount));
            OnPropertyChanged(nameof(CoveredCount));
            return;
        }

        Summary = Kind switch
        {
            DeviceChoiceKind.All => string.Create(CultureInfo.CurrentCulture, $"All devices ({_all.Count:N0} now, also devices added later)"),
            DeviceChoiceKind.EventDevice => Options.EventDeviceText ?? string.Empty,
            _ when tags == 0 => string.Create(CultureInfo.CurrentCulture, $"{_chosen:N0} of {_all.Count:N0} {(_all.Count == 1 ? "device" : "devices")} chosen"),
            _ when _chosen == 0 => string.Create(CultureInfo.CurrentCulture, $"{tags:N0} {(tags == 1 ? "tag" : "tags")}: {total:N0} of {_all.Count:N0} devices"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{tags:N0} {(tags == 1 ? "tag" : "tags")} and {_chosen:N0} {(_chosen == 1 ? "device" : "devices")}: {total:N0} of {_all.Count:N0} devices"),
        };
        OnPropertyChanged(nameof(ChosenCount));
    }

    /// <summary>IPv4 addresses in numeric order, other addresses after them (<see cref="DeviceOrder.AddressKey"/>).</summary>
    internal static string AddressKey(string address) => DeviceOrder.AddressKey(address);
}
