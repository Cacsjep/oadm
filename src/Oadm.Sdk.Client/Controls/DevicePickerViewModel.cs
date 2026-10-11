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
public sealed record DeviceChoice(DeviceChoiceKind Kind, IReadOnlyList<Guid> DeviceIds, IReadOnlyList<string> Tags)
{
    public static DeviceChoice AllDevices { get; } = new(DeviceChoiceKind.All, [], []);

    public static DeviceChoice EventDevice { get; } = new(DeviceChoiceKind.EventDevice, [], []);

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

    /// <summary>Every option: all devices, tags and, with <paramref name="eventDeviceText"/>, the device of the event.</summary>
    public static DevicePickerOptions Full(string? eventDeviceText = null) => new() { AllowAll = true, AllowTags = true, EventDeviceText = eventDeviceText };
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
        SearchText = device.Address + " " + Model + " " + device.Serial + " " + device.HostName + " " + string.Join(' ', device.Tags);
    }

    public IDeviceInfo Device { get; }

    public Guid Id => Device.Id;

    public string Address => Device.Address;

    public string Model { get; }

    /// <summary>Why the device cannot be chosen (shown greyed with this text), or null.</summary>
    public string? UnavailableReason { get; }

    public bool IsAvailable => UnavailableReason is null;

    internal string SearchText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chosen))]
    public partial bool IsChecked { get; set; }

    /// <summary>The chosen tag that includes this device ("Tag Building A"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chosen), nameof(CanToggle), nameof(Note))]
    public partial string? CoveredBy { get; internal set; }

    /// <summary>The check box: chosen by itself or through a tag (a device of a chosen tag cannot be unchosen alone).</summary>
    public bool Chosen
    {
        get => IsChecked || CoveredBy is not null;
        set
        {
            if (CoveredBy is null)
            {
                IsChecked = value;
            }
        }
    }

    public bool CanToggle => IsAvailable && CoveredBy is null;

    /// <summary>The text right of the model: why it cannot be chosen, or the tag that includes it.</summary>
    public string? Note => UnavailableReason ?? CoveredBy;

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
    public string Name { get; } = name;

    public string CountText { get; } = string.Create(CultureInfo.CurrentCulture, $"{deviceCount:N0}");

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

        if (options.AllowTags)
        {
            var wanted = initial.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Tags = [.. _all.SelectMany(i => i.Device.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new TagPickItem(g.First(), g.Count(), OnTagsChanged) { IsChecked = wanted.Contains(g.Key) })];
        }
        else
        {
            Tags = [];
        }

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

    public DevicePickerOptions Options { get; }

    /// <summary>Whether the left column (event device, all devices, tags) is shown.</summary>
    public bool HasChoices => Options.AllowAll || Options.AllowTags || Options.EventDeviceText is not null;

    public bool HasTags => Tags.Count > 0;

    public bool ShowNoTags => Options.AllowTags && Tags.Count == 0;

    public IReadOnlyList<TagPickItem> Tags { get; }

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

    public RangeObservableCollection<DevicePickItem> Visible { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial string Summary { get; private set; } = string.Empty;

    public bool IsEmpty => Visible.Count == 0;

    public string EmptyText => _all.Count == 0 ? "No devices yet. Add devices on the Devices page." : "No device matches the search.";

    /// <summary>The devices chosen one by one, in list order (not those of a chosen tag).</summary>
    public IReadOnlyList<Guid> SelectedIds => [.. _all.Where(i => i.IsChecked).Select(i => i.Id)];

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
        foreach (var item in Visible)
        {
            if (item.IsChecked != value && (item.IsAvailable || !value))
            {
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
        var chosen = Tags.Where(t => t.IsChecked).Select(t => t.Name).ToList();
        foreach (var item in _all)
        {
            var tag = chosen.Count == 0 ? null : chosen.FirstOrDefault(t => item.Device.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
            item.CoveredBy = tag is null ? null : "Tag " + tag;
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var tags = Tags.Count(t => t.IsChecked);
        var total = _all.Count(i => i.Chosen);
        Summary = Kind switch
        {
            DeviceChoiceKind.All => string.Create(CultureInfo.CurrentCulture, $"All devices ({_all.Count:N0} now, also devices added later)"),
            DeviceChoiceKind.EventDevice => Options.EventDeviceText ?? string.Empty,
            _ when tags == 0 => string.Create(CultureInfo.CurrentCulture, $"{_chosen:N0} of {_all.Count:N0} {(_all.Count == 1 ? "device" : "devices")} chosen"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{tags:N0} {(tags == 1 ? "tag" : "tags")} and {_chosen:N0} {(_chosen == 1 ? "device" : "devices")}: {total:N0} of {_all.Count:N0} devices"),
        };
        OnPropertyChanged(nameof(ChosenCount));
    }

    /// <summary>IPv4 addresses in numeric order, other addresses after them.</summary>
    internal static string AddressKey(string address)
    {
        var parts = address.Split(':')[0].Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }
}
