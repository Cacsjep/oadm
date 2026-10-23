using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>One row of the Certificates tab (texts for the grid; the server's row in <see cref="Source"/>).</summary>
public sealed class IssuedCertificateRow(IssuedCertificateInfo source)
{
    public IssuedCertificateInfo Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The device's address; for a device OADM no longer manages its serial, "B8A44F631339 (removed)".</summary>
    public string Device => Source.DeviceManaged ? Source.Device : (Source.DeviceSerial.Length > 0 ? Source.DeviceSerial : "Device") + " (removed)";

    public string Use => DeviceCertificatePurposes.Text(Source.Purpose);

    public string IssuedBy => Source.Source;

    public string ValidFrom => Source.NotBeforeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string ValidUntil => Source.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"7D FD 05 ..." (pairs, like the certificate window).</summary>
    public string SerialNumber => Pairs(Source.SerialNumber);

    public string State => IssuedCertificatesViewModel.StateText(Source.State);

    /// <summary>"Certificate revoked on 2026-11-02 (Key compromised) by admin" for a revoked one; null otherwise.</summary>
    public string? StateTip => Source.Revocation is { } r ? r.Describe() + " by " + r.UserName : null;

    public bool IsOk => Source.State == IssuedCertificateState.Valid;

    public bool IsWarning => Source.State == IssuedCertificateState.ExpiresSoon;

    public bool IsError => Source.State is IssuedCertificateState.Expired or IssuedCertificateState.Revoked;

    public bool IsNeutral => Source.State == IssuedCertificateState.Replaced;

    /// <summary>Still valid and not revoked: Revoke can take it.</summary>
    public bool CanRevoke => Source.State is IssuedCertificateState.Valid or IssuedCertificateState.ExpiresSoon or IssuedCertificateState.Replaced;

    private static string Pairs(string hex)
    {
        var even = hex.Length % 2 == 1 ? "0" + hex : hex;
        return string.Join(' ', Enumerable.Range(0, even.Length / 2).Select(i => even.Substring(i * 2, 2)));
    }
}

/// <summary>What the Certificates tab asks of its view: the certificate window and the revoke confirmation.</summary>
public interface IIssuedCertificatesDialogs
{
    Task ShowCertificateAsync(CertificateDetails certificate);

    /// <summary>The confirmation with the reason; null for Cancel.</summary>
    Task<RevocationReason?> ConfirmRevokeAsync(string title, string message);
}

/// <summary>
/// The Certificates tab of the PKI page (docs/spec/radius-server.md "Certificates and revocation"): every certificate the
/// OADM CA issued, newest first, read from the server one page at a time (500 rows) with the filters and the search;
/// View certificate, Revoke (several rows, the reason, confirmed) and Revoke all of this device for users with all devices
/// who may manage the CA. A change on the server (topic <c>certificates</c>) reads the shown page again.
/// </summary>
public sealed partial class IssuedCertificatesViewModel : ObservableObject
{
    /// <summary>The choices of the reason, in the page's order.</summary>
    public static readonly IReadOnlyList<RevocationReason> Reasons =
        [RevocationReason.KeyCompromised, RevocationReason.DeviceReplaced, RevocationReason.DeviceRemoved, RevocationReason.Other];

    public const string ConfirmMessage = "OADM no longer accepts them for network access or renewal. This cannot be undone.";

    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly List<IssuedCertificateRow> _selected = [];
    private CancellationTokenSource? _searchDelay;
    private int _readVersion;
    private bool _loadedOnce;
    private bool _started;

    public IssuedCertificatesViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
        StateFilter = StateChoices[0];
        UseFilter = UseChoices[0];
        SourceFilter = SourceChoices[0];
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayRevoke));
            RevokeCommand.NotifyCanExecuteChanged();
            RevokeDeviceCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>The window and the confirmation (the page view).</summary>
    public IIssuedCertificatesDialogs? Dialogs { get; set; }

    /// <summary>Wait after typing in the search before the server is asked.</summary>
    public TimeSpan SearchDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Users with all devices whose role manages the CA (the server refuses others).</summary>
    public bool MayRevoke => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    public RangeObservableCollection<IssuedCertificateRow> Rows { get; } = [];

    public IReadOnlyList<Choice> StateChoices { get; } =
    [
        new(string.Empty, "All states"),
        .. Enum.GetValues<IssuedCertificateState>().Select(s => new Choice(s.ToString(), StateText(s))),
    ];

    public IReadOnlyList<Choice> UseChoices { get; } =
    [
        new(string.Empty, "All uses"),
        .. DeviceCertificatePurposes.All.Select(p => new Choice(p, DeviceCertificatePurposes.Text(p))),
    ];

    public IReadOnlyList<Choice> SourceChoices { get; } =
    [
        new(string.Empty, "Issued by any"),
        .. IssuedCertificateSources.All.Select(s => new Choice(s, s)),
    ];

    [ObservableProperty]
    public partial Choice StateFilter { get; set; }

    [ObservableProperty]
    public partial Choice UseFilter { get; set; }

    [ObservableProperty]
    public partial Choice SourceFilter { get; set; }

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(EmptyText), nameof(HasPages))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Total { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Offset { get; set; }

    /// <summary>The first read (the overlay over the grid).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A later read (filter, page, change on the server): the progress row above the grid.</summary>
    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>"1-500 of 12,345"; empty without rows.</summary>
    public string PageText => Total == 0 ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{Offset + 1:N0}-{Math.Min(Offset + PkiPluginInfo.CertificatesPageSize, Total):N0} of {Total:N0}");

    public bool HasPages => Total > PkiPluginInfo.CertificatesPageSize;

    /// <summary>One line when the list is empty: what to do first, or that nothing matches.</summary>
    public string? EmptyText => Total > 0 || !_loadedOnce ? null
        : IsFiltered ? "No certificate matches."
        : "No certificates yet. Enable HTTPS or IEEE 802.1X on devices, or set up Certificate enrollment.";

    private bool IsFiltered => StateFilter.Value.Length > 0 || UseFilter.Value.Length > 0 || SourceFilter.Value.Length > 0 || !string.IsNullOrWhiteSpace(Search);

    /// <summary>The chosen rows (the grid's selection).</summary>
    public IReadOnlyList<IssuedCertificateRow> Selected => _selected;

    /// <summary>"State" texts, one phrase each.</summary>
    public static string StateText(IssuedCertificateState state) => state switch
    {
        IssuedCertificateState.ExpiresSoon => "Expires soon",
        IssuedCertificateState.Expired => "Expired",
        IssuedCertificateState.Replaced => "Replaced",
        IssuedCertificateState.Revoked => "Revoked",
        _ => "Valid",
    };

    /// <summary>The grid's selection changed (one step for many rows).</summary>
    public void SetSelection(IEnumerable<IssuedCertificateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _selected.Clear();
        _selected.AddRange(rows);
        ViewCertificateCommand.NotifyCanExecuteChanged();
        RevokeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the page at <see cref="Offset"/> with the filters (the first time with the overlay).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _started = true;
        var version = Interlocked.Increment(ref _readVersion);
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
            var request = new CertificatesRequest
            {
                State = Enum.TryParse<IssuedCertificateState>(StateFilter.Value, out var state) ? state : null,
                Purpose = UseFilter.Value.Length > 0 ? UseFilter.Value : null,
                Source = SourceFilter.Value.Length > 0 ? SourceFilter.Value : null,
                Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
                Offset = Offset,
                Limit = PkiPluginInfo.CertificatesPageSize,
            };
            var reply = PkiJson.Deserialize<CertificatesReply>(await _ctx.InvokeAsync(PkiMethods.GetCertificates, PkiJson.Serialize(request), ct).ConfigureAwait(true));
            if (version != Volatile.Read(ref _readVersion))
            {
                return; // a newer read (another filter) answers
            }

            var keep = _selected.Select(r => r.Source.SerialNumber).ToHashSet(StringComparer.Ordinal);
            Rows.ReplaceAll(reply.Rows.Select(r => new IssuedCertificateRow(r)));
            _loadedOnce = true;
            Offset = reply.Offset;
            Total = reply.Total;
            Error = null;
            SetSelection(Rows.Where(r => keep.Contains(r.Source.SerialNumber)));
            OnPropertyChanged(nameof(EmptyText));
            SelectionRestored?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == Volatile.Read(ref _readVersion))
            {
                Error = "Cannot read the certificates: " + ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _readVersion))
            {
                IsLoading = false;
                IsReloading = false;
            }
        }
    }

    /// <summary>The rows were replaced and the selection kept: the view selects them again in the grid.</summary>
    public event EventHandler? SelectionRestored;

    /// <summary>A live event of the page (the list changed on the server).</summary>
    public Task HandleEventAsync(string topic) => topic == PkiMethods.CertificatesTopic ? LoadAsync() : Task.CompletedTask;

    partial void OnStateFilterChanged(Choice value) => Refilter();

    partial void OnUseFilterChanged(Choice value) => Refilter();

    partial void OnSourceFilterChanged(Choice value) => Refilter();

    partial void OnSearchChanged(string? value)
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
            await Task.Delay(SearchDelay, _time, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // typed again
        }

        Refilter();
    }

    private void Refilter()
    {
        if (!_started || StateFilter is null || UseFilter is null || SourceFilter is null)
        {
            return; // not shown yet (or the constructor sets them one by one): the first read takes the filters
        }

        Offset = 0;
        _ = LoadAsync();
    }

    private bool CanPrevious() => Offset > 0;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync()
    {
        Offset = Math.Max(0, Offset - PkiPluginInfo.CertificatesPageSize);
        return LoadAsync();
    }

    private bool CanNext() => Offset + PkiPluginInfo.CertificatesPageSize < Total;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync()
    {
        Offset += PkiPluginInfo.CertificatesPageSize;
        return LoadAsync();
    }

    private bool CanView() => _selected.Count == 1;

    /// <summary>View certificate (toolbar, row menu, double click): the shared certificate window.</summary>
    [RelayCommand(CanExecute = nameof(CanView))]
    private async Task ViewCertificateAsync()
    {
        if (_selected.Count != 1)
        {
            return;
        }

        var row = _selected[0];
        try
        {
            var reply = PkiJson.Deserialize<CertificateDetailsReply>(await _ctx.InvokeAsync(PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(row.Source.SerialNumber)), CancellationToken.None).ConfigureAwait(true));
            if (reply.Certificate is not { } certificate)
            {
                await _ctx.ShowMessageAsync("View certificate", reply.Error ?? PkiCertificateTexts.NotListed).ConfigureAwait(true);
                return;
            }

            var details = new CertificateDetails(certificate, $"Certificate · {row.Device} · {row.Use}", _time.GetUtcNow().UtcDateTime, reply.Revocation);
            if (Dialogs is { } dialogs)
            {
                await dialogs.ShowCertificateAsync(details).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("View certificate", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    private bool CanRevoke() => MayRevoke && _selected.Any(r => r.CanRevoke);

    /// <summary>Revoke (red): the chosen rows that can still be revoked, with a reason, confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanRevoke))]
    private async Task RevokeAsync()
    {
        var serials = _selected.Where(r => r.CanRevoke).Select(r => r.Source.SerialNumber).ToList();
        if (serials.Count == 0 || Dialogs is not { } dialogs)
        {
            return;
        }

        var title = serials.Count == 1
            ? $"Revoke the certificate of {_selected.First(r => r.CanRevoke).Device}?"
            : string.Create(CultureInfo.CurrentCulture, $"Revoke {serials.Count:N0} certificates?");
        if (await dialogs.ConfirmRevokeAsync(title, ConfirmMessage).ConfigureAwait(true) is not { } reason)
        {
            return;
        }

        await SendRevokeAsync(PkiMethods.Revoke, PkiJson.Serialize(new RevokeRequest(serials, reason))).ConfigureAwait(true);
    }

    private bool CanRevokeDevice(IssuedCertificateRow? row) => MayRevoke && (row ?? (_selected.Count == 1 ? _selected[0] : null)) is not null;

    /// <summary>Revoke all of this device (row menu): every certificate of the row's device that is still valid.</summary>
    [RelayCommand(CanExecute = nameof(CanRevokeDevice))]
    private async Task RevokeDeviceAsync(IssuedCertificateRow? row)
    {
        row ??= _selected.Count == 1 ? _selected[0] : null;
        if (row is null || Dialogs is not { } dialogs)
        {
            return;
        }

        if (await dialogs.ConfirmRevokeAsync($"Revoke all certificates of {row.Device}?", ConfirmMessage).ConfigureAwait(true) is not { } reason)
        {
            return;
        }

        await SendRevokeAsync(PkiMethods.RevokeDevice, PkiJson.Serialize(new RevokeDeviceRequest(row.Source.DeviceId, reason))).ConfigureAwait(true);
    }

    private async Task SendRevokeAsync(string method, string payload)
    {
        try
        {
            var reply = PkiJson.Deserialize<RevokeReply>(await _ctx.InvokeAsync(method, payload, CancellationToken.None).ConfigureAwait(true));
            if (reply.Error is { } error)
            {
                await _ctx.ShowMessageAsync("Revoke", error).ConfigureAwait(true);
            }
            else if (reply.Revoked == 0)
            {
                await _ctx.ShowMessageAsync("Revoke", "Nothing was revoked: these certificates are already revoked or expired.").ConfigureAwait(true);
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
