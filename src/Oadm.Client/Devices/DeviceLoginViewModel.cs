using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Devices;

/// <summary>
/// "Log in" dialog (device context menu) for managed devices whose stored credentials are rejected (status
/// Credentials required): user name, password, "Save to credential list" (administrators only). One server call for
/// all devices; the devices that accept the login are done, the others stay in the dialog with the reason directly
/// below the password field. All accepted: the dialog closes.
/// </summary>
public sealed partial class DeviceLoginViewModel : ValidatingViewModel
{
    /// <summary>Most device addresses named in the result line; the rest is "+N more".</summary>
    public const int MaxNamedDevices = 5;

    private readonly IOadmApi _api;
    private List<DeviceRowViewModel> _devices;
    private bool _userNameEdited;

    /// <param name="devices">The devices to log in to (those of the selection with status Credentials required).</param>
    /// <param name="canSaveToCredentialList">The user is an administrator (the credential list is Admin only).</param>
    public DeviceLoginViewModel(IOadmApi api, IReadOnlyList<DeviceRowViewModel> devices, bool canSaveToCredentialList)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _devices = [.. devices];
        CanSaveToCredentialList = canSaveToCredentialList;
        Validation
            .Rule(nameof(UserName), () => UserName.Trim().Length == 0 ? "Enter a user name." : null)
            .Rule(nameof(Password), () => Password.Length == 0 ? "Enter the password." : null);
        Validation.Validate();
    }

    /// <summary>The devices still to log in to (accepted ones leave the list).</summary>
    public IReadOnlyList<DeviceRowViewModel> Devices => _devices;

    /// <summary>"10.0.0.48 (P3265-V) rejects the stored credentials." / "5 devices reject the stored credentials."</summary>
    public string Intro => _devices.Count == 1
        ? $"{Label(_devices[0])} rejects the stored credentials."
        : string.Create(CultureInfo.CurrentCulture, $"{_devices.Count:N0} devices reject the stored credentials.");

    [ObservableProperty]
    public partial string UserName { get; set; } = "root";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool SaveToCredentialList { get; set; } = true;

    /// <summary>Administrators only: operators do not see the check box.</summary>
    public bool CanSaveToCredentialList { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogInCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    /// <summary>"Logging in to 5 devices" while the server tries the login.</summary>
    public string BusyText => _devices.Count == 1
        ? "Logging in to " + _devices[0].DisplayAddress
        : string.Create(CultureInfo.CurrentCulture, $"Logging in to {_devices.Count:N0} devices");

    /// <summary>The server could not save the login in the credential list (full); the login itself worked.</summary>
    [ObservableProperty]
    public partial string? CredentialListNote { get; private set; }

    /// <summary>Devices that accepted the login (all calls of this dialog).</summary>
    public int LoggedInCount { get; private set; }

    /// <summary>True: every device accepted the login (the dialog closes); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Prefill: the user name stored for all devices when they share one, else "root". Never fails.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            string stored = await _api.GetCredentialUserNameAsync(_devices.Select(d => d.Id).ToList(), ct).ConfigureAwait(true);
            if (!_userNameEdited && !string.IsNullOrWhiteSpace(stored))
            {
                UserName = stored;
                _userNameEdited = false;
                Validation.Reset(nameof(UserName));
            }
        }
#pragma warning disable CA1031 // Only the prefill: "root" stays.
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    partial void OnUserNameChanged(string value) => _userNameEdited = true;

    [RelayCommand(CanExecute = nameof(CanLogIn))]
    private async Task LogInAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid || _devices.Count == 0)
        {
            return;
        }

        IsBusy = true;
        OnPropertyChanged(nameof(BusyText));
        try
        {
            var byId = _devices.ToDictionary(d => d.Id);
            DeviceLogInReply reply = await _api.LogInDevicesAsync(
                [.. byId.Keys], UserName.Trim(), Password, CanSaveToCredentialList && SaveToCredentialList, CancellationToken.None).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(reply.CredentialListNote))
            {
                CredentialListNote = reply.CredentialListNote;
            }

            var failed = reply.Results.Where(r => !r.Ok && byId.ContainsKey(r.DeviceId)).ToList();
            LoggedInCount += reply.Results.Count(r => r.Ok);
            int total = _devices.Count;
            if (failed.Count == 0)
            {
                CloseRequested?.Invoke(this, true);
                return;
            }

            // The devices that accepted it are done; the dialog keeps the others.
            _devices = [.. failed.Select(r => byId[r.DeviceId])];
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(Intro));
            Validation.SetServerError(nameof(Password), ResultText(total, failed, byId));
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(nameof(Password), "The login could not be checked: " + ex.Status.Detail);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool CanLogIn() => !IsBusy && IsFormValid;

    protected override void OnValidationChanged() => LogInCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// One device: its reason ("The user name or password is wrong."). Several: "2 of 5 devices rejected it: 10.0.0.48,
    /// 10.0.0.49", or with other reasons "2 of 5 devices failed: 10.0.0.48 (wrong user name or password), 10.0.0.49
    /// (Unreachable - No route to host)". At most <see cref="MaxNamedDevices"/> named, then "+N more".
    /// </summary>
    internal static string ResultText(int total, IReadOnlyList<DeviceLogInResult> failed, IReadOnlyDictionary<string, DeviceRowViewModel> byId)
    {
        if (total == 1 && failed.Count == 1)
        {
            return failed[0].Message;
        }

        bool allRejected = failed.All(r => r.Rejected);
        IEnumerable<string> names = failed.Take(MaxNamedDevices).Select(r =>
        {
            string address = byId.TryGetValue(r.DeviceId, out DeviceRowViewModel? row) ? row.DisplayAddress : r.DeviceId;
            return allRejected ? address : $"{address} ({(r.Rejected ? "wrong user name or password" : r.Message.TrimEnd('.'))})";
        });
        string list = string.Join(", ", names) + (failed.Count > MaxNamedDevices
            ? string.Create(CultureInfo.CurrentCulture, $" +{failed.Count - MaxNamedDevices:N0} more")
            : "");
        string verb = allRejected ? "rejected it" : "failed";
        return string.Create(CultureInfo.CurrentCulture, $"{failed.Count:N0} of {total:N0} devices {verb}: {list}");
    }

    private static string Label(DeviceRowViewModel device) =>
        string.IsNullOrEmpty(device.Model) ? device.DisplayAddress : $"{device.DisplayAddress} ({device.Model})";
}
