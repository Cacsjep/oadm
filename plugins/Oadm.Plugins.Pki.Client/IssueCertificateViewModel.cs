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
/// policy of the task's kind (the first preselected) with what it issues in one sentence below it; the run uses the policy
/// as stored (decision 2026-10-25: no values of its own here, they are changed in the policy). Enable gives the payload with
/// the policy. The policies are read once through the task's read-only query with the first selected device.
/// </summary>
public sealed partial class IssueCertificateViewModel : PkiDialogViewModel
{
    private readonly ITaskDialogContext _ctx;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private IReadOnlyDictionary<Guid, string> _summaries = new Dictionary<Guid, string>();

    public IssueCertificateViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, string kind)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        Kind = kind;
        Validation.Rule(nameof(Policy), () => Policy is null ? NoPolicyText : null);
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
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial Choice<PkiPolicy>? Policy { get; set; }

    /// <summary>What the chosen policy issues ("Enable HTTPS issues certificates from OADM Root CA, valid 365 days, with an ECC key.").</summary>
    public string? Summary => Policy?.Value is { } policy ? _summaries.GetValueOrDefault(policy.Id) : null;

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

    /// <summary>Shows the policies of the server (also tests and screenshots); the first is preselected.</summary>
    public void Apply(IssuingPolicyList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _summaries = list.Summaries ?? new Dictionary<Guid, string>();
        Policies = [.. list.Policies.Where(p => p.Kind == Kind).Select(p => new Choice<PkiPolicy>(p, p.Name))];
        Policy = Policies.Count > 0 ? Policies[0] : null;
        OnPropertyChanged(nameof(Summary));
        if (list.Error is { } error)
        {
            Validation.SetServerError(nameof(Policy), error);
        }
        else if (Policy is null)
        {
            Validation.ShowAll();
        }
    }

    private bool CanEnable() => !IsLoading && !IsBusy && IsFormValid && Policy is not null;

    /// <summary>Enable: the payload of the policy; the dialog closes and the clock check follows.</summary>
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

    /// <summary>The payload: the policy (the run reads its values as stored).</summary>
    public static IssuePayload ToPayload(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new IssuePayload { PolicyId = policy.Id };
    }

    protected override void OnValidationChanged() => EnableCommand.NotifyCanExecuteChanged();

    protected override void OnBusyChanged() => EnableCommand.NotifyCanExecuteChanged();
}
