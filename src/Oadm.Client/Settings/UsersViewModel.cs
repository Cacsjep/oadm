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

/// <summary>Choice of the role select.</summary>
public sealed record RoleOption(UserRole Role, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One user of the Users card.</summary>
public sealed partial class UserRowViewModel(UserInfo info, bool isSelf) : ObservableObject
{
    public UserInfo Info { get; } = info;

    public string Id => Info.Id;

    public string UserName => Info.UserName;

    public bool IsAdmin => Info.Role == UserRole.Admin;

    public string RoleText => IsAdmin ? "Administrator" : "Operator";

    public bool IsDisabled => Info.Disabled;

    public string StateText => IsDisabled ? "Disabled" : "Enabled";

    /// <summary>The logged-in user: role, state and deletion are not offered.</summary>
    public bool IsSelf { get; } = isSelf;

    public bool CanChange => !IsSelf;

    public string? SelfTooltip => IsSelf ? "Your own account: ask another administrator to change it." : null;

    public string LastLoginText => Info.LastLogin is null
        ? "Never"
        : Info.LastLogin.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string RoleActionText => IsAdmin ? "Make operator" : "Make administrator";

    public string StateActionText => IsDisabled ? "Enable" : "Disable";
}

/// <summary>
/// Settings page card Users (Admin only): list (user, role, last login, state), add, change role, reset password,
/// disable or enable, delete. The server refuses changes of the own account and of the last enabled administrator;
/// its message is shown in the shared message window. Field errors appear below the fields.
/// </summary>
public sealed partial class UsersViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly UserSession _session;
    private readonly ILogger<UsersViewModel> _logger;

    public UsersViewModel(IOadmApi api, IDialogService dialogs, UserSession session, ILogger<UsersViewModel> logger)
    {
        _api = api;
        _dialogs = dialogs;
        _session = session;
        _logger = logger;
        NewRole = Roles[0];
        Validation
            .Rule(nameof(NewUserName), () => NewUserName.Trim().Length == 0 ? "Enter a user name."
                : NewUserName.Trim().Length > 64 ? "The user name has at most 64 characters." : null)
            .Rule(nameof(NewPassword), () => PasswordError(NewPassword))
            .Rule(nameof(ResetPassword), () => ResetTarget is null ? null : PasswordError(ResetPassword))
            .Rule(nameof(ResetConfirm), () => ResetTarget is not null && ResetConfirm != ResetPassword ? "The passwords do not match." : null);
        Validation.Validate();
    }


    public static IReadOnlyList<RoleOption> Roles { get; } =
    [
        new(UserRole.Operator, "Operator"),
        new(UserRole.Admin, "Administrator"),
    ];

    public RangeObservableCollection<UserRowViewModel> Users { get; } = [];

    [ObservableProperty]
    public partial string NewUserName { get; set; } = "";

    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    public partial RoleOption NewRole { get; set; }

    /// <summary>The user whose password is being reset (inline editor below the list); null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResetOpen), nameof(ResetTitle))]
    public partial UserRowViewModel? ResetTarget { get; private set; }

    [ObservableProperty]
    public partial string ResetPassword { get; set; } = "";

    [ObservableProperty]
    public partial string ResetConfirm { get; set; } = "";

    public bool IsResetOpen => ResetTarget is not null;

    public string ResetTitle => ResetTarget is null ? "" : $"New password for {ResetTarget.UserName}";

    /// <summary>"5 users, 2 administrators, 1 disabled".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public string? AddBlockedReason => Validation.FirstErrorOf([nameof(NewUserName), nameof(NewPassword)]);

    public string? ResetBlockedReason => Validation.FirstErrorOf([nameof(ResetPassword), nameof(ResetConfirm)]);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(AddBlockedReason));
        OnPropertyChanged(nameof(ResetBlockedReason));
        AddUserCommand.NotifyCanExecuteChanged();
        SaveResetCommand.NotifyCanExecuteChanged();
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
            IReadOnlyList<UserInfo> users = await _api.ListUsersAsync(CancellationToken.None).ConfigureAwait(true);
            Users.ReplaceAll(users.Select(u => new UserRowViewModel(u, string.Equals(u.UserName, _session.UserName, StringComparison.OrdinalIgnoreCase))).ToList());
            UpdateSummary();
        }
        catch (RpcException ex)
        {
            LogFailed(_logger, "list users", ex.Status.Detail);
        }
    }

    private bool CanAddUser() => Validation.IsValidFor(nameof(NewUserName), nameof(NewPassword));

    [RelayCommand(CanExecute = nameof(CanAddUser))]
    private async Task AddUserAsync()
    {
        Validation.ShowAll(nameof(NewUserName), nameof(NewPassword));
        if (!CanAddUser())
        {
            return;
        }

        try
        {
            await _api.AddUserAsync(NewUserName.Trim(), NewPassword, NewRole.Role, CancellationToken.None).ConfigureAwait(true);
            NewUserName = "";
            NewPassword = "";
            NewRole = Roles[0];
            Validation.Reset(nameof(NewUserName), nameof(NewPassword));
            await LoadAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(ex.Status.Detail.Contains("password", StringComparison.OrdinalIgnoreCase) ? nameof(NewPassword) : nameof(NewUserName), ex.Status.Detail);
        }
    }

    [RelayCommand]
    private async Task ToggleRoleAsync(UserRowViewModel? row)
    {
        if (row is null || row.IsSelf)
        {
            return;
        }

        UserRole role = row.IsAdmin ? UserRole.Operator : UserRole.Admin;
        string text = role == UserRole.Admin
            ? $"{row.UserName} gets every right: settings, users, credentials and server services. Make {row.UserName} an administrator?"
            : $"{row.UserName} can then work with devices and tasks, but no longer change server settings, users or credentials. Make {row.UserName} an operator?";
        if (!await _dialogs.ConfirmAsync("Change role", text, role == UserRole.Admin ? "Make administrator" : "Make operator").ConfigureAwait(true))
        {
            return;
        }

        await UpdateAsync(new UpdateUserRequest { Id = row.Id, Role = role }, "Change role").ConfigureAwait(true);
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
            if (ResetTarget == row)
            {
                CancelReset();
            }

            await LoadAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            await _dialogs.ShowMessageAsync("Delete user", ex.Status.Detail).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenReset(UserRowViewModel? row)
    {
        ResetTarget = row;
        ResetPassword = "";
        ResetConfirm = "";
        Validation.Reset(nameof(ResetPassword), nameof(ResetConfirm));
    }

    [RelayCommand]
    private void CancelReset() => OpenReset(null);

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
            CancelReset();
            await LoadAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Validation.SetServerError(nameof(ResetPassword), ex.Status.Detail);
        }
    }

    private async Task UpdateAsync(UpdateUserRequest request, string title)
    {
        try
        {
            await _api.UpdateUserAsync(request, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            await _dialogs.ShowMessageAsync(title, ex.Status.Detail).ConfigureAwait(true);
        }
    }

    private void UpdateSummary()
    {
        int admins = Users.Count(u => u.IsAdmin);
        int disabled = Users.Count(u => u.IsDisabled);
        string text = Users.Count == 1 ? "1 user" : $"{Users.Count} users";
        text += admins == 1 ? ", 1 administrator" : $", {admins} administrators";
        if (disabled > 0)
        {
            text += $", {disabled} disabled";
        }

        Summary = text;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Users: {Action} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string action, string reason);
}
