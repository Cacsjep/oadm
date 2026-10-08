using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Tags;
using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

using ContractStatus = Oadm.Contracts.V1.DeviceStatus;
using DeviceCategory = Oadm.Contracts.V1.DeviceCategory;
using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Client.Devices;

/// <summary>
/// An item of the device grid: the device row itself, or (group mode) a device under one of its tags. The grid's
/// columns bind through <see cref="Row"/>, so both kinds show the same cells.
/// </summary>
public interface IDeviceGridItem
{
    DeviceRowViewModel Row { get; }
}

/// <summary>One row of the device grid. Updated in place from DeviceService.Watch events.</summary>
public sealed partial class DeviceRowViewModel : ObservableObject, IDeviceInfo, IDeviceGridItem
{
    public const string NotSynchronized = "Not synchronized";

    /// <summary>Separator of <see cref="TagsText"/> and of the "Tags" column of the device export and import.</summary>
    public const string TagSeparator = "; ";

    private readonly TagStore? _tagStore;

    public DeviceRowViewModel(Device device, TagStore? tags = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        Id = device.Id;
        _tagStore = tags;
        Update(device);
    }

    public string Id { get; }

    /// <inheritdoc />
    public DeviceRowViewModel Row => this;

    /// <summary>The device's tag names as the server sends them (sorted, distinct). A new list only when they changed.</summary>
    public IReadOnlyList<string> Tags { get; private set; } = [];

    /// <summary>The tags for the chips of the Tags column (shared <see cref="TagInfo"/>: a recolor needs no row update).</summary>
    [ObservableProperty] public partial IReadOnlyList<TagInfo> TagChips { get; private set; } = [];

    /// <summary>"Building A; PTZ": sort key of the Tags column, tooltip, export text.</summary>
    [ObservableProperty] public partial string TagsText { get; private set; } = "";

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
    /// <summary>VAPIX API list from the server's last full refresh; task plugin dialogs check compatibility with it.</summary>
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

    bool? IDeviceInfo.DhcpEnabled => _dhcpEnabled;

    bool? IDeviceInfo.HttpsEnabled => _httpsEnabled;

    bool? IDeviceInfo.Dot1xEnabled => _dot1xEnabled;

    private bool? _dhcpEnabled;
    private bool? _httpsEnabled;
    private bool? _dot1xEnabled;

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
        _dhcpEnabled = device.HasDhcpEnabled ? device.DhcpEnabled : null;
        _httpsEnabled = device.HasHttpsEnabled ? device.HttpsEnabled : null;
        _dot1xEnabled = device.HasDot1XEnabled ? device.Dot1XEnabled : null;
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
        if (!Tags.SequenceEqual(device.Tags, StringComparer.Ordinal))
        {
            List<string> tags = [.. device.Tags];
            Tags = tags;
            TagChips = tags.Select(t => _tagStore?.Resolve(t) ?? TagInfo.Detached(t)).ToList();
            TagsText = string.Join(TagSeparator, tags);
            _changed = true;
        }

        IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi> apis = DeviceApiLists.Intern(device.Apis);
        if (!ReferenceEquals(apis, Apis))
        {
            Apis = apis;
            _changed = true;
        }

        DateTime now = DateTime.UtcNow;
        CertNotAfterUtc = device.CertNotAfter?.ToDateTime();
        CertExpires = CertificateDisplay.Expiry(CertNotAfterUtc, now);
        CertTrust = CertificateDisplay.Trust(device.CertTrust, CertNotAfterUtc, now);
        CertTooltip = CertificateDisplay.Tooltip(device.CertSubject, device.CertIssuer, CertNotAfterUtc);
    }

    /// <summary>
    /// Applies a newer device state; returns whether anything the client keeps changed. Scale: most
    /// updates of 5,000 devices change nothing visible, and an unchanged row costs no events.
    /// </summary>
    public bool Apply(Device device)
    {
        _changed = false;
        Update(device);
        return _changed;
    }

    private bool _changed;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        _changed = true;
        base.OnPropertyChanged(e);
    }

    /// <summary>Case-insensitive search across the visible text columns and the tag names.</summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string term = search.Trim();
        return Contains(Serial) || Contains(Address) || Contains(HostName) || Contains(Model) || Contains(FirmwareVersion)
            || Contains(StatusText) || Tags.Any(Contains);

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
