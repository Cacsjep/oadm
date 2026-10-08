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

    /// <summary>
    /// A row for one line of an imported device list, before its address was probed ("Waiting"), or with
    /// the line's problem (invalid address, duplicate, too long). It shows the found device once the
    /// probe answers (<see cref="Adopt"/>).
    /// </summary>
    public static DiscoveredRowViewModel ForImport(ImportLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var row = new DiscoveredRowViewModel(
            new DiscoveredDevice { DiscoveredId = "import:" + line.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), Address = line.Address },
            string.Empty,
            line);
        return row;
    }

    private DiscoveredRowViewModel(DiscoveredDevice device, string sessionId, ImportLine line)
    {
        DiscoveredId = device.DiscoveredId;
        SessionId = sessionId;
        ImportLine = line;
        IsImportPlaceholder = true;
        _importChip = line.Problem is null ? ("Waiting", PillKind.Neutral) : ("Not added", PillKind.Error);
        Update(device);
        if (line.Problem is not null)
        {
            SetImportProblem("Not added", line.Problem);
        }
    }

    /// <summary>Stable within the session; an import row takes the id of the device it found.</summary>
    public string DiscoveredId { get; private set; }

    /// <summary>The line of an imported device list this row belongs to; null for scan and manual rows.</summary>
    public ImportLine? ImportLine { get; }

    /// <summary>An import row without a device (waiting, checking, or with a problem).</summary>
    [ObservableProperty] public partial bool IsImportPlaceholder { get; private set; }

    /// <summary>An import row whose line cannot be added (invalid, duplicate, nothing answered, stopped).</summary>
    [ObservableProperty] public partial bool HasImportProblem { get; private set; }

    private (string Text, PillKind Kind)? _importChip;

    /// <summary>Import: the address is being probed now.</summary>
    public void MarkImportChecking()
    {
        if (IsImportPlaceholder && !HasImportProblem)
        {
            _importChip = null; // "Checking..." of a pending login
            Refresh();
        }
    }

    /// <summary>Import: the line cannot be added; <paramref name="detail"/> says why (next to the chip, tooltip).</summary>
    public void SetImportProblem(string chip, string detail, PillKind kind = PillKind.Error)
    {
        _importChip = (chip, kind);
        AuthDetail = detail;
        HasImportProblem = true;
        Refresh();
    }

    /// <summary>Import: the probe of this line found a device; the row shows it from now on.</summary>
    public void Adopt(DiscoveredDevice device, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(device);
        DiscoveredId = device.DiscoveredId;
        SessionId = sessionId;
        _importChip = null;
        IsImportPlaceholder = false;
        HasImportProblem = false;
        Update(device);
    }

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

    /// <summary>Shown right of the status text for failures (e.g. the login error).</summary>
    [ObservableProperty] public partial string StatusDetail { get; private set; } = "";
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
        (ChipText, ChipKind) = _importChip is { } import ? import
            : IsAdded ? ("Added", PillKind.Ok)
            : IsAlreadyManaged ? ("Already added", PillKind.Neutral)
            : AuthState switch
            {
                AuthState.Authenticated => ($"Authenticated ({AuthUserName})", PillKind.Ok),
                AuthState.PasswordNotSet => ("Password not set", PillKind.Warning),
                AuthState.LoginFailed => ("Login failed", PillKind.Error),
                AuthState.Unreachable => ("Unreachable", PillKind.Error),
                _ => ("Checking...", PillKind.Accent),
            };
        StatusDetail = HasImportProblem || (!IsAdded && !IsAlreadyManaged && AuthState is AuthState.LoginFailed or AuthState.Unreachable) ? AuthDetail : "";
        CanAdd = !IsImportPlaceholder && !IsMuted && (AuthState == AuthState.Authenticated || (AuthState == AuthState.PasswordNotSet && PendingPassword is not null));
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
