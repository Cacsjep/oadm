using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Est.Client;

/// <summary>
/// "Set up certificate enrollment": the confirmation, and for cameras with IEEE 802.1X on the choice to replace their
/// network access certificate too (off by default: a RADIUS server that does not trust the OADM CA would lock them out).
/// </summary>
public sealed partial class EstSetupViewModel : ObservableObject
{
    public const string Title = EstTaskIds.SetupName;

    public EstSetupViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        Dot1xDevices = devices.Count(d => d.Dot1xEnabled == true);
        Message = $"Set up certificate enrollment on {EstConfirmations.DeviceCount(devices.Count)}? They get certificates from the OADM CA for the profiles of the EST server and renew them by themselves.";
    }

    public string Message { get; }

    /// <summary>Selected cameras with IEEE 802.1X on: the check box shows only when there are some.</summary>
    public int Dot1xDevices { get; }

    public bool ShowDot1x => Dot1xDevices > 0;

    public string Dot1xText => Dot1xDevices == 1
        ? "Also replace the network access certificate of 1 camera with IEEE 802.1X on"
        : string.Create(CultureInfo.InvariantCulture, $"Also replace the network access certificate of {Dot1xDevices:N0} cameras with IEEE 802.1X on");

    public const string Dot1xHint = "Only when your RADIUS server trusts the OADM CA. Otherwise these cameras drop off the network.";

    [ObservableProperty]
    public partial bool ReplaceDot1x { get; set; }

    /// <summary>True: set up (the payload is <see cref="Payload"/>); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The task payload: the choice only counts while the check box shows.</summary>
    public string Payload => EstJson.Serialize(new SetupPayload(ShowDot1x && ReplaceDot1x));

    [RelayCommand]
    private void Ok() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
