using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One VLAN rule: a site or a tag to a VLAN ID; the error of the VLAN ID below it.</summary>
public sealed partial class VlanRuleViewModel : ValidatingViewModel
{
    private readonly VlansViewModel _owner;

    internal VlanRuleViewModel(VlansViewModel owner, VlanRule? rule)
    {
        _owner = owner;
        var kind = rule?.Kind ?? (owner.SiteChoices.Count > 0 ? VlanRuleKinds.Site : VlanRuleKinds.Tag);
        Kind = VlansViewModel.Kinds.First(k => k.Value == kind);
        Reference = Find(rule) ?? (References.Count > 0 ? References[0] : null);
        Vlan = rule is null ? string.Empty : rule.Vlan.ToString(CultureInfo.InvariantCulture);
        Validation
            .Rule(nameof(Reference), () => Reference is null ? RadiusRules.ChooseRule
                : _owner.Rules.Any(r => r != this && r.Kind.Value == Kind.Value && string.Equals(r.Reference?.Value, Reference.Value, StringComparison.OrdinalIgnoreCase)) ? RadiusRules.RuleTwice
                : null)
            .Rule(nameof(Vlan), () => VlansViewModel.ParseVlan(Vlan) is null ? RadiusRules.VlanRange : null);
        Validation.Validate();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(References))]
    public partial Choice<string> Kind { get; set; }

    [ObservableProperty]
    public partial Choice<string>? Reference { get; set; }

    [ObservableProperty]
    public partial string Vlan { get; set; }

#pragma warning disable CA1822 // Instance property for the compiled binding of the row template.
    public IReadOnlyList<Choice<string>> KindChoices => VlansViewModel.Kinds;
#pragma warning restore CA1822

    /// <summary>The sites or the tags, by <see cref="Kind"/>.</summary>
    public IReadOnlyList<Choice<string>> References => Kind.Value == VlanRuleKinds.Site ? _owner.SiteChoices : _owner.TagChoices;

    public bool MayManage => _owner.MayManage;

    internal VlanRule ToRule() => new(Kind.Value, Reference?.Value ?? string.Empty, Reference?.Label, VlansViewModel.ParseVlan(Vlan) ?? 0);

    internal void Revalidate() => Validation.Validate();

    internal void ShowAllErrors() => Validation.ShowAll();

    /// <summary>A rule error of the shared rules ("refId", "vlan") below its field.</summary>
    internal bool ShowError(string field, string message)
    {
        var property = field switch
        {
            "refId" or "kind" => nameof(Reference),
            "vlan" => nameof(Vlan),
            _ => null,
        };
        if (property is null)
        {
            return false;
        }

        Validation.SetServerError(property, message);
        return true;
    }

    internal void NotifyRole() => OnPropertyChanged(nameof(MayManage));

    partial void OnKindChanged(Choice<string> value)
    {
        Reference = (References.Count > 0 ? References[0] : null);
        _owner.Edited();
    }

    partial void OnReferenceChanged(Choice<string>? value) => _owner.Edited();

    partial void OnVlanChanged(string value) => _owner.Edited();

    protected override void OnValidationChanged() => _owner.RuleValidationChanged();

    private Choice<string>? Find(VlanRule? rule)
    {
        if (rule is null)
        {
            return null;
        }

        return References.FirstOrDefault(r => string.Equals(r.Value, rule.RefId, StringComparison.OrdinalIgnoreCase))
            ?? new Choice<string>(rule.RefId, (string.IsNullOrEmpty(rule.RefName) ? rule.RefId : rule.RefName) + " (not available)");
    }
}

/// <summary>
/// The VLANs card (docs/spec/radius-server.md): Assign VLANs (off by default), rules in order (first match wins) of a site
/// or a tag to a VLAN ID, and the optional Default VLAN. The page's Save sends it with the general settings. While Assign
/// VLANs is off the card is collapsed: its errors never block Save, and Save keeps the saved rules when the edits are not
/// valid.
/// </summary>
public sealed partial class VlansViewModel : ValidatingViewModel
{
    public static IReadOnlyList<Choice<string>> Kinds { get; } =
    [
        new(VlanRuleKinds.Site, "Site"),
        new(VlanRuleKinds.Tag, "Tag"),
    ];

    private readonly Func<bool> _mayManage;
    private bool _rulesValid = true;
    private VlanSettings _saved = new();

    public VlansViewModel(Func<bool> mayManage)
    {
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        Validation.Rule(nameof(DefaultVlan), () => DefaultVlan.Trim().Length == 0 || ParseVlan(DefaultVlan) is not null ? null : RadiusRules.VlanRange);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string DefaultVlan { get; set; } = string.Empty;

    public RangeObservableCollection<VlanRuleViewModel> Rules { get; } = [];

    public IReadOnlyList<Choice<string>> SiteChoices { get; private set; } = [];

    public IReadOnlyList<Choice<string>> TagChoices { get; private set; } = [];

    public bool MayManage => _mayManage();

    /// <summary>The VLAN form and every rule are valid (and the edits are valid at all).</summary>
    public bool EditsValid => IsFormValid && _rulesValid;

    /// <summary>Save may go on: Assign VLANs is off (the hidden card blocks nothing), or every edit is valid.</summary>
    public bool IsValid => !Enabled || EditsValid;

    /// <summary>Why Save is off (the first error of the card); none while Assign VLANs is off.</summary>
    public string? Error => !Enabled ? null : FormError ?? Rules.Select(r => r.FormError).FirstOrDefault(e => e is not null);

    public bool HasRules => Rules.Count > 0;

    /// <summary>Raised when a rule's or the card's validity changed (the page's Save).</summary>
    public event EventHandler? ValidityChanged;

    /// <summary>The sites of the server and the tags of the server and of the devices this client knows.</summary>
    public void SetChoices(IReadOnlyList<SiteRef> sites, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(tags);
        SiteChoices =
        [
            new Choice<string>(RadiusLimits.ThisServerSiteId, SwitchRowViewModel.ThisServer),
            .. sites.OrderBy(s => s.SiteName, StringComparer.CurrentCultureIgnoreCase).Select(s => new Choice<string>(s.SiteId.ToString(), s.SiteName)),
        ];
        TagChoices = [.. tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).Select(t => new Choice<string>(t, t))];
    }

    /// <summary>Fills the card from the server; nothing shows as an error afterwards.</summary>
    public void Apply(VlanSettings vlans)
    {
        ArgumentNullException.ThrowIfNull(vlans);
        _saved = vlans with { TagChoices = [] };
        Enabled = vlans.Enabled;
        DefaultVlan = vlans.DefaultVlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Rules.ReplaceAll(vlans.Rules.Select(r => new VlanRuleViewModel(this, r)));
        Validation.Reset();
        Edited();
    }

    /// <summary>What Save sends: the card; while Assign VLANs is off with edits that are not valid, the saved rules (off).</summary>
    public VlanSettings ToSettings() => Enabled || EditsValid
        ? new()
        {
            Enabled = Enabled,
            Rules = [.. Rules.Select(r => r.ToRule())],
            DefaultVlan = ParseDefault(),
        }
        : _saved with { Enabled = false };

    /// <summary>An error of the shared rules (<see cref="RadiusRules.Validate(VlanSettings)"/>) below its field; false when no field shows it.</summary>
    public bool ShowError(RadiusFieldError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Field == "defaultVlan")
        {
            Validation.SetServerError(nameof(DefaultVlan), error.Message);
            return true;
        }

        // "rules[2].vlan"
        if (error.Field.StartsWith("rules[", StringComparison.Ordinal) && error.Field.IndexOf(']', StringComparison.Ordinal) is var close and > 6
            && int.TryParse(error.Field.AsSpan(6, close - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < Rules.Count
            && error.Field.Length > close + 2)
        {
            return Rules[index].ShowError(error.Field[(close + 2)..], error.Message);
        }

        return false;
    }

    /// <summary>The default VLAN; null when empty.</summary>
    public int? ParseDefault() => DefaultVlan.Trim().Length == 0 ? null : ParseVlan(DefaultVlan);

    public void ShowAllErrors()
    {
        Validation.ShowAll();
        foreach (var rule in Rules)
        {
            rule.ShowAllErrors();
        }
    }

    /// <summary>"1" to "4094" as a number; null otherwise.</summary>
    public static int? ParseVlan(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var vlan) && vlan is >= RadiusLimits.MinVlan and <= RadiusLimits.MaxVlan ? vlan : null;

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        foreach (var rule in Rules)
        {
            rule.NotifyRole();
        }

        AddRuleCommand.NotifyCanExecuteChanged();
    }

    partial void OnEnabledChanged(bool value) => RuleValidationChanged(); // off: the hidden card no longer blocks Save

    internal void Edited()
    {
        foreach (var rule in Rules)
        {
            rule.Revalidate(); // "Another rule has this site or tag." follows the other rows
        }

        RuleValidationChanged();
        NotifyOrder();
    }

    private void NotifyOrder()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    internal void RuleValidationChanged()
    {
        _rulesValid = Rules.All(r => r.IsFormValid);
        OnPropertyChanged(nameof(EditsValid));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasRules));
        ValidityChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnValidationChanged() => RuleValidationChanged();

    private bool CanAddRule() => MayManage;

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private void AddRule()
    {
        Rules.Add(new VlanRuleViewModel(this, null));
        Edited();
    }

    [RelayCommand]
    private void RemoveRule(VlanRuleViewModel? rule)
    {
        if (rule is not null && MayManage && Rules.Remove(rule))
        {
            Edited();
        }
    }

    /// <summary>Moves a rule one place up (rules are checked in order).</summary>
    private bool CanMoveUp(VlanRuleViewModel? rule) => MayManage && rule is not null && Rules.IndexOf(rule) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp(VlanRuleViewModel? rule)
    {
        var index = rule is null ? -1 : Rules.IndexOf(rule);
        if (MayManage && index > 0)
        {
            Rules.Move(index, index - 1);
            NotifyOrder();
        }
    }

    /// <summary>Moves a rule one place down.</summary>
    private bool CanMoveDown(VlanRuleViewModel? rule) => MayManage && rule is not null && Rules.IndexOf(rule) is var i && i >= 0 && i < Rules.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown(VlanRuleViewModel? rule)
    {
        var index = rule is null ? -1 : Rules.IndexOf(rule);
        if (MayManage && index >= 0 && index < Rules.Count - 1)
        {
            Rules.Move(index, index + 1);
            NotifyOrder();
        }
    }
}
