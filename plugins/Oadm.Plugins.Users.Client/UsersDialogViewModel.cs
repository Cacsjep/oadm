using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Users.Client;

/// <summary>A role choice in the dialog.</summary>
public sealed record RoleOption(UserRole Role, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One existing account of the first selected device.</summary>
public sealed partial class ExistingUserRow(DeviceUser user, string? protectedReason = null) : ObservableObject
{
    public DeviceUser User { get; } = user;

    public string Name => User.Name;

    public string RoleText => UserRoles.Describe(User.Role, User.Ptz) + (User.IsCurrentAccount ? " (used by OADM)" : string.Empty);

    /// <summary>Why OADM never removes this account (the account OADM uses, the last administrator); null otherwise.</summary>
    public string? ProtectedReason { get; } = protectedReason;

    public bool IsProtected => ProtectedReason is not null;

    /// <summary>False for protected rows in Remove mode: greyed and not selectable, the tooltip says why.</summary>
    [ObservableProperty]
    public partial bool IsSelectable { get; set; } = true;

    /// <summary>Row tooltip: the reason when the row cannot be selected.</summary>
    public string? Tooltip => IsSelectable ? null : ProtectedReason;

    partial void OnIsSelectableChanged(bool value) => OnPropertyChanged(nameof(Tooltip));
}

/// <summary>
/// View model of the "Users" dialog: Add, Change (password and/or role) or Remove one user on all
/// selected devices. Reads the existing users of the first device through the read-only plugin
/// query. Holds the password in memory only; <see cref="BuildPayload"/> returns the task payload.
/// Every rule reports below its field (<see cref="ValidatingViewModel"/>); Apply stays disabled while any
/// error exists and its tooltip says why.
/// </summary>
public sealed partial class UsersDialogViewModel : ValidatingViewModel
{
    /// <summary>Validation key of the Change mode check boxes ("password, role or both").</summary>
    public const string ChangeSelection = "ChangeSelection";

    /// <summary>Validation key of the Remove mode selection in the Existing users list (tooltip only).</summary>
    public const string RemoveSelection = "RemoveSelection";

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private string? _currentAccount;
    private IReadOnlyList<ExistingUserRow> _selectedRows = [];

    public UsersDialogViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        SelectedRole = Roles[2];
        Validation
            .Rule(nameof(UserName), () => ShowUserName ? CredentialRules.ValidateUserName(UserName.Trim()) : null)
            .Rule(ChangeSelection, () => IsChange && !ChangePassword && !ChangeRole ? "Choose what to change: password, role or both." : null)
            .Rule(nameof(Password), () => ShowPassword ? CredentialRules.ValidatePassword(Password, Policy) : null)
            .Rule(nameof(ConfirmPassword), () => ShowPassword && !string.Equals(Password, ConfirmPassword, StringComparison.Ordinal)
                ? (ConfirmPassword.Length == 0 ? "Enter the password again." : "The passwords do not match.")
                : null)
            .Rule(nameof(SelectedRole), () => ShowRole && SelectedRole.Role == UserRole.None ? "Choose a role." : null)
            .Rule(RemoveSelection, () => !IsRemove ? null
                : UsersToRemove.Count == 0 ? "Choose the users to remove in the Existing users list."
                : UsersToRemove.Count > UsersPayload.MaxRemoveUsers ? $"At most {UsersPayload.MaxRemoveUsers} users can be removed at once."
                : null);
        Validation.Validate();
        UsersTitle = devices.Count switch
        {
            0 => "Existing users",
            1 => $"Existing users on {Describe(devices[0])}",
            _ => $"Existing users on {Describe(devices[0])} (first of {devices.Count} selected devices)",
        };
        Update();
    }

    /// <summary>Raised with the payload JSON (Apply) or null (Cancel).</summary>
    public event EventHandler<string?>? CloseRequested;

    public IReadOnlyList<RoleOption> Roles { get; } =
    [
        new(UserRole.Administrator, "Administrator"),
        new(UserRole.Operator, "Operator"),
        new(UserRole.Viewer, "Viewer"),
    ];

    public ObservableCollection<ExistingUserRow> ExistingUsers { get; } = [];

    public int DeviceCount => _devices.Count;

    public string UsersTitle { get; }

    /// <summary>Card description of the existing users: the load status (the title names the source device).</summary>
    public string UsersDescription => UsersStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdd), nameof(IsChange), nameof(IsRemove), nameof(ShowUserName), nameof(ShowPassword), nameof(ShowRole), nameof(ApplyText), nameof(MultiSelect))]
    private UsersMode _mode = UsersMode.Add;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private RoleOption _selectedRole;

    [ObservableProperty]
    private bool _ptz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassword))]
    private bool _changePassword = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRole))]
    private bool _changeRole;

    [ObservableProperty]
    private ExistingUserRow? _selectedExistingUser;

    [ObservableProperty]
    private PassphrasePolicy _policy;

    [ObservableProperty]
    private string _policyHint = CredentialRules.Hint(PassphrasePolicy.None);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsersDescription))]
    private string _usersStatus = "Loading users...";

    [ObservableProperty]
    private string? _lockOutWarning;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    /// <summary>Add radio button.</summary>
    public bool IsAdd
    {
        get => Mode == UsersMode.Add;
        set
        {
            if (value)
            {
                Mode = UsersMode.Add;
            }
        }
    }

    /// <summary>Change radio button.</summary>
    public bool IsChange
    {
        get => Mode == UsersMode.Change;
        set
        {
            if (value)
            {
                Mode = UsersMode.Change;
            }
        }
    }

    /// <summary>Remove radio button.</summary>
    public bool IsRemove
    {
        get => Mode == UsersMode.Remove;
        set
        {
            if (value)
            {
                Mode = UsersMode.Remove;
            }
        }
    }

    /// <summary>Add and Change type the user name; Remove picks the users in the Existing users list.</summary>
    public bool ShowUserName => !IsRemove;

    /// <summary>Remove mode allows several rows to be selected (one task per device removes all of them).</summary>
    public bool MultiSelect => IsRemove;

    /// <summary>The users Remove removes: the selected rows that are not protected, in list order.</summary>
    public IReadOnlyList<string> UsersToRemove => _selectedRows.Where(r => !r.IsProtected).Select(r => r.Name).ToList();

    /// <summary>Remove mode, left card: the users chosen in the Existing users list, one per line.</summary>
    public string UsersToRemoveText => UsersToRemove.Count == 0 ? "None selected yet." : string.Join(Environment.NewLine, UsersToRemove);

    /// <summary>Remove mode: why some users cannot be chosen.</summary>
    public static string RemoveNote => "The account OADM uses and the last administrator cannot be removed.";

    public bool ShowPassword => IsAdd || (IsChange && ChangePassword);

    public bool ShowRole => IsAdd || (IsChange && ChangeRole);

    /// <summary>Error of the Change mode check boxes (shown below them).</summary>
    public string? ChangeSelectionError => ErrorOf(ChangeSelection);

    /// <summary>Why Apply is disabled (tooltip).</summary>
    public string? ApplyBlockedReason => CanApply ? null : FormError ?? "Check the input.";

    public string ApplyText => Mode switch
    {
        UsersMode.Add => "Add user",
        UsersMode.Change => "Change user",
        _ => UsersToRemove.Count > 1 ? "Remove users" : "Remove user",
    };

    /// <summary>Loads the users of the first selected device. Failures are shown, never thrown: the dialog stays usable.</summary>
    public async Task LoadAsync(ITaskDialogContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_devices.Count == 0)
        {
            UsersStatus = "No device selected.";
            return;
        }

        try
        {
            var json = await ctx.QueryAsync(_devices[0].Id, UsersJson.ListUsersMethod, null, ct).ConfigureAwait(true);
            var result = UsersJson.ParseQueryResult(json);
            if (result is null)
            {
                UsersStatus = "The device returned no user list.";
                return;
            }

            Apply(result);
        }
        catch (OperationCanceledException)
        {
            UsersStatus = "Loading users was cancelled.";
        }
#pragma warning disable CA1031 // Any failure (server unreachable, query not available yet) only disables the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UsersStatus = "Existing users could not be loaded: " + ex.Message;
        }
    }

    /// <summary>Shows a query result (public for tests).</summary>
    public void Apply(UsersQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ExistingUsers.Clear();
        _selectedRows = [];
        var admins = result.Users.Count(u => u.Role == UserRole.Administrator);
        foreach (var user in result.Users)
        {
            var isCurrent = user.IsCurrentAccount
                || (result.CurrentAccount is not null && string.Equals(user.Name, result.CurrentAccount, StringComparison.OrdinalIgnoreCase));
            var reason = isCurrent
                ? "OADM uses this account for the device; it cannot be removed."
                : user.Role == UserRole.Administrator && admins <= 1
                    ? "The last administrator of the device cannot be removed."
                    : null;
            ExistingUsers.Add(new ExistingUserRow(user, reason) { IsSelectable = !(IsRemove && reason is not null) });
        }

        _currentAccount = result.CurrentAccount;
        Policy = result.Policy;
        PolicyHint = CredentialRules.Hint(result.Policy) + (_devices.Count > 1 ? " Every device also checks its own policy." : string.Empty);
        UsersStatus = !result.Supported
            ? result.Message ?? "This device does not support user management."
            : result.Message ?? $"{result.Users.Count} user(s).";
        Update();
    }

    /// <summary>The task payload for the current input, or null when the input is invalid.</summary>
    public string? BuildPayload()
    {
        var payload = CreatePayload();
        try
        {
            UserChangePlanner.ValidatePayload(payload, Policy);
        }
        catch (UserManagementException)
        {
            return null;
        }

        return ConfirmError() is null ? UsersJson.Serialize(payload) : null;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Validation.ShowAll();
        var payload = BuildPayload();
        if (payload is not null)
        {
            CloseRequested?.Invoke(this, payload);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    partial void OnModeChanged(UsersMode value)
    {
        foreach (var row in ExistingUsers)
        {
            row.IsSelectable = !(value == UsersMode.Remove && row.IsProtected);
        }

        Update();
    }

    /// <summary>
    /// The rows selected in the Existing users list (the view forwards the grid selection). Remove mode
    /// removes every selectable one; Add and Change use the last one like a single click.
    /// </summary>
    public void SetSelectedUsers(IEnumerable<ExistingUserRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selected = rows.Where(r => ExistingUsers.Contains(r)).ToList();
        _selectedRows = selected;
        if (!IsRemove)
        {
            SelectedExistingUser = selected.Count == 0 ? null : selected[^1];
        }

        OnPropertyChanged(nameof(UsersToRemove));
        OnPropertyChanged(nameof(UsersToRemoveText));
        OnPropertyChanged(nameof(ApplyText));
        Update();
    }

    partial void OnUserNameChanged(string value) => Update();

    partial void OnPasswordChanged(string value) => Update();

    partial void OnConfirmPasswordChanged(string value) => Update();

    partial void OnSelectedRoleChanged(RoleOption value) => Update();

    partial void OnPtzChanged(bool value) => Update();

    partial void OnChangePasswordChanged(bool value)
    {
        Validation.Touch(ChangeSelection);
        Update();
    }

    partial void OnChangeRoleChanged(bool value)
    {
        Validation.Touch(ChangeSelection);
        Update();
    }

    partial void OnPolicyChanged(PassphrasePolicy value) => Update();

    partial void OnSelectedExistingUserChanged(ExistingUserRow? value)
    {
        if (value is null)
        {
            return;
        }

        UserName = value.Name;
        if (value.User.Role != UserRole.None)
        {
            SelectedRole = Roles.First(r => r.Role == value.User.Role);
        }

        Ptz = value.User.Ptz;
        if (IsAdd)
        {
            Mode = UsersMode.Change;
        }
    }

    private UsersPayload CreatePayload() => new()
    {
        Mode = Mode,
        UserName = IsRemove ? string.Empty : UserName.Trim(),
        UserNames = IsRemove ? UsersToRemove : null,
        Password = ShowPassword ? Password : null,
        Role = SelectedRole.Role,
        Ptz = Ptz,
        ChangePassword = IsChange && ChangePassword,
        ChangeRole = IsChange && ChangeRole,
    };

    private string? ConfirmError() =>
        ShowPassword && !string.Equals(Password, ConfirmPassword, StringComparison.Ordinal) ? "The passwords do not match." : null;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(ChangeSelectionError));
        Update();
    }

    private void Update()
    {
        var payload = CreatePayload();
        Validation.Validate(); // the field rules (each below its field)
        var valid = Validation.IsValid;
        if (valid)
        {
            try
            {
                UserChangePlanner.ValidatePayload(payload, Policy); // what the server checks, as a last guard
            }
            catch (UserManagementException)
            {
                valid = false;
            }
        }

        CanApply = valid && ConfirmError() is null;
        OnPropertyChanged(nameof(ApplyBlockedReason));
        LockOutWarning = BuildLockOutWarning(payload);
        Summary = BuildSummary(payload);
    }

    private string? BuildLockOutWarning(UsersPayload payload)
    {
        if (payload.Mode == UsersMode.Add)
        {
            return null;
        }

        if (payload.Mode == UsersMode.Change && _currentAccount is not null && string.Equals(payload.UserName, _currentAccount, StringComparison.OrdinalIgnoreCase))
        {
            return $"{_currentAccount} is the account OADM uses for {Describe(_devices[0])}. OADM will refuse to demote it or change its password there.";
        }

        return null;
    }

    private string BuildSummary(UsersPayload p)
    {
        var name = p.UserName.Length == 0 ? "<user name>" : $"{p.UserName}";
        var removeNames = p.RemoveNames;
        var devices = DeviceCount == 1 ? "1 device" : $"{DeviceCount} devices";
        return p.Mode switch
        {
            UsersMode.Add => $"Add user {name} as {UserRoles.Describe(p.Role, p.Ptz)} on {devices}.",
            UsersMode.Change when p.ChangePassword && p.ChangeRole => $"Set a new password and the role {UserRoles.Describe(p.Role, p.Ptz)} for user {name} on {devices}.",
            UsersMode.Change when p.ChangeRole => $"Set the role {UserRoles.Describe(p.Role, p.Ptz)} for user {name} on {devices}.",
            UsersMode.Change => $"Set a new password for user {name} on {devices}.",
            _ when removeNames.Count == 0 => "Select the users to remove in the Existing users list.",
            _ when removeNames.Count == 1 => $"Remove user {removeNames[0]} from {devices}.",
            _ => $"Remove users {string.Join(", ", removeNames)} from {devices}.",
        };
    }

    private static string Describe(IDeviceInfo device) =>
        string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} ({device.Address})";
}
