using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>Choice of a role select: a role by its id.</summary>
public sealed record RoleOption(string Id, string Title, RoleKind Kind)
{
    public override string ToString() => Title;
}

/// <summary>Choice of the Devices field: all devices (<see cref="GroupId"/> null) or a device group.</summary>
public sealed record DeviceAccessOption(string? GroupId, string Title)
{
    /// <summary>"All devices".</summary>
    public static DeviceAccessOption All { get; } = new(null, DeviceGroupWords.AllDevices);

    public bool IsAll => GroupId is null;

    public override string ToString() => Title;
}

/// <summary>One user of the Users page.</summary>
public sealed class UserRowViewModel(UserInfo info, bool isSelf)
{
    public UserInfo Info { get; } = info;

    public string Id => Info.Id;

    public string UserName => Info.UserName;

    public bool IsAdmin => Info.Role == UserRole.Admin;

    public string RoleText => Info.RoleName.Length > 0 ? Info.RoleName : IsAdmin ? "Administrator" : "Operator";

    /// <summary>The user may reach every device: an administrator, All devices, or a server before device groups.</summary>
    public bool HasAllDevices => IsAdmin || !Info.HasAllDevices || Info.AllDevices;

    /// <summary>"All devices", the group's name, or "No devices" (the group was deleted).</summary>
    public string DevicesText => HasAllDevices ? DeviceGroupWords.AllDevices
        : Info.DeviceGroupName.Length > 0 ? Info.DeviceGroupName
        : DeviceGroupWords.NoDevices;

    public bool IsDisabled => Info.Disabled;

    public string StateText => IsDisabled ? "Disabled" : "Enabled";

    /// <summary>The logged-in user: role, state and deletion are not offered.</summary>
    public bool IsSelf { get; } = isSelf;

    public bool CanChange => !IsSelf;

    public string? SelfTooltip => IsSelf ? "Your own account: ask another administrator to change it." : null;

    public string LastLoginText => Info.LastLogin is null
        ? "Never"
        : Info.LastLogin.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string StateActionText => IsDisabled ? "Enable" : "Disable";
}

/// <summary>The dialog of the Users card that is open.</summary>
public enum UserForm
{
    None,
    Add,
    Edit,
    Password,
}

/// <summary>
/// Navigation page Users (Admin only, loaded on every connect): the users card (user, role, last login, state; add, edit
/// the role, change the password, disable or enable, delete) and below it the roles card (<see cref="RolesCard"/>, the
/// permission matrix). Add user, Edit user and Change password are dialogs (<see cref="Form"/>, one window for the three);
/// the server refuses changes of the own account and of the last enabled administrator. Field errors and the server's
/// refusals appear below the fields; refusals of row actions in the shared message window.
/// </summary>
public sealed partial class UsersViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly UserSession _session;
    private readonly ILogger<UsersViewModel> _logger;

    public UsersViewModel(IOadmApi api, IDialogService dialogs, UserSession session, ILogger<UsersViewModel> logger,
        ServerConnection? connection = null, RolesViewModel? roles = null, DeviceGroupsViewModel? groups = null)
    {
        if (connection is not null)
        {
            connection.Connected += (_, _) => _ = LoadAsync();
        }

        _api = api;
        _dialogs = dialogs;
        _session = session;
        _logger = logger;
        RolesCard = roles ?? new RolesViewModel(api, dialogs, session);
        RolesCard.Loaded += (_, _) => OnRolesLoaded();
        DeviceGroups = groups ?? new DeviceGroupsViewModel(api, dialogs, session);
        DeviceGroups.Changed += (_, _) => OnGroupsChanged();
        DeviceGroups.UsersAffected += (_, _) => _ = ReloadUsersAsync();
        Validation
            .Rule(nameof(NewUserName), () => NewUserName.Trim().Length == 0 ? "Enter a user name."
                : NewUserName.Trim().Length > 64 ? "The user name has at most 64 characters." : null)
            .Rule(nameof(NewPassword), () => PasswordError(NewPassword))
            .Rule(nameof(ResetPassword), () => ResetTarget is null ? null : PasswordError(ResetPassword))
            .Rule(nameof(ResetConfirm), () => ResetTarget is not null && ResetConfirm != ResetPassword ? "The passwords do not match." : null)
            .Rule(nameof(EditRole), () => EditTarget is not null && EditRole is null ? "Choose a role." : null)
            .Rule(nameof(NewDevices), () => Form == UserForm.Add && NewRole?.Kind != RoleKind.Administrator && NewDevices is null ? DeviceGroupWords.ChooseDevices : null)
            .Rule(nameof(EditDevices), () => EditTarget is not null && EditRole?.Kind != RoleKind.Administrator && EditDevices is null ? DeviceGroupWords.ChooseDevices : null);
        Validation.Validate();
    }

    /// <summary>The device groups card below the users.</summary>
    public DeviceGroupsViewModel DeviceGroups { get; }

    /// <summary>The choices of the Devices field: All devices, then the groups by name.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DeviceAccessOption> DeviceOptions { get; private set; } = [DeviceAccessOption.All];

    /// <summary>Add user: the devices of the new user.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewDevicesHint))]
    public partial DeviceAccessOption? NewDevices { get; set; } = DeviceAccessOption.All;

    /// <summary>Edit user: the devices of the user; null when the user has no devices (the group was deleted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditDevicesHint))]
    public partial DeviceAccessOption? EditDevices { get; set; }

    /// <summary>The Devices field of Add user can be changed (not for an administrator).</summary>
    public bool IsNewDevicesEnabled => NewRole?.Kind != RoleKind.Administrator;

    /// <summary>The Devices field of Edit user can be changed (not for an administrator).</summary>
    public bool IsEditDevicesEnabled => EditRole?.Kind != RoleKind.Administrator;

    /// <summary>Below the Devices field of Add user: administrators have all devices; a group keeps server-wide pages read only.</summary>
    public string? NewDevicesHint => DevicesHint(NewRole, NewDevices);

    /// <summary>Below the Devices field of Edit user.</summary>
    public string? EditDevicesHint => DevicesHint(EditRole, EditDevices);

    private static string? DevicesHint(RoleOption? role, DeviceAccessOption? devices) =>
        role?.Kind == RoleKind.Administrator ? DeviceGroupWords.AdministratorsHaveAll
        : devices is { IsAll: false } ? DeviceGroupWords.ServerWideNote
        : null;


    /// <summary>The roles card below the users (the permission matrix).</summary>
    public RolesViewModel RolesCard { get; }

    /// <summary>The saved roles, for the role selects (Add user and every row).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RoleOption> Roles { get; private set; } = [];

    public RangeObservableCollection<UserRowViewModel> Users { get; } = [];

    [ObservableProperty]
    public partial string NewUserName { get; set; } = "";

    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewDevicesEnabled), nameof(NewDevicesHint))]
    public partial RoleOption? NewRole { get; set; }

    /// <summary>The dialog that is open; the window closes when it is <see cref="UserForm.None"/> again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(IsAddForm), nameof(IsEditForm), nameof(IsPasswordForm), nameof(FormButton), nameof(FormBlockedReason))]
    public partial UserForm Form { get; private set; }

    public bool IsAddForm => Form == UserForm.Add;

    public bool IsEditForm => Form == UserForm.Edit;

    public bool IsPasswordForm => Form == UserForm.Password;

    public string FormTitle => Form switch
    {
        UserForm.Add => "Add user",
        UserForm.Edit => $"Edit {EditTarget?.UserName}",
        UserForm.Password => ResetTitle,
        _ => "",
    };

    public string FormButton => Form switch
    {
        UserForm.Add => "Add user",
        UserForm.Password => "Change password",
        _ => "Save",
    };

    public string? FormBlockedReason => Form switch
    {
        UserForm.Add => AddBlockedReason,
        UserForm.Edit => Validation.FirstErrorOf([nameof(EditRole), nameof(EditDevices)]),
        UserForm.Password => ResetBlockedReason,
        _ => null,
    };

    /// <summary>The user of Edit user; null when closed.</summary>
    [ObservableProperty]
    public partial UserRowViewModel? EditTarget { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditRoleHint), nameof(IsEditDevicesEnabled), nameof(EditDevicesHint))]
    public partial RoleOption? EditRole { get; set; }

    /// <summary>What Administrator means (the other roles are explained in the matrix below).</summary>
    public string? EditRoleHint => EditRole?.Kind == RoleKind.Administrator ? "Every permission, also users, roles and server settings." : null;

    /// <summary>The user whose password is being changed (Change password); null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResetOpen), nameof(ResetTitle))]
    public partial UserRowViewModel? ResetTarget { get; private set; }

    [ObservableProperty]
    public partial string ResetPassword { get; set; } = "";

    [ObservableProperty]
    public partial string ResetConfirm { get; set; } = "";

    public bool IsResetOpen => ResetTarget is not null;

    public string ResetTitle => ResetTarget is null ? "" : $"New password for {ResetTarget.UserName}";

    public string? AddBlockedReason => Validation.FirstErrorOf([nameof(NewUserName), nameof(NewPassword), nameof(NewDevices)]);

    public string? ResetBlockedReason => Validation.FirstErrorOf([nameof(ResetPassword), nameof(ResetConfirm)]);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(AddBlockedReason));
        OnPropertyChanged(nameof(ResetBlockedReason));
        OnPropertyChanged(nameof(FormBlockedReason));
        AddUserCommand.NotifyCanExecuteChanged();
        SaveResetCommand.NotifyCanExecuteChanged();
        SaveEditCommand.NotifyCanExecuteChanged();
        SaveFormCommand.NotifyCanExecuteChanged();
    }

    private static string? PasswordError(string password) =>
        password.Length == 0 ? "Enter a password." : null;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_session.IsAdmin)
        {
            Users.Clear();
            return;
        }

        try
        {
            if (Roles.Count == 0)
            {
                await RolesCard.LoadAsync().ConfigureAwait(true);
            }

            IReadOnlyList<UserInfo> users = await _api.ListUsersAsync(CancellationToken.None).ConfigureAwait(true);
            ShowUsers(users);
            await DeviceGroups.LoadAsync().ConfigureAwait(true); // the users of each group
        }
        catch (Exception ex)
        {
            LogFailed(_logger, "list users", ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
    }

    /// <summary>The users again (a group was saved or deleted), without the groups.</summary>
    private async Task ReloadUsersAsync()
    {
        try
        {
            ShowUsers(await _api.ListUsersAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (RpcException ex)
        {
            LogFailed(_logger, "list users", ex.Status.Detail);
        }
    }

    /// <summary>"+ User": the Add user dialog.</summary>
    [RelayCommand]
    private void OpenAdd()
    {
        CloseForm();
        NewUserName = "";
        NewPassword = "";
        NewRole = DefaultRole();
        NewDevices = DeviceAccessOption.All;
        Validation.Reset(nameof(NewUserName), nameof(NewPassword), nameof(NewDevices));
        ShowForm(UserForm.Add);
    }

    /// <summary>The row's Edit: the Edit user dialog (the role).</summary>
    [RelayCommand]
    private void OpenEdit(UserRowViewModel? row)
    {
        CloseForm();
        if (row is null || row.IsSelf)
        {
            return;
        }

        EditTarget = row;
        EditRole = Roles.FirstOrDefault(r => r.Id == row.Info.RoleId);
        EditDevices = CurrentDevices(row);
        Validation.Reset(nameof(EditRole), nameof(EditDevices));
        ShowForm(UserForm.Edit);
    }

    /// <summary>The dialog's main button.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveForm))]
    private Task SaveFormAsync() => Form switch
    {
        UserForm.Add => AddUserAsync(),
        UserForm.Edit => SaveEditAsync(),
        UserForm.Password => SaveResetAsync(),
        _ => Task.CompletedTask,
    };

    private bool CanSaveForm() => Form switch
    {
        UserForm.Add => CanAddUser(),
        UserForm.Edit => CanSaveEdit(),
        UserForm.Password => CanSaveReset(),
        _ => false,
    };

    /// <summary>Cancel, the X or Escape of the dialog.</summary>
    [RelayCommand]
    public void CloseForm()
    {
        ResetTarget = null;
        EditTarget = null;
        Form = UserForm.None;
        Validation.Validate();
    }

    private void ShowForm(UserForm form)
    {
        Form = form;
        Validation.Validate();
        _ = _dialogs.ShowUserFormAsync(this);
    }

    private bool CanSaveEdit() => EditTarget is not null && EditRole is not null && Validation.IsValidFor(nameof(EditDevices));

    /// <summary>
    /// Gives the user of Edit user the chosen role and devices (they apply to the user's next action; the user's open
    /// lists reconnect with the new devices).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        Validation.ShowAll(nameof(EditRole), nameof(EditDevices));
        if (EditTarget is not { } row || EditRole is not { } role || !CanSaveEdit())
        {
            return;
        }

        var request = new UpdateUserRequest { Id = row.Id };
        bool roleChanges = role.Id != row.Info.RoleId;
        if (roleChanges)
        {
            request.RoleId = role.Id;
        }

        // Any role change below Administrator sends the Devices choice the form shows, so a group stored on an administrator
        // never applies unseen.
        bool devicesChange = role.Kind != RoleKind.Administrator && EditDevices is { } devices && (roleChanges || !Equals(devices, CurrentDevices(row)));
        if (devicesChange)
        {
            SetDevices(request, EditDevices!);
        }

        if (!roleChanges && !devicesChange)
        {
            CloseForm();
            return;
        }

        try
        {
            await _api.UpdateUserAsync(request, CancellationToken.None).ConfigureAwait(true);
            CloseForm();
            await LoadAsync().ConfigureAwait(true);
            await RolesCard.LoadAsync().ConfigureAwait(true); // user counts
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(devicesChange && !roleChanges ? nameof(EditDevices) : nameof(EditRole), ex.Status.Detail);
        }
    }

    partial void OnEditRoleChanged(RoleOption? value) => Validation.Validate();

    partial void OnEditDevicesChanged(DeviceAccessOption? value) => Validation.Validate();

    partial void OnNewDevicesChanged(DeviceAccessOption? value) => Validation.Validate();

    partial void OnNewRoleChanged(RoleOption? value) => Validation.Validate();

    /// <summary>
    /// The Devices choice of a user as stored: All devices, the group, or null (no devices). Also for an administrator (who
    /// has all devices anyway): the form shows the group stored on the account, which applies once the role changes.
    /// </summary>
    private DeviceAccessOption? CurrentDevices(UserRowViewModel row)
    {
        var info = row.Info;
        if (!info.HasAllDevices)
        {
            return DeviceAccessOption.All; // a server before device groups
        }

        if (info.DeviceGroupId.Length > 0)
        {
            return DeviceOptions.FirstOrDefault(o => o.GroupId == info.DeviceGroupId);
        }

        return info.AllDevices ? DeviceAccessOption.All : null;
    }

    private static void SetDevices(UpdateUserRequest request, DeviceAccessOption devices)
    {
        request.AllDevices = devices.IsAll;
        if (!devices.IsAll)
        {
            request.DeviceGroupId = devices.GroupId!;
        }
    }

    private bool CanAddUser() => Validation.IsValidFor(nameof(NewUserName), nameof(NewPassword), nameof(NewDevices));

    [RelayCommand(CanExecute = nameof(CanAddUser))]
    private async Task AddUserAsync()
    {
        Validation.ShowAll(nameof(NewUserName), nameof(NewPassword), nameof(NewDevices));
        if (!CanAddUser())
        {
            return;
        }

        // No fallback to All devices: a choice that vanished (its group was deleted) blocks until a new one is made.
        DeviceAccessOption? devices = NewRole?.Kind == RoleKind.Administrator ? DeviceAccessOption.All : NewDevices;
        if (NewRole is null || devices is null)
        {
            return;
        }

        try
        {
            var request = new AddUserRequest { UserName = NewUserName.Trim(), Password = NewPassword, RoleId = NewRole.Id };
            request.AllDevices = devices.IsAll;
            if (!devices.IsAll)
            {
                request.DeviceGroupId = devices.GroupId!;
            }

            await _api.AddUserAsync(request, CancellationToken.None).ConfigureAwait(true);
            CloseForm();
            NewUserName = "";
            NewPassword = "";
            NewRole = DefaultRole();
            NewDevices = DeviceAccessOption.All;
            Validation.Reset(nameof(NewUserName), nameof(NewPassword), nameof(NewDevices));
            await LoadAsync().ConfigureAwait(true);
            await RolesCard.LoadAsync().ConfigureAwait(true); // user counts
        }
        catch (RpcException ex)
        {
            string detail = ex.Status.Detail;
            Validation.SetServerError(
                detail.Contains("password", StringComparison.OrdinalIgnoreCase) ? nameof(NewPassword)
                : detail.Contains("device group", StringComparison.OrdinalIgnoreCase) || detail.Contains("All devices", StringComparison.Ordinal) ? nameof(NewDevices)
                : nameof(NewUserName),
                detail);
        }
    }

    [RelayCommand]
    private async Task ToggleDisabledAsync(UserRowViewModel? row)
    {
        if (row is null || row.IsSelf)
        {
            return;
        }

        if (!row.IsDisabled
            && !await _dialogs.ConfirmAsync("Disable user", $"{row.UserName} is logged out and cannot log in until enabled again. Disable {row.UserName}?", "Disable").ConfigureAwait(true))
        {
            return;
        }

        await UpdateAsync(new UpdateUserRequest { Id = row.Id, Disabled = !row.IsDisabled }, row.IsDisabled ? "Enable user" : "Disable user").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(UserRowViewModel? row)
    {
        if (row is null || row.IsSelf
            || !await _dialogs.ConfirmAsync("Delete user", $"{row.UserName} is logged out and removed. Tasks keep their owner name. Delete {row.UserName}?", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.DeleteUserAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            if (ResetTarget == row || EditTarget == row)
            {
                CloseForm();
            }

            await LoadAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            await _dialogs.ShowMessageAsync("Delete user", ex.Status.Detail).ConfigureAwait(true);
        }
    }

    /// <summary>The row's Change password: the dialog with the new password.</summary>
    [RelayCommand]
    private void OpenReset(UserRowViewModel? row)
    {
        CloseForm();
        ResetPassword = "";
        ResetConfirm = "";
        Validation.Reset(nameof(ResetPassword), nameof(ResetConfirm));
        if (row is null)
        {
            return;
        }

        ResetTarget = row;
        ShowForm(UserForm.Password);
    }

    private bool CanSaveReset() => ResetTarget is not null && Validation.IsValidFor(nameof(ResetPassword), nameof(ResetConfirm));

    [RelayCommand(CanExecute = nameof(CanSaveReset))]
    private async Task SaveResetAsync()
    {
        Validation.ShowAll(nameof(ResetPassword), nameof(ResetConfirm));
        if (ResetTarget is not { } row || !CanSaveReset())
        {
            return;
        }

        try
        {
            await _api.UpdateUserAsync(new UpdateUserRequest { Id = row.Id, NewPassword = ResetPassword }, CancellationToken.None).ConfigureAwait(true);
            CloseForm();
            await LoadAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(nameof(ResetPassword), ex.Status.Detail);
        }
    }

    private async Task<bool> UpdateAsync(UpdateUserRequest request, string title)
    {
        try
        {
            await _api.UpdateUserAsync(request, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            return true;
        }
        catch (RpcException ex)
        {
            await _dialogs.ShowMessageAsync(title, ex.Status.Detail).ConfigureAwait(true);
            return false;
        }
    }

    private void ShowUsers(IReadOnlyList<UserInfo> users)
    {
        Users.ReplaceAll(users.Select(u => new UserRowViewModel(u, string.Equals(u.UserName, _session.UserName, StringComparison.OrdinalIgnoreCase))).ToList());
    }

    /// <summary>The saved roles became the role choices; the rows get them too.</summary>
    private void OnRolesLoaded()
    {
        Roles = [.. RolesCard.Columns.Where(c => !c.IsNew).Select(c => new RoleOption(c.Id!, c.Name, c.Kind))];
        NewRole = Roles.FirstOrDefault(r => r.Id == NewRole?.Id) ?? DefaultRole();
        ShowUsers([.. Users.Select(u => u.Info)]);
    }

    private RoleOption? DefaultRole() => Roles.FirstOrDefault(r => r.Kind == RoleKind.Operator) ?? (Roles.Count > 0 ? Roles[0] : null);

    /// <summary>The groups were loaded or changed: the Devices choices follow, the rows show the groups' new names.</summary>
    private void OnGroupsChanged()
    {
        DeviceOptions = [DeviceAccessOption.All, .. DeviceGroups.Groups.Select(g => new DeviceAccessOption(g.Id, g.Name))];
        // A chosen group that was deleted meanwhile leaves no choice (never All devices); the field says so below it.
        if (NewDevices is { } chosen)
        {
            NewDevices = DeviceOptions.FirstOrDefault(o => o.GroupId == chosen.GroupId);
            if (NewDevices is null)
            {
                Validation.Touch(nameof(NewDevices));
            }
        }

        if (EditTarget is { } row)
        {
            bool hadChoice = EditDevices is not null;
            EditDevices = EditDevices is null ? CurrentDevices(row) : DeviceOptions.FirstOrDefault(o => o.GroupId == EditDevices.GroupId);
            if (hadChoice && EditDevices is null)
            {
                Validation.Touch(nameof(EditDevices));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Users: {Action} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string action, string reason);
}
