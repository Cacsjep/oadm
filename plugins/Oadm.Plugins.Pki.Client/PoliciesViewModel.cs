using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>The groups of the policy table, in their order (decision 2026-10-25), and the kinds of each.</summary>
public static class PolicyTableGroups
{
    public const string Issuing = "Issuing policies";
    public const string Servers = "Server policies";
    public const string Access = "802.1X access policies";
    public const string Enrollment = "EST enrollment policies";
    public const string Expiry = "Expiry warnings";

    public static IReadOnlyList<string> All { get; } = [Issuing, Servers, Access, Enrollment, Expiry];

    /// <summary>The group of a kind.</summary>
    public static string Of(string kind) => kind switch
    {
        PolicyKinds.HttpsCertificate or PolicyKinds.Dot1xCertificate => Issuing,
        PolicyKinds.Dot1xServer or PolicyKinds.EstServer => Servers,
        PolicyKinds.AllowCa or PolicyKinds.Vlan or PolicyKinds.Block => Access,
        PolicyKinds.Enrollment or PolicyKinds.SignInCa => Enrollment,
        _ => Expiry,
    };

    /// <summary>
    /// The icon of a group's header (theme <c>Icon.*</c>): a key for issuing, a server, the network for 802.1X access, a
    /// download for EST enrollment, a bell for expiry warnings.
    /// </summary>
    public static string IconOf(string group) => group switch
    {
        Issuing => "key",
        Servers => "server",
        Access => "network",
        Enrollment => "download",
        _ => "bell",
    };

    /// <summary>The group key of the table: the header shows the chevron, <see cref="IconOf"/> and the name.</summary>
    public static GridGroup KeyOf(string group) => new(group, IconOf(group));
}

/// <summary>
/// The policies a jump to the Policies tab points to (a server tab's "N policies applied"): the kinds of that server, or
/// one policy. They are highlighted for <see cref="PoliciesViewModel.HighlightTime"/>, the first scrolled into view.
/// </summary>
public sealed record PolicyHighlight(Func<string, bool> Kinds, Guid? PolicyId = null)
{
    /// <summary>The 802.1X server's policies (the 802.1X server, trusted CA, VLAN, Block).</summary>
    public static PolicyHighlight Dot1x { get; } = new(PolicyKinds.IsDot1x);

    /// <summary>The EST server's policies (the EST server, Enrollment, EST sign-in CA).</summary>
    public static PolicyHighlight Est { get; } = new(PolicyKinds.IsEst);

    /// <summary>Exactly the policy <paramref name="id"/>.</summary>
    public static PolicyHighlight Policy(Guid id) => new(_ => false, id);

    public bool Matches(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return PolicyId is { } id ? policy.Id == id : Kinds(policy.Kind);
    }
}

/// <summary>
/// One row of the policy table: the name (a red dot when it needs attention: an overlap, a CA that cannot issue; the
/// Built-in badge), the type and the summary sentence; its group (<see cref="PolicyTableGroups"/>).
/// </summary>
public sealed partial class PolicyRowViewModel(PolicyRow row, string? problem) : ObservableObject
{
    public PolicyRow Row { get; } = row ?? throw new ArgumentNullException(nameof(row));

    public PkiPolicy Source => Row.Policy;

    public Guid Id => Source.Id;

    public string Name => Source.Name;

    /// <summary>"VLAN", "802.1X trusted CA".</summary>
    public string Type => PolicyKinds.Name(Source.Kind);

    /// <summary>"Issuing policies", "802.1X access policies".</summary>
    public string Group => PolicyTableGroups.Of(Source.Kind);

    /// <summary>The table's group key: <see cref="Group"/> with its header icon.</summary>
    public GridGroup GroupKey => PolicyTableGroups.KeyOf(Group);

    /// <summary>A jump to the tab points to this policy (<see cref="PolicyHighlight"/>): the accent tint for a few seconds.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    public string Summary => Row.Summary;

    /// <summary>What needs attention (the red dot's tooltip); null otherwise.</summary>
    public string? Problem { get; } = problem;

    public bool HasProblem => Problem is not null;

    /// <summary>A built-in policy (OADM needs it): the Built-in badge, no Delete.</summary>
    public bool IsSystem => Row.IsSystem;

    /// <summary>Why a built-in policy has no Delete (the panel's badge tooltip).</summary>
    public const string SystemTip = "OADM needs it, so it cannot be deleted";
}

/// <summary>A type of the Create panel: its name and what it does in one line.</summary>
public sealed record PolicyKindChoice(string Kind, string Name, string? Description)
{
    public bool HasDescription => !string.IsNullOrEmpty(Description);
}

/// <summary>What the Policies tab asks of the view: the shared device picker for a policy's devices, Create server certificate.</summary>
public interface IPolicyDialogs
{
    /// <summary>Shows <paramref name="picker"/> (<see cref="DevicePickerWindow"/> in its group mode); true when OK was clicked.</summary>
    Task<bool> PickDevicesAsync(DevicePickerViewModel picker, string heading);
}

/// <summary>
/// The Policies tab of the PKI page (docs/spec/pki.md "Policies", decision 2026-10-25, like Intune): one table (Name, Type,
/// Summary) grouped into Issuing, Server, 802.1X access, EST enrollment policies and Expiry warnings, with Create and a
/// search above it. A row or Create opens the side panel, which pushes the table to the left:
/// Create first lists the types with what each does, Next shows the form; a row shows its form at once. Save stores the
/// policy and closes the panel; Cancel or closing with changes not saved asks first. Changing needs "Manage the certificate
/// authority" and all devices; other users see it read only; users limited to a device group do not see it (the server
/// sends them none).
/// </summary>
public sealed partial class PoliciesViewModel : ObservableObject
{
    public const string CreateTitle = "Create a policy";

    /// <summary>The table when the search matches nothing.</summary>
    public const string NoMatchText = "No policies match the search.";

    private readonly ICorePluginClientContext _ctx;
    private PolicyList _list = new();
    private List<PolicyRowViewModel> _all = [];
    private int _loads;
    private bool _selecting;
    private IPkiFiles? _files;
    private readonly TimeProvider _time;
    private PolicyHighlight? _highlight;
    private ITimer? _highlightTimer;
    private int _highlightRun;
    private bool _applied;

    /// <summary>How long the policies a jump points to stay highlighted.</summary>
    public static readonly TimeSpan HighlightTime = TimeSpan.FromSeconds(3);

    public PoliciesViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The device picker (the page view).</summary>
    public IPolicyDialogs? Dialogs { get; set; }

    /// <summary>File pickers of the page (none of the policy form uses one any more; kept for the page view).</summary>
    public IPkiFiles? Files
    {
        get => _files;
        set => _files = value;
    }

    /// <summary>The list as the server answered it.</summary>
    public PolicyList List => _list;

    // ------------------------------------------------------------------ table

    /// <summary>
    /// The rows the search shows, grouped by <see cref="PolicyRowViewModel.Group"/> in the order of
    /// <see cref="PolicyTableGroups.All"/>, built-in first in each group (at most 200 policies; the table is virtualized).
    /// </summary>
    public RangeObservableCollection<PolicyRowViewModel> Rows { get; } = [];

    /// <summary>The search over name, type and summary.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>The row whose policy is open in the panel.</summary>
    [ObservableProperty]
    public partial PolicyRowViewModel? SelectedRow { get; set; }

    /// <summary>The search hides every policy (one line instead of the table).</summary>
    public bool HasNoMatch => Rows.Count == 0 && _all.Count > 0;

    partial void OnSearchTextChanged(string? value) => Filter();

    private void Filter()
    {
        var text = SearchText?.Trim();
        Rows.ReplaceAll(string.IsNullOrEmpty(text) ? _all : _all.Where(r =>
            r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || r.Type.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || r.Summary.Contains(text, StringComparison.CurrentCultureIgnoreCase)));
        OnPropertyChanged(nameof(HasNoMatch));
        if (SelectedRow is { } selected && !Rows.Contains(selected))
        {
            Select(null);
        }
    }

    // ------------------------------------------------------------------ highlight

    /// <summary>The highlighted rows changed (<see cref="PolicyRowViewModel.IsHighlighted"/>): the view sets the rows' class.</summary>
    public event EventHandler? HighlightChanged;

    /// <summary>A highlight started: the view scrolls this row (the first highlighted one) into view.</summary>
    public event EventHandler<PolicyRowViewModel>? HighlightShown;

    /// <summary>
    /// Highlights the policies of <paramref name="highlight"/> for <see cref="HighlightTime"/> (a jump from a server tab).
    /// Before the first read it waits for the policies; a search that would hide one of them is cleared.
    /// </summary>
    public void Highlight(PolicyHighlight highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        StopHighlightTimer();
        _highlight = highlight;
        if (_applied)
        {
            StartHighlight();
        }
    }

    private void StartHighlight()
    {
        if (_highlight is not { } highlight)
        {
            return;
        }

        if (!string.IsNullOrEmpty(SearchText) && _all.Any(r => highlight.Matches(r.Source) && !Rows.Contains(r)))
        {
            SearchText = null;
        }

        MarkHighlighted();
        var run = ++_highlightRun;
        var ui = SynchronizationContext.Current;
        _highlightTimer = _time.CreateTimer(_ =>
        {
            if (ui is null)
            {
                EndHighlight(run);
            }
            else
            {
                ui.Post(_ => EndHighlight(run), null);
            }
        }, null, HighlightTime, Timeout.InfiniteTimeSpan);
        if (Rows.FirstOrDefault(r => r.IsHighlighted) is { } first)
        {
            HighlightShown?.Invoke(this, first);
        }
    }

    private void EndHighlight(int run)
    {
        if (run != _highlightRun)
        {
            return; // a later jump started its own
        }

        StopHighlightTimer();
        _highlight = null;
        MarkHighlighted();
    }

    private void StopHighlightTimer()
    {
        _highlightTimer?.Dispose();
        _highlightTimer = null;
    }

    private void MarkHighlighted()
    {
        foreach (var row in _all)
        {
            row.IsHighlighted = _highlight?.Matches(row.Source) == true;
        }

        HighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ panel

    /// <summary>The side panel shows (it slides in and pushes the table to the left).</summary>
    [ObservableProperty]
    public partial bool IsPanelOpen { get; set; }

    /// <summary>The panel lists the types (Create, before Next).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelTitle), nameof(IsEditing))]
    public partial bool IsChoosingKind { get; set; }

    /// <summary>The form in the panel; null while the types are listed or the panel is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelTitle), nameof(IsEditing))]
    public partial PolicyEditorViewModel? Editor { get; set; }

    /// <summary>The panel shows a form.</summary>
    public bool IsEditing => Editor is not null && !IsChoosingKind;

    /// <summary>The System badge next to the panel's title (a system policy).</summary>
    public bool ShowsSystemBadge => IsEditing && Editor!.IsInPlace;

    /// <summary>The type below the panel's title ("VLAN"); none for a system policy, whose name says it.</summary>
    public string? PanelKind => IsEditing && !Editor!.IsInPlace ? Editor.KindName : null;

    public bool HasPanelKind => PanelKind is not null;

    /// <summary>Delete shows in the panel: a stored user policy, for a user who may change the policies.</summary>
    public bool CanDelete => IsEditing && Editor!.CanDelete;

    /// <summary>Save shows in the panel: a form the user may change.</summary>
    public bool ShowsSave => IsEditing && Editor!.IsEditable;

    /// <summary>"Not saved" left of Save.</summary>
    public string? SaveStatus => IsEditing ? Editor!.SaveStatus : null;

    public bool HasSaveStatus => SaveStatus is not null;

    /// <summary>Why Save is off (its tooltip).</summary>
    public string? SaveBlockedReason => IsEditing ? Editor!.SaveBlockedReason : null;

    private bool CanSave() => IsEditing && Editor!.CanSave;

    /// <summary>Save of the panel: the form's save (the panel closes when the server stored the policy).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveOpenAsync()
    {
        if (Editor is { } editor)
        {
            await editor.SaveAsync().ConfigureAwait(true);
        }
    }

    private bool CanDeleteOpen() => CanDelete && !Editor!.IsBusy;

    /// <summary>Delete of the panel (a stored user policy); waits while a save or a delete runs.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteOpen))]
    private async Task DeleteOpenAsync()
    {
        if (Editor is { } editor)
        {
            await DeleteAsync(editor).ConfigureAwait(true);
        }
    }

    partial void OnEditorChanged(PolicyEditorViewModel? oldValue, PolicyEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
        }

        NotifyPanel();
    }

    partial void OnIsChoosingKindChanged(bool value) => NotifyPanel();

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyPanel();

    private void NotifyPanel()
    {
        OnPropertyChanged(nameof(ShowsSystemBadge));
        OnPropertyChanged(nameof(PanelKind));
        OnPropertyChanged(nameof(HasPanelKind));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ShowsSave));
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(PanelTitle));
        SaveOpenCommand.NotifyCanExecuteChanged();
        DeleteOpenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Create a policy" while the types are listed, else the policy's name or "New VLAN policy".</summary>
    public string PanelTitle => IsChoosingKind || Editor is null ? CreateTitle : Editor.Heading;

    /// <summary>The types Create offers (never a second 802.1X server or EST server policy).</summary>
    public IReadOnlyList<PolicyKindChoice> KindChoices
    {
        get
        {
            var taken = _list.Rows.Where(r => PolicyKinds.OneOnly(r.Policy.Kind)).Select(r => r.Policy.Kind).ToHashSet(StringComparer.Ordinal);
            return [.. PolicyKinds.All.Where(k => !taken.Contains(k) && (_list.Pro || !PolicyKinds.IsPro(k)))
                .Select(k => new PolicyKindChoice(k, PolicyKinds.Name(k), PolicyKinds.Description(k)))];
        }
    }

    /// <summary>The type chosen in the Create panel.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial PolicyKindChoice? SelectedKind { get; set; }

    // ------------------------------------------------------------------ state

    /// <summary>The first read (the overlay over the tab).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A later read (the progress row; the table stays).</summary>
    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    /// <summary>A read or a delete failed (directly below the table).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The tab shows: users with all devices (the policies are server-wide).</summary>
    public bool IsShown => _ctx.HasAllDevices;

    /// <summary>The user may change the policies (the server's answer, and the role now).</summary>
    public bool MayManage => _list.MayManage && _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>Why Create is off.</summary>
    public string? CreateTip => _list.Rows.Count >= PolicyLimits.MaxPolicies ? PolicyTexts.TooMany : null;

    // ------------------------------------------------------------------ reading

    /// <summary>Reads the policies (the overlay the first time, the progress row later).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (!IsShown)
        {
            Apply(new PolicyList());
            return;
        }

        var first = _loads++ == 0;
        IsLoading = first;
        IsReloading = !first;
        try
        {
            var list = PkiJson.Deserialize<PolicyList>(await _ctx.InvokeAsync(PolicyMethods.GetPolicies, null, ct).ConfigureAwait(true));
            Apply(list);
            Error = list.Error; // the stored policies cannot be read
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown on the tab.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
            IsReloading = false;
        }
    }

    /// <summary>
    /// Shows <paramref name="list"/> (also used by tests and the screenshots). The panel stays: a form with changes not saved
    /// as the user left it, an unchanged form of a stored policy with its new values; it closes when its policy is gone.
    /// </summary>
    public void Apply(PolicyList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = IsShown ? list : new PolicyList();
        var issuing = _list.IssuingCas.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = PolicyTableGroups.All.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i, StringComparer.Ordinal);
        _all = [.. _list.Rows.Where(r => _list.Pro || !PolicyKinds.IsPro(r.Policy.Kind)).Select(r => new PolicyRowViewModel(r, ProblemOf(r, issuing)))
            .OrderBy(r => order[r.Group]).ThenBy(r => r.IsSystem ? 0 : 1)]; // stable: the server's order within

        _selecting = true;
        try
        {
            Filter();
            // A form with an open dialog (Choose devices, a question) stays as it is: the dialog works on it.
            var open = Editor is { IsNew: false } editor ? _all.FirstOrDefault(r => r.Id == editor.PolicyId) : null;
            if (Editor is { IsNew: false, IsDirty: false, DialogsOpen: 0 } && !IsChoosingKind)
            {
                if (open is null)
                {
                    ClosePanel();
                }
                else
                {
                    Editor = NewEditor(open.Row);
                }
            }

            Select(IsPanelOpen ? open : null);
        }
        finally
        {
            _selecting = false;
        }

        // A jump made before the first read highlights once the policies are there; a later read keeps the highlight on.
        _applied = true;
        if (_highlight is not null)
        {
            if (_highlightTimer is null)
            {
                StartHighlight();
            }
            else
            {
                MarkHighlighted();
            }
        }

        _role = (IsShown, MayManage);
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(CreateTip));
        OnPropertyChanged(nameof(KindChoices));
        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The row's dot: its warning, or an issuing policy whose CA cannot issue (also when no CA can issue at all).</summary>
    private static string? ProblemOf(PolicyRow row, HashSet<string> issuing) =>
        row.Warning
        ?? (PolicyKinds.IsIssuing(row.Policy.Kind) && !issuing.Contains(row.Policy.CaId ?? CertificateAuthorityInfo.OadmId) ? PolicyTexts.CannotIssue : null);

    /// <summary>
    /// The user's role or device group changed. The open form stays (with its changes) unless what the user may do here
    /// changed: the tab hidden closes it, losing Manage makes a stored policy's form read only and closes a new one.
    /// </summary>
    internal void NotifyRole()
    {
        (bool Shown, bool Manage) role = (IsShown, MayManage);
        if (_role == role)
        {
            return; // another role, or a group that leaves the tab as it was: nothing typed is lost
        }

        bool onlyManage = _role is { } before && before.Shown == role.Shown && role.Shown;
        _role = role;
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(MayManage));
        if (!onlyManage)
        {
            ClosePanel();
            Apply(IsShown ? _list : new PolicyList()); // nothing server-wide stays for a user limited to a group
            return;
        }

        if (!role.Manage && (IsChoosingKind || Editor is { IsNew: true }))
        {
            ClosePanel(); // a new policy cannot be created any more
        }
        else if (Editor is { } editor)
        {
            editor.ReadOnly = !role.Manage;
        }

        OnPropertyChanged(nameof(CreateTip));
        OnPropertyChanged(nameof(KindChoices));
        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the role allowed when the tab last followed it (<see cref="NotifyRole"/>).</summary>
    private (bool Shown, bool Manage)? _role;

    // ------------------------------------------------------------------ opening and closing

    partial void OnSelectedRowChanged(PolicyRowViewModel? oldValue, PolicyRowViewModel? newValue)
    {
        if (_selecting || newValue is null || newValue == oldValue)
        {
            return;
        }

        _ = OpenAsync(oldValue, newValue);
    }

    /// <summary>Opens the policy of <paramref name="row"/> in the panel; changes not saved in the panel ask first.</summary>
    private async Task OpenAsync(PolicyRowViewModel? previous, PolicyRowViewModel row)
    {
        if (Editor is { IsDirty: true })
        {
            await Task.Yield(); // the table finishes its own selection first
            Select(Current(previous));
            if (!await MayLeaveAsync().ConfigureAwait(true))
            {
                return;
            }

            // A reload during the question made new rows: the one asked for, as it is now (gone: nothing opens).
            if (Current(row) is not { } now)
            {
                return;
            }

            row = now;
        }

        Select(row);
        IsChoosingKind = false;
        Editor = NewEditor(row.Row);
        IsPanelOpen = true;
    }

    /// <summary>The row of the same policy in the list as it is now; null when the policy is gone.</summary>
    private PolicyRowViewModel? Current(PolicyRowViewModel? row) => row is null ? null : _all.FirstOrDefault(r => r.Id == row.Id);

    private void Select(PolicyRowViewModel? row)
    {
        var selecting = _selecting;
        _selecting = true;
        try
        {
            SelectedRow = row;
        }
        finally
        {
            _selecting = selecting;
        }
    }

    /// <summary>The open form may go: it has no changes, or the user discards them (a reload meanwhile keeps the form).</summary>
    private async Task<bool> MayLeaveAsync()
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        editor.DialogsOpen++;
        try
        {
            return await _ctx.ConfirmAsync("Discard changes", $"The changes to {editor.Heading} are not saved.", "Discard").ConfigureAwait(true);
        }
        finally
        {
            editor.DialogsOpen--;
        }
    }

    private void ClosePanel()
    {
        IsPanelOpen = false;
        IsChoosingKind = false;
        Editor = null;
        SelectedKind = null;
        Select(null);
    }

    /// <summary>The form of a stored policy (a system policy keeps its name and devices).</summary>
    private PolicyEditorViewModel NewEditor(PolicyRow row) =>
        Configure(new PolicyEditorViewModel(_ctx, this, row.Policy, _list, _ctx.Devices, inPlace: row.IsSystem));

    /// <summary>The form of a new policy of <paramref name="kind"/> (also used by tests).</summary>
    public PolicyEditorViewModel NewEditor(string kind) => Configure(new PolicyEditorViewModel(_ctx, this, null, _list, _ctx.Devices, kind: kind));

    private PolicyEditorViewModel Configure(PolicyEditorViewModel editor)
    {
        editor.ReadOnly = !MayManage;
        return editor;
    }

    private bool CanCreate() => MayManage && _list.Rows.Count < PolicyLimits.MaxPolicies;

    /// <summary>Create: the panel with the types.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (!await MayLeaveAsync().ConfigureAwait(true))
        {
            return;
        }

        Select(null);
        Editor = null;
        SelectedKind = null;
        IsChoosingKind = true;
        IsPanelOpen = true;
    }

    private bool CanNext() => SelectedKind is not null;

    /// <summary>Next of the Create panel: the form of the chosen type.</summary>
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (SelectedKind is not { } kind)
        {
            return;
        }

        Editor = NewEditor(kind.Kind);
        IsChoosingKind = false;
    }

    /// <summary>Cancel, or the panel's close button: changes not saved ask first.</summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        if (await MayLeaveAsync().ConfigureAwait(true))
        {
            ClosePanel();
        }
    }

    // ------------------------------------------------------------------ changing

    /// <summary>Delete of the open policy (not a system policy), after a question; the panel then closes.</summary>
    internal async Task DeleteAsync(PolicyEditorViewModel editor)
    {
        if (!MayManage || editor.Existing is not { } policy || editor.IsInPlace)
        {
            return;
        }

        var message = policy.Kind switch
        {
            PolicyKinds.AllowCa => $"Delete the policy {policy.Name}? Certificates of this CA no longer pass the 802.1X server.",
            PolicyKinds.SignInCa => $"Delete the policy {policy.Name}? Cameras can no longer sign in to the EST server with this CA.",
            PolicyKinds.Enrollment => $"Delete the policy {policy.Name}? The EST server refuses its devices unless another Enrollment policy holds them.",
            _ => $"Delete the policy {policy.Name}?",
        };
        editor.DialogsOpen++;
        bool confirmed;
        try
        {
            confirmed = await _ctx.ConfirmAsync("Delete policy", message, "Delete").ConfigureAwait(true);
        }
        finally
        {
            editor.DialogsOpen--;
        }

        if (!confirmed)
        {
            return;
        }

        editor.IsBusy = true; // the form waits for the answer
        try
        {
            var reply = PkiJson.Deserialize<PolicySaveReply>(await _ctx.InvokeAsync(PolicyMethods.DeletePolicy, PkiJson.Serialize(new PolicyRef(policy.Id)), CancellationToken.None)
                .ConfigureAwait(true));
            if (reply.List is { } list)
            {
                ClosePanel();
                Apply(list);
            }

            Error = reply.Error?.Message;
        }
#pragma warning disable CA1031 // Shown on the tab.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
        finally
        {
            editor.IsBusy = false;
        }
    }

    /// <summary>Saves <paramref name="policy"/>; the list after it (the panel closes), or the error that refused it.</summary>
    internal async Task<PolicySaveReply> SaveAsync(PkiPolicy policy, PolicyEditorViewModel editor)
    {
        var reply = PkiJson.Deserialize<PolicySaveReply>(await _ctx.InvokeAsync(PolicyMethods.SavePolicy, PkiJson.Serialize(policy), CancellationToken.None).ConfigureAwait(true));
        if (reply.List is { } list && reply.Error is null)
        {
            if (Editor == editor)
            {
                ClosePanel();
            }

            Apply(list);
        }

        return reply;
    }

    /// <summary>Choose devices of the open policy: the shared picker over a copy of its choice, taken over on OK.</summary>
    internal async Task ChooseDevicesAsync(PolicyEditorViewModel editor)
    {
        if (Dialogs is not { } dialogs)
        {
            return;
        }

        var picker = editor.NewPicker();
        bool ok;
        editor.DialogsOpen++; // a reload while the dialog is open keeps this form (the dialog's choice goes into it)
        try
        {
            ok = await dialogs.PickDevicesAsync(picker, "Choose devices").ConfigureAwait(true);
        }
        finally
        {
            editor.DialogsOpen--;
        }

        if (ok)
        {
            editor.UsePicker(picker);
        }
    }
}

/// <summary>One EST profile of an Enrollment policy: a check box with the profile's name.</summary>
public sealed partial class EnrollmentProfileViewModel(string name, bool isChecked, PolicyEditorViewModel owner) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.ProfilesEdited();
}

/// <summary>
/// The policy form in the side panel of the Policies tab (docs/spec/pki.md "Policies", decision 2026-10-25): the name and the
/// kind's own fields (the kind is preset by Add; a policy keeps its kind), and where the kind has devices "All devices" or
/// the shared device picker in its group mode behind Choose devices (sites with "This server", tags, single devices, Except;
/// a Block that also revokes without tags). Save sends the policy (a new one at once, a stored one only with changes, the
/// status left of Save); a Block policy that newly revokes or whose devices changed asks first with the count of
/// certificates it revokes. Errors directly below the fields. A system policy (<see cref="IsInPlace"/>) keeps its name and
/// devices and has no Delete.
/// </summary>
public sealed partial class PolicyEditorViewModel : ValidatingViewModel
{
    public const string OldTlsHint = "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure.";
    /// <summary>The choice next to the Minutes field ("Every" [60] minutes).</summary>
    public const string ReauthEvery = "Every";
    public const string OnlyManagedText = PolicyTexts.OnlyManaged;

    /// <summary>"Off: any device with a valid certificate of this CA".</summary>
    public static string OnlyManagedTip { get; } = "Off: " + PolicyTexts.AllowCaScope(onlyManaged: false) + " of this CA";

    public static IReadOnlyList<Choice<string>> TlsVersions { get; } = [.. PolicyRules.TlsVersions.Select(v => new Choice<string>(v, "TLS " + v))];

    public static IReadOnlyList<Choice<bool>> ReauthChoices { get; } = [new(false, PolicyTexts.SwitchDecides), new(true, ReauthEvery)];

    /// <summary>"All devices" (every device, also of sites added later and, for 802.1X, devices OADM does not manage) or the picker's choice.</summary>
    public static IReadOnlyList<Choice<bool>> DeviceScopes { get; } = [new(true, PolicyTexts.AllDevices), new(false, PolicyTexts.ChosenDevices)];

    private readonly ICorePluginClientContext _ctx;
    private readonly PoliciesViewModel _owner;
    private readonly PkiPolicy? _existing;
    private readonly PolicyList _list;
    private readonly string _baseline = string.Empty;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly DeviceGroupPickerOptions _pickerOptions;
    private bool _applying = true;
    private DeviceGroupChoice _choice; // the chosen sites, tags and devices (the picker is made only when one is needed)
    private DevicePickerViewModel? _picker;
    private string _devicesText = NoDevicesText;
    private bool _dirty;

    /// <summary>The validator's name of the Devices field (its error below the device choice).</summary>
    internal const string DevicesField = "Devices";

    /// <summary>The properties that follow the fields (never a change of the policy themselves).</summary>
    private static readonly HashSet<string> Derived = new(StringComparer.Ordinal)
    {
        nameof(Preview), nameof(IsDirty), nameof(CanSave), nameof(SaveBlockedReason), nameof(SaveStatus), nameof(HasSaveStatus), nameof(IsSaveStatusOk),
        nameof(ShowsDevicesField), nameof(DevicesText), nameof(DevicesError), nameof(CertificateHint), nameof(HasNoCertificates),
        nameof(IsEditable), nameof(IsBusy), nameof(HasErrors), nameof(IsFormValid), nameof(FormError), nameof(ProfilesError), nameof(Picker),
    };

    /// <param name="ctx">The page's context.</param>
    /// <param name="owner">The Policies tab.</param>
    /// <param name="existing">The policy to change; null for a new one.</param>
    /// <param name="list">The list as the server answered it.</param>
    /// <param name="devices">The devices of the device picker.</param>
    /// <param name="kind">A new policy's kind (Add); null: the existing policy's, or the first kind Add could offer.</param>
    /// <param name="inPlace">A system policy (no name, no Delete; its devices change like any policy's).</param>
    public PolicyEditorViewModel(ICorePluginClientContext ctx, PoliciesViewModel owner, PkiPolicy? existing, PolicyList list, IEnumerable<IDeviceInfo> devices, string? kind = null, bool inPlace = false)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _existing = existing;
        IsInPlace = inPlace && existing is not null;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && Derived.Contains(name))
            {
                return;
            }

            OnPropertyChanged(nameof(Preview)); // the sentence follows every field
            OnPropertyChanged(nameof(ShowsDevicesField));
            if (!_applying)
            {
                // One comparison per change (never one per binding that reads IsDirty).
                _dirty = !string.IsNullOrEmpty(_baseline) && !string.Equals(PkiJson.Serialize(ToPolicy()), _baseline, StringComparison.Ordinal);
                NotifySaveState();
            }
        };
        // One 802.1X server and one EST server policy: Add never offers a second one.
        var taken = list.Rows.Where(r => PolicyKinds.OneOnly(r.Policy.Kind) && r.Policy.Id != existing?.Id).Select(r => r.Policy.Kind).ToHashSet(StringComparer.Ordinal);
        KindChoices = existing is { } kept
            ? [new Choice<string>(kept.Kind, PolicyKinds.Name(kept.Kind))]
            : kind is not null && PolicyKinds.IsKnown(kind) && !taken.Contains(kind)
                ? [new Choice<string>(kind, PolicyKinds.Name(kind))]
                : [.. PolicyKinds.All.Where(k => !taken.Contains(k)).Select(k => new Choice<string>(k, PolicyKinds.Name(k)))];
        CaChoices = [.. list.Cas.Select(c => new Choice<string>(c.Id, c.Name))];
        SignInCaChoices = [.. list.SignInCas.Select(c => new Choice<string>(c.Id, c.Name))];

        // "Issued by": every CA with its key (decision 2026-10-25); a CA the policy names that can no longer issue stays shown
        // so the error says so below the field.
        IssuingCaChoices = [.. (list.IssuingCas.Count > 0 ? list.IssuingCas : list.Cas.Where(c => c.Kind == CertificateAuthorityKind.Oadm)).Select(c => new Choice<string>(c.Id, c.Name))];
        // RADIUS server CA: a CA of the list (decision 2026-10-25), the OADM CA first.
        RadiusCaChoices = [.. list.Cas.Select(c => new Choice<string>(c.Id, c.Name))];
        _certificates = list.ServerCertificates;

        var policy = existing ?? new PkiPolicy();
        Name = policy.Name;
        Kind = KindChoices.FirstOrDefault(k => k.Value == policy.Kind) ?? KindChoices.FirstOrDefault(k => k.Value == PolicyKinds.Vlan) ?? KindChoices[0];
        CertificateChoices = ChoicesOf(_certificates, Kind.Value, policy.CertificateId);
        Certificate = CertificateChoices.FirstOrDefault(c => c.Value == policy.CertificateId);
        Vlan = policy.Vlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Days = (policy.Days ?? PolicyLimits.DefaultExpiryDays).ToString(CultureInfo.InvariantCulture);
        // A new Allow CA starts with the first CA no Allow CA policy names yet.
        var allowed = list.Rows.Where(r => r.Policy.Kind == PolicyKinds.AllowCa).Select(r => r.Policy.CaId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Ca = CaChoices.FirstOrDefault(c => string.Equals(c.Value, policy.CaId, StringComparison.OrdinalIgnoreCase))
            ?? CaChoices.FirstOrDefault(c => !allowed.Contains(c.Value)) ?? (CaChoices.Count > 0 ? CaChoices[0] : null);
        OnlyManaged = policy.OnlyManaged ?? true;

        // Sign-in CA: a new one starts with the first CA no Sign-in CA policy names yet, for new certificates.
        var signingIn = list.Rows.Where(r => r.Policy.Kind == PolicyKinds.SignInCa && r.Policy.Id != existing?.Id).Select(r => r.Policy.CaId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var signIn = policy.Kind == PolicyKinds.SignInCa ? policy : null;
        SignInCa = SignInCaChoices.FirstOrDefault(c => string.Equals(c.Value, signIn?.CaId, StringComparison.OrdinalIgnoreCase))
            ?? SignInCaChoices.FirstOrDefault(c => !signingIn.Contains(c.Value)) ?? (SignInCaChoices.Count > 0 ? SignInCaChoices[0] : null);
        SignInFor = SignInForChoices.FirstOrDefault(c => c.Value == signIn?.SignInFor) ?? SignInForChoices[0];

        // Enrollment: all profiles for a new one; the EST server's profiles as check boxes (and a profile the policy names
        // that the server no longer has, so it can be unchecked).
        var enrollment = policy.Kind == PolicyKinds.Enrollment ? policy : null;
        ProfileScope = ProfileScopeChoices.FirstOrDefault(c => c.Value == enrollment?.ProfileScope) ?? ProfileScopeChoices[0];
        var named = new HashSet<string>(enrollment?.Profiles ?? [], StringComparer.OrdinalIgnoreCase);
        ProfileChoices = [.. list.EstProfiles.Concat(enrollment?.Profiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new EnrollmentProfileViewModel(p, named.Contains(p), this))];
        var server = existing?.Kind == PolicyKinds.Dot1xServer ? existing : null;
        var serverDefaults = PolicyRules.Normalize(new PkiPolicy { Kind = Kind.Value });
        MinimumTls = TlsVersions.FirstOrDefault(v => v.Value == server?.MinimumTls) ?? TlsVersions[PolicyRules.MinimumIndex(PolicyRules.DefaultMinimumTls) ?? 2];
        LargestPacket = (server?.LargestEapPacket ?? PolicyRules.DefaultEapPacket).ToString("N0", CultureInfo.InvariantCulture);
        Reauth = ReauthChoices[server?.ReauthMinutes is null ? 0 : 1];
        ReauthMinutes = (server?.ReauthMinutes ?? 60).ToString(CultureInfo.InvariantCulture);

        // Issuing: the policy's values; a new one starts like the first policy of its kind (the 802.1X settings too).
        var issuing = PolicyKinds.IsIssuing(policy.Kind) ? policy : null;
        var dot1x = existing?.Kind == PolicyKinds.Dot1xCertificate ? existing : list.Rows.Select(r => r.Policy).FirstOrDefault(p => p.Kind == PolicyKinds.Dot1xCertificate);
        IssuingCa = IssuingCaChoices.FirstOrDefault(c => string.Equals(c.Value, issuing?.CaId ?? CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase))
            ?? (issuing?.CaId is { } namedCa ? new Choice<string>(namedCa, PolicyTexts.RemovedCa) : IssuingCaChoices.Count > 0 ? IssuingCaChoices[0] : null);
        ValidDays = (issuing?.ValidDays ?? serverDefaults.ValidDays ?? PolicyLimits.DefaultValidDays).ToString(CultureInfo.InvariantCulture);
        Key = KeyChoices.FirstOrDefault(k => k.Value == (issuing?.Key ?? serverDefaults.Key)) ?? KeyChoices[0];
        Eapol = EapolChoices.FirstOrDefault(e => e.Value == (dot1x?.EapolVersion ?? 1)) ?? EapolChoices[0];
        Identity = IdentityChoices.FirstOrDefault(i => i.Value == dot1x?.Identity) ?? IdentityChoices[0];
        CustomIdentity = dot1x?.CustomIdentity ?? string.Empty;
        RadiusCa = RadiusCaChoices.FirstOrDefault(c => string.Equals(c.Value, dot1x?.RadiusCaId ?? CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase))
            ?? (dot1x?.RadiusCaId is { } removed ? new Choice<string>(removed, PolicyTexts.RemovedCa) : null);

        var chosen = policy.Devices ?? new PolicyDevices();
        Scope = DeviceScopes[chosen.AllDevices ? 0 : 1];
        var tags = list.Tags.Concat(chosen.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pickerOptions = new DeviceGroupPickerOptions
        {
            Sites = [new DevicePickerSite(null, PolicyTexts.ThisServer), .. list.Sites.Select(s => new DevicePickerSite(s.Id, s.Name))],

            // Plugins know tags by name: the name is the id.
            Tags = [.. tags.Select(t => new DevicePickerTag(t, t))],
        };
        // No copy of the device list and no picker over it for every form (30,000 devices): only Choose devices makes one.
        _devices = devices as IReadOnlyList<IDeviceInfo> ?? [.. devices ?? []];
        _choice = new DeviceGroupChoice(chosen.ThisServer, chosen.SiteIds ?? [], chosen.Tags ?? [], chosen.Serials ?? [], chosen.ExceptSerials ?? []);
        _devicesText = TextOf(_choice);
        _applying = false;

        var others = list.Rows.Select(r => r.Policy).Where(p => p.Id != existing?.Id).ToList();
        Validation
            .Rule(nameof(Name), () => Name.Trim().Length == 0 ? PolicyTexts.EnterName
                : Name.Trim().Length > PolicyLimits.MaxNameLength ? PolicyTexts.NameTooLong
                : others.Any(p => p.Kind == Kind.Value && string.Equals(p.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? PolicyTexts.NameTaken(Kind.Value)
                : null)
            .Rule(nameof(Vlan), () => !IsVlan || ParseInt(Vlan) is >= PolicyRules.MinVlan and <= PolicyRules.MaxVlan ? null : PolicyTexts.VlanRange)
            .Rule(nameof(Days), () => !IsExpiry || ParseInt(Days) is >= PolicyLimits.MinExpiryDays and <= PolicyLimits.MaxExpiryDays ? null : PolicyTexts.DaysRange)
            .Rule(nameof(Ca), () => !IsAllowCa || Ca is not null ? null : PolicyTexts.ChooseCa)
            .Rule(nameof(SignInCa), () => !IsSignInCa || SignInCa is not null ? null : PolicyTexts.ChooseCa)
            .Rule(nameof(ProfileChoices), () => !IsChosenProfiles || ProfileChoices.Any(p => p.IsChecked) ? null : PolicyTexts.ChooseProfiles)
            .Rule(nameof(LargestPacket), () => !IsServer || ParseInt(LargestPacket) is >= PolicyRules.MinEapPacket and <= PolicyRules.MaxEapPacket ? null : PolicyRules.EapPacketRange)
            .Rule(nameof(ReauthMinutes), () => !IsServer || !Reauth.Value || ParseInt(ReauthMinutes) is >= PolicyLimits.MinReauthMinutes and <= PolicyLimits.MaxReauthMinutes
                ? null : PolicyTexts.ReauthRange)
            .Rule(nameof(IssuingCa), () => !IsIssuing ? null : IssuingCa is null ? PolicyTexts.ChooseCa
                : IssuingCaChoices.Any(c => string.Equals(c.Value, IssuingCa.Value, StringComparison.OrdinalIgnoreCase)) ? null : PolicyTexts.CannotIssue)
            .Rule(nameof(ValidDays), () => !IsIssuing || ParseInt(ValidDays) is >= PolicyLimits.MinValidDays and <= PolicyLimits.MaxValidDays ? null : PolicyTexts.ValidDaysRange)
            .Rule(nameof(Certificate), () => !IsServerKind ? null : Certificate is null ? PolicyTexts.ChooseCertificate
                : _certificates.FirstOrDefault(c => c.Id == Certificate.Value) is not { } chosen ? PolicyTexts.CertificateGone
                : chosen.Problem ?? (chosen.UsableFor(Kind.Value) ? null : PolicyTexts.Dot1xServerNeedsEccCertificate))
            .Rule(nameof(CustomIdentity), () => IsDot1xCertificate ? PkiValidation.CustomIdentity(Identity.Value, CustomIdentity) : null)
            .Rule(nameof(RadiusCa), () => !IsDot1xCertificate ? null
                : RadiusCa is { } radius && RadiusCaChoices.Any(c => string.Equals(c.Value, radius.Value, StringComparison.OrdinalIgnoreCase)) ? null : PolicyTexts.ChooseRadiusCa)
            // The devices are a field like the others (CLAUDE.md validation): none chosen,
            // and the server's refusal (an overlap) below the device choice until the choice changes.
            .Rule(DevicesField, () => !ShowsPicker ? null
                : _choice.IsEmpty ? PolicyTexts.ChooseDevices
                : null);
        Validation.Validate();
        _baseline = PkiJson.Serialize(ToPolicy());
    }

    /// <summary>A Choose devices dialog or a question of the form is open: a reload of the list leaves the form as it is.</summary>
    internal int DialogsOpen { get; set; }

    /// <summary>A system policy (decision 2026-10-25): it keeps its name and devices and cannot be deleted.</summary>
    public bool IsInPlace { get; }

    /// <summary>The id of the policy (empty for a new one).</summary>
    public Guid PolicyId => _existing?.Id ?? Guid.Empty;

    /// <summary>The policy as it was read; null for a new one.</summary>
    internal PkiPolicy? Existing => _existing;

    /// <summary>Delete shows: a stored user policy, for a user who may change the policies.</summary>
    public bool CanDelete => _existing is not null && !IsInPlace && !ReadOnly;

    /// <summary>Delete of the open policy (the page asks first); waits while a save or a delete runs.</summary>
    [RelayCommand(CanExecute = nameof(CanRunDelete))]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    private bool CanRunDelete() => CanDelete && !IsBusy;

    partial void OnIsBusyChanged(bool value) => DeleteCommand.NotifyCanExecuteChanged();

    // ------------------------------------------------------------------ devices

    /// <summary>The Devices field shows: "All devices" can be chosen, or the devices are chosen in the picker.</summary>
    public bool ShowsDevicesField => ShowsDeviceScope || ShowsPicker;

    /// <summary>The chosen sites, tags and devices in one line ("Site Building B, Tag PTZ, 3 devices"), or that none are chosen yet.</summary>
    public string DevicesText => _devicesText;

    private string TextOf(DeviceGroupChoice choice) =>
        choice.IsEmpty ? NoDevicesText : PolicyTexts.DevicesText(DevicesOf(choice), _list.Sites.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name));

    public const string NoDevicesText = "No devices chosen yet.";

    /// <summary>Why the devices cannot be saved (below the Devices field, from the validator); null otherwise.</summary>
    public string? DevicesError => Validation[DevicesField];

    /// <summary>Choose devices: the shared device picker over a copy of the choice, taken over on OK.</summary>
    [RelayCommand]
    private Task ChooseDevicesAsync() => _owner.ChooseDevicesAsync(this);

    /// <summary>A picker over the current choice (Cancel leaves the form as it is); its error shows in the picker too.</summary>
    internal DevicePickerViewModel NewPicker() => new(_devices, _choice, _pickerOptions) { ErrorText = DevicesError };

    /// <summary>Takes over the choice of <paramref name="picker"/> after OK: the Devices field is edited (its server error goes).</summary>
    internal void UsePicker(DevicePickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        _picker = picker;
        _choice = picker.GroupChoice;
        _devicesText = TextOf(_choice);
        OnPropertyChanged(nameof(Picker));
        OnPropertyChanged(nameof(DevicesText));
        OnPropertyChanged(DevicesField); // touches the field: validates, clears the server's error of the old choice
        OnPropertyChanged(nameof(DevicesError));
    }

    private static PolicyDevices DevicesOf(DeviceGroupChoice choice) => new()
    {
        ThisServer = choice.ThisServer,
        SiteIds = choice.SiteIds,
        Tags = choice.TagIds,
        Serials = choice.Serials,
        ExceptSerials = choice.ExceptSerials,
    };

    /// <summary>The fields cannot be changed (a user who may not manage the policies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool ReadOnly { get; set; }

    /// <summary>The fields can be changed: a user who may manage the policies, while no save runs.</summary>
    public bool IsEditable => !ReadOnly && !IsBusy;

    /// <summary>The name field shows (a system policy keeps its name).</summary>
    public bool ShowsName => !IsInPlace;

    /// <summary>The heading of the form: the policy's name, or "New VLAN policy".</summary>
    public string Heading => _existing?.Name ?? $"New {PolicyKinds.Name(Kind.Value)} policy";

    /// <summary>"VLAN", "802.1X trusted CA": the kind below the heading.</summary>
    public string KindName => PolicyKinds.Name(Kind.Value);

    /// <summary>The fields differ from the policy as it was read (Save is on, the status says Not saved); worked out once per change.</summary>
    public bool IsDirty => _dirty;

    /// <summary>This form follows its own save (the status says Saved until the next change).</summary>
    public bool JustSaved { get; set; }

    public const string SavedText = "Saved";
    public const string NotSavedText = "Not saved";
    public const string NoChangesText = "No changes to save.";

    /// <summary>The status left of Save: "Not saved" for a new policy or with changes, "Saved" after this form saved, else none.</summary>
    public string? SaveStatus => IsNew || IsDirty ? NotSavedText : JustSaved ? SavedText : null;

    public bool HasSaveStatus => SaveStatus is not null;

    public bool IsSaveStatusOk => SaveStatus == SavedText;

    private void NotifySaveState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(IsSaveStatusOk));
    }

    /// <summary>Save of the form (the page then opens the saved policy's form).</summary>
    [RelayCommand]
    private async Task SaveChangesAsync() => await SaveAsync().ConfigureAwait(true);

    public static IReadOnlyList<Choice<string>> KeyChoices { get; } = [.. PolicyKeys.All.Select(k => new Choice<string>(k, PolicyKeys.Name(k)))];

    /// <summary>New certificates, Renewals (Sign-in CA).</summary>
    public static IReadOnlyList<Choice<string>> SignInForChoices { get; } = [.. PolicySignIn.All.Select(v => new Choice<string>(v, PolicySignIn.Name(v)))];

    /// <summary>All profiles, Chosen profiles, No enrollment (Enrollment).</summary>
    public static IReadOnlyList<Choice<string>> ProfileScopeChoices { get; } = [.. PolicyProfiles.Scopes.Select(v => new Choice<string>(v, PolicyProfiles.Name(v)))];

    /// <summary>The hint below For: a CA for new certificates lets the cameras renew with it too.</summary>
    public const string SignInNewHint = "Cameras may renew with it too.";

    /// <summary>The CAs a Sign-in CA policy can name (the Axis device ID, the OADM CA, previous OADM CAs).</summary>
    public IReadOnlyList<Choice<string>> SignInCaChoices { get; }

    [ObservableProperty]
    public partial Choice<string>? SignInCa { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignInForHint))]
    public partial Choice<string> SignInFor { get; set; }

    /// <summary>"Cameras may renew with it too." for new certificates; null for renewals.</summary>
    public string? SignInForHint => SignInFor.Value == PolicySignIn.New ? SignInNewHint : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChosenProfiles))]
    public partial Choice<string> ProfileScope { get; set; }

    /// <summary>The EST server's profiles (at most four: no virtualization needed).</summary>
    public IReadOnlyList<EnrollmentProfileViewModel> ProfileChoices { get; }

    /// <summary>One line when the EST server has no profiles to choose.</summary>
    public string? ProfilesEmptyText => ProfileChoices.Count == 0 ? "The EST server has no profiles." : null;

    /// <summary>The error of the profiles (a group of check boxes: shown below it by the field).</summary>
    public string? ProfilesError => IsChosenProfiles ? Validation[nameof(ProfileChoices)] : null;

    public bool IsSignInCa => Kind.Value == PolicyKinds.SignInCa;

    public bool IsEnrollment => Kind.Value == PolicyKinds.Enrollment;

    /// <summary>An Enrollment policy of chosen profiles: the check boxes show.</summary>
    public bool IsChosenProfiles => IsEnrollment && ProfileScope.Value == PolicyProfiles.Chosen;

    internal void ProfilesEdited()
    {
        if (_applying)
        {
            return;
        }

        Validation.Touch(nameof(ProfileChoices));
        Validation.Validate();
        OnPropertyChanged(nameof(ProfilesError));
    }

    partial void OnProfileScopeChanged(Choice<string> value)
    {
        if (_applying)
        {
            return;
        }

        Validation.Validate();
        OnPropertyChanged(nameof(ProfilesError));
    }

    public static IReadOnlyList<Choice<int>> EapolChoices { get; } = [new(1, "1"), new(2, "2"), new(3, "3")];

    public static IReadOnlyList<Choice<string>> IdentityChoices { get; } =
    [
        new(Dot1xIdentity.Mac, "MAC address"),
        new(Dot1xIdentity.HostName, "Host name"),
        new(Dot1xIdentity.IpAddress, "IP address"),
        new(Dot1xIdentity.IpLastOctet, "Last octet of the IP address"),
        new(Dot1xIdentity.Custom, "Custom"),
    ];

    /// <summary>The CAs of the list (the Certificate authorities card) for the RADIUS server CA.</summary>
    public IReadOnlyList<Choice<string>> RadiusCaChoices { get; }

    /// <summary>"The 802.1X server's certificate is issued by OADM Root CA." below the RADIUS server CA; null when it has none.</summary>
    public string? RadiusCaHint => _list.Dot1xServerIssuer is { Length: > 0 } issuer ? $"The 802.1X server's certificate is issued by {issuer}." : null;

    /// <summary>The CAs that can issue ("Issued by"): the OADM CA and the imported CAs with their key.</summary>
    public IReadOnlyList<Choice<string>> IssuingCaChoices { get; }

    [ObservableProperty]
    public partial Choice<string>? IssuingCa { get; set; }

    /// <summary>A Block policy.</summary>
    public bool IsBlock => Kind.Value == PolicyKinds.Block;

    [ObservableProperty]
    public partial string ValidDays { get; set; }

    [ObservableProperty]
    public partial Choice<string> Key { get; set; }

    [ObservableProperty]
    public partial Choice<int> Eapol { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIdentity))]
    public partial Choice<string> Identity { get; set; }

    [ObservableProperty]
    public partial string CustomIdentity { get; set; }

    /// <summary>The CA the cameras check the RADIUS server with (IEEE 802.1X certificate).</summary>
    [ObservableProperty]
    public partial Choice<string>? RadiusCa { get; set; }

    public bool IsIssuing => PolicyKinds.IsIssuing(Kind.Value);

    public bool IsDot1xCertificate => Kind.Value == PolicyKinds.Dot1xCertificate;

    public bool IsCustomIdentity => Identity.Value == Dot1xIdentity.Custom;

    /// <summary>"Add VLAN policy" / "Edit Building C".</summary>
    public string Title => _existing is null ? $"Add {PolicyKinds.Name(Kind.Value)} policy" : $"Edit {_existing.Name}";

    /// <summary>The kind is chosen for a new policy only (a policy keeps its kind).</summary>
    public bool IsNew => _existing is null;

    /// <summary>The picker of the current choice, made when first asked for (the form itself keeps only the choice).</summary>
    public DevicePickerViewModel Picker => _picker ??= new DevicePickerViewModel(_devices, _choice, _pickerOptions);

    /// <summary>A picker exists (tests: opening a form or typing makes none over the devices).</summary>
    internal bool HasPicker => _picker is not null;

    public IReadOnlyList<Choice<string>> KindChoices { get; }

    public IReadOnlyList<Choice<string>> CaChoices { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVlan), nameof(IsExpiry), nameof(IsAllowCa), nameof(IsServer), nameof(HasDevices), nameof(KindHint), nameof(NamePlaceholder),
        nameof(OffersAllDevices), nameof(IsAllDevices), nameof(ShowsPicker), nameof(ShowsDeviceScope), nameof(Title), nameof(Heading), nameof(KindName), nameof(IsIssuing), nameof(IsServerKind), nameof(IsDot1xCertificate), nameof(IsBlock),
        nameof(IsSignInCa), nameof(IsEnrollment), nameof(IsChosenProfiles), nameof(ProfilesError))]
    public partial Choice<string> Kind { get; set; }

    /// <summary>The Devices choice: "All devices" or "Chosen devices" (the picker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllDevices), nameof(ShowsPicker), nameof(CanSave), nameof(SaveBlockedReason))]
    public partial Choice<bool> Scope { get; set; }

    [ObservableProperty]
    public partial string Vlan { get; set; }

    [ObservableProperty]
    public partial string Days { get; set; }

    [ObservableProperty]
    public partial Choice<string>? Ca { get; set; }

    [ObservableProperty]
    public partial bool OnlyManaged { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TlsHint))]
    public partial Choice<string> MinimumTls { get; set; }

    [ObservableProperty]
    public partial string LargestPacket { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReauthEvery))]
    public partial Choice<bool> Reauth { get; set; }

    [ObservableProperty]
    public partial string ReauthMinutes { get; set; }

    public bool IsVlan => Kind.Value == PolicyKinds.Vlan;

    public bool IsExpiry => Kind.Value == PolicyKinds.ExpiryWarning;

    public bool IsAllowCa => Kind.Value == PolicyKinds.AllowCa;

    public bool IsServer => Kind.Value == PolicyKinds.Dot1xServer;

    /// <summary>The 802.1X or EST server policy: the Certificate field shows (decision 2026-10-25).</summary>
    public bool IsServerKind => PolicyKinds.IsServer(Kind.Value);

    private IReadOnlyList<PolicyCertificateChoice> _certificates;

    /// <summary>
    /// The certificates a server policy can pick: those of the Certificates list with a key that this server can show (the
    /// 802.1X server: ECC keys), and the one it picks now even when it can no longer be used (its error says why).
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<string>> CertificateChoices { get; set; }

    [ObservableProperty]
    public partial Choice<string>? Certificate { get; set; }

    /// <summary>The line below the Certificate field: what it names ("10.0.0.5, oadm-server").</summary>
    public string? CertificateHint => Certificate is { } chosen && _certificates.FirstOrDefault(c => c.Id == chosen.Value) is { Names.Count: > 0 } known
        ? "Names " + string.Join(", ", known.Names)
        : null;

    partial void OnCertificateChanged(Choice<string>? value) => OnPropertyChanged(nameof(CertificateHint));

    /// <summary>The empty Certificate field: what to do first.</summary>
    public const string NoCertificatesText = "No certificate with a key yet. Create one on the Certificates tab.";

    public bool HasNoCertificates => CertificateChoices.Count == 0;

    private static IReadOnlyList<Choice<string>> ChoicesOf(IReadOnlyList<PolicyCertificateChoice> certificates, string kind, string? picked) =>
        [.. certificates.Where(c => c.UsableFor(kind) || c.Id == picked).Select(c => new Choice<string>(c.Id, c.Label))];

    /// <summary>After Create: the choices of the server's answer, the new certificate picked.</summary>
    internal void UseCertificates(IReadOnlyList<PolicyCertificateChoice> certificates, string id)
    {
        _certificates = certificates;
        CertificateChoices = ChoicesOf(certificates, Kind.Value, id);
        OnPropertyChanged(nameof(HasNoCertificates));
        Certificate = CertificateChoices.FirstOrDefault(c => c.Value == id);
        Validation.Touch(nameof(Certificate));
        Validation.Validate();
    }

    public bool IsReauthEvery => Reauth.Value;

    /// <summary>The kind chooses devices.</summary>
    public bool HasDevices => PolicyKinds.HasDevices(Kind.Value);

    /// <summary>The kind may hold "All devices": the Devices choice shows.</summary>
    public bool OffersAllDevices => PolicyKinds.AllowsAllDevices(Kind.Value);

    /// <summary>"All devices" is chosen (for a kind that offers it).</summary>
    public bool IsAllDevices => OffersAllDevices && Scope.Value;

    /// <summary>
    /// The devices are chosen in the picker: a kind with devices and not "All devices". A system policy's devices change too
    /// (decision 2026-10-26): its "All devices" would otherwise overlap every policy added next to it.
    /// </summary>
    public bool ShowsPicker => HasDevices && !IsAllDevices;

    /// <summary>The Devices choice shows (a kind that offers All devices, also its system policy).</summary>
    public bool ShowsDeviceScope => OffersAllDevices;

    /// <summary>What the kind does (below the kind).</summary>
    public string? KindHint => PolicyKinds.Description(Kind.Value);

    public string NamePlaceholder => Kind.Value switch
    {
        PolicyKinds.AllowCa => "e.g. OADM CA",
        PolicyKinds.EstServer => "e.g. EST server",
        PolicyKinds.Block => "e.g. Storage room",
        PolicyKinds.ExpiryWarning => "e.g. All devices",
        PolicyKinds.Dot1xServer => "e.g. 802.1X server",
        PolicyKinds.HttpsCertificate => "e.g. HTTPS 2 years",
        PolicyKinds.Dot1xCertificate => "e.g. IEEE 802.1X Building B",

        PolicyKinds.SignInCa => "e.g. Axis device ID",
        _ => "e.g. Building C",
    };

    /// <summary>"Only for old devices ..." for TLS 1.0 and 1.1; null otherwise.</summary>
    public string? TlsHint => PolicyRules.MinimumIndex(MinimumTls.Value) is < 2 ? OldTlsHint : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SaveBlockedReason), nameof(IsEditable), nameof(CanDelete))]
    public partial bool IsBusy { get; set; }

    partial void OnReadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDelete));
    }

    public bool CanSave => !IsBusy && !ReadOnly && IsFormValid && (IsNew || IsDirty);

    /// <summary>Why Save is off (its tooltip).</summary>
    public string? SaveBlockedReason => FormError ?? (!IsNew && !IsDirty ? NoChangesText : null);

    /// <summary>
    /// What the policy does, in the sentence of its row (<see cref="PolicyStore.Summary"/>), from the fields as they are now;
    /// null while the form cannot say it yet.
    /// </summary>
    public string? Preview
    {
        get
        {
            if (_applying || Kind is null)
            {
                return null;
            }

            var names = _list.Cas.Concat(_list.SignInCas).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            try
            {
                return PolicyStore.Summary(ToPolicy(), names);
            }
#pragma warning disable CA1031 // A half-filled form has no sentence yet.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }
    }

    /// <summary>The policy of the editor (the fields of its kind).</summary>
    public PkiPolicy ToPolicy()
    {
        var choice = _choice;
        return PolicyRules.Normalize(new PkiPolicy
        {
            Id = _existing?.Id ?? Guid.Empty,
            Name = Name.Trim(),
            Kind = Kind.Value,
            Devices = IsAllDevices ? PolicyDevices.Everything : new PolicyDevices
            {
                ThisServer = choice.ThisServer,
                SiteIds = choice.SiteIds,
                Tags = choice.TagIds,
                Serials = choice.Serials,
                ExceptSerials = choice.ExceptSerials,
            },
            Vlan = ParseInt(Vlan),
            Days = ParseInt(Days),
            CaId = IsIssuing ? IssuingCa?.Value : IsSignInCa ? SignInCa?.Value : Ca?.Value,
            CertificateId = IsServerKind ? Certificate?.Value : null,
            OnlyManaged = OnlyManaged,
            MinimumTls = MinimumTls.Value,
            LargestEapPacket = ParseInt(LargestPacket),
            ReauthMinutes = Reauth.Value ? ParseInt(ReauthMinutes) : null,
            ValidDays = ParseInt(ValidDays),
            Key = Key.Value,
            EapolVersion = Eapol.Value,
            Identity = Identity.Value,
            CustomIdentity = CustomIdentity,
            RadiusCaId = RadiusCa?.Value,
            SignInFor = SignInFor.Value,
            ProfileScope = ProfileScope.Value,
            Profiles = [.. ProfileChoices.Where(p => p.IsChecked).Select(p => p.Name)],
            SavedBy = _existing?.SavedBy,
            Revision = _existing?.Revision ?? 0, // the revision this form read: PKI refuses a save over a later change
        });
    }

    /// <summary>Save: true when the server stored the policy.</summary>
    public async Task<bool> SaveAsync()
    {
        Validation.ShowAll();
        OnPropertyChanged(nameof(ProfilesError));
        OnPropertyChanged(nameof(DevicesError));
        if (!IsFormValid)
        {
            return false;
        }

        var policy = ToPolicy();
        if (PolicyRules.Validate(policy, []) is { } error)
        {
            await ShowErrorAsync(error).ConfigureAwait(true);
            return false;
        }

        IsBusy = true;
        try
        {
            var reply = await _owner.SaveAsync(policy, this).ConfigureAwait(true);
            if (reply.Error is { } refused)
            {
                await ShowErrorAsync(refused).ConfigureAwait(true);
                return false;
            }

            return true;
        }
#pragma warning disable CA1031 // A refusal of the server without a field: in a message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(Title, ServerErrors.Message(ex)).ConfigureAwait(true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A refusal below the field it names; one of a field the form does not show (the kind, which Create preset) or of the
    /// whole policy (changed by someone else) in a message.
    /// </summary>
    private async Task ShowErrorAsync(PolicyFieldError error)
    {
        var property = error.Field switch
        {
            PolicyFields.Name when ShowsName => nameof(Name),
            PolicyFields.Vlan => nameof(Vlan),
            PolicyFields.Days => nameof(Days),
            PolicyFields.Ca => IsIssuing ? nameof(IssuingCa) : IsSignInCa ? nameof(SignInCa) : nameof(Ca),
            PolicyFields.Certificate => nameof(Certificate),
            PolicyFields.SignInFor => nameof(SignInFor),
            PolicyFields.ProfileScope => nameof(ProfileScope),
            PolicyFields.Profiles => nameof(ProfileChoices),
            PolicyFields.ValidDays => nameof(ValidDays),
            PolicyFields.Key => nameof(Key),
            PolicyFields.EapolVersion => nameof(Eapol),
            PolicyFields.Identity => nameof(Identity),
            PolicyFields.CustomIdentity => nameof(CustomIdentity),
            PolicyFields.RadiusCa => nameof(RadiusCa),
            PolicyFields.LargestEapPacket => nameof(LargestPacket),
            PolicyFields.ReauthMinutes => nameof(ReauthMinutes),
            PolicyFields.MinimumTls => nameof(MinimumTls),
            PolicyFields.Devices when IsAllDevices => nameof(Scope), // the picker is hidden: below the Devices choice
            PolicyFields.Devices when ShowsPicker => DevicesField, // below the device choice until the choice changes
            _ => null,
        };
        if (property is null)
        {
            await _ctx.ShowMessageAsync(Title, error.Message).ConfigureAwait(true);
            return;
        }

        Validation.SetServerError(property, error.Message);
        OnPropertyChanged(nameof(ProfilesError));
        OnPropertyChanged(nameof(DevicesError));
    }

    partial void OnKindChanged(Choice<string> value)
    {
        if (_applying)
        {
            return;
        }

        Validation.Validate();
        OnPropertyChanged(nameof(ProfilesError));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(ProfilesError));
        OnPropertyChanged(nameof(DevicesError));
    }

    internal static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) ? value : null;
}
