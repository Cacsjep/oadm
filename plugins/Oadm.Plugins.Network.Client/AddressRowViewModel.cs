using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>One device in the address assignment table: current address, new address (editable), conflict.</summary>
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

    /// <summary>The new IPv4 address, suggested from the range or typed by the user; "Unchanged" when IPv4 is kept.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsChanged), nameof(IsReady))]
    public partial string NewAddress { get; set; } = string.Empty;

    /// <summary>The user typed the address (kept when the suggestions are recomputed).</summary>
    [ObservableProperty]
    public partial bool IsEdited { get; set; }

    /// <summary>False while IPv4 is kept unchanged: the cell is read-only.</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; }

    [ObservableProperty]
    public partial string NewHostName { get; set; } = string.Empty;

    /// <summary>Why the new address cannot be used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasConflict), nameof(IsReady))]
    public partial string? Conflict { get; set; }

    public bool HasConflict => Conflict is not null;

    /// <summary>Ready and actually changing the address (green chip).</summary>
    public bool IsReady => Conflict is null && IsChanged;

    public bool IsChanged => IsEditable && !string.Equals(NewAddress.Trim(), CurrentAddress, StringComparison.OrdinalIgnoreCase);

    public string StatusText => Conflict ?? (!IsEditable ? "Unchanged" : IsChanged ? "Ready" : "Keeps its address");

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
