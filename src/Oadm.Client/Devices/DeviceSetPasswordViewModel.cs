using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Discovery;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Devices;

/// <summary>
/// "Set password" dialog (device context menu) for managed devices in factory default (status Password not set): the
/// first root password like the add page's editor (new + confirm, user root fixed, hint with the devices' passphrase
/// policy, the strictest when they differ; checked here, the device checks again). One server call for all devices; the
/// devices that took it are done, the others stay in the dialog with the reason directly below the password field.
/// </summary>
public sealed partial class DeviceSetPasswordViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private List<DeviceRowViewModel> _devices;
    private IReadOnlyList<string?> _policies = [];

    public DeviceSetPasswordViewModel(IOadmApi api, IReadOnlyList<DeviceRowViewModel> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _devices = [.. devices];
        Validation
            .Rule(nameof(Password), () => PasswordRules.PasswordError(Password, _policies))
            .Rule(nameof(ConfirmPassword), () => PasswordRules.ConfirmError(Password, ConfirmPassword));
        Validation.Validate();
    }

    /// <summary>The devices still without a password (those that took it leave the list).</summary>
    public IReadOnlyList<DeviceRowViewModel> Devices => _devices;

    /// <summary>"10.0.0.40 (AXIS F9111) has no password yet (factory default)." / "3 devices have no password yet (factory default)."</summary>
    public string Intro => _devices.Count == 1
        ? $"{Label(_devices[0])} has no password yet (factory default)."
        : string.Create(CultureInfo.CurrentCulture, $"{_devices.Count:N0} devices have no password yet (factory default).");

    /// <summary>The user is always root (pwdgrp.cgi).</summary>
    public string UserName { get; } = "root";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    /// <summary>The rules of the devices' passphrase policy (strictest of all).</summary>
    [ObservableProperty]
    public partial string PolicyHint { get; private set; } = PasswordRules.Hint((string?)null);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetPasswordCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string BusyText => _devices.Count == 1
        ? "Setting the password on " + _devices[0].DisplayAddress
        : string.Create(CultureInfo.CurrentCulture, $"Setting the password on {_devices.Count:N0} devices");

    /// <summary>Devices that took the password (all calls of this dialog).</summary>
    public int SetCount { get; private set; }

    /// <summary>True: every device took the password (the dialog closes); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Reads the devices' passphrase policies for the hint and the checks. Never fails (the device checks anyway).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyDictionary<string, string> policies = await _api.GetPassphrasePoliciesAsync(_devices.Select(d => d.Id).ToList(), ct).ConfigureAwait(true);
            ApplyPolicies([.. policies.Values.Where(p => !string.IsNullOrWhiteSpace(p))]);
        }
#pragma warning disable CA1031 // Only the hint: the basic rules stay.
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Uses these policies for the hint and the checks (also tests).</summary>
    public void ApplyPolicies(IReadOnlyList<string?> policies)
    {
        _policies = policies ?? [];
        PolicyHint = PasswordRules.Hint(_policies);
        Validation.Validate();
    }

    [RelayCommand(CanExecute = nameof(CanSetPassword))]
    private async Task SetPasswordAsync()
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
            SetFirstPasswordReply reply = await _api.SetFirstPasswordAsync([.. byId.Keys], Password, CancellationToken.None).ConfigureAwait(true);
            var failed = reply.Results.Where(r => !r.Ok && byId.ContainsKey(r.DeviceId)).ToList();
            SetCount += reply.Results.Count(r => r.Ok);
            int total = _devices.Count;
            if (failed.Count == 0)
            {
                CloseRequested?.Invoke(this, true);
                return;
            }

            _devices = [.. failed.Select(r => byId[r.DeviceId])];
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(Intro));
            Validation.SetServerError(nameof(Password), ResultText(total, failed, byId));
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(nameof(Password), "The password could not be set: " + ex.Status.Detail);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool CanSetPassword() => !IsBusy && IsFormValid;

    protected override void OnValidationChanged() => SetPasswordCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// One device: its reason. Several: "2 of 5 devices failed: 10.0.0.40 (The device already has a password. Nothing was
    /// changed), ..." with at most <see cref="DeviceLoginViewModel.MaxNamedDevices"/> named, then "+N more".
    /// </summary>
    internal static string ResultText(int total, IReadOnlyList<SetFirstPasswordResult> failed, IReadOnlyDictionary<string, DeviceRowViewModel> byId)
    {
        if (total == 1 && failed.Count == 1)
        {
            return failed[0].Message;
        }

        IEnumerable<string> names = failed.Take(DeviceLoginViewModel.MaxNamedDevices).Select(r =>
        {
            string address = byId.TryGetValue(r.DeviceId, out DeviceRowViewModel? row) ? row.DisplayAddress : r.DeviceId;
            return $"{address} ({r.Message.TrimEnd('.')})";
        });
        string list = string.Join(", ", names) + (failed.Count > DeviceLoginViewModel.MaxNamedDevices
            ? string.Create(CultureInfo.CurrentCulture, $" +{failed.Count - DeviceLoginViewModel.MaxNamedDevices:N0} more")
            : "");
        return string.Create(CultureInfo.CurrentCulture, $"{failed.Count:N0} of {total:N0} devices failed: {list}");
    }

    private static string Label(DeviceRowViewModel device) =>
        string.IsNullOrEmpty(device.Model) ? device.DisplayAddress : $"{device.DisplayAddress} ({device.Model})";
}
