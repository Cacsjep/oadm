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

/// <summary>One user of the Users page.</summary>
public sealed class UserRowViewModel(UserInfo info, bool isSelf)
{
    public UserInfo Info { get; } = info;

    public string Id => Info.Id;

    public string UserName => Info.UserName;

    public bool IsAdmin => Info.Role == UserRole.Admin;

    public string RoleText => Info.RoleName.Length > 0 ? Info.RoleName : IsAdmin ? "Administrator" : "Operator";

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
        ServerConnection? connection = null, RolesViewModel? roles = null)
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
        Validation
            .Rule(nameof(NewUserName), () => NewUserName.Trim().Length == 0 ? "Enter a user name."
                : NewUserName.Trim().Length > 64 ? "The user name has at most 64 characters." : null)
            .Rule(nameof(NewPassword), () => PasswordError(NewPassword))
            .Rule(nameof(ResetPassword), () => ResetTarget is null ? null : PasswordError(ResetPassword))
            .Rule(nameof(ResetConfirm), () => ResetTarget is not null && ResetConfirm != ResetPassword ? "The passwords do not match." : null)
            .Rule(nameof(EditRole), () => EditTarget is not null && EditRole is null ? "Choose a role." : null);
        Validation.Validate();
    }


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
        UserForm.Edit => Validation.FirstErrorOf([nameof(EditRole)]),
        UserForm.Password => ResetBlockedReason,
        _ => null,
    };

    /// <summary>The user of Edit user; null when closed.</summary>
    [ObservableProperty]
    public partial UserRowViewModel? EditTarget { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditRoleHint))]
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

    public string? AddBlockedReason => Validation.FirstErrorOf([nameof(NewUserName), nameof(NewPassword)]);

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
        }
        catch (Exception ex)
        {
            LogFailed(_logger, "list users", ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
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
        Validation.Reset(nameof(NewUserName), nameof(NewPassword));
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
        Validation.Reset(nameof(EditRole));
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

    private bool CanSaveEdit() => EditTarget is not null && EditRole is not null;

    /// <summary>Gives the user of Edit user the chosen role (it applies to the user's next action).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        if (EditTarget is not { } row || EditRole is not { } role)
        {
            return;
        }

        if (role.Id == row.Info.RoleId)
        {
            CloseForm();
            return;
        }

        try
        {
            await _api.UpdateUserAsync(new UpdateUserRequest { Id = row.Id, RoleId = role.Id }, CancellationToken.None).ConfigureAwait(true);
            CloseForm();
            await LoadAsync().ConfigureAwait(true);
            await RolesCard.LoadAsync().ConfigureAwait(true); // user counts
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(nameof(EditRole), ex.Status.Detail);
        }
    }

    partial void OnEditRoleChanged(RoleOption? value) => Validation.Validate();

    private bool CanAddUser() => Validation.IsValidFor(nameof(NewUserName), nameof(NewPassword));

    [RelayCommand(CanExecute = nameof(CanAddUser))]
    private async Task AddUserAsync()
    {
        Validation.ShowAll(nameof(NewUserName), nameof(NewPassword));
        if (!CanAddUser())
        {
            return;
        }

        if (NewRole is null)
        {
            return;
        }

        try
        {
            await _api.AddUserAsync(NewUserName.Trim(), NewPassword, NewRole.Id, CancellationToken.None).ConfigureAwait(true);
            CloseForm();
            NewUserName = "";
            NewPassword = "";
            NewRole = DefaultRole();
            Validation.Reset(nameof(NewUserName), nameof(NewPassword));
            await LoadAsync().ConfigureAwait(true);
            await RolesCard.LoadAsync().ConfigureAwait(true); // user counts
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(ex.Status.Detail.Contains("password", StringComparison.OrdinalIgnoreCase) ? nameof(NewPassword) : nameof(NewUserName), ex.Status.Detail);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Users: {Action} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string action, string reason);
}
