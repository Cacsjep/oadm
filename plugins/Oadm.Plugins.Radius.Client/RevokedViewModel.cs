using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One revoked certificate of the Revoked tab.</summary>
public sealed class RevokedRowViewModel(RevokedRow source)
{
    public RevokedRow Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The device's address, "(removed)" for a device OADM no longer manages.</summary>
    public string Device => Source.DeviceName.Length > 0 ? Source.DeviceName : "Device (removed)";

    /// <summary>"7D FD 05 ..." like the certificate window.</summary>
    public string SerialNumber => IssuedCertificateTexts.Pairs(Source.SerialNumber);

    public string Revoked => Source.RevokedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DateTime RevokedSortKey => Source.RevokedUtc;

    public string Reason => Source.Reason;

    public string RevokedBy => Source.UserName;

    public string ValidUntil => Source.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool Matches(string search) =>
        search.Length == 0
        || Device.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Source.SerialNumber.Contains(CertificateSerials.Normalize(search), StringComparison.OrdinalIgnoreCase)
        || Reason.Contains(search, StringComparison.OrdinalIgnoreCase)
        || RevokedBy.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the Revoked tab asks of its view: the certificate choice and the confirmation with the reason.</summary>
public interface IRevokeDialogs
{
    /// <summary>The certificate choice (the shared list of issued certificates); the chosen rows, or null for Cancel.</summary>
    Task<IReadOnlyList<IssuedCertificateRow>?> ChooseCertificatesAsync(RevokePickerViewModel picker);

    /// <summary>The confirmation with the reason; null for Cancel.</summary>
    Task<RevocationReason?> ConfirmRevokeAsync(string title, string message);
}

/// <summary>The certificate choice of Revoke reads PKI's list through the 802.1X server's methods.</summary>
internal sealed class RadiusCertificateSource(ICorePluginClientContext ctx) : IIssuedCertificateSource
{
    public async Task<IssuedCertificateListPage> ReadAsync(IssuedCertificateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var request = new CertificateListQuery
        {
            State = query.State,
            Purpose = query.Purpose,
            Source = query.Source,
            Search = query.Search,
            Offset = query.Offset,
            Limit = query.Limit,
        };
        var page = RadiusJson.Deserialize<CertificateListPage>(await ctx.InvokeAsync(RadiusPageMethods.ListCertificates, RadiusJson.Serialize(request), ct).ConfigureAwait(true));
        return new IssuedCertificateListPage(page.Rows, page.Total, page.Offset);
    }

    public async Task<IssuedCertificateDetails> GetCertificateAsync(string serialNumber, CancellationToken ct)
    {
        var reply = RadiusJson.Deserialize<LogCertificate>(await ctx.InvokeAsync(RadiusPageMethods.GetIssuedCertificate, RadiusJson.Serialize(new SerialRequest(serialNumber)), ct).ConfigureAwait(true));
        return string.IsNullOrEmpty(reply.Der)
            ? new IssuedCertificateDetails(null, null, "OADM keeps no copy of this certificate.")
            : new IssuedCertificateDetails(CertificateDescriber.Describe(Convert.FromBase64String(reply.Der)), null, null);
    }
}

/// <summary>
/// The certificate choice of Revoke: the shared list of issued certificates (search, filters, pages, View certificate);
/// Revoke takes the chosen rows that are still valid.
/// </summary>
public sealed partial class RevokePickerViewModel : IssuedCertificateListViewModel
{
    private readonly ICorePluginClientContext _ctx;

    public RevokePickerViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
        : base(new RadiusCertificateSource(ctx ?? throw new ArgumentNullException(nameof(ctx))), time)
    {
        _ctx = ctx;
        UseFilter = UseChoices.First(c => c.Value == DeviceCertificatePurposes.Dot1x);
    }

    /// <summary>The chosen rows that can still be revoked.</summary>
    public IReadOnlyList<IssuedCertificateRow> Revocable => [.. Selected.Where(r => r.CanRevoke)];

    /// <summary>"2 certificates chosen" or what to do.</summary>
    public string ChoiceText => Revocable.Count switch
    {
        0 => "Choose the certificates to revoke.",
        1 => "1 certificate chosen",
        var n => string.Create(CultureInfo.CurrentCulture, $"{n:N0} certificates chosen"),
    };

    /// <summary>Why Revoke is off.</summary>
    public string? RevokeTip => Revocable.Count == 0 ? "Choose a certificate that is still valid." : null;

    /// <summary>The window closes with the chosen rows (null = Cancel).</summary>
    public event EventHandler<IReadOnlyList<IssuedCertificateRow>?>? CloseRequested;

    protected override void OnSelectionChanged()
    {
        ChooseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ChoiceText));
        OnPropertyChanged(nameof(RevokeTip));
    }

    protected override Task ShowMessageAsync(string title, string message) => _ctx.ShowMessageAsync(title, message);

    private bool CanChoose() => Selected.Any(r => r.CanRevoke);

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose() => CloseRequested?.Invoke(this, Revocable);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);
}

/// <summary>
/// The Revoked tab (docs/spec/radius-server.md "Certificates and revocation"): the revoked certificates of PKI's list
/// (a second door to it) with a search, and Revoke, which opens the certificate choice and revokes the chosen ones with a
/// reason (users with all devices).
/// </summary>
public sealed partial class RevokedViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Func<bool> _mayManage;
    private readonly TimeProvider? _time;
    private List<RevokedRowViewModel> _all = [];
    private bool _loadedOnce;

    public RevokedViewModel(ICorePluginClientContext ctx, Func<bool> mayManage, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        _time = time;
    }

    public IRevokeDialogs? Dialogs { get; set; }

    /// <summary>Newest revocation first, filtered by <see cref="Search"/>.</summary>
    public RangeObservableCollection<RevokedRowViewModel> Rows { get; } = [];

    public bool IsLoaded => _loadedOnce;

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool MayManage => _mayManage();

    /// <summary>"3 certificates revoked".</summary>
    public string CountText => RadiusTexts.Count(_all.Count, "certificate revoked", "certificates revoked");

    public string? EmptyText => Rows.Count > 0 || !_loadedOnce ? null
        : _all.Count > 0 ? "No certificate matches the search."
        : "No certificate is revoked.";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loadedOnce)
        {
            IsReloading = true;
        }
        else
        {
            IsLoading = true;
        }

        try
        {
            var list = RadiusJson.Deserialize<RevokedList>(await _ctx.InvokeAsync(RadiusMethods.ListRevoked, null, ct).ConfigureAwait(true));
            _all = [.. list.Rows.OrderByDescending(r => r.RevokedUtc).Select(r => new RevokedRowViewModel(r))];
            _loadedOnce = true;
            Error = null;
            Filter();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = "Cannot read the revoked certificates: " + ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
            IsReloading = false;
        }
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        RevokeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchChanged(string value) => Filter();

    /// <summary>One pass and one grid reset.</summary>
    private void Filter()
    {
        var search = Search.Trim();
        Rows.ReplaceAll(search.Length == 0 ? _all : _all.Where(r => r.Matches(search)));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsLoaded));
    }

    /// <summary>The reason as the contract names it ("keyCompromised", ...).</summary>
    internal static string ReasonCode(RevocationReason reason) => reason switch
    {
        RevocationReason.KeyCompromised => RevokeReasons.KeyCompromised,
        RevocationReason.DeviceReplaced => RevokeReasons.DeviceReplaced,
        RevocationReason.DeviceRemoved => RevokeReasons.DeviceRemoved,
        _ => RevokeReasons.Other,
    };

    private bool CanRevoke() => MayManage;

    /// <summary>Revoke (red): choose certificates from PKI's list, confirm with the reason, revoke them.</summary>
    [RelayCommand(CanExecute = nameof(CanRevoke))]
    private async Task RevokeAsync()
    {
        if (Dialogs is not { } dialogs)
        {
            return;
        }

        var chosen = await dialogs.ChooseCertificatesAsync(new RevokePickerViewModel(_ctx, _time)).ConfigureAwait(true);
        if (chosen is not { Count: > 0 })
        {
            return;
        }

        var message = chosen.Count == 1 ? IssuedCertificateTexts.ConfirmMessageOne : IssuedCertificateTexts.ConfirmMessage;
        if (await dialogs.ConfirmRevokeAsync(IssuedCertificateTexts.RevokeTitle(chosen), message).ConfigureAwait(true) is not { } reason)
        {
            return;
        }

        try
        {
            var result = RadiusJson.Deserialize<RevokeReply>(await _ctx.InvokeAsync(RadiusMethods.Revoke,
                RadiusJson.Serialize(new RevokeRequest([.. chosen.Select(r => r.Source.SerialNumber)], ReasonCode(reason))), CancellationToken.None).ConfigureAwait(true));
            if (result.Revoked == 0)
            {
                await _ctx.ShowMessageAsync("Revoke", IssuedCertificateTexts.NothingRevoked).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("Revoke", ServerErrors.Message(ex)).ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
