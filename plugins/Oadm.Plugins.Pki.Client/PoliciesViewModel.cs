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

/// <summary>One row of the Policies card: name, kind, summary, devices and the warning of a later overlap.</summary>
public sealed class PolicyRowViewModel(PolicyRow row, string devicesText)
{
    public PolicyRow Row { get; } = row ?? throw new ArgumentNullException(nameof(row));

    public PkiPolicy Source => Row.Policy;

    public string Name => Source.Name;

    public string Kind => Row.KindName;

    public string Summary => Row.Summary;

    /// <summary>"Site Building B, Tag PTZ, 3 devices, except 1"; "-" for a kind without devices.</summary>
    public string Devices { get; } = PolicyKinds.HasDevices(row.Policy.Kind) ? devicesText : "-";

    public string? Warning => Row.Warning;

    public bool HasWarning => !string.IsNullOrEmpty(Row.Warning);

    public string Group => Row.Group;
}

/// <summary>One chip of the filter (All, Certificates, 802.1X, Revoke).</summary>
public sealed partial class PolicyFilterViewModel(string group) : ObservableObject
{
    public string Group { get; } = group;

    public string Label { get; } = PolicyGroups.Label(group);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>What the Policies card asks of the view: the policy editor.</summary>
public interface IPolicyDialogs
{
    /// <summary>Shows the editor until it saved (true) or was closed.</summary>
    Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor);
}

/// <summary>
/// The Policies card of the PKI page (docs/spec/pki.md "Policies"): every policy in age order, a filter by group, Add policy
/// in the header, Edit and Delete per row. Changing needs "Manage the certificate authority" and all devices; other users
/// see the card read only; users limited to a device group do not see it (the server sends them none).
/// </summary>
public sealed partial class PoliciesViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private PolicyList _list = new();
    private int _loads;

    public PoliciesViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Filters = [.. PolicyGroups.Shown.Select(g => new PolicyFilterViewModel(g) { IsSelected = g == PolicyGroups.All })];
    }

    /// <summary>The editor (the page view).</summary>
    public IPolicyDialogs? Dialogs { get; set; }

    /// <summary>The filter chips (only groups that have kinds now).</summary>
    public IReadOnlyList<PolicyFilterViewModel> Filters { get; }

    /// <summary>The rows of the chosen group (at most 200 policies; still a DataGrid, one reset per change).</summary>
    public RangeObservableCollection<PolicyRowViewModel> Rows { get; } = [];

    /// <summary>The list as the server answered it.</summary>
    public PolicyList List => _list;

    /// <summary>The chosen group of the filter.</summary>
    [ObservableProperty]
    public partial string Group { get; set; } = PolicyGroups.All;

    /// <summary>The first read (the overlay over the card).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A later read (the progress row; the rows stay).</summary>
    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    /// <summary>A read or a delete failed (below the table).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The card shows: users with all devices (the policies are server-wide).</summary>
    public bool IsShown => _ctx.HasAllDevices;

    /// <summary>The user may change the policies (the server's answer, and the role now).</summary>
    public bool MayManage => _list.MayManage && _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>One line when the chosen group has no policy.</summary>
    public string? EmptyText => Rows.Count > 0 || IsLoading ? null
        : _list.Rows.Count == 0 ? (MayManage ? "No policies yet. Add one with Add policy." : "No policies yet.")
        : "No policies of this group.";

    /// <summary>Why Add policy is off.</summary>
    public string? AddTip => _list.Rows.Count >= PolicyLimits.MaxPolicies ? PolicyTexts.TooMany : null;

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
            Apply(PkiJson.Deserialize<PolicyList>(await _ctx.InvokeAsync(PolicyMethods.GetPolicies, null, ct).ConfigureAwait(true)));
            Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
            IsReloading = false;
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    /// <summary>Shows a list of the server (also used by tests and the screenshots).</summary>
    public void Apply(PolicyList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = IsShown ? list : new PolicyList();
        Refilter();
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(AddTip));
        AddCommand.NotifyCanExecuteChanged();
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(EmptyText));
        AddCommand.NotifyCanExecuteChanged();
        if (!IsShown && _list.Rows.Count > 0)
        {
            Apply(new PolicyList()); // nothing server-wide stays for a user limited to a group
        }
    }

    [RelayCommand]
    private void SelectFilter(string? group)
    {
        Group = group is not null && PolicyGroups.Shown.Contains(group) ? group : PolicyGroups.All;
    }

    partial void OnGroupChanged(string value)
    {
        foreach (var filter in Filters)
        {
            filter.IsSelected = filter.Group == value;
        }

        Refilter();
    }

    private void Refilter()
    {
        var names = SiteNames();
        Rows.ReplaceAll(_list.Rows.Where(r => Group == PolicyGroups.All || r.Group == Group)
            .Select(r => new PolicyRowViewModel(r, PolicyTexts.DevicesText(r.Policy.Devices, names))));
        OnPropertyChanged(nameof(EmptyText));
    }

    private Dictionary<Guid, string> SiteNames() => _list.Sites.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);

    /// <summary>The editor for a new policy (null) or <paramref name="existing"/>.</summary>
    public PolicyEditorViewModel NewEditor(PkiPolicy? existing) => new(_ctx, this, existing, _list, _ctx.Devices);

    private bool CanAdd() => MayManage && _list.Rows.Count < PolicyLimits.MaxPolicies;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (Dialogs is { } dialogs)
        {
            await dialogs.ShowPolicyAsync(NewEditor(null)).ConfigureAwait(true);
        }
    }

    /// <summary>Edit of a row (also a double click).</summary>
    [RelayCommand]
    private async Task EditRowAsync(PolicyRowViewModel? row)
    {
        if (row is not null && MayManage && Dialogs is { } dialogs)
        {
            await dialogs.ShowPolicyAsync(NewEditor(row.Source)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteRowAsync(PolicyRowViewModel? row)
    {
        if (row is null || !MayManage)
        {
            return;
        }

        var message = row.Source.Kind switch
        {
            PolicyKinds.Revoke => $"Delete the policy {row.Name}? Revoked certificates stay revoked: the devices need new certificates.",
            PolicyKinds.Dot1xServer => $"Delete the policy {row.Name}? The 802.1X server then uses the default TLS settings.",
            PolicyKinds.AllowCa => $"Delete the policy {row.Name}? Certificates of this CA no longer pass the 802.1X server.",
            _ => $"Delete the policy {row.Name}?",
        };
        if (!await _ctx.ConfirmAsync("Delete policy", message, "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var reply = PkiJson.Deserialize<PolicySaveReply>(await _ctx.InvokeAsync(PolicyMethods.DeletePolicy, PkiJson.Serialize(new PolicyRef(row.Source.Id)), CancellationToken.None)
                .ConfigureAwait(true));
            if (reply.List is { } list)
            {
                Apply(list);
            }

            Error = reply.Error?.Message;
        }
#pragma warning disable CA1031 // Shown below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
    }

    /// <summary>Saves <paramref name="policy"/> (the editor); the list after it, or the error that refused it.</summary>
    internal async Task<PolicySaveReply> SaveAsync(PkiPolicy policy)
    {
        var reply = PkiJson.Deserialize<PolicySaveReply>(await _ctx.InvokeAsync(PolicyMethods.SavePolicy, PkiJson.Serialize(policy), CancellationToken.None).ConfigureAwait(true));
        if (reply.List is { } list)
        {
            Apply(list);
        }

        return reply;
    }

    /// <summary>The IEEE 802.1X certificates a revoke policy with <paramref name="devices"/> revokes now.</summary>
    internal async Task<int> CountRevocationsAsync(PolicyDevices devices) =>
        PkiJson.Deserialize<PolicyRevocationCount>(await _ctx.InvokeAsync(PolicyMethods.CountRevocations, PkiJson.Serialize(devices), CancellationToken.None).ConfigureAwait(true)).Count;
}

/// <summary>One cipher suite of the 802.1X server policy: a check box with the suite's name.</summary>
public sealed partial class CipherSuiteViewModel(PolicyCipherSuite suite, PolicyEditorViewModel owner) : ObservableObject
{
    public int Id => suite.Id;

    public string Name => suite.Name;

    /// <summary>An old CBC suite: listed only while the minimum is TLS 1.0 or 1.1.</summary>
    public bool IsLegacy => suite.Legacy;

    /// <summary>The server's certificate can use it and the minimum version allows it.</summary>
    public bool IsUsable(string? minimumTls) =>
        suite.Usable && (suite.Tls13 || (PolicyRules.MinimumIndex(minimumTls) is { } min && (suite.Legacy ? min < 2 : min <= 2)));

    /// <summary>The server's certificate cannot use it: the hint below the suite's name; null otherwise.</summary>
    public string? KeyHint => suite.Usable ? null : PolicyEditorViewModel.UnusableSuiteHint;

    public bool HasKeyHint => !suite.Usable;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Listed for the chosen minimum.</summary>
    [ObservableProperty]
    public partial bool IsListed { get; set; } = !suite.Legacy;

    partial void OnIsCheckedChanged(bool value) => owner.CipherSuitesEdited();
}

/// <summary>
/// The policy editor (docs/spec/pki.md "Policies"): one dialog with the name, the kind (a new policy only; Kind and VLAN ID
/// on one row) and the kind's own fields, and the shared device picker in its group mode where the kind has devices (sites
/// with "This server", tags, single devices, Except; Revoke without tags). Save sends the policy; a Revoke policy that is
/// new or whose devices changed asks first with the count of certificates it revokes. Errors directly below the fields.
/// </summary>
public sealed partial class PolicyEditorViewModel : ValidatingViewModel
{
    public const string OldTlsHint = "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure.";
    public const string UnusableSuiteHint = "Not usable with the server certificate.";
    public const string ReauthEvery = "Every N minutes";
    public const string OnlyManagedText = PolicyTexts.OnlyManaged;

    public static IReadOnlyList<Choice<string>> TlsVersions { get; } = [.. PolicyRules.TlsVersions.Select(v => new Choice<string>(v, "TLS " + v))];

    public static IReadOnlyList<Choice<bool>> ReauthChoices { get; } = [new(false, PolicyTexts.SwitchDecides), new(true, ReauthEvery)];

    private readonly ICorePluginClientContext _ctx;
    private readonly PoliciesViewModel _owner;
    private readonly PkiPolicy? _existing;
    private readonly PolicyList _list;
    private bool _applying = true;

    public PolicyEditorViewModel(ICorePluginClientContext ctx, PoliciesViewModel owner, PkiPolicy? existing, PolicyList list, IEnumerable<IDeviceInfo> devices)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _existing = existing;
        var hasServer = list.Rows.Any(r => r.Policy.Kind == PolicyKinds.Dot1xServer && r.Policy.Id != existing?.Id);
        KindChoices = existing is { } kept
            ? [new Choice<string>(kept.Kind, PolicyKinds.Name(kept.Kind))]
            : [.. PolicyKinds.All.Where(k => !(PolicyKinds.OneOnly(k) && hasServer)).Select(k => new Choice<string>(k, PolicyKinds.Name(k)))];
        CaChoices = [.. list.Cas.Select(c => new Choice<string>(c.Id, CaLabel(c)))];
        CipherSuites = [.. list.CipherSuites.Select(s => new CipherSuiteViewModel(s, this))];

        var policy = existing ?? new PkiPolicy();
        Name = policy.Name;
        Kind = KindChoices.FirstOrDefault(k => k.Value == policy.Kind) ?? KindChoices.FirstOrDefault(k => k.Value == PolicyKinds.Vlan) ?? KindChoices[0];
        Vlan = policy.Vlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Days = (policy.Days ?? PolicyLimits.DefaultExpiryDays).ToString(CultureInfo.InvariantCulture);
        // A new Allow CA starts with the first CA no Allow CA policy names yet.
        var allowed = list.Rows.Where(r => r.Policy.Kind == PolicyKinds.AllowCa).Select(r => r.Policy.CaId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Ca = CaChoices.FirstOrDefault(c => string.Equals(c.Value, policy.CaId, StringComparison.OrdinalIgnoreCase))
            ?? CaChoices.FirstOrDefault(c => !allowed.Contains(c.Value)) ?? (CaChoices.Count > 0 ? CaChoices[0] : null);
        OnlyManaged = policy.OnlyManaged ?? true;
        var server = existing?.Kind == PolicyKinds.Dot1xServer ? existing : null;
        MinimumTls = TlsVersions.FirstOrDefault(v => v.Value == server?.MinimumTls) ?? TlsVersions[PolicyRules.MinimumIndex(PolicyRules.DefaultMinimumTls) ?? 2];
        var checkedIds = (server?.CipherSuites ?? PolicyRules.DefaultSuites(list.CipherSuites, MinimumTls.Value)).ToHashSet();
        foreach (var suite in CipherSuites)
        {
            suite.IsChecked = checkedIds.Contains(suite.Id);
        }

        UpdateListed(lowered: false);
        LargestPacket = (server?.LargestEapPacket ?? PolicyRules.DefaultEapPacket).ToString("N0", CultureInfo.InvariantCulture);
        Reauth = ReauthChoices[server?.ReauthMinutes is null ? 0 : 1];
        ReauthMinutes = (server?.ReauthMinutes ?? 60).ToString(CultureInfo.InvariantCulture);

        var chosen = policy.Devices ?? new PolicyDevices();
        var tags = list.Tags.Concat(chosen.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var options = new DeviceGroupPickerOptions
        {
            Sites = [new DevicePickerSite(null, PolicyTexts.ThisServer), .. list.Sites.Select(s => new DevicePickerSite(s.Id, s.Name))],

            // Plugins know tags by name: the name is the id.
            Tags = [.. tags.Select(t => new DevicePickerTag(t, t))],
        };
        Picker = new DevicePickerViewModel(devices ?? [], new DeviceGroupChoice(chosen.ThisServer, chosen.SiteIds ?? [], chosen.Tags ?? [], chosen.Serials ?? [], chosen.ExceptSerials ?? []), options);
        Picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DevicePickerViewModel.CoveredCount) or nameof(DevicePickerViewModel.Summary))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(SaveBlockedReason));
                CheckTags();
            }
        };
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
            .Rule(nameof(LargestPacket), () => !IsServer || ParseInt(LargestPacket) is >= PolicyRules.MinEapPacket and <= PolicyRules.MaxEapPacket ? null : PolicyRules.EapPacketRange)
            .Rule(nameof(ReauthMinutes), () => !IsServer || !Reauth.Value || ParseInt(ReauthMinutes) is >= PolicyLimits.MinReauthMinutes and <= PolicyLimits.MaxReauthMinutes
                ? null : PolicyTexts.ReauthRange)
            .Rule(nameof(CipherSuites), () => !IsServer || CipherSuites.Any(s => s.IsListed && s.IsChecked && s.IsUsable(MinimumTls.Value)) ? null : PolicyRules.CheckCipherSuite);
        Validation.Validate();
    }

    /// <summary>"Add policy" / "Edit Building C".</summary>
    public string Title => _existing is null ? "Add policy" : $"Edit {_existing.Name}";

    /// <summary>The kind is chosen for a new policy only (a policy keeps its kind).</summary>
    public bool IsNew => _existing is null;

    public DevicePickerViewModel Picker { get; }

    public IReadOnlyList<Choice<string>> KindChoices { get; }

    public IReadOnlyList<Choice<string>> CaChoices { get; }

    public IReadOnlyList<CipherSuiteViewModel> CipherSuites { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVlan), nameof(IsExpiry), nameof(IsAllowCa), nameof(IsServer), nameof(HasDevices), nameof(KindHint), nameof(NamePlaceholder))]
    public partial Choice<string> Kind { get; set; }

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

    public bool IsReauthEvery => Reauth.Value;

    /// <summary>The kind chooses devices: the device picker shows.</summary>
    public bool HasDevices => PolicyKinds.HasDevices(Kind.Value);

    /// <summary>What the kind does (below the kind).</summary>
    public string? KindHint => PolicyKinds.Description(Kind.Value);

    public string NamePlaceholder => Kind.Value switch
    {
        PolicyKinds.AllowCa => "e.g. OADM CA",
        PolicyKinds.ExpiryWarning => "e.g. All devices",
        PolicyKinds.Dot1xServer => "e.g. 802.1X server",
        _ => "e.g. Building C",
    };

    /// <summary>"Only for old devices ..." for TLS 1.0 and 1.1; null otherwise.</summary>
    public string? TlsHint => PolicyRules.MinimumIndex(MinimumTls.Value) is < 2 ? OldTlsHint : null;

    /// <summary>The error of the cipher suites (a group of check boxes: shown below it by the field).</summary>
    public string? CipherSuitesError => IsServer ? Validation[nameof(CipherSuites)] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SaveBlockedReason))]
    public partial bool IsBusy { get; set; }

    /// <summary>Asks before a revoke policy revokes (the view sets it, owned by the editor window); null: the page's question.</summary>
    public Func<string, string, Task<bool>>? ConfirmRevoke { get; set; }

    public bool CanSave => !IsBusy && IsFormValid && (!HasDevices || HasChosenDevices);

    private bool HasChosenDevices => Picker is not null && !Picker.GroupChoice.IsEmpty;

    /// <summary>Why Save is off (its tooltip).</summary>
    public string? SaveBlockedReason => FormError ?? (!HasDevices || HasChosenDevices ? null : PolicyTexts.ChooseDevices);

    /// <summary>The policy of the editor (the fields of its kind).</summary>
    public PkiPolicy ToPolicy()
    {
        var choice = Picker.GroupChoice;
        return PolicyRules.Normalize(new PkiPolicy
        {
            Id = _existing?.Id ?? Guid.Empty,
            Name = Name.Trim(),
            Kind = Kind.Value,
            Devices = new PolicyDevices
            {
                ThisServer = choice.ThisServer,
                SiteIds = choice.SiteIds,
                Tags = choice.TagIds,
                Serials = choice.Serials,
                ExceptSerials = choice.ExceptSerials,
            },
            Vlan = ParseInt(Vlan),
            Days = ParseInt(Days),
            CaId = Ca?.Value,
            OnlyManaged = OnlyManaged,
            MinimumTls = MinimumTls.Value,
            CipherSuites = [.. CipherSuites.Where(s => s.IsListed && s.IsChecked).Select(s => s.Id)],
            LargestEapPacket = ParseInt(LargestPacket),
            ReauthMinutes = Reauth.Value ? ParseInt(ReauthMinutes) : null,
            SavedBy = _existing?.SavedBy,
        });
    }

    /// <summary>Save (the window's accept): true when the server stored the policy and the window may close.</summary>
    public async Task<bool> SaveAsync()
    {
        Validation.ShowAll();
        OnPropertyChanged(nameof(CipherSuitesError));
        Picker.ErrorText = null;
        if (!IsFormValid)
        {
            return false;
        }

        var policy = ToPolicy();
        if (PolicyRules.Validate(policy, _list.CipherSuites) is { } error)
        {
            ShowError(error);
            return false;
        }

        IsBusy = true;
        try
        {
            if (policy.Kind == PolicyKinds.Revoke && RevokeChanged(policy))
            {
                var count = await _owner.CountRevocationsAsync(policy.Devices!).ConfigureAwait(true);
                var question = count == 0
                    ? "These devices have no IEEE 802.1X certificate now. Every new one is revoked while the policy exists."
                    : string.Create(CultureInfo.CurrentCulture, $"Saving revokes {count:N0} IEEE 802.1X {(count == 1 ? "certificate" : "certificates")} of these devices, and every new one while the policy exists. This cannot be undone.");
                var confirmed = ConfirmRevoke is { } confirm
                    ? await confirm(Title, question).ConfigureAwait(true)
                    : await _ctx.ConfirmAsync(Title, question, "Revoke").ConfigureAwait(true);
                if (!confirmed)
                {
                    return false;
                }
            }

            var reply = await _owner.SaveAsync(policy).ConfigureAwait(true);
            if (reply.Error is { } refused)
            {
                ShowError(refused);
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

    internal void CipherSuitesEdited()
    {
        if (_applying)
        {
            return;
        }

        Validation.Touch(nameof(CipherSuites));
        OnPropertyChanged(nameof(CipherSuitesError));
    }

    /// <summary>Reset to default: every suite listed for the chosen minimum checked (the secure default set).</summary>
    [RelayCommand]
    private void ResetCipherSuites()
    {
        _applying = true;
        try
        {
            foreach (var suite in CipherSuites)
            {
                suite.IsChecked = suite.IsListed;
            }
        }
        finally
        {
            _applying = false;
        }

        CipherSuitesEdited();
    }

    /// <summary>A new revoke policy, or one whose devices changed: it revokes with the save.</summary>
    private bool RevokeChanged(PkiPolicy policy) =>
        _existing is null || _existing.Kind != PolicyKinds.Revoke || !(_existing.Devices ?? new PolicyDevices()).SameAs(policy.Devices);

    private void ShowError(PolicyFieldError error)
    {
        var property = error.Field switch
        {
            PolicyFields.Name => nameof(Name),
            PolicyFields.Kind => nameof(Kind),
            PolicyFields.Vlan => nameof(Vlan),
            PolicyFields.Days => nameof(Days),
            PolicyFields.Ca => nameof(Ca),
            PolicyFields.LargestEapPacket => nameof(LargestPacket),
            PolicyFields.ReauthMinutes => nameof(ReauthMinutes),
            PolicyFields.MinimumTls => nameof(MinimumTls),
            PolicyFields.CipherSuites => nameof(CipherSuites),
            _ => null,
        };
        if (property is null)
        {
            Picker.ErrorText = error.Message; // devices: below the device choice
            return;
        }

        Validation.SetServerError(property, error.Message);
        OnPropertyChanged(nameof(CipherSuitesError));
    }

    /// <summary>Revoke policies choose devices and sites only: said below the device choice at once.</summary>
    private void CheckTags()
    {
        if (Picker is null)
        {
            return;
        }

        if (PolicyKinds.NoTags(Kind.Value) && Picker.GroupChoice.TagIds.Count > 0)
        {
            Picker.ErrorText = PolicyTexts.RevokeNoTags;
        }
        else if (Picker.ErrorText == PolicyTexts.RevokeNoTags)
        {
            Picker.ErrorText = null;
        }
    }

    partial void OnKindChanged(Choice<string> value)
    {
        if (_applying)
        {
            return;
        }

        Validation.Validate();
        OnPropertyChanged(nameof(CipherSuitesError));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        CheckTags();
    }

    partial void OnMinimumTlsChanged(Choice<string> oldValue, Choice<string> newValue)
    {
        if (!_applying && oldValue is not null)
        {
            UpdateListed(lowered: PolicyRules.MinimumIndex(oldValue.Value) >= 2 && PolicyRules.MinimumIndex(newValue.Value) < 2);
            CipherSuitesEdited();
        }
    }

    /// <summary>The old suites show only below TLS 1.2 and are checked when the minimum is lowered there.</summary>
    private void UpdateListed(bool lowered)
    {
        var legacy = PolicyRules.MinimumIndex(MinimumTls.Value) is < 2;
        foreach (var suite in CipherSuites.Where(s => s.IsLegacy))
        {
            suite.IsListed = legacy;
            if (lowered)
            {
                suite.IsChecked = true;
            }
        }
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(CipherSuitesError));
    }

    /// <summary>"OADM CA (with 2 previous CAs)", "Axis device ID", "Contoso Root".</summary>
    private static string CaLabel(PolicyCaChoice ca) => ca.PreviousCount switch
    {
        0 => ca.Name,
        1 => ca.Name + " (with its previous CA)",
        _ => string.Create(CultureInfo.CurrentCulture, $"{ca.Name} (with its {ca.PreviousCount:N0} previous CAs)"),
    };

    internal static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) ? value : null;
}
