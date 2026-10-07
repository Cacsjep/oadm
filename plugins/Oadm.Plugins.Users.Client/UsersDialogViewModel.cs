using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Users.Client;

/// <summary>A role choice in the dialog.</summary>
public sealed record RoleOption(UserRole Role, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One existing account of the first selected device.</summary>
public sealed record ExistingUserRow(DeviceUser User)
{
    public string Name => User.Name;

    public string RoleText => UserRoles.Describe(User.Role, User.Ptz) + (User.IsCurrentAccount ? " (used by OADM)" : string.Empty);
}

/// <summary>
/// View model of the "Users" dialog: Add, Change (password and/or role) or Remove one user on all
/// selected devices. Reads the existing users of the first device through the read-only plugin
/// query. Holds the password in memory only; <see cref="BuildPayload"/> returns the task payload.
/// </summary>
public sealed partial class UsersDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private string? _currentAccount;

    public UsersDialogViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        SelectedRole = Roles[2];
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdd), nameof(IsChange), nameof(IsRemove), nameof(ShowPassword), nameof(ShowRole), nameof(ApplyText))]
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
    private string _usersStatus = "Loading users...";

    [ObservableProperty]
    private string? _validationError;

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

    public bool ShowPassword => IsAdd || (IsChange && ChangePassword);

    public bool ShowRole => IsAdd || (IsChange && ChangeRole);

    public string ApplyText => Mode switch
    {
        UsersMode.Add => "Add user",
        UsersMode.Change => "Change user",
        _ => "Remove user",
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
        foreach (var user in result.Users)
        {
            ExistingUsers.Add(new ExistingUserRow(user));
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
        var payload = BuildPayload();
        if (payload is not null)
        {
            CloseRequested?.Invoke(this, payload);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    partial void OnModeChanged(UsersMode value) => Update();

    partial void OnUserNameChanged(string value) => Update();

    partial void OnPasswordChanged(string value) => Update();

    partial void OnConfirmPasswordChanged(string value) => Update();

    partial void OnSelectedRoleChanged(RoleOption value) => Update();

    partial void OnPtzChanged(bool value) => Update();

    partial void OnChangePasswordChanged(bool value) => Update();

    partial void OnChangeRoleChanged(bool value) => Update();

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
        UserName = UserName.Trim(),
        Password = ShowPassword ? Password : null,
        Role = SelectedRole.Role,
        Ptz = Ptz,
        ChangePassword = IsChange && ChangePassword,
        ChangeRole = IsChange && ChangeRole,
    };

    private string? ConfirmError() =>
        ShowPassword && !string.Equals(Password, ConfirmPassword, StringComparison.Ordinal) ? "The passwords do not match." : null;

    private void Update()
    {
        var payload = CreatePayload();
        string? error = null;
        try
        {
            UserChangePlanner.ValidatePayload(payload, Policy);
        }
        catch (UserManagementException ex)
        {
            error = ex.Message.Replace(" Nothing was changed.", string.Empty, StringComparison.Ordinal);
        }

        error ??= ConfirmError();
        ValidationError = UserName.Length == 0 && error is not null ? null : error;
        CanApply = error is null;
        LockOutWarning = BuildLockOutWarning(payload);
        Summary = BuildSummary(payload);
    }

    private string? BuildLockOutWarning(UsersPayload payload)
    {
        if (payload.Mode == UsersMode.Add)
        {
            return null;
        }

        if (_currentAccount is not null && string.Equals(payload.UserName, _currentAccount, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{_currentAccount}' is the account OADM uses for {Describe(_devices[0])}. OADM will refuse to remove it, demote it or change its password there.";
        }

        return "OADM never removes or demotes the account it uses for a device, nor the last administrator. Such devices fail with \"Nothing was changed\".";
    }

    private string BuildSummary(UsersPayload p)
    {
        var name = p.UserName.Length == 0 ? "<user name>" : $"'{p.UserName}'";
        var devices = DeviceCount == 1 ? "1 device" : $"{DeviceCount} devices";
        return p.Mode switch
        {
            UsersMode.Add => $"Add user {name} as {UserRoles.Describe(p.Role, p.Ptz)} on {devices}. Devices where the user already exists are skipped with a warning.",
            UsersMode.Change when p.ChangePassword && p.ChangeRole => $"Set a new password and the role {UserRoles.Describe(p.Role, p.Ptz)} for user {name} on {devices}. Devices without this user are skipped with a warning.",
            UsersMode.Change when p.ChangeRole => $"Set the role {UserRoles.Describe(p.Role, p.Ptz)} for user {name} on {devices}. Devices without this user are skipped with a warning.",
            UsersMode.Change => $"Set a new password for user {name} on {devices}. Devices without this user are skipped with a warning.",
            _ => $"Remove user {name} from {devices}. Devices without this user are skipped with a warning.",
        };
    }

    private static string Describe(IDeviceInfo device) =>
        string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} ({device.Address})";
}
