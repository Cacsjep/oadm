using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// The dialog of Enable HTTPS and Enable IEEE 802.1X (docs/spec/pki.md "Issuing policies have no devices"): the issuing
/// policy of the task's kind (the first preselected) fills the form, the user may change its values for this run (decision
/// 2026-10-24); Enable gives the payload with the policy and the values used. The policies are read once through the task's
/// read-only query with the first selected device.
/// </summary>
public sealed partial class IssueCertificateViewModel : PkiDialogViewModel
{
    private readonly ITaskDialogContext _ctx;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private IReadOnlyDictionary<Guid, string> _radiusLines = new Dictionary<Guid, string>();

    public IssueCertificateViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, string kind)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        Kind = kind;
        Key = PolicyEditorViewModel.KeyChoices[0];
        Eapol = PolicyEditorViewModel.EapolChoices[0];
        Identity = PolicyEditorViewModel.IdentityChoices[0];
        RadiusCa = RadiusChoices[0];
        Validation
            .Rule(nameof(Policy), () => Policy is null ? NoPolicyText : null)
            .Rule(nameof(ValidDays), () => PolicyEditorViewModel.ParseInt(ValidDays) is >= PolicyLimits.MinValidDays and <= PolicyLimits.MaxValidDays ? null : PolicyTexts.ValidDaysRange)
            .Rule(nameof(CustomIdentity), () => IsDot1x ? PkiValidation.CustomIdentity(Identity.Value, CustomIdentity) : null);
        Validation.Reset();
    }

    /// <summary><see cref="PolicyKinds.HttpsCertificate"/> or <see cref="PolicyKinds.Dot1xCertificate"/>.</summary>
    public string Kind { get; }

    public bool IsDot1x => Kind == PolicyKinds.Dot1xCertificate;

    /// <summary>"Enable HTTPS", "Enable IEEE 802.1X".</summary>
    public string Title => IsDot1x ? PkiTaskIds.Dot1xEnableName : PkiTaskIds.HttpsEnableName;

    /// <summary>The safety warning of IEEE 802.1X (devices on enforcing ports go off the network when authentication fails).</summary>
    public string? Warning => IsDot1x ? PkiTaskIds.Dot1xWarning : null;

    public bool HasWarning => IsDot1x;

    /// <summary>"There is no HTTPS certificate policy. Add one on the PKI page."</summary>
    public string NoPolicyText => $"There is no {PolicyKinds.Name(Kind)} policy. Add one on the PKI page.";

    /// <summary>The policies of the kind, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<PkiPolicy>> Policies { get; set; } = [];

    [ObservableProperty]
    public partial Choice<PkiPolicy>? Policy { get; set; }

    /// <summary>"OADM Root CA SERVER01" (the CA that issues; not a choice today).</summary>
    [ObservableProperty]
    public partial string CaName { get; set; } = PolicyStore.IssuingCaName;

    [ObservableProperty]
    public partial string ValidDays { get; set; } = PolicyLimits.DefaultValidDays.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial Choice<string> Key { get; set; }

    [ObservableProperty]
    public partial Choice<int> Eapol { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIdentity))]
    public partial Choice<string> Identity { get; set; }

    public bool IsCustomIdentity => IsDot1x && Identity.Value == Dot1xIdentity.Custom;

    [ObservableProperty]
    public partial string CustomIdentity { get; set; } = string.Empty;

    /// <summary>The RADIUS server CA choices: the OADM CA, and the policy's imported CA when it has one.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<string>> RadiusChoices { get; set; } = [new(RadiusCaSource.Oadm, "This OADM CA")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RadiusCaLine))]
    public partial Choice<string> RadiusCa { get; set; }

    /// <summary>The imported RADIUS server CA of the policy ("Corp RADIUS CA, valid until 2030-01-01") when it is chosen.</summary>
    public string? RadiusCaLine => RadiusCa.Value == RadiusCaSource.Imported && Policy is { } policy ? _radiusLines.GetValueOrDefault(policy.Value.Id) : null;

    /// <summary>The policies are being read (the overlay over the form; Cancel stays usable).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    public partial bool IsLoading { get; set; }

    /// <summary>The payload of the run after Enable (without the clock answer, which the dialog adds); null until then.</summary>
    public IssuePayload? Payload { get; private set; }

    /// <summary>Reads the policies of the kind with the first selected device (read only).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_devices.Count == 0)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var list = PkiJson.Deserialize<IssuingPolicyList>(await _ctx.QueryAsync(_devices[0].Id, PkiQueries.ListIssuingPolicies, null, ct).ConfigureAwait(true));
            Apply(list);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown below the policy field; Cancel closes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(Policy), Message(ex));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Shows the policies of the server (also tests and screenshots); the first is preselected and fills the form.</summary>
    public void Apply(IssuingPolicyList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _radiusLines = list.RadiusCaSummaries ?? new Dictionary<Guid, string>();
        CaName = list.CaName ?? PolicyStore.IssuingCaName;
        Policies = [.. list.Policies.Where(p => p.Kind == Kind).Select(p => new Choice<PkiPolicy>(p, p.Name))];
        Policy = Policies.Count > 0 ? Policies[0] : null;
        if (list.Error is { } error)
        {
            Validation.SetServerError(nameof(Policy), error);
        }
        else if (Policy is null)
        {
            Validation.ShowAll();
        }
    }

    partial void OnPolicyChanged(Choice<PkiPolicy>? value)
    {
        if (value?.Value is not { } policy)
        {
            return;
        }

        ValidDays = (policy.ValidDays ?? PolicyLimits.DefaultValidDays).ToString(CultureInfo.InvariantCulture);
        Key = PolicyEditorViewModel.KeyChoices.FirstOrDefault(k => k.Value == policy.Key) ?? PolicyEditorViewModel.KeyChoices[0];
        Eapol = PolicyEditorViewModel.EapolChoices.FirstOrDefault(e => e.Value == (policy.EapolVersion ?? 1)) ?? PolicyEditorViewModel.EapolChoices[0];
        Identity = PolicyEditorViewModel.IdentityChoices.FirstOrDefault(i => i.Value == policy.Identity) ?? PolicyEditorViewModel.IdentityChoices[0];
        CustomIdentity = policy.CustomIdentity ?? string.Empty;
        RadiusChoices = policy.RadiusCa == RadiusCaSource.Imported || _radiusLines.ContainsKey(policy.Id) // never a silent switch away from the policy's CA
            ? [new(RadiusCaSource.Oadm, "This OADM CA"), new(RadiusCaSource.Imported, "Imported CA")]
            : [new(RadiusCaSource.Oadm, "This OADM CA")];
        RadiusCa = RadiusChoices.FirstOrDefault(r => r.Value == policy.RadiusCa) ?? RadiusChoices[0];

        Validation.Reset();
        OnPropertyChanged(nameof(RadiusCaLine));
    }

    private bool CanEnable() => !IsLoading && !IsBusy && IsFormValid && Policy is not null;

    /// <summary>Enable: the payload of the policy with the values of the form; the dialog closes and the clock check follows.</summary>
    [RelayCommand(CanExecute = nameof(CanEnable))]
    private void Enable()
    {
        Validation.ShowAll();
        if (!IsFormValid || Policy?.Value is not { } policy)
        {
            return;
        }

        Payload = ToPayload(policy);
        Close();
    }

    /// <summary>The policy and the values of the form (every value, so the run does not depend on a later change of the policy).</summary>
    public IssuePayload ToPayload(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new IssuePayload
        {
            PolicyId = policy.Id,
            ValidDays = PolicyEditorViewModel.ParseInt(ValidDays),
            Key = Key.Value,
            EapolVersion = IsDot1x ? Eapol.Value : null,
            Identity = IsDot1x ? Identity.Value : null,
            CustomIdentity = IsDot1x && IsCustomIdentity ? CustomIdentity.Trim() : null,
            RadiusCa = IsDot1x ? RadiusCa.Value : null,
        };
    }

    protected override void OnValidationChanged() => EnableCommand.NotifyCanExecuteChanged();

    protected override void OnBusyChanged() => EnableCommand.NotifyCanExecuteChanged();
}
