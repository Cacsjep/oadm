using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>A page a role can start on: the rail key (empty = Devices) and its name.</summary>
public sealed record StartPageChoice(string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One role of the matrix: a column. Holds the permissions as edited until Save.</summary>
public sealed partial class RoleColumnViewModel : ObservableObject
{
    private readonly HashSet<string> _saved;
    private string _savedName;
    private string _savedDescription;
    private readonly string _savedStartPage;

    public RoleColumnViewModel(RoleInfo role)
    {
        ArgumentNullException.ThrowIfNull(role);
        Id = string.IsNullOrEmpty(role.Id) ? null : role.Id;
        Kind = role.Kind;
        UserCount = role.UserCount;
        Version = role.Version;
        _savedName = role.Name;
        _savedDescription = role.Description;
        Name = role.Name;
        Description = role.Description;
        _savedStartPage = role.StartPage;
        StartPage = role.StartPage;
        _saved = new HashSet<string>(role.Permissions, StringComparer.Ordinal);
        Granted = new HashSet<string>(_saved, StringComparer.Ordinal);
    }

    /// <summary>Null for a role that is not saved yet.</summary>
    public string? Id { get; }

    public RoleKind Kind { get; }

    /// <summary>The stored version this column was loaded from (a save on an older one is refused).</summary>
    public long Version { get; }

    public int UserCount { get; private set; }

    /// <summary>A fresh count from the server (users added or moved); the edits of the column stay.</summary>
    internal void UpdateUserCount(int count)
    {
        if (count != UserCount)
        {
            UserCount = count;
            OnPropertyChanged(nameof(UserCount));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string Description { get; set; }

    /// <summary>The page users of the role see first after a login, as edited (empty = Devices).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string StartPage { get; set; }

    /// <summary>"Starts on Compliance"; set by the matrix, which knows the page names.</summary>
    [ObservableProperty]
    public partial string StartPageText { get; internal set; } = "";

    /// <summary>The start page changed and is not saved yet.</summary>
    public bool StartPageChanged => StartPage != _savedStartPage;

    /// <summary>The permission keys as edited.</summary>
    public HashSet<string> Granted { get; }

    public bool IsAdministrator => Kind == RoleKind.Administrator;

    public bool IsEditable => !IsAdministrator;

    public bool IsNew => Id is null;

    /// <summary>Operator and Viewer go back to their defaults.</summary>
    public bool CanReset => Kind is RoleKind.Operator or RoleKind.Viewer;

    /// <summary>A saved custom role without users (and nothing unsaved).</summary>
    public bool CanDelete => Kind == RoleKind.Custom;

    /// <summary>Name and description can change (not for the Administrator role); the start page can for every role.</summary>
    public bool CanRename => IsEditable;

    /// <summary>"3 users", "1 user", "No users", "Not saved", "Everything".</summary>
    public string Subtitle => IsAdministrator ? "Everything"
        : IsNew ? "Not saved"
        : UserCount switch
        {
            0 => "No users",
            1 => "1 user",
            _ => $"{UserCount:N0} users",
        };

    public string? DescriptionTip => string.IsNullOrWhiteSpace(Description) ? null : Description;

    public bool IsDirty => IsNew || Name != _savedName || Description != _savedDescription || StartPageChanged || !Granted.SetEquals(_saved);

    public bool Has(string key) => IsAdministrator || Granted.Contains(key);

    /// <summary>Gives or takes one permission; false when nothing changed.</summary>
    public bool Set(string key, bool on)
    {
        bool changed = on ? Granted.Add(key) : Granted.Remove(key);
        if (changed)
        {
            OnPropertyChanged(nameof(IsDirty));
        }

        return changed;
    }

    /// <summary>"+Web View: Open devices, -Restart" for the confirmation.</summary>
    public IReadOnlyList<string> Changes(Func<string, string> nameOf)
    {
        var added = Granted.Except(_saved).Select(k => "+" + nameOf(k));
        var removed = _saved.Except(Granted).Select(k => "-" + nameOf(k));
        return [.. added.Order(StringComparer.CurrentCulture), .. removed.Order(StringComparer.CurrentCulture)];
    }

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(DescriptionTip));

    public RoleChange ToChange()
    {
        var change = new RoleChange { Id = Id ?? "", Name = Name.Trim(), Description = Description.Trim(), Permissions = { Granted.Order(StringComparer.Ordinal) }, Version = Version };
        if (StartPageChanged || (IsNew && StartPage.Length > 0))
        {
            change.StartPage = StartPage;
        }

        return change;
    }
}

/// <summary>One check box of the matrix: a permission (or a whole group) for one role.</summary>
public sealed partial class PermissionCellViewModel(RolesViewModel owner, PermissionRowViewModel row, RoleColumnViewModel role) : ObservableObject
{
    public PermissionRowViewModel Row { get; } = row;

    public RoleColumnViewModel Role { get; } = role;

    /// <summary>The Administrator column shows every box checked and cannot change.</summary>
    public bool IsEnabled => Role.IsEditable;

    /// <summary>A permission: on or off. A group: on (all), off (none) or null (some).</summary>
    public bool? IsChecked
    {
        get => Row.IsGroup ? RolesViewModel.GroupState(Row, Role) : Role.Has(Row.Key);
        set
        {
            if (!Role.IsEditable)
            {
                return;
            }

            // A click on a group with some or none makes all; on all makes none.
            bool on = Row.IsGroup ? RolesViewModel.GroupState(Row, Role) != true : value == true;
            owner.Set(Row, Role, on);
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>A row of the matrix: a group header (Devices, Tasks, a plugin) or a permission.</summary>
public sealed partial class PermissionRowViewModel : ObservableObject
{
    public PermissionRowViewModel(string group, string? iconKey)
    {
        IsGroup = true;
        Group = group;
        Title = group;
        IconKey = iconKey;
    }

    public PermissionRowViewModel(PermissionInfo permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        Permission = permission;
        Key = permission.Key;
        Group = permission.Group;
        Title = permission.Name;
        IconKey = permission.IconKey;
    }

    public bool IsGroup { get; }

    public bool IsPermission => !IsGroup;

    public PermissionInfo? Permission { get; }

    public string Key { get; } = "";

    public string Group { get; }

    public string Title { get; }

    public string? IconKey { get; }

    public string? Description => Permission?.Description;

    /// <summary>Acts with the stored device login: the row warns.</summary>
    public bool FullDeviceControl => Permission?.FullDeviceControl == true;

    /// <summary>The plugin is turned off: the right stays in the roles but does nothing now.</summary>
    public bool IsUnavailable => Permission is { Available: false };

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>One cell per role, in column order.</summary>
    public List<PermissionCellViewModel> Cells { get; } = [];

    /// <summary>The permission rows of a group row.</summary>
    public List<PermissionRowViewModel> Children { get; } = [];
}

/// <summary>
/// The Roles card of the Users page (Administrator only): the permission matrix. Rows are the permissions, grouped like
/// the client (Devices, Tasks, Event rules, then one group per plugin), columns the roles (Administrator, Operator,
/// Viewer, custom roles). Changes are collected and saved together after one confirmation; a new role, a copy and a
/// rename are pending until Save as well. The grid's columns are built by the view from <see cref="Columns"/>.
/// </summary>
public sealed partial class RolesViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly UserSession _session;
    private readonly List<PermissionRowViewModel> _allRows = [];
    private readonly Dictionary<string, PermissionInfo> _byKey = new(StringComparer.Ordinal);
    private IReadOnlyList<CorePluginInfo> _pluginPages = [];

    public RolesViewModel(IOadmApi api, IDialogService dialogs, UserSession session, ServerConnection? connection = null)
    {
        _api = api;
        _dialogs = dialogs;
        _session = session;
        if (connection is not null)
        {
            connection.Connected += (_, _) => _ = LoadAsync();
        }

        Validation
            .Rule(nameof(EditName), () => !IsEditorOpen ? null
                : EditName.Trim().Length == 0 ? "Enter a role name."
                : EditName.Trim().Length > 64 ? "The role name has at most 64 characters."
                : Columns.Any(c => c != EditTarget && string.Equals(c.Name.Trim(), EditName.Trim(), StringComparison.OrdinalIgnoreCase)) ? $"A role named {EditName.Trim()} exists already."
                : null)
            .Rule(nameof(EditDescription), () => EditDescription.Trim().Length > 200 ? "The description has at most 200 characters." : null);
        Validation.Validate();
    }

    /// <summary>The roles in column order; the view rebuilds its columns on <see cref="ColumnsChanged"/>.</summary>
    public ObservableCollection<RoleColumnViewModel> Columns { get; } = [];

    /// <summary>The visible rows (search, collapsed groups), one reset per change.</summary>
    public RangeObservableCollection<PermissionRowViewModel> Rows { get; } = [];

    /// <summary>Raised after the columns changed (load, new role, copy, delete): the view builds its grid columns again.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Raised after the saved roles were read from the server (the Users card's role choices follow them).</summary>
    public event EventHandler? Loaded;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>A failed load or save, below the matrix.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Roles · 4 · unsaved changes".</summary>
    public string Summary => Columns.Count == 1 ? "1 role" : $"{Columns.Count} roles";

    public string? SaveBlockedReason => IsDirty ? null : "Nothing changed.";

    // The role dialog (RoleFormWindow): a new role, a copy or a rename.

    /// <summary>The role being renamed; null for a new role.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorButton), nameof(IsNewEditor), nameof(IsNameEditable))]
    public partial RoleColumnViewModel? EditTarget { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(EditorButton), nameof(IsNewEditor))]
    public partial bool IsEditorOpen { get; private set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    [ObservableProperty]
    public partial string EditDescription { get; set; } = "";

    /// <summary>The start page chosen in the role dialog.</summary>
    [ObservableProperty]
    public partial StartPageChoice? EditStartPage { get; set; }

    /// <summary>Devices, Compliance, Event rules, the plugin pages that are on, Logs (and a stored page that is off now).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<StartPageChoice> StartPageChoices { get; private set; } = [new("", "Devices")];

    /// <summary>The Administrator role keeps its name: the dialog only changes its start page.</summary>
    public bool IsNameEditable => EditTarget?.IsEditable != false;

    /// <summary>A new role starts empty or from this role's permissions.</summary>
    [ObservableProperty]
    public partial RoleColumnViewModel? StartFrom { get; set; }

    public bool IsNewEditor => IsEditorOpen && EditTarget is null;

    public string EditorTitle => EditTarget is null ? "New role" : $"Edit {EditTarget.Name}";

    public string EditorButton => EditTarget is null ? "Add role" : "Apply";

    /// <summary>"Empty" plus every role, for "Start from".</summary>
    public IReadOnlyList<RoleColumnViewModel?> StartChoices => [null, .. Columns];

    public string? EditorBlockedReason => Validation.FirstErrorOf([nameof(EditName), nameof(EditDescription)]);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(EditorBlockedReason));
        ApplyEditorCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(SaveBlockedReason));

    [RelayCommand]
    public Task LoadAsync() => LoadAsync(keepEdits: true);

    /// <summary>Reads the matrix; with <paramref name="keepEdits"/> unsaved changes stay and only the user counts change.</summary>
    public async Task LoadAsync(bool keepEdits)
    {
        if (!_session.IsAdmin)
        {
            Show(new RoleMatrix());
            return;
        }

        try
        {
            var matrix = await _api.GetRoleMatrixAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                // The plugin pages a role can start on; without them only the built-in pages are offered.
                _pluginPages = [.. (await _api.ListCorePluginsAsync(CancellationToken.None).ConfigureAwait(true)).Where(p => !p.NoPage)];
            }
            catch (RpcException)
            {
                _pluginPages = [];
            }

            if (IsDirty && keepEdits)
            {
                // Unsaved changes stay (a reconnect, a new user): only the user counts follow.
                var counts = matrix.Roles.ToDictionary(r => r.Id, r => r.UserCount, StringComparer.Ordinal);
                foreach (var column in Columns)
                {
                    if (column.Id is { } id && counts.TryGetValue(id, out var count))
                    {
                        column.UpdateUserCount(count);
                    }
                }
            }
            else
            {
                Show(matrix);
            }

            ErrorText = null;
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
    }

    /// <summary>The group state of one role: true (all), false (none), null (some).</summary>
    public static bool? GroupState(PermissionRowViewModel group, RoleColumnViewModel role)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(role);
        int on = group.Children.Count(r => role.Has(r.Key));
        return on == 0 ? false : on == group.Children.Count ? true : null;
    }

    /// <summary>Gives or takes a permission (or every permission of a group) for one role.</summary>
    public void Set(PermissionRowViewModel row, RoleColumnViewModel role, bool on)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(role);
        if (!role.IsEditable)
        {
            return;
        }

        var rows = row.IsGroup ? row.Children : [row];
        foreach (var r in rows)
        {
            if (role.Set(r.Key, on))
            {
                CellOf(r, role)?.Refresh();
            }
        }

        if (GroupRowOf(row) is { } group)
        {
            CellOf(group, role)?.Refresh();
        }

        UpdateDirty();
    }

    /// <summary>"New role": the role dialog.</summary>
    [RelayCommand]
    private void NewRole()
    {
        EditTarget = null;
        EditName = "";
        EditDescription = "";
        StartFrom = null;
        StartPageChoices = ChoicesWith("");
        EditStartPage = StartPageChoices[0];
        IsEditorOpen = true;
        Validation.Reset(nameof(EditName), nameof(EditDescription));
        Validation.Validate();
        _ = _dialogs.ShowRoleFormAsync(this);
    }

    [RelayCommand]
    private void CopyRole(RoleColumnViewModel? role)
    {
        if (role is null)
        {
            return;
        }

        NewRole();
        EditName = UniqueName("Copy of " + role.Name);
        StartFrom = role;
    }

    [RelayCommand]
    private void EditRole(RoleColumnViewModel? role)
    {
        if (role is null)
        {
            return;
        }

        EditTarget = role;
        EditName = role.Name;
        EditDescription = role.Description;
        StartPageChoices = ChoicesWith(role.StartPage);
        EditStartPage = StartPageChoices.FirstOrDefault(c => c.Key == role.StartPage) ?? StartPageChoices[0];
        IsEditorOpen = true;
        Validation.Reset(nameof(EditName), nameof(EditDescription));
        Validation.Validate();
        _ = _dialogs.ShowRoleFormAsync(this);
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditorOpen = false;
        EditTarget = null;
        Validation.Validate();
    }

    private bool CanApplyEditor() => IsEditorOpen && Validation.IsValidFor(nameof(EditName), nameof(EditDescription));

    /// <summary>Adds the new role as a column (not saved yet) or renames the role.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyEditor))]
    private void ApplyEditor()
    {
        Validation.ShowAll(nameof(EditName), nameof(EditDescription));
        if (!CanApplyEditor())
        {
            return;
        }

        string startPage = EditStartPage?.Key ?? "";
        if (EditTarget is { } target)
        {
            if (target.IsEditable)
            {
                target.Name = EditName.Trim();
                target.Description = EditDescription.Trim();
            }

            target.StartPage = startPage;
            target.StartPageText = StartPageTextOf(startPage);
        }
        else
        {
            var role = new RoleColumnViewModel(new RoleInfo { Name = EditName.Trim(), Description = EditDescription.Trim(), Kind = RoleKind.Custom });
            role.StartPage = startPage;
            role.StartPageText = StartPageTextOf(startPage);
            if (StartFrom is { } from)
            {
                foreach (var key in _byKey.Keys.Where(from.Has))
                {
                    role.Set(key, true);
                }
            }

            Columns.Add(role);
            BuildCells();
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }

        CancelEditor();
        UpdateDirty();
    }

    /// <summary>Operator and Viewer: back to their default permissions (pending until Save).</summary>
    [RelayCommand]
    private void ResetRole(RoleColumnViewModel? role)
    {
        if (role is not { CanReset: true })
        {
            return;
        }

        foreach (var info in _byKey.Values)
        {
            bool on = role.Kind == RoleKind.Operator ? info.OperatorDefault : info.ViewerDefault;
            role.Set(info.Key, on);
        }

        RefreshCells();
        UpdateDirty();
    }

    /// <summary>A custom role: an unsaved one leaves at once, a saved one is deleted on the server after a confirmation.</summary>
    [RelayCommand]
    private async Task DeleteRoleAsync(RoleColumnViewModel? role)
    {
        if (role is not { CanDelete: true })
        {
            return;
        }

        if (role.IsNew)
        {
            Columns.Remove(role);
            BuildCells();
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            UpdateDirty();
            return;
        }

        if (role.UserCount > 0)
        {
            await _dialogs.ShowMessageAsync("Delete role", role.UserCount == 1
                ? $"1 user has the role {role.Name}. Give that user another role first."
                : $"{role.UserCount:N0} users have the role {role.Name}. Give them another role first.").ConfigureAwait(true);
            return;
        }

        if (IsDirty)
        {
            await _dialogs.ShowMessageAsync("Delete role", "Save or discard your changes first.").ConfigureAwait(true);
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete role", $"Delete the role {role.Name}?", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            Show(await _api.DeleteRoleAsync(role.Id!, CancellationToken.None).ConfigureAwait(true));
            ErrorText = null;
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
    }

    private bool CanSave() => IsDirty && !IsBusy;

    /// <summary>One confirmation that lists what changes per role, then one save of every changed role.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var changed = Columns.Where(c => c.IsDirty).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var lines = changed.Select(c =>
        {
            var parts = c.Changes(NameOf);
            if (c.StartPageChanged && !c.IsNew)
            {
                parts = [$"start page {TitleOf(c.StartPage)}", .. parts];
            }

            string what = c.IsNew ? "new role" : parts.Count == 0 ? "name or description" : string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $" and {parts.Count - 8} more" : "");
            if (c.IsNew && parts.Count > 0)
            {
                what += ": " + string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $" and {parts.Count - 8} more" : "");
            }

            return $"{c.Name}: {what}";
        });
        if (!await _dialogs.ConfirmAsync("Save roles", string.Join(Environment.NewLine, lines), "Save").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            Show(await _api.SaveRolesAsync([.. changed.Select(c => c.ToChange())], CancellationToken.None).ConfigureAwait(true));
            ErrorText = null;
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDiscard() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private Task DiscardAsync() => LoadAsync(keepEdits: false);

    [RelayCommand]
    private void ToggleGroup(PermissionRowViewModel? group)
    {
        if (group is { IsGroup: true })
        {
            group.IsExpanded = !group.IsExpanded;
            ApplyFilter();
        }
    }

    /// <summary>The name a permission has in the matrix ("Web View: Open devices", "Restart").</summary>
    public string NameOf(string key)
    {
        if (!_byKey.TryGetValue(key, out var info))
        {
            return key;
        }

        return info.Group is "Devices" or "Tasks" or "Event rules" or "Logs" ? info.Name : info.Group + ": " + info.Name;
    }

    private void Show(RoleMatrix matrix)
    {
        CancelEditor();
        _byKey.Clear();
        foreach (var p in matrix.Permissions)
        {
            _byKey[p.Key] = p;
        }

        // Rows: a group header before the first permission of each group, in the server's order.
        _allRows.Clear();
        PermissionRowViewModel? group = null;
        foreach (var p in matrix.Permissions)
        {
            if (group is null || group.Group != p.Group)
            {
                group = new PermissionRowViewModel(p.Group, string.IsNullOrEmpty(p.IconKey) ? null : p.IconKey);
                _allRows.Add(group);
            }

            var row = new PermissionRowViewModel(p);
            group.Children.Add(row);
            _allRows.Add(row);
        }

        Columns.Clear();
        foreach (var role in matrix.Roles)
        {
            var column = new RoleColumnViewModel(role);
            column.StartPageText = StartPageTextOf(role.StartPage);
            column.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RoleColumnViewModel.IsDirty))
                {
                    UpdateDirty();
                }
            };
            Columns.Add(column);
        }

        BuildCells();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StartChoices));
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        ApplyFilter();
        UpdateDirty();
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    private void BuildCells()
    {
        foreach (var row in _allRows)
        {
            row.Cells.Clear();
            row.Cells.AddRange(Columns.Select(c => new PermissionCellViewModel(this, row, c)));
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StartChoices));
    }

    private void RefreshCells()
    {
        foreach (var cell in _allRows.SelectMany(r => r.Cells))
        {
            cell.Refresh();
        }
    }

    /// <summary>The search matches permission names, descriptions and group names; a collapsed group shows only its header.</summary>
    private void ApplyFilter()
    {
        string text = SearchText.Trim();
        var visible = new List<PermissionRowViewModel>(_allRows.Count);
        foreach (var group in _allRows.Where(r => r.IsGroup))
        {
            bool groupMatches = text.Length == 0 || group.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase);
            var children = group.Children.Where(r => groupMatches
                || r.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || (r.Description?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToList();
            if (children.Count == 0)
            {
                continue;
            }

            visible.Add(group);
            if (group.IsExpanded || text.Length > 0)
            {
                visible.AddRange(children);
            }
        }

        Rows.ReplaceAll(visible);
    }

    private void UpdateDirty() => IsDirty = Columns.Any(c => c.IsDirty);

    /// <summary>The start page choices, plus <paramref name="current"/> when it is a page that is not offered now (a plugin that is off).</summary>
    private List<StartPageChoice> ChoicesWith(string current)
    {
        List<StartPageChoice> choices =
        [
            new("", "Devices"),
            new(Oadm.Sdk.Client.HostPages.Compliance, "Compliance"),
            new(Oadm.Sdk.Client.HostPages.EventRules, "Event rules"),
            .. _pluginPages.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(p => new StartPageChoice("plugin:" + p.Id, p.DisplayName)),
            new(Oadm.Sdk.Client.HostPages.Logs, "Logs"),
        ];
        if (current.Length > 0 && choices.All(c => c.Key != current))
        {
            choices.Add(new StartPageChoice(current, TitleOf(current)));
        }

        return choices;
    }

    /// <summary>The name of a start page ("Live View"; a plugin page that is off: its id with "(off)").</summary>
    private string TitleOf(string key) => key switch
    {
        "" or Oadm.Sdk.Client.HostPages.Devices => "Devices",
        Oadm.Sdk.Client.HostPages.Compliance => "Compliance",
        Oadm.Sdk.Client.HostPages.EventRules => "Event rules",
        Oadm.Sdk.Client.HostPages.Logs => "Logs",
        _ => _pluginPages.FirstOrDefault(p => "plugin:" + p.Id == key)?.DisplayName
            ?? (key.StartsWith("plugin:", StringComparison.Ordinal) ? key["plugin:".Length..] + " (off)" : key),
    };

    private string StartPageTextOf(string key) => "Starts on " + TitleOf(key);

    private static PermissionCellViewModel? CellOf(PermissionRowViewModel row, RoleColumnViewModel role) =>
        row.Cells.FirstOrDefault(c => c.Role == role);

    private PermissionRowViewModel? GroupRowOf(PermissionRowViewModel row) =>
        row.IsGroup ? row : _allRows.FirstOrDefault(r => r.IsGroup && r.Children.Contains(row));

    private string UniqueName(string name)
    {
        string candidate = name.Length > 64 ? name[..64] : name;
        for (int i = 2; Columns.Any(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }
}
