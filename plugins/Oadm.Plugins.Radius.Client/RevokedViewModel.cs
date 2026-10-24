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
    public string Device => Source.DeviceName.Length > 0 ? Source.DeviceName : Source.DeviceId is null ? IssuedCertificateTexts.UnknownDevice : IssuedCertificateTexts.RemovedDevice(null);

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
            ? new IssuedCertificateDetails(null, null, IssuedCertificateTexts.NoCopy)
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
/// (a second door to it), 500 per page from the server with the search (300 ms after the last key), and Revoke, which opens
/// the certificate choice and revokes the chosen ones with a reason (users with all devices).
/// </summary>
public sealed partial class RevokedViewModel : ObservableObject
{
    /// <summary>Rows per page (the server answers at most 500).</summary>
    public const int PageSize = 500;

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<bool> _mayManage;
    private readonly TimeProvider? _time;
    private CancellationTokenSource? _searchDelay;
    private int _readVersion;
    private bool _loadedOnce;

    public RevokedViewModel(ICorePluginClientContext ctx, Func<bool> mayManage, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        _time = time;
    }

    public IRevokeDialogs? Dialogs { get; set; }

    /// <summary>The shown page, newest revocation first.</summary>
    public RangeObservableCollection<RevokedRowViewModel> Rows { get; } = [];

    public bool IsLoaded => _loadedOnce;

    /// <summary>Wait after typing in the search before the server is asked.</summary>
    public TimeSpan SearchDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>How many match the search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(HasPages), nameof(CountText), nameof(EmptyText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Total { get; set; }

    /// <summary>The first row of the shown page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Offset { get; set; }

    /// <summary>"1-500 of 12,345"; empty without rows.</summary>
    public string PageText => Total == 0 ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{Offset + 1:N0}-{Math.Min(Offset + PageSize, Total):N0} of {Total:N0}");

    public bool HasPages => Total > PageSize;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressText))]
    public partial bool IsReloading { get; set; }

    /// <summary>Revoke runs on the server (up to 5,000 certificates): the progress row shows, Revoke is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(RevokeCommand))]
    public partial bool IsRevoking { get; set; }

    /// <summary>The progress row above the table: a later read or a revocation.</summary>
    public bool ShowsProgress => IsReloading || IsRevoking;

    /// <summary>"Revoking the certificates" while Revoke runs, else "Reading the revoked certificates".</summary>
    public string ProgressText => IsRevoking ? RevokingText : ReadingText;

    public const string ReadingText = "Reading the revoked certificates";

    public const string RevokingText = "Revoking the certificates";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool MayManage => _mayManage();

    /// <summary>"3 certificates revoked", with a search "2 certificates match".</summary>
    public string CountText => string.IsNullOrWhiteSpace(Search)
        ? RadiusTexts.Count(Total, "certificate revoked", "certificates revoked")
        : RadiusTexts.Count(Total, "certificate matches", "certificates match");

    public string? EmptyText => Rows.Count > 0 || !_loadedOnce ? null
        : !string.IsNullOrWhiteSpace(Search) ? "No certificate matches the search."
        : "No certificate is revoked.";

    /// <summary>Reads the page at <see cref="Offset"/> with the search (the overlay the first time, then the progress row).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var version = ++_readVersion;
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
            var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
            var query = new RevokedQuery(search, Offset, PageSize);
            var page = RadiusJson.Deserialize<RevokedPage>(await _ctx.InvokeAsync(RadiusMethods.ListRevoked, RadiusJson.Serialize(query), ct).ConfigureAwait(true));
            if (version != _readVersion)
            {
                return; // a newer read (search, page) answers
            }

            var rows = page.Rows.Select(r => new RevokedRowViewModel(r)).ToList();
            var total = page.Total;
            if (total == 0 && rows.Count > 0)
            {
                // A server part without pages sends every row: the search and the page are applied here.
                rows = [.. rows.Where(r => search is null || r.Matches(search)).OrderByDescending(r => r.RevokedSortKey)];
                total = rows.Count;
                rows = [.. rows.Skip(Offset).Take(PageSize)];
            }

            Rows.ReplaceAll(rows); // one grid reset
            _loadedOnce = true;
            Error = null;
            Total = total;
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(IsLoaded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == _readVersion)
            {
                Error = "Cannot read the revoked certificates: " + ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (version == _readVersion)
            {
                IsLoading = false;
                IsReloading = false;
            }
        }
    }

    private bool CanPrevious() => Offset > 0;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync()
    {
        Offset = Math.Max(0, Offset - PageSize);
        return LoadAsync();
    }

    private bool CanNext() => Offset + PageSize < Total;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync()
    {
        Offset += PageSize;
        return LoadAsync();
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        RevokeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchChanged(string value)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        var delay = _searchDelay = new CancellationTokenSource();
        _ = SearchLaterAsync(delay.Token);
    }

    private async Task SearchLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDelay, _time ?? TimeProvider.System, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // typed again
        }

        if (_loadedOnce || IsLoading)
        {
            Offset = 0;
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>The reason as the contract names it ("keyCompromised", ...).</summary>
    internal static string ReasonCode(RevocationReason reason) => reason switch
    {
        RevocationReason.KeyCompromised => RevokeReasons.KeyCompromised,
        RevocationReason.DeviceReplaced => RevokeReasons.DeviceReplaced,
        RevocationReason.DeviceRemoved => RevokeReasons.DeviceRemoved,
        _ => RevokeReasons.Other,
    };

    private bool CanRevoke() => MayManage && !IsRevoking;

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

        // A write on the server: it runs to its end (no Cancel), the progress row shows meanwhile.
        string? answer = null;
        IsRevoking = true;
        try
        {
            var result = RadiusJson.Deserialize<RevokeReply>(await _ctx.InvokeAsync(RadiusMethods.Revoke,
                RadiusJson.Serialize(new RevokeRequest([.. chosen.Select(r => r.Source.SerialNumber)], ReasonCode(reason))), CancellationToken.None).ConfigureAwait(true));
            answer = result.Revoked == 0 ? IssuedCertificateTexts.NothingRevoked : null;
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            answer = ServerErrors.Message(ex);
        }
        finally
        {
            IsRevoking = false;
        }

        if (answer is not null)
        {
            await _ctx.ShowMessageAsync("Revoke", answer).ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
