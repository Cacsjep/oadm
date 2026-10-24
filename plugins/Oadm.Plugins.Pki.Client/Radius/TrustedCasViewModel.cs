using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One CA of the Trusted CAs card: its check box and "Only devices OADM manages".</summary>
public sealed partial class TrustedCaViewModel : ObservableObject
{
    private readonly TrustedCasViewModel _owner;

    public TrustedCaViewModel(TrustedCaRow source, TrustedCasViewModel owner)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        IsTrusted = source.Trusted;
        OnlyManaged = source.OnlyManaged;
    }

    public TrustedCaRow Source { get; }

    public string Name => Source.Name;

    /// <summary>"OADM CA and 2 previous, valid until 2036-10-24", "Imported, valid until 2030-01-01".</summary>
    public string Detail
    {
        get
        {
            var until = "valid until " + Source.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Source.Kind switch
            {
                CertificateAuthorityKind.Oadm when Source.PreviousCount == 1 => "With its previous CA, " + until,
                CertificateAuthorityKind.Oadm when Source.PreviousCount > 1 => string.Create(CultureInfo.CurrentCulture, $"With its {Source.PreviousCount:N0} previous CAs, {until}"),
                CertificateAuthorityKind.Imported => "Imported, " + until,
                CertificateAuthorityKind.AxisDeviceId => "Factory certificates of Axis devices",
                _ => char.ToUpperInvariant(until[0]) + until[1..],
            };
        }
    }

    [ObservableProperty]
    public partial bool IsTrusted { get; set; }

    [ObservableProperty]
    public partial bool OnlyManaged { get; set; }

    public bool MayManage => _owner.MayManage;

    internal void NotifyRole() => OnPropertyChanged(nameof(MayManage));

    partial void OnIsTrustedChanged(bool value) => _owner.Edited();

    partial void OnOnlyManagedChanged(bool value) => _owner.Edited();
}

/// <summary>
/// The Trusted CAs card (docs/spec/radius-server.md, docs/spec/pki.md "Services select CAs"): PKI's CAs as check boxes,
/// each with "Only devices OADM manages"; a CA PKI no longer lists is gone at the next read. Save (secondary) stores the
/// choice; the CAs themselves are managed on the PKI page.
/// </summary>
public sealed partial class TrustedCasViewModel : ObservableObject
{
    /// <summary>The line below the list (the client SDK has no page navigation).</summary>
    public const string ManageText = "Add or remove CAs on the PKI page.";

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<bool> _mayManage;
    private bool _applying;

    public TrustedCasViewModel(ICorePluginClientContext ctx, Func<bool> mayManage)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
    }

    /// <summary>A handful of CAs: no virtualization needed.</summary>
    public RangeObservableCollection<TrustedCaViewModel> Cas { get; } = [];

    public bool MayManage => _mayManage();

    /// <summary>The choice differs from the saved one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsChanged { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>A save the server refused (below the list: a whole-list error).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Without CAs: what to do first.</summary>
    public string? EmptyText => Cas.Count > 0 ? null : "The PKI has no certificate authority. Create one on the PKI page.";

    /// <summary>Nothing is checked: no device passes (the tooltip of Save says so).</summary>
    public string? SaveTip => Cas.Count > 0 && !Cas.Any(c => c.IsTrusted) ? "No CA is checked: no device passes." : null;

    public async Task LoadAsync(CancellationToken ct) =>
        Apply(RadiusJson.Deserialize<TrustedCaList>(await _ctx.InvokeAsync(RadiusMethods.GetTrustedCas, null, ct).ConfigureAwait(true)));

    public void Apply(TrustedCaList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _applying = true;
        try
        {
            Cas.ReplaceAll((list.Cas ?? []).Select(c => new TrustedCaViewModel(c, this)));
        }
        finally
        {
            _applying = false;
        }

        IsChanged = false;
        Error = null;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SaveTip));
    }

    /// <summary>A user limited to a group: nothing server-wide stays.</summary>
    public void Clear() => Apply(new TrustedCaList());

    /// <summary>The choice of the card.</summary>
    public TrustedCaSettings ToSettings() => new() { Cas = [.. Cas.Where(c => c.IsTrusted).Select(c => new TrustedCaChoice(c.Source.Id, c.OnlyManaged))] };

    internal void Edited()
    {
        if (_applying)
        {
            return;
        }

        IsChanged = Cas.Any(c => c.IsTrusted != c.Source.Trusted || (c.IsTrusted && c.OnlyManaged != c.Source.OnlyManaged));
        OnPropertyChanged(nameof(SaveTip));
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        foreach (var ca in Cas)
        {
            ca.NotifyRole();
        }

        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => MayManage && IsChanged && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsSaving = true;
        Error = null;
        try
        {
            Apply(RadiusJson.Deserialize<TrustedCaList>(await _ctx.InvokeAsync(RadiusMethods.SaveTrustedCas, RadiusJson.Serialize(ToSettings()), CancellationToken.None).ConfigureAwait(true)));
        }
#pragma warning disable CA1031 // Shown below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
