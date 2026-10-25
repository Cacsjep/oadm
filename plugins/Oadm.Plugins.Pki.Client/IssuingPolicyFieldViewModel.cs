using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// The issuing policy field of the certificate dialogs (Enable HTTPS, Enable IEEE 802.1X, Renew certificates, Install
/// certificates for IEEE 802.1X; view <see cref="IssuingPolicyField"/>): the policies of one kind, read once through the
/// task's read-only query <see cref="PkiQueries.ListIssuingPolicies"/> with the first selected device, the first preselected,
/// with what the chosen one issues in one sentence below it. Its error shows below the field.
/// </summary>
public sealed partial class IssuingPolicyFieldViewModel : ValidatingViewModel
{
    private IReadOnlyDictionary<Guid, string> _summaries = new Dictionary<Guid, string>();

    /// <param name="kind"><see cref="PolicyKinds.HttpsCertificate"/> or <see cref="PolicyKinds.Dot1xCertificate"/>.</param>
    /// <param name="label">The field's label ("Policy").</param>
    public IssuingPolicyFieldViewModel(string kind, string label = "Policy")
    {
        Kind = kind;
        Label = label;
        Validation.Rule(nameof(Policy), () => Policy is null ? NoPolicyText : null);
        Validation.Reset();
    }

    /// <summary>The policy, its loading state or its error changed (the dialog refreshes its main button).</summary>
    public event EventHandler? StateChanged;

    public string Kind { get; }

    public string Label { get; }

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

    /// <summary>The policies are being read (the dialog's overlay; Cancel stays usable).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>The policies were read (with or without an error).</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>A policy is chosen and nothing is wrong: the dialog may start the task.</summary>
    public bool IsReady => !IsLoading && IsLoaded && IsFormValid && Policy is not null;

    /// <summary>Why the dialog cannot start yet (its main button's tooltip), or null.</summary>
    public string? NotReadyReason => IsLoading || !IsLoaded ? "Reading the policies." : IsReady ? null : FormError ?? NoPolicyText;

    /// <summary>The chosen policy's id; null while none is chosen.</summary>
    public Guid? PolicyId => Policy?.Value.Id;

    /// <summary>Reads the policies through the task's query with <paramref name="deviceId"/> (read only).</summary>
    public async Task LoadAsync(ITaskDialogContext ctx, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IsLoading = true;
        try
        {
            Apply(PkiJson.Deserialize<IssuingPolicyList>(await ctx.QueryAsync(deviceId, PkiQueries.ListIssuingPolicies, null, ct).ConfigureAwait(true)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown below the policy field; Cancel closes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            IsLoaded = true;
            Validation.SetServerError(nameof(Policy), PkiDialogViewModel.Message(ex));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Shows the policies of the server (also tests and screenshots): <paramref name="presetId"/> preselected when listed,
    /// else the first.
    /// </summary>
    public void Apply(IssuingPolicyList list, Guid? presetId = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        _summaries = list.Summaries ?? new Dictionary<Guid, string>();
        Policies = [.. list.Policies.Where(p => p.Kind == Kind).Select(p => new Choice<PkiPolicy>(p, p.Name))];
        Policy = Policies.FirstOrDefault(p => p.Value.Id == presetId) ?? (Policies.Count > 0 ? Policies[0] : null);
        IsLoaded = true;
        OnPropertyChanged(nameof(Summary));
        if (list.Error is { } error)
        {
            Validation.SetServerError(nameof(Policy), error);
        }
        else if (Policy is null)
        {
            Validation.ShowAll();
        }

        RaiseStateChanged();
    }

    /// <summary>Shows the field's error (a submit was tried).</summary>
    public void ShowErrors() => Validation.ShowAll();

    partial void OnPolicyChanged(Choice<PkiPolicy>? value) => RaiseStateChanged();

    partial void OnIsLoadingChanged(bool value) => RaiseStateChanged();

    protected override void OnValidationChanged() => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(NotReadyReason));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
