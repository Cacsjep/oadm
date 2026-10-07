using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Discovery;

/// <summary>
/// A device on the add page, updated in place from the discovery stream: where it was found, the
/// result of the server's automatic login (chip) and what the technician can do (Log in, Set password).
/// Only devices that can be added (authenticated, or factory default with a password ready) can be selected.
/// </summary>
public sealed partial class DiscoveredRowViewModel : ObservableObject
{
    public DiscoveredRowViewModel(DiscoveredDevice device, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(device);
        DiscoveredId = device.DiscoveredId;
        SessionId = sessionId;
        Update(device);
    }

    public string DiscoveredId { get; }

    /// <summary>The discovery session that reported the device last (RetryAuth and Commit use it).</summary>
    [ObservableProperty] public partial string SessionId { get; set; }

    [ObservableProperty] public partial string Serial { get; private set; } = "";

    /// <summary>The entered address for "Add manually", else the IP address.</summary>
    [ObservableProperty] public partial string Address { get; private set; } = "";
    [ObservableProperty] public partial string HostName { get; private set; } = "";
    [ObservableProperty] public partial string Model { get; private set; } = "";
    [ObservableProperty] public partial DeviceStatus Status { get; private set; }
    [ObservableProperty] public partial string CategoryIconKey { get; private set; } = "device.generic";
    [ObservableProperty] public partial string CategoryTooltip { get; private set; } = "";
    [ObservableProperty] public partial AuthState AuthState { get; private set; }
    [ObservableProperty] public partial string AuthUserName { get; private set; } = "";
    [ObservableProperty] public partial string AuthDetail { get; private set; } = "";
    [ObservableProperty] public partial string PassphrasePolicy { get; private set; } = "";
    [ObservableProperty] public partial bool IsAlreadyManaged { get; private set; }

    /// <summary>Added from this page (the chip says "Added").</summary>
    [ObservableProperty] public partial bool IsAdded { get; private set; }

    /// <summary>First root password for a factory-default device, set on add. Client memory only until then.</summary>
    [ObservableProperty] public partial string? PendingPassword { get; private set; }

    /// <summary>Checked for adding. Always false when the device cannot be added.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty] public partial string ChipText { get; private set; } = "";
    [ObservableProperty] public partial PillKind ChipKind { get; private set; }
    [ObservableProperty] public partial bool CanAdd { get; private set; }

    public bool IsStatusOk => ChipKind == PillKind.Ok;
    public bool IsStatusWarning => ChipKind == PillKind.Warning;
    public bool IsStatusError => ChipKind == PillKind.Error;
    public bool IsStatusAccent => ChipKind == PillKind.Accent;

    /// <summary>Greyed out: managed already or just added.</summary>
    public bool IsMuted => IsAlreadyManaged || IsAdded;

    public bool IsAuthenticated => AuthState == AuthState.Authenticated && !IsMuted;
    public bool IsFactoryDefault => AuthState == AuthState.PasswordNotSet && !IsMuted;

    /// <summary>"Log in" link: no known credential worked.</summary>
    public bool ShowLogIn => AuthState == AuthState.LoginFailed && !IsMuted;

    /// <summary>"Set password" link: factory default without a password yet.</summary>
    public bool ShowSetPassword => IsFactoryDefault && PendingPassword is null;

    /// <summary>"Change" link next to "Password ready".</summary>
    public bool ShowPasswordReady => IsFactoryDefault && PendingPassword is not null;

    /// <summary>Tooltip of the chip: why the login failed, who is logged in, or the passphrase policy.</summary>
    public string ChipTooltip => AuthState switch
    {
        AuthState.Authenticated => $"OADM logged in as {AuthUserName} with a known credential",
        AuthState.PasswordNotSet => PasswordRules.Hint(PassphrasePolicy),
        _ when AuthDetail.Length > 0 => AuthDetail,
        _ => ChipText,
    };

    public void Update(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Serial = device.Serial;
        Address = device.EnteredAddress.Length > 0 ? device.EnteredAddress : device.Address;
        HostName = device.HostName;
        if (device.Model.Length > 0 || Model.Length == 0)
        {
            Model = device.Model;
        }

        Status = device.Status;
        if (device.Category != DeviceCategory.Unknown || CategoryTooltip.Length == 0)
        {
            CategoryIconKey = DeviceCategoryInfo.ToIconKey(device.Category);
            CategoryTooltip = DeviceCategoryInfo.ToTooltip(device.Category, device.ProductType);
        }

        AuthState = device.AlreadyManaged ? AuthState.AlreadyAdded : device.AuthState;
        AuthUserName = device.AuthUserName;
        AuthDetail = device.AuthDetail;
        if (device.PassphrasePolicy.Length > 0 || AuthState != AuthState.PasswordNotSet)
        {
            PassphrasePolicy = device.PassphrasePolicy;
        }

        IsAlreadyManaged = device.AlreadyManaged || device.AuthState == AuthState.AlreadyAdded;
        Refresh();
    }

    /// <summary>Stores (or clears) the first password; the device becomes selectable.</summary>
    public void SetPendingPassword(string? password)
    {
        PendingPassword = string.IsNullOrEmpty(password) ? null : password;
        Refresh();
    }

    /// <summary>The page added the device.</summary>
    public void MarkAdded()
    {
        IsAdded = true;
        PendingPassword = null;
        Refresh();
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
            || ChipText.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanAdd)
        {
            IsSelected = false;
        }
    }

    partial void OnChipKindChanged(PillKind value)
    {
        OnPropertyChanged(nameof(IsStatusOk));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusAccent));
    }

    private void Refresh()
    {
        (ChipText, ChipKind) = IsAdded ? ("Added", PillKind.Ok)
            : IsAlreadyManaged ? ("Already added", PillKind.Neutral)
            : AuthState switch
            {
                AuthState.Authenticated => ($"Authenticated ({AuthUserName})", PillKind.Ok),
                AuthState.PasswordNotSet => ("Password not set", PillKind.Warning),
                AuthState.LoginFailed => ("Login failed", PillKind.Error),
                AuthState.Unreachable => ("Unreachable", PillKind.Error),
                _ => ("Checking...", PillKind.Accent),
            };
        CanAdd = !IsMuted && (AuthState == AuthState.Authenticated || (AuthState == AuthState.PasswordNotSet && PendingPassword is not null));
        if (!CanAdd)
        {
            IsSelected = false;
        }

        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(IsFactoryDefault));
        OnPropertyChanged(nameof(ShowLogIn));
        OnPropertyChanged(nameof(ShowSetPassword));
        OnPropertyChanged(nameof(ShowPasswordReady));
        OnPropertyChanged(nameof(ChipTooltip));
    }
}
