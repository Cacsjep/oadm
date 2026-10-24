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

/// <summary>One line of a rule list: name, devices, the summary sentence and the warning of a later overlap.</summary>
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

    /// <summary>A system policy (OADM needs it): a System badge, no Delete.</summary>
    public bool IsSystem => Row.IsSystem;

    public bool CanDelete => !Row.IsSystem;

    /// <summary>Why a system policy has no Delete (the badge's tooltip).</summary>
    public const string SystemTip = "OADM needs it, so it cannot be deleted";
}

/// <summary>
/// One rule list of a card of the Policies tab ("VLANs", "Trusted CAs"): the policies of its kinds in age order, one line
/// each, Add with the kind preset (a menu of the kinds when it has several), Edit and Delete per row (no Delete for a
/// system policy). At most 200 policies in all: the view's table is virtualized and bounded in height.
/// </summary>
public sealed partial class PolicySectionViewModel : ObservableObject
{
    private readonly PoliciesViewModel _owner;
    private readonly string _empty;
    private readonly string _emptyHowTo;

    internal PolicySectionViewModel(PoliciesViewModel owner, string title, IReadOnlyList<string> kinds, bool withSystem, string empty, string emptyHowTo)
    {
        _owner = owner;
        Title = title;
        Kinds = kinds;
        WithSystem = withSystem;
        _empty = empty;
        _emptyHowTo = emptyHowTo;
        AddChoices = [.. kinds.Select(k => new Choice<string>(k, PolicyKinds.Name(k)))];
    }

    /// <summary>"Other issuing", "Trusted CAs", "VLANs".</summary>
    public string Title { get; }

    /// <summary>The kinds of the list (Add offers them).</summary>
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>The system policy of the kind is a row of this list (Enrollment "All devices"); else it is edited in place.</summary>
    public bool WithSystem { get; }

    /// <summary>The kinds choose devices: the Devices column shows.</summary>
    public bool HasDevices => Kinds.Any(PolicyKinds.HasDevices);

    /// <summary>The kinds Add offers: one opens the editor at once, several a menu.</summary>
    public IReadOnlyList<Choice<string>> AddChoices { get; }

    public RangeObservableCollection<PolicyRowViewModel> Rows { get; } = [];

    public bool MayManage => _owner.MayManage;

    /// <summary>One line when the list is empty: what it means, and for users who may change the policies what to do first.</summary>
    public string? EmptyText => Rows.Count > 0 || _owner.IsLoading ? null : MayManage ? _emptyHowTo : _empty;

    public bool HasEmptyText => EmptyText is not null;

    /// <summary>Why Add is off (at most 200 policies).</summary>
    public string? AddTip => _owner.AddTip;

    /// <summary>Add is on (the user may manage the policies, fewer than 200).</summary>
    public bool CanAddRule => _owner.CanAdd();

    /// <summary>Holds <paramref name="policy"/> (its kind, and it is a rule here, not a setting edited in place).</summary>
    internal bool Holds(PolicyRow row) => Kinds.Contains(row.Policy.Kind, StringComparer.Ordinal) && (WithSystem || !row.IsSystem);

    internal void Fill(IEnumerable<PolicyRowViewModel> rows)
    {
        Rows.ReplaceAll(rows);
        Notify();
    }

    internal void Notify()
    {
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(AddTip));
        OnPropertyChanged(nameof(CanAddRule));
        AddCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd() => _owner.CanAdd();

    /// <summary>Add of the list: the editor with <paramref name="kind"/> preset (the first kind when none is given).</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync(string? kind) => _owner.AddAsync(kind is not null && Kinds.Contains(kind, StringComparer.Ordinal) ? kind : Kinds[0]);

    /// <summary>Edit of a row (also a double click).</summary>
    [RelayCommand]
    private Task EditRowAsync(PolicyRowViewModel? row) => _owner.EditAsync(row);

    [RelayCommand]
    private Task DeleteRowAsync(PolicyRowViewModel? row) => _owner.DeleteAsync(row);
}

/// <summary>One line of "Check a device": the area, the sentence, the deciding policies (dimmed) and a warning.</summary>
public sealed class PolicyCheckLineViewModel(PolicyCheckLine line)
{
    public string Area { get; } = line.Area;

    public string Text { get; } = line.Text;

    /// <summary>"By Building B", "By OADM CA and Axis device ID"; null when no policy decides it (what happens without one).</summary>
    public string? By { get; } = line.Policies.Count == 0 ? null
        : "By " + (line.Policies.Count == 1 ? line.Policies[0] : string.Join(", ", line.Policies.Take(line.Policies.Count - 1)) + " and " + line.Policies[^1]);

    public bool HasBy => By is not null;

    public bool IsWarning { get; } = line.IsWarning;
}

/// <summary>What the Policies tab asks of the view: the policy editor and the one-device picker.</summary>
public interface IPolicyDialogs
{
    /// <summary>Shows the editor until it saved (true) or was closed.</summary>
    Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor);

    /// <summary>The shared picker for one device (<see cref="DevicePickerWindow.ShowOneAsync"/>); null when cancelled.</summary>
    Task<Guid?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, Guid? selected) => Task.FromResult<Guid?>(null);
}

/// <summary>
/// The Policies tab of the PKI page (docs/spec/pki.md "Policies", decision 2026-10-25): cards by area in a fixed order.
/// Check a device on top (one device, the server says which policies apply to it); Certificates for cameras (the system
/// HTTPS, IEEE 802.1X and Expiry warning policies edited in place, the rules Other issuing and Expiry warnings); 802.1X
/// network access (the 802.1X server policy in place, the rules Trusted CAs, VLANs, Blocked); EST (the EST server policy in
/// place, the rules Enrollment with its system policy as the first row, and Sign-in CAs). Changing needs "Manage the
/// certificate authority" and all devices; other users see it read only; users limited to a device group do not see it
/// (the server sends them none).
/// </summary>
public sealed partial class PoliciesViewModel : ObservableObject
{
    /// <summary>The device button before a device is chosen.</summary>
    public const string ChooseDeviceText = "Choose a device";

    private readonly ICorePluginClientContext _ctx;
    private PolicyList _list = new();
    private int _loads;
    private int _checks;
    private IPkiFiles? _files;

    public PoliciesViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        OtherIssuing = new(this, "Other issuing", [PolicyKinds.HttpsCertificate, PolicyKinds.Dot1xCertificate], false,
            "No other issuing policies.", "No other issuing policies. Add one to issue with other values, e.g. HTTPS 90 days.");
        ExpiryRules = new(this, "Expiry warnings for some devices", [PolicyKinds.ExpiryWarning], false,
            "No other expiry warnings.", "No other expiry warnings. Add one to warn earlier for some devices.");
        TrustedCas = new(this, "Trusted CAs", [PolicyKinds.AllowCa], false,
            "No trusted CAs: the 802.1X server lets no device in.", "No trusted CAs: the 802.1X server lets no device in. Add a CA.");
        Vlans = new(this, "VLANs", [PolicyKinds.Vlan], false,
            "No VLANs: switch ports keep their own.", "No VLANs: switch ports keep their own. Add one to put devices in a VLAN.");
        Blocked = new(this, "Blocked", [PolicyKinds.Block], false,
            "No devices are blocked.", "No devices are blocked. Add a rule to refuse devices.");
        Enrollment = new(this, "Enrollment", [PolicyKinds.Enrollment], true,
            "No enrollment: the EST server refuses every camera.", "No enrollment: the EST server refuses every camera. Add a rule.");
        SignInCas = new(this, "Sign-in CAs", [PolicyKinds.SignInCa], false,
            "No sign-in CAs: cameras cannot sign in to the EST server.", "No sign-in CAs: cameras cannot sign in to the EST server. Add a CA.");
        Sections = [OtherIssuing, ExpiryRules, TrustedCas, Vlans, Blocked, Enrollment, SignInCas];
    }

    /// <summary>The editor and the device picker (the page view).</summary>
    public IPolicyDialogs? Dialogs { get; set; }

    /// <summary>File pickers of the page (the RADIUS server CA of the IEEE 802.1X policy edited in place).</summary>
    public IPkiFiles? Files
    {
        get => _files;
        set
        {
            _files = value;
            foreach (var editor in Settings)
            {
                editor.Files ??= value;
            }
        }
    }

    /// <summary>The list as the server answered it.</summary>
    public PolicyList List => _list;

    // ------------------------------------------------------------------ cards

    /// <summary>Certificates for cameras: the HTTPS certificate system policy, edited in place (null without one).</summary>
    [ObservableProperty]
    public partial PolicyEditorViewModel? Https { get; set; }

    /// <summary>Certificates for cameras: the IEEE 802.1X certificate system policy.</summary>
    [ObservableProperty]
    public partial PolicyEditorViewModel? Dot1x { get; set; }

    /// <summary>Certificates for cameras: the Expiry warning system policy (its days).</summary>
    [ObservableProperty]
    public partial PolicyEditorViewModel? Expiry { get; set; }

    /// <summary>802.1X network access: the 802.1X server policy.</summary>
    [ObservableProperty]
    public partial PolicyEditorViewModel? Dot1xServer { get; set; }

    /// <summary>EST: the EST server policy.</summary>
    [ObservableProperty]
    public partial PolicyEditorViewModel? EstServer { get; set; }

    public PolicySectionViewModel OtherIssuing { get; }

    public PolicySectionViewModel ExpiryRules { get; }

    public PolicySectionViewModel TrustedCas { get; }

    public PolicySectionViewModel Vlans { get; }

    public PolicySectionViewModel Blocked { get; }

    public PolicySectionViewModel Enrollment { get; }

    public PolicySectionViewModel SignInCas { get; }

    /// <summary>Every rule list in the page's order.</summary>
    public IReadOnlyList<PolicySectionViewModel> Sections { get; }

    /// <summary>The system policies edited in place, in the page's order (those that exist).</summary>
    public IEnumerable<PolicyEditorViewModel> Settings => new[] { Https, Dot1x, Expiry, Dot1xServer, EstServer }.OfType<PolicyEditorViewModel>();

    // ------------------------------------------------------------------ state

    /// <summary>The first read (the overlay over the tab).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A later read (the progress row; the cards stay).</summary>
    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    /// <summary>A read or a delete failed (below the Check a device card, above the cards it concerns).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The tab shows: users with all devices (the policies are server-wide).</summary>
    public bool IsShown => _ctx.HasAllDevices;

    /// <summary>The user may change the policies (the server's answer, and the role now).</summary>
    public bool MayManage => _list.MayManage && _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>Why Add is off.</summary>
    public string? AddTip => _list.Rows.Count >= PolicyLimits.MaxPolicies ? PolicyTexts.TooMany : null;

    // ------------------------------------------------------------------ Check a device

    /// <summary>The device of Check a device; null before one was chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckButtonText))]
    public partial IDeviceInfo? CheckedDevice { get; set; }

    /// <summary>"10.0.0.48 · AXIS P3265-LVE", or "Choose a device".</summary>
    public string CheckButtonText => CheckedDevice is { } device ? DeviceLabel(device) : ChooseDeviceText;

    /// <summary>What applies to the chosen device, a few plain lines (at most about seven: no virtualization needed).</summary>
    public RangeObservableCollection<PolicyCheckLineViewModel> CheckLines { get; } = [];

    /// <summary>The server is checking the chosen device (the ring next to the button).</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    /// <summary>The check failed, or the device is gone (below the button).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckError))]
    public partial string? CheckError { get; set; }

    public bool HasCheckError => !string.IsNullOrEmpty(CheckError);

    /// <summary>"10.0.0.48 · AXIS P3265-LVE" (the address alone without a model).</summary>
    internal static string DeviceLabel(IDeviceInfo device) => string.IsNullOrWhiteSpace(device.Model) ? device.Address : device.Address + " · " + device.Model;

    /// <summary>Choose a device (the shared one-device picker over the devices this client knows), then check it.</summary>
    [RelayCommand]
    private async Task ChooseCheckDeviceAsync()
    {
        if (Dialogs is not { } dialogs || !IsShown)
        {
            return;
        }

        var devices = _ctx.Devices;
        if (await dialogs.PickDeviceAsync(devices, CheckedDevice?.Id).ConfigureAwait(true) is { } id && devices.FirstOrDefault(d => d.Id == id) is { } chosen)
        {
            CheckedDevice = chosen;
            await CheckAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Asks the server which policies apply to the chosen device (one call for one device; nothing before a choice).</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (CheckedDevice is not { } device || !IsShown)
        {
            CheckLines.Clear();
            return;
        }

        var run = ++_checks;
        IsChecking = true;
        CheckError = null;
        try
        {
            var reply = PkiJson.Deserialize<PolicyCheck>(await _ctx.InvokeAsync(PolicyMethods.CheckDevice, PkiJson.Serialize(new PolicyCheckRequest(device.Id)), ct).ConfigureAwait(true));
            if (run != _checks)
            {
                return; // a newer check runs
            }

            CheckLines.ReplaceAll(reply.Lines.Select(l => new PolicyCheckLineViewModel(l)));
            CheckError = reply.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown below the button.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (run == _checks)
            {
                CheckLines.Clear();
                CheckError = ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (run == _checks)
            {
                IsChecking = false;
            }
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Reads the policies (the overlay the first time, the progress row later) and checks the chosen device again.</summary>
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
            NotifySections();
        }

        if (CheckedDevice is not null)
        {
            await CheckAsync(ct).ConfigureAwait(true); // a change of the policies changes what applies
        }
    }

    /// <summary>Shows a list of the server (also used by tests and the screenshots).</summary>
    public void Apply(PolicyList list) => Apply(list, null);

    /// <summary>
    /// Shows <paramref name="list"/>: the rule lists, and a new in-place editor per system policy, except one with changes not
    /// saved yet (kept as the user left it); <paramref name="saved"/> just saved and is replaced, saying Saved.
    /// </summary>
    internal void Apply(PolicyList list, PolicyEditorViewModel? saved)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = IsShown ? list : new PolicyList();
        var system = _list.Rows.Where(r => r.IsSystem).GroupBy(r => r.Policy.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Policy, StringComparer.Ordinal);
        Https = InPlace(Https, PolicyKinds.HttpsCertificate);
        Dot1x = InPlace(Dot1x, PolicyKinds.Dot1xCertificate);
        Expiry = InPlace(Expiry, PolicyKinds.ExpiryWarning);
        Dot1xServer = InPlace(Dot1xServer, PolicyKinds.Dot1xServer);
        EstServer = InPlace(EstServer, PolicyKinds.EstServer);

        var names = SiteNames();
        foreach (var section in Sections)
        {
            section.Fill(_list.Rows.Where(section.Holds).Select(r => new PolicyRowViewModel(r, PolicyTexts.DevicesText(r.Policy.Devices, names))));
        }

        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(AddTip));

        PolicyEditorViewModel? InPlace(PolicyEditorViewModel? current, string kind)
        {
            if (!system.TryGetValue(kind, out var policy))
            {
                return null;
            }

            if (current is not null && current != saved && current.IsDirty && current.PolicyId == policy.Id)
            {
                return current; // the user's changes stay until saved
            }

            var editor = new PolicyEditorViewModel(_ctx, this, policy, _list, [], inPlace: true) { Files = Files };
            editor.JustSaved = saved is not null && current == saved;
            editor.ReadOnly = !MayManage;
            return editor;
        }
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(MayManage));
        if (!IsShown && _list.Rows.Count > 0)
        {
            CheckedDevice = null;
            CheckLines.Clear();
            Apply(new PolicyList()); // nothing server-wide stays for a user limited to a group
        }
        else if (_list.Rows.Count > 0)
        {
            Apply(_list); // read only, or editable again
        }

        NotifySections();
    }

    private void NotifySections()
    {
        foreach (var section in Sections)
        {
            section.Notify();
        }
    }

    private Dictionary<Guid, string> SiteNames() => _list.Sites.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);

    // ------------------------------------------------------------------ changing

    /// <summary>The dialog editor for a new policy of <paramref name="kind"/> (null: the existing policy's) or <paramref name="existing"/>.</summary>
    public PolicyEditorViewModel NewEditor(PkiPolicy? existing, string? kind = null) => new(_ctx, this, existing, _list, _ctx.Devices, kind: kind);

    internal bool CanAdd() => MayManage && _list.Rows.Count < PolicyLimits.MaxPolicies;

    /// <summary>Add of a rule list: the editor with the kind preset (no kind choice).</summary>
    internal async Task AddAsync(string kind)
    {
        if (CanAdd() && Dialogs is { } dialogs)
        {
            await dialogs.ShowPolicyAsync(NewEditor(null, kind)).ConfigureAwait(true);
        }
    }

    internal async Task EditAsync(PolicyRowViewModel? row)
    {
        if (row is not null && MayManage && Dialogs is { } dialogs)
        {
            await dialogs.ShowPolicyAsync(NewEditor(row.Source)).ConfigureAwait(true);
        }
    }

    internal async Task DeleteAsync(PolicyRowViewModel? row)
    {
        if (row is null || !MayManage || !row.CanDelete)
        {
            return;
        }

        var message = row.Source.Kind switch
        {
            PolicyKinds.Block when PolicyRules.Revokes(row.Source) => $"Delete the policy {row.Name}? Revoked certificates stay revoked: the devices need new certificates.",
            PolicyKinds.AllowCa => $"Delete the policy {row.Name}? Certificates of this CA no longer pass the 802.1X server.",
            PolicyKinds.SignInCa => $"Delete the policy {row.Name}? Cameras can no longer sign in to the EST server with this CA.",
            PolicyKinds.Enrollment => $"Delete the policy {row.Name}? The EST server refuses its devices unless another Enrollment policy holds them.",
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
#pragma warning disable CA1031 // Shown on the tab.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
    }

    /// <summary>Saves <paramref name="policy"/> of <paramref name="editor"/>; the list after it, or the error that refused it.</summary>
    internal async Task<PolicySaveReply> SaveAsync(PkiPolicy policy, PolicyEditorViewModel editor)
    {
        var reply = PkiJson.Deserialize<PolicySaveReply>(await _ctx.InvokeAsync(PolicyMethods.SavePolicy, PkiJson.Serialize(policy), CancellationToken.None).ConfigureAwait(true));
        if (reply.List is { } list && reply.Error is null)
        {
            Apply(list, editor);
        }

        return reply;
    }

    /// <summary>The IEEE 802.1X certificates a Block policy that also revokes with <paramref name="devices"/> revokes now.</summary>
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

    /// <summary>The server's certificate can use it (whatever the minimum).</summary>
    public bool IsUsableWithKey => suite.Usable;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Listed for the chosen minimum.</summary>
    [ObservableProperty]
    public partial bool IsListed { get; set; } = !suite.Legacy;

    partial void OnIsCheckedChanged(bool value) => owner.CipherSuitesEdited();
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
/// The policy editor (docs/spec/pki.md "Policies"): one dialog with the name and the kind's own fields (the kind is preset
/// by the rule list Add came from; a policy keeps its kind), and the shared device picker in its group mode where the kind
/// has devices (sites with "This server", tags, single devices, Except; a Block that also revokes without tags). Save sends
/// the policy; a Block policy that newly revokes or whose devices changed asks first with the count of certificates it
/// revokes. Errors directly below the fields. In place (<see cref="IsInPlace"/>, decision 2026-10-25): the same form for a
/// system policy on its card, without the name and the devices (they stay as stored), Save only with changes, the status
/// left of Save.
/// </summary>
public sealed partial class PolicyEditorViewModel : ValidatingViewModel
{
    public const string OldTlsHint = "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure.";
    public const string UnusableSuiteHint = "Not usable with the server certificate.";
    /// <summary>The choice next to the Minutes field ("Every" [60] minutes).</summary>
    public const string ReauthEvery = "Every";
    public const string OnlyManagedText = PolicyTexts.OnlyManaged;

    /// <summary>The check box of a Block policy.</summary>
    public const string AlsoRevokeText = PolicyTexts.AlsoRevoke;

    /// <summary>The RADIUS server CA choice that follows the CA of the 802.1X server policy (decision 2026-10-25).</summary>
    public const string OadmRadiusServer = "OADM 802.1X server";

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
    private bool _applying = true;

    /// <summary>The properties that follow the fields (never a change of the policy themselves).</summary>
    private static readonly HashSet<string> Derived = new(StringComparer.Ordinal)
    {
        nameof(Preview), nameof(IsDirty), nameof(CanSave), nameof(SaveBlockedReason), nameof(SaveStatus), nameof(HasSaveStatus), nameof(IsSaveStatusOk),
    };

    /// <param name="ctx">The page's context.</param>
    /// <param name="owner">The Policies tab.</param>
    /// <param name="existing">The policy to change; null for a new one.</param>
    /// <param name="list">The list as the server answered it.</param>
    /// <param name="devices">The devices of the device picker.</param>
    /// <param name="kind">A new policy's kind (the rule list's Add); null: the existing policy's, or the first kind Add could offer.</param>
    /// <param name="inPlace">The system policy's form on its card (no name, no devices, Save only with changes).</param>
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
            if (IsInPlace && !_applying)
            {
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
        CaChoices = [.. list.Cas.Select(c => new Choice<string>(c.Id, CaLabel(c)))];
        SignInCaChoices = [.. list.SignInCas.Select(c => new Choice<string>(c.Id, c.Name))];

        // "Issued by": every CA with its key (decision 2026-10-25); a CA the policy names that can no longer issue stays shown
        // so the error says so below the field.
        IssuingCaChoices = [.. (list.IssuingCas.Count > 0 ? list.IssuingCas : list.Cas.Where(c => c.Kind == CertificateAuthorityKind.Oadm)).Select(c => new Choice<string>(c.Id, c.Name))];
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
        AlsoRevoke = policy.AlsoRevoke ?? false;

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
        var checkedIds = (server?.CipherSuites ?? PolicyRules.DefaultSuites(list.CipherSuites, MinimumTls.Value)).ToHashSet();
        foreach (var suite in CipherSuites)
        {
            suite.IsChecked = checkedIds.Contains(suite.Id);
        }

        UpdateListed(lowered: false);
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
        RadiusCa = RadiusChoices.FirstOrDefault(r => r.Value == dot1x?.RadiusCa) ?? RadiusChoices[0];
        RadiusCaPem = dot1x?.RadiusCaPem;
        RadiusCaLine = RadiusLineOf(RadiusCaPem);

        var chosen = policy.Devices ?? new PolicyDevices();
        Scope = DeviceScopes[chosen.AllDevices ? 0 : 1];
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
            .Rule(nameof(SignInCa), () => !IsSignInCa || SignInCa is not null ? null : PolicyTexts.ChooseCa)
            .Rule(nameof(ProfileChoices), () => !IsChosenProfiles || ProfileChoices.Any(p => p.IsChecked) ? null : PolicyTexts.ChooseProfiles)
            .Rule(nameof(LargestPacket), () => !IsServer || ParseInt(LargestPacket) is >= PolicyRules.MinEapPacket and <= PolicyRules.MaxEapPacket ? null : PolicyRules.EapPacketRange)
            .Rule(nameof(ReauthMinutes), () => !IsServer || !Reauth.Value || ParseInt(ReauthMinutes) is >= PolicyLimits.MinReauthMinutes and <= PolicyLimits.MaxReauthMinutes
                ? null : PolicyTexts.ReauthRange)
            .Rule(nameof(CipherSuites), () => !IsServer || CipherSuites.Any(s => s.IsListed && s.IsChecked && s.IsUsable(MinimumTls.Value)) ? null : PolicyRules.CheckCipherSuite)
            .Rule(nameof(IssuingCa), () => !IsIssuing ? null : IssuingCa is null ? PolicyTexts.ChooseCa
                : IssuingCaChoices.Any(c => string.Equals(c.Value, IssuingCa.Value, StringComparison.OrdinalIgnoreCase)) ? null : PolicyTexts.CannotIssue)
            .Rule(nameof(ValidDays), () => !IsIssuing || ParseInt(ValidDays) is >= PolicyLimits.MinValidDays and <= PolicyLimits.MaxValidDays ? null : PolicyTexts.ValidDaysRange)
            .Rule(nameof(Key), () => IsServer && Key.Value != PolicyKeys.Ecc ? PolicyTexts.Dot1xServerNeedsEcc : null)
            .Rule(nameof(CustomIdentity), () => IsDot1xCertificate ? PkiValidation.CustomIdentity(Identity.Value, CustomIdentity) : null)
            .Rule(nameof(RadiusCa), () => IsDot1xCertificate ? PkiValidation.RadiusCa(RadiusCa.Value, RadiusCaPem) : null);
        Validation.Validate();
        _baseline = PkiJson.Serialize(ToPolicy());
    }

    /// <summary>The system policy's form on its card (decision 2026-10-25): no name, no devices, Save only with changes.</summary>
    public bool IsInPlace { get; }

    /// <summary>The id of the policy (empty for a new one).</summary>
    public Guid PolicyId => _existing?.Id ?? Guid.Empty;

    /// <summary>The fields cannot be changed (in place for a user who may not manage the policies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool ReadOnly { get; set; }

    public bool IsEditable => !ReadOnly;

    /// <summary>The name field shows (the dialog; in place the system policy keeps its name).</summary>
    public bool ShowsName => !IsInPlace;

    /// <summary>The heading of the form in place: "HTTPS", "IEEE 802.1X", "Expiry warning", "802.1X server", "EST server".</summary>
    public string Heading => Kind.Value switch
    {
        PolicyKinds.HttpsCertificate => "HTTPS",
        PolicyKinds.Dot1xCertificate => "IEEE 802.1X",
        _ => PolicyKinds.Name(Kind.Value),
    };

    /// <summary>The fields differ from the policy as it was read (in place: Save is on, the status says Not saved).</summary>
    public bool IsDirty => !string.IsNullOrEmpty(_baseline) && !string.Equals(PkiJson.Serialize(ToPolicy()), _baseline, StringComparison.Ordinal);

    /// <summary>This form follows its own save (the status says Saved until the next change).</summary>
    public bool JustSaved { get; set; }

    public const string SavedText = "Saved";
    public const string NotSavedText = "Not saved";
    public const string NoChangesText = "No changes to save.";

    /// <summary>The status left of Save in place: "Not saved" with changes, "Saved" after this form saved, else none.</summary>
    public string? SaveStatus => !IsInPlace ? null : IsDirty ? NotSavedText : JustSaved ? SavedText : null;

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

    /// <summary>Save of the form in place (the page then replaces this form with the saved policy's).</summary>
    [RelayCommand]
    private async Task SaveInPlaceAsync() => await SaveAsync().ConfigureAwait(true);

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

    public static IReadOnlyList<Choice<int>> EapolChoices { get; } = [new(1, "1"), new(2, "2")];

    public static IReadOnlyList<Choice<string>> IdentityChoices { get; } =
    [
        new(Dot1xIdentity.Mac, "MAC address"),
        new(Dot1xIdentity.HostName, "Host name"),
        new(Dot1xIdentity.Custom, "Custom"),
    ];

    public static IReadOnlyList<Choice<string>> RadiusChoices { get; } =
    [
        new(RadiusCaSource.Oadm, OadmRadiusServer),
        new(RadiusCaSource.Imported, "Imported CA"),
    ];

    /// <summary>The CAs that can issue ("Issued by"): the OADM CA and the imported CAs with their key.</summary>
    public IReadOnlyList<Choice<string>> IssuingCaChoices { get; }

    [ObservableProperty]
    public partial Choice<string>? IssuingCa { get; set; }

    /// <summary>Block: also revoke the IEEE 802.1X certificates of its devices (then no tags and no "All devices").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Revokes), nameof(OffersAllDevices), nameof(ShowsDeviceScope), nameof(IsAllDevices), nameof(ShowsPicker), nameof(CanSave), nameof(SaveBlockedReason))]
    public partial bool AlsoRevoke { get; set; }

    /// <summary>The check box Also revoke shows (a Block policy).</summary>
    public bool IsBlock => Kind.Value == PolicyKinds.Block;

    /// <summary>The policy also revokes: a Block policy with the box checked.</summary>
    public bool Revokes => IsBlock && AlsoRevoke;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRadiusImported))]
    public partial Choice<string> RadiusCa { get; set; }

    /// <summary>The imported RADIUS server CA (PEM, public only), checked by the server when it was chosen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewRadiusCaCommand))]
    public partial string? RadiusCaPem { get; set; }

    /// <summary>"Corp RADIUS CA, valid until 2030-01-01"; null without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RadiusCaText))]
    public partial string? RadiusCaLine { get; set; }

    /// <summary>The line below the RADIUS server CA: the imported CA, or what to do first.</summary>
    public string RadiusCaText => RadiusCaLine ?? "No CA certificate imported yet.";

    public bool IsIssuing => PolicyKinds.IsIssuing(Kind.Value);

    public bool IsDot1xCertificate => Kind.Value == PolicyKinds.Dot1xCertificate;

    public bool IsCustomIdentity => Identity.Value == Dot1xIdentity.Custom;

    public bool IsRadiusImported => RadiusCa.Value == RadiusCaSource.Imported;

    /// <summary>File pickers owned by the editor window (the RADIUS server CA); set by the window.</summary>
    public IPkiFiles? Files { get; set; }

    /// <summary>A message owned by the editor window (title, text); set by the window, else the page's.</summary>
    public Func<string, string, Task>? ShowMessage { get; set; }

    /// <summary>Import of the RADIUS server CA: the server checks the file (a CA certificate, PEM or DER), the policy stores it on Save.</summary>
    [RelayCommand]
    private async Task ImportRadiusCaAsync()
    {
        if (Files is not { } files || await files.OpenAsync("Choose the CA of the RADIUS server", PkiFileKind.RadiusCa).ConfigureAwait(true) is not { } file)
        {
            return;
        }

        await ApplyRadiusFileAsync(file).ConfigureAwait(true);
    }

    /// <summary>Checks a RADIUS CA file on the server and puts it into the form (also used by tests).</summary>
    public async Task ApplyRadiusFileAsync(PickedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var reply = PkiJson.Deserialize<RadiusCaReply>(await _ctx.InvokeAsync(PkiMethods.ImportRadiusCa, PkiJson.Serialize(new ImportRadiusCaRequest(Convert.ToBase64String(file.Data))), CancellationToken.None)
            .ConfigureAwait(true));
        if (reply.Errors is { Count: > 0 } errors)
        {
            Validation.SetServerError(nameof(RadiusCa), errors.Values.First());
            return;
        }

        RadiusCaPem = reply.Pem;
        RadiusCaLine = reply.Summary is { } summary ? PolicyTexts.RadiusCaLine(summary.CommonName, summary.NotAfterUtc) : RadiusLineOf(reply.Pem);
        Validation.Touch(nameof(RadiusCa));
    }

    [RelayCommand(CanExecute = nameof(CanViewRadiusCa))]
    private async Task ViewRadiusCaAsync()
    {
        if (RadiusCaPem is not { } pem)
        {
            return;
        }

        string text;
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
            text = string.Join(
                Environment.NewLine,
                $"Subject: {certificate.Subject}",
                $"Issuer: {certificate.Issuer}",
                string.Create(CultureInfo.InvariantCulture, $"Valid: {certificate.NotBefore.ToUniversalTime():yyyy-MM-dd} to {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}"),
                $"Fingerprint (SHA-256): {certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            text = PolicyTexts.RadiusCaUnreadable;
        }

        if (ShowMessage is { } show)
        {
            await show("RADIUS server CA", text).ConfigureAwait(true);
        }
        else
        {
            await _ctx.ShowMessageAsync("RADIUS server CA", text).ConfigureAwait(true);
        }
    }

    private bool CanViewRadiusCa() => !string.IsNullOrEmpty(RadiusCaPem);

    /// <summary>"Corp RADIUS CA, valid until 2030-01-01" of a PEM; null when there is none or it cannot be read.</summary>
    internal static string? RadiusLineOf(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
            return PolicyTexts.RadiusCaLine(certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false), certificate.NotAfter.ToUniversalTime());
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>"Add VLAN policy" / "Edit Building C".</summary>
    public string Title => _existing is null ? $"Add {PolicyKinds.Name(Kind.Value)} policy" : $"Edit {_existing.Name}";

    /// <summary>The kind is chosen for a new policy only (a policy keeps its kind).</summary>
    public bool IsNew => _existing is null;

    public DevicePickerViewModel Picker { get; }

    public IReadOnlyList<Choice<string>> KindChoices { get; }

    public IReadOnlyList<Choice<string>> CaChoices { get; }

    public IReadOnlyList<CipherSuiteViewModel> CipherSuites { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVlan), nameof(IsExpiry), nameof(IsAllowCa), nameof(IsServer), nameof(HasDevices), nameof(KindHint), nameof(NamePlaceholder),
        nameof(OffersAllDevices), nameof(IsAllDevices), nameof(ShowsPicker), nameof(ShowsDeviceScope), nameof(Title), nameof(Heading), nameof(IsIssuing), nameof(IsDot1xCertificate), nameof(IsBlock), nameof(Revokes),
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

    public bool IsReauthEvery => Reauth.Value;

    /// <summary>The kind chooses devices.</summary>
    public bool HasDevices => PolicyKinds.HasDevices(Kind.Value);

    /// <summary>The kind may hold "All devices" (not a Block that also revokes): the Devices choice shows.</summary>
    public bool OffersAllDevices => PolicyKinds.AllowsAllDevices(Kind.Value) && !Revokes;

    /// <summary>"All devices" is chosen (for a kind that offers it).</summary>
    public bool IsAllDevices => OffersAllDevices && Scope.Value;

    /// <summary>The device picker shows: a kind with devices and not "All devices" (never in place: the devices stay as stored).</summary>
    public bool ShowsPicker => HasDevices && !IsAllDevices && !IsInPlace;

    /// <summary>The Devices choice shows (the dialog of a kind that offers All devices).</summary>
    public bool ShowsDeviceScope => OffersAllDevices && !IsInPlace;

    /// <summary>In place, a system Expiry warning for chosen devices: which ones (shown below the days).</summary>
    public string? DaysHint => IsInPlace && HasDevices && !IsAllDevices
        ? "For " + PolicyTexts.DevicesText(_existing?.Devices, _list.Sites.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name))
        : null;

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

    /// <summary>The error of the cipher suites (a group of check boxes: shown below it by the field).</summary>
    public string? CipherSuitesError => IsServer ? Validation[nameof(CipherSuites)] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SaveBlockedReason))]
    public partial bool IsBusy { get; set; }

    partial void OnReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    /// <summary>Asks before a revoke policy revokes (the view sets it, owned by the editor window); null: the page's question.</summary>
    public Func<string, string, Task<bool>>? ConfirmRevoke { get; set; }

    public bool CanSave => !IsBusy && !ReadOnly && IsFormValid && (!ShowsPicker || HasChosenDevices) && (!IsInPlace || IsDirty);

    private bool HasChosenDevices => Picker is not null && !Picker.GroupChoice.IsEmpty;

    /// <summary>Why Save is off (its tooltip).</summary>
    public string? SaveBlockedReason => FormError ?? (ShowsPicker && !HasChosenDevices ? PolicyTexts.ChooseDevices : IsInPlace && !IsDirty ? NoChangesText : null);

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
        var choice = Picker.GroupChoice;
        return PolicyRules.Normalize(new PkiPolicy
        {
            Id = _existing?.Id ?? Guid.Empty,
            Name = Name.Trim(),
            Kind = Kind.Value,
            Devices = IsInPlace ? _existing?.Devices : IsAllDevices ? PolicyDevices.Everything : new PolicyDevices
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
            OnlyManaged = OnlyManaged,
            AlsoRevoke = AlsoRevoke,
            MinimumTls = MinimumTls.Value,
            CipherSuites = [.. CipherSuites.Where(s => s.IsListed && s.IsChecked).Select(s => s.Id)],
            LargestEapPacket = ParseInt(LargestPacket),
            ReauthMinutes = Reauth.Value ? ParseInt(ReauthMinutes) : null,
            ValidDays = ParseInt(ValidDays),
            Key = Key.Value,
            EapolVersion = Eapol.Value,
            Identity = Identity.Value,
            CustomIdentity = CustomIdentity,
            RadiusCa = RadiusCa.Value,
            RadiusCaPem = RadiusCaPem,
            SignInFor = SignInFor.Value,
            ProfileScope = ProfileScope.Value,
            Profiles = [.. ProfileChoices.Where(p => p.IsChecked).Select(p => p.Name)],
            SavedBy = _existing?.SavedBy,
        });
    }

    /// <summary>Save (the window's accept): true when the server stored the policy and the window may close.</summary>
    public async Task<bool> SaveAsync()
    {
        Validation.ShowAll();
        OnPropertyChanged(nameof(CipherSuitesError));
        OnPropertyChanged(nameof(ProfilesError));
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
            if (PolicyRules.Revokes(policy) && RevokeChanged(policy))
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

            var reply = await _owner.SaveAsync(policy, this).ConfigureAwait(true);
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

    /// <summary>Reset to default: the default suites of the chosen minimum (<see cref="PolicyRules.DefaultSuites"/>, those the server's certificate can use).</summary>
    [RelayCommand]
    private void ResetCipherSuites()
    {
        _applying = true;
        try
        {
            var defaults = PolicyRules.DefaultSuites(_list.CipherSuites, MinimumTls.Value).ToHashSet();
            foreach (var suite in CipherSuites)
            {
                suite.IsChecked = defaults.Contains(suite.Id);
            }
        }
        finally
        {
            _applying = false;
        }

        CipherSuitesEdited();
    }

    /// <summary>A new Block policy that revokes, one that starts to revoke, or one whose devices changed: it revokes with the save.</summary>
    private bool RevokeChanged(PkiPolicy policy) =>
        _existing is null || !PolicyRules.Revokes(_existing) || !(_existing.Devices ?? new PolicyDevices()).SameAs(policy.Devices);

    private void ShowError(PolicyFieldError error)
    {
        var property = error.Field switch
        {
            PolicyFields.Name => nameof(Name),
            PolicyFields.Kind => nameof(Kind),
            PolicyFields.Vlan => nameof(Vlan),
            PolicyFields.Days => nameof(Days),
            PolicyFields.Ca => IsIssuing ? nameof(IssuingCa) : IsSignInCa ? nameof(SignInCa) : nameof(Ca),
            PolicyFields.SignInFor => nameof(SignInFor),
            PolicyFields.ProfileScope => nameof(ProfileScope),
            PolicyFields.Profiles => nameof(ProfileChoices),
            PolicyFields.ValidDays => nameof(ValidDays),
            PolicyFields.Key => nameof(Key),
            PolicyFields.AlsoRevoke => nameof(AlsoRevoke),
            PolicyFields.EapolVersion => nameof(Eapol),
            PolicyFields.Identity => nameof(Identity),
            PolicyFields.CustomIdentity => nameof(CustomIdentity),
            PolicyFields.RadiusCa => nameof(RadiusCa),
            PolicyFields.LargestEapPacket => nameof(LargestPacket),
            PolicyFields.ReauthMinutes => nameof(ReauthMinutes),
            PolicyFields.MinimumTls => nameof(MinimumTls),
            PolicyFields.CipherSuites => nameof(CipherSuites),
            PolicyFields.Devices when IsAllDevices => nameof(Scope), // the picker is hidden: below the Devices choice
            _ => null,
        };
        if (property is null)
        {
            Picker.ErrorText = error.Message; // devices: below the device choice
            return;
        }

        Validation.SetServerError(property, error.Message);
        OnPropertyChanged(nameof(CipherSuitesError));
        OnPropertyChanged(nameof(ProfilesError));
    }

    /// <summary>A Block policy that also revokes chooses devices and sites only: said below the device choice at once.</summary>
    private void CheckTags()
    {
        if (Picker is null)
        {
            return;
        }

        if (Revokes && Picker.GroupChoice.TagIds.Count > 0)
        {
            Picker.ErrorText = PolicyTexts.RevokeNoTags;
        }
        else if (Picker.ErrorText == PolicyTexts.RevokeNoTags)
        {
            Picker.ErrorText = null;
        }
    }

    partial void OnAlsoRevokeChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        CheckTags();
    }

    partial void OnKindChanged(Choice<string> value)
    {
        if (_applying)
        {
            return;
        }

        Validation.Validate();
        OnPropertyChanged(nameof(CipherSuitesError));
        OnPropertyChanged(nameof(ProfilesError));
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

    /// <summary>The old suites show only below TLS 1.2; those the server's certificate can use are checked when the minimum is lowered there.</summary>
    private void UpdateListed(bool lowered)
    {
        var legacy = PolicyRules.MinimumIndex(MinimumTls.Value) is < 2;
        foreach (var suite in CipherSuites.Where(s => s.IsLegacy))
        {
            suite.IsListed = legacy;
            if (lowered)
            {
                suite.IsChecked = suite.IsUsableWithKey;
            }
        }
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(CipherSuitesError));
        OnPropertyChanged(nameof(ProfilesError));
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
