using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Shared;

namespace Oadm.Plugins.EdgeStorage.Client;

/// <summary>A camera row of the Edge storage page; updated in place when the server pushes a change.</summary>
public sealed partial class StorageRowViewModel : ObservableObject
{
    public StorageRowViewModel(StorageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        DeviceId = row.DeviceId;
        Address = row.Address;
        AddressSortKey = AddressSortKeys.Of(row.Address);
        Model = row.Model ?? string.Empty;
        Row = row;
    }

    public Guid DeviceId { get; }

    public string Address { get; }

    public string AddressSortKey { get; }

    public string Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsOk), nameof(IsWarning), nameof(IsProblem), nameof(IsReading), nameof(HasCard),
        nameof(SizeText), nameof(UsedText), nameof(UsedPercent), nameof(ShowUsed), nameof(WearText), nameof(WearSortKey), nameof(IsWorn),
        nameof(EncryptedText), nameof(RetentionText), nameof(RetentionDays), nameof(StateSortKey), nameof(SizeSortKey))]
    public partial StorageRow Row { get; private set; }

    /// <summary>"OK", the problem ("The SD card failed"), "No SD card" or why the camera could not be read.</summary>
    public string StateText => Row.Text ?? Row.State;

    public bool IsOk => Row.State == CardStates.Ok;

    public bool IsWarning => Row.State == CardStates.Warning;

    public bool IsProblem => Row.State is CardStates.Problem or CardStates.Error;

    public bool IsReading => Row.State == CardStates.Reading;

    /// <summary>A card is in the slot: the tools apply.</summary>
    public bool HasCard => Row.HasCard;

    /// <summary>Problems first, then worn cards, OK, no card.</summary>
    public int StateSortKey => Row.State switch
    {
        CardStates.Problem => 0,
        CardStates.Error => 1,
        CardStates.Warning => 2,
        CardStates.Ok => 3,
        _ => 4,
    };

    public string SizeText => Row.TotalKb is { } total and > 0 ? StorageFormat.Size(total) : string.Empty;

    public long SizeSortKey => Row.TotalKb ?? -1;

    public int UsedPercent => Row.UsedPercent ?? 0;

    public bool ShowUsed => Row.UsedPercent is not null;

    /// <summary>"12 % · 52.1 GB free".</summary>
    public string UsedText => Row.UsedPercent is { } used && Row.FreeKb is { } free
        ? string.Create(CultureInfo.CurrentCulture, $"{used} % · {StorageFormat.Size(free)} free")
        : string.Empty;

    /// <summary>"1 %", "Not reported" when the card does not say, empty without a card.</summary>
    public string WearText => !HasCard ? string.Empty : Row.WearPercent is { } wear ? string.Create(CultureInfo.CurrentCulture, $"{wear} %") : "Not reported";

    public int WearSortKey => Row.WearPercent ?? -1;

    public bool IsWorn => Row.WearPercent is { } wear && wear >= (Row.WearAlertPercent ?? EdgeStorageInfo.DefaultWearAlert);

    public string EncryptedText => !HasCard || Row.Encrypted is null ? string.Empty : Row.Encrypted == true ? "Yes" : "No";

    /// <summary>"7 days", "No limit".</summary>
    public string RetentionText => !HasCard || Row.RetentionDays is null ? string.Empty : StorageTaskPlugin.RetentionText(Row.RetentionDays);

    public int RetentionDays => Row.RetentionDays ?? -1;

    public string SearchText => $"{Address} {Model} {StateText}";

    public void Apply(StorageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
    }
}
