using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Plugins.ImageHealth.Client;

/// <summary>
/// One detection cell: OK (ok chip), Pending (warning chip: the app waits to confirm), Detected (error chip), Off (neutral:
/// turned off in the app), another value as the camera sent it (neutral), or empty while the app does not run.
/// </summary>
public sealed record DetectionCell(string? State)
{
    public static readonly DetectionCell Unknown = new((string?)null);

    public string Text => State ?? string.Empty;

    public bool IsOk => State == DetectionStates.Ok;

    public bool IsWarning => State == DetectionStates.Pending;

    public bool IsError => State == DetectionStates.Detected;

    public bool HasValue => State is not null;

    /// <summary>Sort: detected first, then pending, OK, other values, off, empty.</summary>
    public int SortKey => State switch
    {
        DetectionStates.Detected => 0,
        DetectionStates.Pending => 1,
        DetectionStates.Ok => 2,
        DetectionStates.Off => 4,
        null => 5,
        _ => 3,
    };
}

/// <summary>A camera row of the Image Health Dashboard; updated in place when the server pushes a change.</summary>
public sealed partial class ImageHealthRowViewModel : ObservableObject
{
    public ImageHealthRowViewModel(ImageHealthRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        DeviceId = row.DeviceId;
        Address = row.Address;
        AddressSortKey = AddressKey(row.Address);
        Model = row.Model ?? string.Empty;
        Apply(row);
    }

    public Guid DeviceId { get; }

    public string Address { get; }

    /// <summary>IPv4 addresses sort numerically.</summary>
    public string AddressSortKey { get; }

    public string Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppText), nameof(IsRunning), nameof(IsNotRunning), nameof(IsAppError), nameof(IsChecking))]
    public partial string App { get; private set; } = AppStates.Checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppText))]
    public partial string? Text { get; private set; }

    [ObservableProperty] public partial DetectionCell Blur { get; private set; } = DetectionCell.Unknown;

    [ObservableProperty] public partial DetectionCell Block { get; private set; } = DetectionCell.Unknown;

    [ObservableProperty] public partial DetectionCell Redirect { get; private set; } = DetectionCell.Unknown;

    [ObservableProperty] public partial DetectionCell UnderExposure { get; private set; } = DetectionCell.Unknown;

    [ObservableProperty] public partial DetectionCell Unsuitability { get; private set; } = DetectionCell.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangedText))]
    public partial DateTimeOffset? ChangedUtc { get; private set; }

    /// <summary>"Running", "Not running", "Checking", or the reason the camera cannot be read.</summary>
    public string AppText => App == AppStates.Error && !string.IsNullOrEmpty(Text) ? Text : App;

    public bool IsRunning => App == AppStates.Running;

    public bool IsNotRunning => App == AppStates.NotRunning;

    public bool IsChecking => App == AppStates.Checking;

    public bool IsAppError => App == AppStates.Error;

    /// <summary>When OADM saw a detection change between two checks, local time.</summary>
    public string ChangedText => ChangedUtc is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Any detection detected.</summary>
    public bool AnyDetected => Blur.IsError || Block.IsError || Redirect.IsError || UnderExposure.IsError || Unsuitability.IsError;

    /// <summary>Any detection pending (and none detected).</summary>
    public bool AnyPending => !AnyDetected && (Blur.IsWarning || Block.IsWarning || Redirect.IsWarning || UnderExposure.IsWarning || Unsuitability.IsWarning);

    /// <summary>Text the search matches.</summary>
    public string SearchText => $"{Address} {Model}";

    public void Apply(ImageHealthRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        App = row.App;
        Text = row.Text;
        Blur = Cell(Blur, row.Blur);
        Block = Cell(Block, row.Block);
        Redirect = Cell(Redirect, row.Redirect);
        UnderExposure = Cell(UnderExposure, row.UnderExposure);
        Unsuitability = Cell(Unsuitability, row.Unsuitability);
        ChangedUtc = row.ChangedUtc;
    }

    private static DetectionCell Cell(DetectionCell current, string? state) => current.State == state ? current : new DetectionCell(state);

    private static string AddressKey(string address)
    {
        var host = address.Split(':')[0];
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }
}
