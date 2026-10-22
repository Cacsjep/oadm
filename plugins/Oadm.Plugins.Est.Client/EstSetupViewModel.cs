using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Est.Client;

/// <summary>A selected camera in the dialog's camera choice.</summary>
public sealed record EstDeviceChoice(IDeviceInfo Device)
{
    public string Label => string.IsNullOrWhiteSpace(Device.Model) ? Device.Address : $"{Device.Model} - {Device.Address}";
}

/// <summary>One enrollment profile of the chosen camera.</summary>
public sealed record EnrollmentProfileRow(CameraProfileInfo Profile)
{
    public string Id => Profile.Id;

    /// <summary>"HTTPS", "IEEE 802.1X", "MQTT", "RTSPS".</summary>
    public string Uses => string.Join(", ", Profile.Services.Select(UseOf));

    public string Server => Profile.Server ?? string.Empty;

    /// <summary>Set up by OADM (named "oadm_...") or by another EST server.</summary>
    public bool IsOadm => Profile.Id.StartsWith(EstTaskIds.ProfilePrefix, StringComparison.Ordinal);

    public string SetUpBy => IsOadm ? "OADM" : "Other";

    public string Status => Profile.Status?.ToUpperInvariant() switch
    {
        "ENROLLED" => "Enrolled",
        "CONNECTED" => "No certificate",
        "UNCONNECTED" => "Server not reached",
        _ => "Unknown",
    };

    public bool IsEnrolled => string.Equals(Profile.Status, "ENROLLED", StringComparison.OrdinalIgnoreCase);

    public bool IsProblem => !IsEnrolled;

    public static string UseOf(string service) => service.ToUpperInvariant() switch
    {
        "WEBSERVER" => "HTTPS",
        "NETAUTH" => "IEEE 802.1X",
        _ => service,
    };
}

/// <summary>
/// "Certificate enrollment" (like the Applications dialog): the enrollment profiles of the chosen camera, read through the
/// task's read-only query; <b>Remove</b> (any profile, confirmed) and <b>Set up</b> apply to every selected camera. For
/// cameras with IEEE 802.1X on the choice to replace their network access certificate too (off by default: a RADIUS
/// server that does not trust the OADM CA would lock them out).
/// </summary>
public sealed partial class EstSetupViewModel : ObservableObject
{
    public const string Title = EstTaskIds.MenuName;

    public const string Dot1xHint = "Only when your RADIUS server trusts the OADM CA. Otherwise these cameras drop off the network.";

    private readonly ITaskDialogContext? _ctx;
    private readonly Dictionary<Guid, Task<CameraProfileList>> _cache = [];
    private bool _started;

    public EstSetupViewModel(IReadOnlyList<IDeviceInfo> devices, ITaskDialogContext? ctx = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _ctx = ctx;
        Devices = [.. devices.Select(d => new EstDeviceChoice(d))];
        Dot1xDevices = devices.Count(d => d.Dot1xEnabled == true);
        SelectedDevice = Devices.Count > 0 ? Devices[0] : null;
    }

    public IReadOnlyList<EstDeviceChoice> Devices { get; }

    public bool ShowDevicePicker => Devices.Count > 1;

    public string ScopeText => Devices.Count == 1
        ? $"Set up and Remove apply to {Devices[0].Label}."
        : string.Create(CultureInfo.InvariantCulture, $"Set up and Remove apply to all {Devices.Count:N0} selected devices.");

    [ObservableProperty]
    public partial EstDeviceChoice? SelectedDevice { get; set; }

    public ObservableCollection<EnrollmentProfileRow> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial EnrollmentProfileRow? SelectedProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(RefreshCommand))]
    public partial bool IsLoading { get; set; }

    /// <summary>Why the profiles cannot be shown (read failed, no certificate enrollment on the camera).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    public bool IsEmpty => !IsLoading && !HasError && Profiles.Count == 0;

    [ObservableProperty]
    public partial bool ConfirmRemoveVisible { get; set; }

    [ObservableProperty]
    public partial string? ConfirmRemoveText { get; set; }

    /// <summary>Selected cameras with IEEE 802.1X on: the check box shows only when there are some.</summary>
    public int Dot1xDevices { get; }

    public bool ShowDot1x => Dot1xDevices > 0;

    public string Dot1xText => Dot1xDevices == 1
        ? "Also replace the network access certificate of 1 camera with IEEE 802.1X on"
        : string.Create(CultureInfo.InvariantCulture, $"Also replace the network access certificate of {Dot1xDevices:N0} cameras with IEEE 802.1X on");

    [ObservableProperty]
    public partial bool ReplaceDot1x { get; set; }

    /// <summary>True: completed (the payload is <see cref="Payload"/>); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The task payload: set up (the IEEE 802.1X choice only counts while the check box shows) or remove.</summary>
    public string Payload { get; private set; } = string.Empty;

    /// <summary>Reads the profiles of the first camera (the window calls it when it opens); later choices read by themselves.</summary>
    public Task LoadAsync()
    {
        _started = true;
        return LoadProfilesAsync();
    }

    partial void OnSelectedDeviceChanged(EstDeviceChoice? value)
    {
        if (_started)
        {
            _ = LoadProfilesAsync();
        }
    }

    private async Task LoadProfilesAsync()
    {
        var device = SelectedDevice;
        ConfirmRemoveVisible = false;
        Profiles.Clear();
        SelectedProfile = null;
        ErrorMessage = null;
        if (device is null || _ctx is null)
        {
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        IsLoading = true;
        try
        {
            var list = await QueryAsync(device.Device.Id).ConfigureAwait(true);
            if (!ReferenceEquals(device, SelectedDevice))
            {
                return;
            }

            if (!list.Supported)
            {
                ErrorMessage = EstTaskIds.NeedsApis.Replace(" Nothing was changed.", string.Empty, StringComparison.Ordinal);
            }

            foreach (var profile in list.Profiles)
            {
                Profiles.Add(new EnrollmentProfileRow(profile));
            }
        }
#pragma warning disable CA1031 // Any query failure is shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ReferenceEquals(device, SelectedDevice))
            {
                ErrorMessage = "Could not read the enrollment profiles: " + ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(device, SelectedDevice))
            {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private Task<CameraProfileList> QueryAsync(Guid deviceId)
    {
        if (!_cache.TryGetValue(deviceId, out var task) || task.IsFaulted || task.IsCanceled)
        {
            task = QueryCoreAsync(deviceId);
            _cache[deviceId] = task;
        }

        return task;
    }

    private async Task<CameraProfileList> QueryCoreAsync(Guid deviceId)
    {
        var json = await _ctx!.QueryAsync(deviceId, EstTaskIds.ListProfilesMethod, null, CancellationToken.None).ConfigureAwait(true);
        return string.IsNullOrWhiteSpace(json)
            ? throw new InvalidOperationException("The server returned no data.")
            : EstJson.Deserialize<CameraProfileList>(json);
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        if (SelectedDevice is { } device)
        {
            _cache.Remove(device.Device.Id);
        }

        return LoadProfilesAsync();
    }

    private bool CanRemove() => SelectedProfile is not null && !IsLoading;

    /// <summary>Asks first (inline, like the Applications dialog); <see cref="ConfirmRemoveCommand"/> completes the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        var profile = SelectedProfile!;
        var where = Devices.Count == 1 ? Devices[0].Label : string.Create(CultureInfo.InvariantCulture, $"all {Devices.Count:N0} selected devices");
        ConfirmRemoveText = profile.IsOadm
            ? $"Remove {profile.Id} from {where}? The cameras keep their certificates until they end, but no longer renew them."
            : $"Remove {profile.Id} from {where}? OADM did not set it up: the cameras stop renewing the certificates of that EST server.";
        ConfirmRemoveVisible = true;
    }

    [RelayCommand]
    private void ConfirmRemove()
    {
        if (SelectedProfile is { } profile)
        {
            Complete(new SetupPayload { Action = SetupAction.Remove, Profiles = [profile.Id] });
        }
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmRemoveVisible = false;

    [RelayCommand]
    private void Ok() => Complete(new SetupPayload(ShowDot1x && ReplaceDot1x));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void Complete(SetupPayload payload)
    {
        Payload = EstJson.Serialize(payload);
        CloseRequested?.Invoke(this, true);
    }
}
