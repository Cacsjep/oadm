using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

using ContractStatus = Oadm.Contracts.V1.DeviceStatus;
using DeviceCategory = Oadm.Contracts.V1.DeviceCategory;
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
    [ObservableProperty] public partial string Dot1xText { get; private set; } = "";
    [ObservableProperty] public partial ContractStatus ContractStatus { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial PillKind StatusKind { get; private set; }
    [ObservableProperty] public partial bool HasCredentials { get; private set; }
    [ObservableProperty] public partial string Scheme { get; private set; } = "";
    [ObservableProperty] public partial DeviceCategory Category { get; private set; }
    [ObservableProperty] public partial string? ProductType { get; private set; }
    [ObservableProperty] public partial string CategoryIconKey { get; private set; } = "device.generic";
    [ObservableProperty] public partial string CategoryTooltip { get; private set; } = "";
    [ObservableProperty] public partial bool HasVideo { get; private set; }
    public IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi> Apis { get; private set; } = [];
    [ObservableProperty] public partial DateTime? CertNotAfterUtc { get; private set; }
    [ObservableProperty] public partial ChipInfo CertExpires { get; private set; } = ChipInfo.Empty;
    [ObservableProperty] public partial ChipInfo CertTrust { get; private set; } = ChipInfo.Empty;
    [ObservableProperty] public partial string? CertTooltip { get; private set; }

    /// <summary>Sort key of the "Certificate expires" column; devices without a certificate sort last.</summary>
    public DateTime CertExpiresSortKey => CertNotAfterUtc ?? DateTime.MaxValue;

    Guid IDeviceInfo.Id => Guid.TryParse(Id, out Guid id) ? id : Guid.Empty;

    SdkStatus IDeviceInfo.Status => DeviceStatusInfo.ToSdk(ContractStatus);

    Oadm.Sdk.Devices.DeviceCategory IDeviceInfo.Category => DeviceCategoryInfo.ToSdk(Category);

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
        ContractStatus = device.Status;
        StatusText = DeviceStatusInfo.ToText(device.Status);
        StatusKind = DeviceStatusInfo.ToKind(device.Status);
        HasCredentials = device.HasCredentials;
        Scheme = device.Scheme;
        Category = device.Category;
        ProductType = string.IsNullOrEmpty(device.ProductType) ? null : device.ProductType;
        CategoryIconKey = DeviceCategoryInfo.ToIconKey(device.Category);
        CategoryTooltip = DeviceCategoryInfo.ToTooltip(device.Category, ProductType);
        HasVideo = device.HasVideo;
        DateTime now = DateTime.UtcNow;
        CertNotAfterUtc = device.CertNotAfter?.ToDateTime();
        CertExpires = CertificateDisplay.Expiry(CertNotAfterUtc, now);
        CertTrust = CertificateDisplay.Trust(device.CertTrust, CertNotAfterUtc, now);
        CertTooltip = CertificateDisplay.Tooltip(device.CertSubject, device.CertIssuer, CertNotAfterUtc);
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
            || Contains(StatusText);

        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    partial void OnCertNotAfterUtcChanged(DateTime? value) => OnPropertyChanged(nameof(CertExpiresSortKey));

    partial void OnStatusKindChanged(PillKind value)
    {
        OnPropertyChanged(nameof(IsStatusOk));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusNeutral));
    }

    private static string EnabledText(bool known, bool enabled) => known ? (enabled ? "Enabled" : "Disabled") : "";
}
