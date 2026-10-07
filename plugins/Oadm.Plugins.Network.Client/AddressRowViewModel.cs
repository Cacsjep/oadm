using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>
/// One device in the address assignment table: current address, new IPv4 address and (static IPv6 only) new IPv6
/// address, both editable, and the problem of the row. Problems are shown only here (Status column), never repeated
/// elsewhere in the dialog.
/// </summary>
public sealed partial class AddressRowViewModel : ObservableObject
{
    private bool _settingSuggestion;

    public AddressRowViewModel(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
    }

    public IDeviceInfo Device { get; }

    /// <summary>MAC address as the device grid shows it (AC:CC:8E:00:00:01).</summary>
    public string MacAddress => FormatMac(Device.Serial);

    public string Model => Device.Model ?? string.Empty;

    public string CurrentAddress => Device.Address;

    /// <summary>The new IPv4 address, suggested or typed by the user; "Unchanged" when IPv4 is kept.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsChanged), nameof(IsReady))]
    public partial string NewAddress { get; set; } = string.Empty;

    /// <summary>The user typed the address (kept when the suggestions are recomputed).</summary>
    [ObservableProperty]
    public partial bool IsEdited { get; set; }

    /// <summary>False while IPv4 is kept unchanged: the cell is read-only.</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; }

    /// <summary>The new static IPv6 address (column "New IPv6 address", shown only for static IPv6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady))]
    public partial string NewIpv6Address { get; set; } = string.Empty;

    /// <summary>True while IPv6 is set to static: the IPv6 cell is editable and counts for the status.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady))]
    public partial bool IsIpv6Editable { get; set; }

    [ObservableProperty]
    public partial string NewHostName { get; set; } = string.Empty;

    /// <summary>Why the new IPv4 address cannot be used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasConflict), nameof(IsReady))]
    public partial string? Conflict { get; set; }

    /// <summary>Why the new IPv6 address cannot be used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasConflict), nameof(IsReady))]
    public partial string? Ipv6Conflict { get; set; }

    public bool HasConflict => Conflict is not null || Ipv6Conflict is not null;

    /// <summary>Ready and actually changing an address (green chip).</summary>
    public bool IsReady => !HasConflict && (IsChanged || IsIpv6Editable);

    public bool IsChanged => IsEditable && !string.Equals(NewAddress.Trim(), CurrentAddress, StringComparison.OrdinalIgnoreCase);

    public string StatusText =>
        Conflict ?? (Ipv6Conflict is { } v6 ? "IPv6: " + v6 : null)
        ?? (IsChanged || IsIpv6Editable ? "Ready" : IsEditable ? "Keeps its address" : "Unchanged");

    /// <summary>Sets a suggestion (not a user edit).</summary>
    public void Suggest(string? address)
    {
        _settingSuggestion = true;
        try
        {
            NewAddress = address ?? string.Empty;
            IsEdited = false;
        }
        finally
        {
            _settingSuggestion = false;
        }
    }

    partial void OnNewAddressChanged(string value)
    {
        if (!_settingSuggestion && IsEditable)
        {
            IsEdited = true;
        }
    }

    partial void OnIsEditableChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(IsReady));
    }

    public AssignmentRow ToAssignmentRow() => new(new AssignmentDevice(Device.Id, Device.Address), NewAddress);

    public AssignmentRow ToIpv6Row() => new(new AssignmentDevice(Device.Id, Device.Address), NewIpv6Address);

    private static string FormatMac(string serial)
    {
        var hex = new string((serial ?? string.Empty).Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length != 12)
        {
            return serial ?? string.Empty;
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToUpper(CultureInfo.InvariantCulture);
    }
}
