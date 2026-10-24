using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>What the Certificates tab asks of its view: the certificate window and the revoke confirmation.</summary>
public interface IIssuedCertificatesDialogs : IIssuedCertificateListDialogs
{
    /// <summary>The confirmation with the reason; null for Cancel.</summary>
    Task<RevocationReason?> ConfirmRevokeAsync(string title, string message);
}

/// <summary>The list reads PKI's own page methods.</summary>
internal sealed class PkiCertificateSource(ICorePluginClientContext ctx) : IIssuedCertificateSource
{
    public async Task<IssuedCertificateListPage> ReadAsync(IssuedCertificateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var request = new CertificatesRequest
        {
            State = query.State,
            Purpose = query.Purpose,
            Source = query.Source,
            Search = query.Search,
            Offset = query.Offset,
            Limit = query.Limit,
        };
        var reply = PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(PkiMethods.GetCertificates, PkiJson.Serialize(request), ct).ConfigureAwait(true));
        return new IssuedCertificateListPage(reply.Rows, reply.Total, reply.Offset) { WarningDays = reply.WarningDays };
    }

    public async Task<IssuedCertificateDetails> GetCertificateAsync(string serialNumber, CancellationToken ct)
    {
        var reply = PkiJson.Deserialize<CertificateDetailsReply>(await ctx.InvokeAsync(PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(serialNumber)), ct).ConfigureAwait(true));
        return new IssuedCertificateDetails(reply.Certificate, reply.Revocation, reply.Certificate is null ? reply.Error ?? PkiCertificateTexts.NotListed : null);
    }
}

/// <summary>
/// The Certificates tab of the PKI page (docs/spec/radius-server.md "Certificates and revocation"): the shared list of
/// issued certificates (<see cref="IssuedCertificateListViewModel"/>: pages of 500, filters, search, View certificate) read
/// from PKI, plus Revoke (several rows, the reason, confirmed) and Revoke all of this device for users with all devices who
/// may manage the CA. A change on the server (topic <c>certificates</c>) reads the shown page again.
/// </summary>
public sealed partial class IssuedCertificatesViewModel : IssuedCertificateListViewModel
{
    /// <summary>The choices of the reason, in the page's order.</summary>
    public static IReadOnlyList<RevocationReason> Reasons => IssuedCertificateTexts.Reasons;

    public const string ConfirmMessage = IssuedCertificateTexts.ConfirmMessage;

    /// <summary>The confirmation of one certificate.</summary>
    public const string ConfirmMessageOne = IssuedCertificateTexts.ConfirmMessageOne;

    private readonly ICorePluginClientContext _ctx;

    public IssuedCertificatesViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
        : base(new PkiCertificateSource(ctx ?? throw new ArgumentNullException(nameof(ctx))), time)
    {
        _ctx = ctx;
        PageSize = PkiPluginInfo.CertificatesPageSize;
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayRevoke));
            RevokeCommand.NotifyCanExecuteChanged();
            RevokeDeviceCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Users with all devices whose role manages the CA (the server refuses others).</summary>
    public bool MayRevoke => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>A live event of the page (the list changed on the server).</summary>
    public Task HandleEventAsync(string topic) => topic == PkiMethods.CertificatesTopic ? LoadAsync() : Task.CompletedTask;

    protected override void OnSelectionChanged() => RevokeCommand.NotifyCanExecuteChanged();

    protected override void OnWorkChanged()
    {
        RevokeCommand.NotifyCanExecuteChanged();
        RevokeDeviceCommand.NotifyCanExecuteChanged();
    }

    protected override Task ShowMessageAsync(string title, string message) => _ctx.ShowMessageAsync(title, message);

    private bool CanRevoke() => MayRevoke && !IsWorking && Selected.Any(r => r.CanRevoke);

    /// <summary>Revoke (red): the chosen rows that can still be revoked, with a reason, confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanRevoke))]
    private async Task RevokeAsync()
    {
        var rows = Selected.Where(r => r.CanRevoke).ToList();
        if (rows.Count == 0 || Dialogs is not IIssuedCertificatesDialogs dialogs)
        {
            return;
        }

        if (await dialogs.ConfirmRevokeAsync(IssuedCertificateTexts.RevokeTitle(rows), rows.Count == 1 ? ConfirmMessageOne : ConfirmMessage).ConfigureAwait(true) is not { } reason)
        {
            return;
        }

        await SendRevokeAsync(PkiMethods.Revoke, PkiJson.Serialize(new RevokeRequest([.. rows.Select(r => r.Source.SerialNumber)], reason))).ConfigureAwait(true);
    }

    private bool CanRevokeDevice(IssuedCertificateRow? row) => MayRevoke && !IsWorking && (row ?? (Selected.Count == 1 ? Selected[0] : null)) is not null;

    /// <summary>Revoke all of this device (row menu): every certificate of the row's device that is still valid.</summary>
    [RelayCommand(CanExecute = nameof(CanRevokeDevice))]
    private async Task RevokeDeviceAsync(IssuedCertificateRow? row)
    {
        row ??= Selected.Count == 1 ? Selected[0] : null;
        if (row is null || Dialogs is not IIssuedCertificatesDialogs dialogs)
        {
            return;
        }

        if (await dialogs.ConfirmRevokeAsync($"Revoke all certificates of {row.Device}?", ConfirmMessage).ConfigureAwait(true) is not { } reason)
        {
            return;
        }

        await SendRevokeAsync(PkiMethods.RevokeDevice, PkiJson.Serialize(new RevokeDeviceRequest(row.Source.DeviceId, reason))).ConfigureAwait(true);
    }

    /// <summary>
    /// Sends Revoke or Revoke all of this device (a write: it runs to its end, no Cancel; Revoke all of this device reads the
    /// device first, up to 20 s): the progress row above the grid meanwhile, both commands off; then the page again.
    /// </summary>
    private async Task SendRevokeAsync(string method, string payload)
    {
        string? answer;
        WorkText = IssuedCertificateTexts.Revoking;
        try
        {
            var reply = PkiJson.Deserialize<RevokeReply>(await _ctx.InvokeAsync(method, payload, CancellationToken.None).ConfigureAwait(true));
            answer = reply.Error ?? (reply.Revoked == 0 ? IssuedCertificateTexts.NothingRevoked : null);
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            answer = ServerErrors.Message(ex);
        }
        finally
        {
            WorkText = null;
        }

        if (answer is not null)
        {
            await _ctx.ShowMessageAsync("Revoke", answer).ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
