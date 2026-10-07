using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

using ContractStatus = Oadm.Contracts.V1.DeviceStatus;
using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Client.Devices;

/// <summary>One row of the device grid. Updated in place from DeviceService.Watch events.</summary>
public sealed partial class DeviceRowViewModel : ObservableObject, IDeviceInfo
{
    public const string NotSynchronized = "Not synchronized";

    public DeviceRowViewModel(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Id = device.Id;
        Update(device);
    }

    public string Id { get; }

    [ObservableProperty] public partial string Serial { get; private set; } = "";
    [ObservableProperty] public partial string Address { get; private set; } = "";
    [ObservableProperty] public partial string? HostName { get; private set; }
    [ObservableProperty] public partial bool UseHostName { get; private set; }
    [ObservableProperty] public partial string DisplayAddress { get; private set; } = "";
    [ObservableProperty] public partial string? Model { get; private set; }
    [ObservableProperty] public partial string? FirmwareVersion { get; private set; }
    [ObservableProperty] public partial string DhcpText { get; private set; } = "";
    [ObservableProperty] public partial string HttpsText { get; private set; } = "";
    [ObservableProperty] public partial string ServerName { get; private set; } = "";
    [ObservableProperty] public partial string Dot1xText { get; private set; } = "";
    [ObservableProperty] public partial string UpnpFriendlyName { get; private set; } = "";
    [ObservableProperty] public partial string WarrantyText { get; private set; } = "";
    [ObservableProperty] public partial string ReplacementText { get; private set; } = "";
    [ObservableProperty] public partial ContractStatus ContractStatus { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial PillKind StatusKind { get; private set; }
    [ObservableProperty] public partial bool HasCredentials { get; private set; }
    [ObservableProperty] public partial string Scheme { get; private set; } = "";

    Guid IDeviceInfo.Id => Guid.TryParse(Id, out Guid id) ? id : Guid.Empty;

    SdkStatus IDeviceInfo.Status => DeviceStatusInfo.ToSdk(ContractStatus);

    public bool IsStatusOk => StatusKind == PillKind.Ok;
    public bool IsStatusWarning => StatusKind == PillKind.Warning;
    public bool IsStatusError => StatusKind == PillKind.Error;
    public bool IsStatusNeutral => StatusKind == PillKind.Neutral;

    public void Update(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Serial = device.Serial;
        Address = device.Address;
        HostName = string.IsNullOrEmpty(device.HostName) ? null : device.HostName;
        UseHostName = device.UseHostName;
        DisplayAddress = device.UseHostName && !string.IsNullOrEmpty(device.HostName) ? device.HostName : device.Address;
        Model = device.Model;
        FirmwareVersion = device.FirmwareVersion;
        DhcpText = device.HasDhcpEnabled ? (device.DhcpEnabled ? "Yes" : "No") : "";
        HttpsText = EnabledText(device.HasHttpsEnabled, device.HttpsEnabled);
        Dot1xText = EnabledText(device.HasDot1XEnabled, device.Dot1XEnabled);
        ServerName = device.ServerName;
        UpnpFriendlyName = device.UpnpFriendlyName;
        WarrantyText = string.IsNullOrEmpty(device.WarrantyExpiry) ? NotSynchronized : device.WarrantyExpiry;
        ReplacementText = device.ReplacementModel;
        ContractStatus = device.Status;
        StatusText = DeviceStatusInfo.ToText(device.Status);
        StatusKind = DeviceStatusInfo.ToKind(device.Status);
        HasCredentials = device.HasCredentials;
        Scheme = device.Scheme;
    }

    /// <summary>Case-insensitive search across the visible text columns.</summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string term = search.Trim();
        return Contains(Serial) || Contains(Address) || Contains(HostName) || Contains(Model) || Contains(FirmwareVersion)
            || Contains(StatusText) || Contains(ServerName) || Contains(UpnpFriendlyName);

        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    partial void OnStatusKindChanged(PillKind value)
    {
        OnPropertyChanged(nameof(IsStatusOk));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusNeutral));
    }

    private static string EnabledText(bool known, bool enabled) => known ? (enabled ? "Enabled" : "Disabled") : "";
}
