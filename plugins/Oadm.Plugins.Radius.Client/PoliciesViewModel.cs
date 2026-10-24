using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Client;

/// <summary>The texts of the policies (one phrase per concept).</summary>
public static class PolicyTexts
{
    public const string ThisServer = "This server";
    public const string Block = "Block";
    public const string Revoke = "Revoke certificates";
    public const string Vlan = "VLAN";

    /// <summary>"VLAN 100", "Block", "Revoke certificates".</summary>
    public static string KindText(RadiusPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Kind switch
        {
            PolicyKinds.Block => Block,
            PolicyKinds.Revoke => Revoke,
            _ => string.Create(CultureInfo.InvariantCulture, $"VLAN {policy.Vlan}"),
        };
    }

    /// <summary>"This server, site Building B, tag Lobby, 3 devices, except 1".</summary>
    public static string DevicesText(PolicyDevices devices, IReadOnlyDictionary<Guid, string> siteNames)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(siteNames);
        var parts = new List<string>();
        if (devices.ThisServer)
        {
            parts.Add(ThisServer);
        }

        parts.AddRange((devices.SiteIds ?? []).Select(id => "Site " + (siteNames.TryGetValue(id, out var name) ? name : "(removed)")));
        parts.AddRange((devices.Tags ?? []).Select(t => "Tag " + t));
        var serials = devices.Serials?.Count ?? 0;
        if (serials > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{serials:N0} {(serials == 1 ? "device" : "devices")}"));
        }

        var excepts = devices.ExceptSerials?.Count ?? 0;
        if (excepts > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"except {excepts:N0}"));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>One row of the Policies card.</summary>
public sealed class PolicyRowViewModel(RadiusPolicy source, string devicesText)
{
    public RadiusPolicy Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    public string Name => Source.Name;

    public string Kind => PolicyTexts.KindText(Source);

    public bool IsBlock => Source.Kind is PolicyKinds.Block or PolicyKinds.Revoke;

    public string Devices { get; } = devicesText;
}

/// <summary>What the Policies card asks of the view: the policy editor (the shared device picker with the form on top).</summary>
public interface IPolicyDialogs
{
    /// <summary>Shows the editor until it saved (true) or was closed.</summary>
    Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor);
}

/// <summary>
/// The Policies card (docs/spec/radius-server.md "Policies"): the policies in order (Add policy, Edit, Delete red and
/// confirmed, Move up, Move down; each change saves the whole list at once) and the Default VLAN while a VLAN policy
/// exists. Users limited to a group read none.
/// </summary>
public sealed partial class PoliciesViewModel : ValidatingViewModel
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Func<bool> _mayManage;
    private readonly Func<bool> _mayRevoke;
    private PolicySettings _saved = new();
    private IReadOnlyList<SiteRef> _sites = [];
    private IReadOnlyList<string> _tagChoices = [];

    /// <param name="mayRevoke">The role also has PKI's "Manage the certificate authority" (revoke policies).</param>
    public PoliciesViewModel(ICorePluginClientContext ctx, Func<bool> mayManage, Func<bool> mayRevoke)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        _mayRevoke = mayRevoke ?? throw new ArgumentNullException(nameof(mayRevoke));
        Validation.Rule(nameof(DefaultVlan), () => DefaultVlan.Trim().Length == 0 || SettingsViewModel.ParseInt(DefaultVlan) is >= RadiusLimits.MinVlan and <= RadiusLimits.MaxVlan
            ? null : RadiusRules.VlanRange);
        Validation.Validate();
    }

    /// <summary>The policy editor (the page view).</summary>
    public IPolicyDialogs? Dialogs { get; set; }

    /// <summary>At most 100 policies: no virtualization concern, still a DataGrid.</summary>
    public RangeObservableCollection<PolicyRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial PolicyRowViewModel? Selected { get; set; }

    /// <summary>The VLAN of the devices no VLAN policy matches; empty = none.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDefaultVlanCommand))]
    public partial string DefaultVlan { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(EditCommand), nameof(DeleteCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(SaveDefaultVlanCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>A change the server refused that belongs to no field (below the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool MayManage => _mayManage();

    /// <summary>The Default VLAN shows while a VLAN policy exists (VLANs stay off before).</summary>
    public bool HasVlanPolicies => _saved.Policies.Any(p => p.Kind == PolicyKinds.Vlan);

    public string? EmptyText => Rows.Count > 0 ? null
        : MayManage ? "No policies yet. Add one to give devices a VLAN or to block them." : "No policies yet.";

    /// <summary>Why Add policy is off.</summary>
    public string? AddTip => Rows.Count >= RadiusLimits.MaxPolicies ? RadiusRules.TooManyPolicies : null;

    public PolicySettings Saved => _saved;

    /// <summary>The sites of the server (the editor's Sites and the row texts).</summary>
    public void SetSites(IReadOnlyList<SiteRef> sites)
    {
        _sites = sites ?? [];
        Apply(_saved);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var policies = RadiusJson.Deserialize<PolicySettings>(await _ctx.InvokeAsync(RadiusMethods.GetPolicies, null, ct).ConfigureAwait(true));
        Apply(policies);
    }

    /// <summary>Replaces the rows (one reset), keeps the selected policy; the tag choices: the server's, the policies' and the devices'.</summary>
    public void Apply(PolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _saved = settings with { Policies = settings.Policies ?? [] };
        if (settings.TagChoices is { Count: > 0 } choices)
        {
            _tagChoices = [.. choices.Select(t => t.Name)];
        }

        var names = SiteNames();
        var selected = Selected?.Source.Id;
        Rows.ReplaceAll(_saved.Policies.Select(p => new PolicyRowViewModel(p, PolicyTexts.DevicesText(p.Devices ?? new PolicyDevices(), names))));
        Selected = selected is { } id ? Rows.FirstOrDefault(r => r.Source.Id == id) : null;
        DefaultVlan = _saved.DefaultVlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Validation.Reset();
        OnPropertyChanged(nameof(HasVlanPolicies));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(AddTip));
        AddCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A user limited to a group: nothing server-wide stays.</summary>
    public void Clear()
    {
        _tagChoices = [];
        Apply(new PolicySettings());
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(EmptyText));
        AddCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SaveDefaultVlanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The editor for a new policy (null) or <paramref name="existing"/>.</summary>
    public PolicyEditorViewModel NewEditor(RadiusPolicy? existing)
    {
        var tags = _tagChoices.Concat(_ctx.Devices.SelectMany(d => d.Tags)).Concat(_saved.Policies.SelectMany(p => p.Devices?.Tags ?? []))
            .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var others = _saved.Policies.Where(p => p.Id != existing?.Id).Select(p => p.Name).ToList();
        return new PolicyEditorViewModel(_ctx, this, existing, _ctx.Devices, _sites, tags, others, _mayRevoke());
    }

    /// <summary>
    /// Saves <paramref name="policy"/> (new: last; else in its place) with the others; throws the server's refusal. A revoke
    /// policy revokes with this save.
    /// </summary>
    internal async Task SaveOneAsync(RadiusPolicy policy)
    {
        var list = _saved.Policies.ToList();
        var index = list.FindIndex(p => p.Id == policy.Id && policy.Id != Guid.Empty);
        if (index >= 0)
        {
            list[index] = policy;
        }
        else
        {
            list.Add(policy);
        }

        Apply(await SaveListAsync(_saved with { Policies = list }).ConfigureAwait(true));
        Selected = Rows.FirstOrDefault(r => r.Name == policy.Name.Trim()) ?? Selected;
    }

    /// <summary>The IEEE 802.1X certificates a revoke policy with <paramref name="devices"/> revokes now.</summary>
    internal async Task<int> CountRevocationsAsync(PolicyDevices devices) =>
        RadiusJson.Deserialize<RevocationCount>(await _ctx.InvokeAsync(RadiusMethods.CountRevocations, RadiusJson.Serialize(devices), CancellationToken.None).ConfigureAwait(true)).Count;

    private async Task<PolicySettings> SaveListAsync(PolicySettings settings) =>
        RadiusJson.Deserialize<PolicySettings>(await _ctx.InvokeAsync(RadiusMethods.SavePolicies, RadiusJson.Serialize(settings with { TagChoices = [] }), CancellationToken.None).ConfigureAwait(true));

    private Dictionary<Guid, string> SiteNames() => _sites.GroupBy(s => s.SiteId).ToDictionary(g => g.Key, g => g.First().SiteName);

    private bool CanAdd() => MayManage && !IsSaving && Rows.Count < RadiusLimits.MaxPolicies;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (Dialogs is { } dialogs)
        {
            await dialogs.ShowPolicyAsync(NewEditor(null)).ConfigureAwait(true);
        }
    }

    private bool CanEdit() => MayManage && !IsSaving && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAsync()
    {
        if (Selected is { } row && Dialogs is { } dialogs)
        {
            await dialogs.ShowPolicyAsync(NewEditor(row.Source)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        var message = row.Source.Kind == PolicyKinds.Revoke
            ? $"Delete the policy {row.Name}? Revoked certificates stay revoked: the devices need new certificates."
            : $"Delete the policy {row.Name}? It applies at the next authentication of each device.";
        if (!await _ctx.ConfirmAsync("Delete policy", message, "Delete").ConfigureAwait(true))
        {
            return;
        }

        await ChangeAsync(_saved with { Policies = [.. _saved.Policies.Where(p => p.Id != row.Source.Id)] }).ConfigureAwait(true);
    }

    private bool CanMoveUp() => CanEdit() && Rows.IndexOf(Selected!) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync() => MoveAsync(-1);

    private bool CanMoveDown() => CanEdit() && Rows.IndexOf(Selected!) is var i && i >= 0 && i < Rows.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync() => MoveAsync(1);

    private async Task MoveAsync(int step)
    {
        if (Selected is not { } row)
        {
            return;
        }

        var list = _saved.Policies.ToList();
        var index = list.FindIndex(p => p.Id == row.Source.Id);
        if (index < 0 || index + step < 0 || index + step >= list.Count)
        {
            return;
        }

        (list[index], list[index + step]) = (list[index + step], list[index]);
        await ChangeAsync(_saved with { Policies = list }).ConfigureAwait(true);
    }

    private bool CanSaveDefaultVlan() => MayManage && !IsSaving && IsFormValid
        && !string.Equals(DefaultVlan.Trim(), _saved.DefaultVlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Save of the Default VLAN (secondary: the page's accent is the Server card's Save).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveDefaultVlan))]
    private Task SaveDefaultVlanAsync() =>
        ChangeAsync(_saved with { DefaultVlan = DefaultVlan.Trim().Length == 0 ? null : SettingsViewModel.ParseInt(DefaultVlan) }, defaultVlan: true);

    private async Task ChangeAsync(PolicySettings settings, bool defaultVlan = false)
    {
        IsSaving = true;
        Error = null;
        try
        {
            Apply(await SaveListAsync(settings).ConfigureAwait(true));
        }
#pragma warning disable CA1031 // Shown below its field, else below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ServerErrors.Message(ex);
            if (defaultVlan && message == RadiusRules.VlanRange)
            {
                Validation.SetServerError(nameof(DefaultVlan), message);
            }
            else
            {
                Error = message;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    protected override void OnValidationChanged() => SaveDefaultVlanCommand.NotifyCanExecuteChanged();
}

/// <summary>
/// The policy editor: the shared device picker in its group mode (sites with "This server", tags, single devices, Except)
/// with Name, Policy (VLAN, Block, Revoke certificates) and VLAN ID on top. Save stores the whole list; a revoke policy that
/// is new or changed asks first with the count of certificates it revokes.
/// </summary>
public sealed partial class PolicyEditorViewModel : ValidatingViewModel
{
    public const string RevokeHint = "Revokes the IEEE 802.1X certificates of these devices in PKI, also new ones while the policy exists.";
    public const string BlockHint = "Refuses these devices even with a valid certificate. Nothing is revoked.";

    private readonly ICorePluginClientContext _ctx;
    private readonly PoliciesViewModel _owner;
    private readonly RadiusPolicy? _existing;
    private readonly HashSet<string> _otherNames;
    private readonly bool _mayRevoke;

    public PolicyEditorViewModel(ICorePluginClientContext ctx, PoliciesViewModel owner, RadiusPolicy? existing, IEnumerable<IDeviceInfo> devices,
        IReadOnlyList<SiteRef> sites, IEnumerable<string> tags, IEnumerable<string> otherNames, bool mayRevoke)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(tags);
        _existing = existing;
        _otherNames = new HashSet<string>((otherNames ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        _mayRevoke = mayRevoke;
        KindChoices =
        [
            new Choice<string>(PolicyKinds.Vlan, PolicyTexts.Vlan),
            new Choice<string>(PolicyKinds.Block, PolicyTexts.Block),
            .. mayRevoke || existing?.Kind == PolicyKinds.Revoke ? [new Choice<string>(PolicyKinds.Revoke, PolicyTexts.Revoke)] : Array.Empty<Choice<string>>(),
        ];
        Name = existing?.Name ?? string.Empty;
        Kind = KindChoices.FirstOrDefault(k => k.Value == existing?.Kind) ?? KindChoices[0];
        Vlan = existing?.Vlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var chosen = existing?.Devices ?? new PolicyDevices();
        var options = new DeviceGroupPickerOptions
        {
            Sites = [new DevicePickerSite(null, PolicyTexts.ThisServer), .. sites.OrderBy(s => s.SiteName, StringComparer.CurrentCultureIgnoreCase).Select(s => new DevicePickerSite(s.SiteId, s.SiteName))],

            // Plugins know tags by name: the name is the id.
            Tags = [.. tags.Concat(chosen.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(t => new DevicePickerTag(t, t))],
        };
        Picker = new DevicePickerViewModel(devices ?? [], new DeviceGroupChoice(chosen.ThisServer, chosen.SiteIds ?? [], chosen.Tags ?? [], chosen.Serials ?? [], chosen.ExceptSerials ?? []), options);
        Picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DevicePickerViewModel.CoveredCount) or nameof(DevicePickerViewModel.Summary))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(SaveBlockedReason));
            }
        };
        Validation
            .Rule(nameof(Name), () => Name.Trim().Length == 0 ? RadiusRules.EnterName
                : Name.Trim().Length > RadiusLimits.MaxNameLength ? RadiusRules.NameTooLong
                : _otherNames.Contains(Name.Trim()) ? RadiusRules.PolicyNameTaken
                : null)
            .Rule(nameof(Vlan), () => !IsVlan || SettingsViewModel.ParseInt(Vlan) is >= RadiusLimits.MinVlan and <= RadiusLimits.MaxVlan ? null : RadiusRules.VlanRange);
        Validation.Validate();
    }

    /// <summary>"Add policy" / "Edit Building C".</summary>
    public string Title => _existing is null ? "Add policy" : $"Edit {_existing.Name}";

    public DevicePickerViewModel Picker { get; }

    public IReadOnlyList<Choice<string>> KindChoices { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVlan), nameof(KindHint))]
    public partial Choice<string> Kind { get; set; }

    [ObservableProperty]
    public partial string Vlan { get; set; }

    public bool IsVlan => Kind.Value == PolicyKinds.Vlan;

    /// <summary>What a Block or Revoke policy does; null for VLAN.</summary>
    public string? KindHint => Kind.Value switch
    {
        PolicyKinds.Block => BlockHint,
        PolicyKinds.Revoke => RevokeHint,
        _ => null,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SaveBlockedReason))]
    public partial bool IsBusy { get; set; }

    /// <summary>Asks before a revoke policy revokes (the view sets it, owned by the editor window); null: the page's question.</summary>
    public Func<string, string, Task<bool>>? ConfirmRevoke { get; set; }

    public bool CanSave => !IsBusy && IsFormValid && HasDevices;

    private bool HasDevices => Picker is not null && !Picker.GroupChoice.IsEmpty;

    /// <summary>Why Save is off (its tooltip).</summary>
    public string? SaveBlockedReason => FormError ?? (HasDevices ? null : RadiusRules.ChoosePolicyDevices);

    /// <summary>The policy of the editor.</summary>
    public RadiusPolicy ToPolicy()
    {
        var choice = Picker.GroupChoice;
        return new RadiusPolicy
        {
            Id = _existing?.Id ?? Guid.Empty,
            Name = Name.Trim(),
            Kind = Kind.Value,
            Vlan = IsVlan ? SettingsViewModel.ParseInt(Vlan) : null,
            Devices = new PolicyDevices
            {
                ThisServer = choice.ThisServer,
                SiteIds = choice.SiteIds,
                Tags = choice.TagIds,
                Serials = choice.Serials,
                ExceptSerials = choice.ExceptSerials,
            },
            SavedBy = _existing?.SavedBy,
        };
    }

    /// <summary>Save (the window's accept): true when the server stored the list and the window may close.</summary>
    public async Task<bool> SaveAsync()
    {
        Validation.ShowAll();
        Picker.ErrorText = null;
        if (!IsFormValid)
        {
            return false;
        }

        var policy = ToPolicy();
        if (RadiusRules.Validate(policy) is { } error)
        {
            ShowError(error.Field, error.Message);
            return false;
        }

        IsBusy = true;
        try
        {
            if (policy.Kind == PolicyKinds.Revoke && RevokeChanged(policy))
            {
                var count = await _owner.CountRevocationsAsync(policy.Devices).ConfigureAwait(true);
                var question = count == 0
                    ? "These devices have no IEEE 802.1X certificate now. Every new one is revoked while the policy exists."
                    : string.Create(CultureInfo.CurrentCulture, $"Saving revokes {count:N0} IEEE 802.1X {(count == 1 ? "certificate" : "certificates")} of these devices in PKI, and every new one while the policy exists. This cannot be undone.");
                var confirmed = ConfirmRevoke is { } confirm
                    ? await confirm(Title, question).ConfigureAwait(true)
                    : await _ctx.ConfirmAsync(Title, question, "Revoke").ConfigureAwait(true);
                if (!confirmed)
                {
                    return false;
                }
            }

            await _owner.SaveOneAsync(policy).ConfigureAwait(true);
            return true;
        }
#pragma warning disable CA1031 // Shown below the field it belongs to, else in a message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ServerErrors.Message(ex);
            var field = message switch
            {
                RadiusRules.EnterName or RadiusRules.NameTooLong or RadiusRules.PolicyNameTaken => "name",
                RadiusRules.VlanRange => "vlan",
                RadiusRules.ChoosePolicyDevices => "devices",
                _ => null,
            };
            if (field is not null)
            {
                ShowError(field, message);
            }
            else
            {
                await _ctx.ShowMessageAsync(Title, message).ConfigureAwait(true);
            }

            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A new revoke policy, or one whose kind or devices changed: it revokes with the save.</summary>
    private bool RevokeChanged(RadiusPolicy policy) =>
        _existing is null || _existing.Kind != PolicyKinds.Revoke || !SameDevices(_existing.Devices ?? new PolicyDevices(), policy.Devices);

    private static bool SameDevices(PolicyDevices a, PolicyDevices b) =>
        a.ThisServer == b.ThisServer
        && (a.SiteIds ?? []).ToHashSet().SetEquals(b.SiteIds ?? [])
        && (a.Tags ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Tags ?? [])
        && (a.Serials ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Serials ?? [])
        && (a.ExceptSerials ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.ExceptSerials ?? []);

    private void ShowError(string field, string message)
    {
        switch (field)
        {
            case "name":
                Validation.SetServerError(nameof(Name), message);
                break;
            case "vlan":
                Validation.SetServerError(nameof(Vlan), message);
                break;
            default:
                Picker.ErrorText = message;
                break;
        }
    }

    partial void OnKindChanged(Choice<string> value) => Validation.Validate();

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
    }
}
