using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Devices;

/// <summary>
/// The context menu's "OADM > RTSP port": the port OADM connects to for live view and events of the selected video
/// devices, for cameras behind a port forward (NAT, AXIS Virtual Loan Tool). Stored in OADM only; the devices are not
/// changed. Empty or 554 = the default. One call for the whole selection ("Optics Control").
/// </summary>
public sealed partial class RtspPortViewModel : ValidatingViewModel
{
    public const int DefaultPort = 554;

    private readonly IOadmApi _api;
    private readonly IReadOnlyList<DeviceRowViewModel> _devices;

    public RtspPortViewModel(IOadmApi api, IReadOnlyList<DeviceRowViewModel> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _devices = devices;
        Validation.Rule(nameof(PortText), () => ParsePort(PortText, out _) ? null : "Enter a port from 1 to 65535.");
        Validation.Validate();
    }

    public IReadOnlyList<DeviceRowViewModel> Devices => _devices;

    /// <summary>"OADM connects to this port for live view and events of 10.0.0.48 (P3265-V). The device is not changed."</summary>
    public string Intro => (_devices.Count == 1
        ? $"OADM connects to this port for live view and events of {Label(_devices[0])}."
        : string.Create(CultureInfo.CurrentCulture, $"OADM connects to this port for live view and events of {_devices.Count:N0} devices."))
        + " The device is not changed.";

    /// <summary>The port; empty = 554.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string PortText { get; set; } = DefaultPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>"The devices use different ports." when the selection does not share one.</summary>
    [ObservableProperty]
    public partial string? Hint { get; private set; } = "Default 554. Change it only for a port forward.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string? SaveBlockedReason => Validation.FirstErrorOf([nameof(PortText)]);

    /// <summary>True: saved (the dialog closes); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the port the devices use now.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var reply = await _api.GetRtspPortAsync([.. _devices.Select(d => d.Id)], ct).ConfigureAwait(true);
            PortText = reply.Mixed ? "" : reply.Port.ToString(CultureInfo.InvariantCulture);
            if (reply.Mixed)
            {
                Hint = "The devices use different ports. Save sets this one for all of them.";
            }

            Validation.Reset(nameof(PortText));
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException)
        {
            // The field keeps 554; Save still works.
        }
    }

    /// <summary>Empty = default (554); otherwise a whole number from 1 to 65535.</summary>
    public static bool ParsePort(string? text, out int port)
    {
        port = DefaultPort;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 65535)
        {
            port = value;
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void UseDefault() => PortText = DefaultPort.ToString(CultureInfo.InvariantCulture);

    private bool CanSave() => !IsBusy && ParsePort(PortText, out _);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        if (!ParsePort(PortText, out var port))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _api.SetRtspPortAsync([.. _devices.Select(d => d.Id)], port, CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(nameof(PortText), ex.Status.Detail);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private static string Label(DeviceRowViewModel device) =>
        string.IsNullOrEmpty(device.Model) ? device.DisplayAddress : $"{device.DisplayAddress} ({device.Model})";
}
