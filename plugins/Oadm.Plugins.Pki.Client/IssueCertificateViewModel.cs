using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// The dialog of Enable HTTPS and Enable IEEE 802.1X (docs/spec/pki.md "Issuing policies have no devices"): the issuing
/// policy of the task's kind (<see cref="IssuingPolicyFieldViewModel"/>, the first preselected) with what it issues in one sentence
/// below it; the run uses the policy as stored (decision 2026-10-25: no values of its own here, they are changed in the
/// policy). Enable gives the payload with the policy. Renew certificates (<see cref="ForRenew"/>) shows the same form for its
/// IEEE 802.1X certificate policy (HTTPS renews with the first HTTPS certificate policy).
/// </summary>
public sealed partial class IssueCertificateViewModel : PkiDialogViewModel
{
    private readonly ITaskDialogContext _ctx;
    private readonly IReadOnlyList<IDeviceInfo> _devices;

    public IssueCertificateViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, string kind)
        : this(ctx, devices, kind, renew: false)
    {
    }

    private IssueCertificateViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, string kind, bool renew)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        Kind = kind;
        IsRenew = renew;
        Choice = new IssuingPolicyFieldViewModel(Kind, renew ? RenewPolicyLabel : "Policy");
        Choice.StateChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The dialog of Renew certificates: its IEEE 802.1X certificate policy.</summary>
    public static IssueCertificateViewModel ForRenew(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices) =>
        new(ctx, devices, PolicyKinds.Dot1xCertificate, renew: true);

    /// <summary>The label of Renew certificates' policy field.</summary>
    public const string RenewPolicyLabel = "IEEE 802.1X policy";

    /// <summary><see cref="PolicyKinds.HttpsCertificate"/> or <see cref="PolicyKinds.Dot1xCertificate"/>.</summary>
    public string Kind { get; }

    public bool IsDot1x => Kind == PolicyKinds.Dot1xCertificate;

    /// <summary>Renew certificates: the IEEE 802.1X certificate policy of the renewal.</summary>
    public bool IsRenew { get; }

    /// <summary>"Enable HTTPS", "Enable IEEE 802.1X", "Renew certificates".</summary>
    public string Title => IsRenew ? PkiTaskIds.RenewName : IsDot1x ? PkiTaskIds.Dot1xEnableName : PkiTaskIds.HttpsEnableName;

    /// <summary>The main button: "Enable", "Renew".</summary>
    public string StartText => IsRenew ? "Renew" : "Enable";

    /// <summary>The safety warning of IEEE 802.1X (devices on enforcing ports go off the network when authentication fails).</summary>
    public string? Warning => IsDot1x ? PkiTaskIds.Dot1xWarning : null;

    public bool HasWarning => IsDot1x;

    /// <summary>The policy field.</summary>
    public IssuingPolicyFieldViewModel Choice { get; }

    /// <summary>The payload of the run after Enable / Renew (without the clock answer, which the dialog adds); null until then.</summary>
    public IssuePayload? Payload { get; private set; }

    /// <summary>Reads the policies of the kind with the first selected device (read only).</summary>
    public Task LoadAsync(CancellationToken ct) => _devices.Count == 0 ? Task.CompletedTask : Choice.LoadAsync(_ctx, _devices[0].Id, ct);

    private bool CanStart() => !IsBusy && Choice.IsReady;

    /// <summary>Enable / Renew: the payload of the policy; the dialog closes and the clock check follows.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        Choice.ShowErrors();
        if (!Choice.IsReady || Choice.Policy?.Value is not { } policy)
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

    protected override void OnBusyChanged() => StartCommand.NotifyCanExecuteChanged();
}
