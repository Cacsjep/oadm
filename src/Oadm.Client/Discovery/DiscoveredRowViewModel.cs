using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Discovery;

/// <summary>A device found by mDNS or range scan, shown in the "Select devices" step.</summary>
public sealed partial class DiscoveredRowViewModel : ObservableObject
{
    public DiscoveredRowViewModel(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DiscoveredId = device.DiscoveredId;
        Update(device);
    }

    public string DiscoveredId { get; }

    [ObservableProperty] public partial string Serial { get; private set; } = "";
    [ObservableProperty] public partial string Address { get; private set; } = "";
    [ObservableProperty] public partial string HostName { get; private set; } = "";
    [ObservableProperty] public partial string Model { get; private set; } = "";
    [ObservableProperty] public partial DeviceStatus Status { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial PillKind StatusKind { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectable))]
    public partial bool IsAlreadyManaged { get; private set; }

    /// <summary>Checked in the grid. Always false for devices that are already managed.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool IsSelectable => !IsAlreadyManaged;

    public bool IsStatusOk => StatusKind == PillKind.Ok;
    public bool IsStatusWarning => StatusKind == PillKind.Warning;
    public bool IsStatusError => StatusKind == PillKind.Error;
    public bool IsStatusNeutral => StatusKind == PillKind.Neutral;

    public void Update(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Serial = device.Serial;
        Address = device.Address;
        HostName = device.HostName;
        Model = string.IsNullOrEmpty(device.Model) ? "" : device.Model;
        Status = device.Status;
        IsAlreadyManaged = device.AlreadyManaged;
        StatusText = device.AlreadyManaged ? "Already added" : DeviceStatusInfo.ToText(device.Status);
        StatusKind = device.AlreadyManaged ? PillKind.Neutral : DeviceStatusInfo.ToKind(device.Status);
        if (IsAlreadyManaged)
        {
            IsSelected = false;
        }
    }

    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string term = search.Trim();
        return Serial.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Address.Contains(term, StringComparison.OrdinalIgnoreCase)
            || HostName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Model.Contains(term, StringComparison.OrdinalIgnoreCase)
            || StatusText.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && IsAlreadyManaged)
        {
            IsSelected = false;
        }
    }

    partial void OnStatusKindChanged(PillKind value)
    {
        OnPropertyChanged(nameof(IsStatusOk));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusNeutral));
    }
}

/// <summary>A device in the Credentials step, with an optional per-device override.</summary>
public sealed partial class CredentialRowViewModel(AddPlanItem item) : ObservableObject
{
    public AddPlanItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
    public string Address => Item.Address;
    public string Serial => Item.Serial;
    public string Model => Item.Model;

    [ObservableProperty] public partial string UserName { get; set; } = "root";
    [ObservableProperty] public partial string Password { get; set; } = "";
}

/// <summary>A device in the Set password step.</summary>
public sealed class PlanRowViewModel(AddPlanItem item)
{
    public AddPlanItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
    public string Address => Item.Address;
    public string Serial => Item.Serial;
    public string Model => Item.Model;
}

/// <summary>One line of the Review step.</summary>
public sealed record ReviewRow(string Address, string Serial, string Model, string Action, bool IsWarning);

public sealed partial class WizardStepItem(WizardStep step, string title) : ObservableObject
{
    public WizardStep Step { get; } = step;
    public string Title { get; } = title;

    [ObservableProperty] public partial int Number { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool IsSkipped { get; set; }
}
