using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>What the Certificates card asks of its view: the certificate window, the confirmations, its dialogs and file pickers.</summary>
public interface IIssuedCertificatesDialogs : IIssuedCertificateListDialogs
{
    /// <summary>The confirmation with the reason; null for Cancel.</summary>
    Task<RevocationReason?> ConfirmRevokeAsync(string title, string message);

    /// <summary>The confirmation of Delete (red Delete); true when confirmed.</summary>
    Task<bool> ConfirmDeleteAsync(string title, string message) => Task.FromResult(false);

    /// <summary>The confirmation of Delete with its own red confirm button ("Revoke and delete"); true when confirmed.</summary>
    Task<bool> ConfirmDeleteAsync(string title, string message, string confirmText) => ConfirmDeleteAsync(title, message);

    Task ShowCreateAsync(CreateCertificateViewModel dialog) => Task.CompletedTask;

    Task ShowSignRequestAsync(SignRequestViewModel dialog) => Task.CompletedTask;

    Task ShowImportCertificateAsync(ImportCertificateViewModel dialog) => Task.CompletedTask;

    Task ShowExportKeyAsync(ExportKeyViewModel dialog) => Task.CompletedTask;

    /// <summary>File pickers of the page; null without.</summary>
    IPkiFiles? CertificateFiles => null;
}

/// <summary>Which rows a list of the PKI page shows (decision 2026-10-25: the CAs in their own card).</summary>
public enum PkiListPart
{
    /// <summary>Every row (the CAs among them).</summary>
    All,

    /// <summary>The CAs only (the Certificate authorities card).</summary>
    Authorities,

    /// <summary>Every row but the CAs (the Certificates card).</summary>
    Certificates,
}

/// <summary>The list reads PKI's own page methods.</summary>
internal sealed class PkiCertificateSource(ICorePluginClientContext ctx, PkiListPart part = PkiListPart.All) : IIssuedCertificateSource
{
    public async Task<IssuedCertificateListPage> ReadAsync(IssuedCertificateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var request = new CertificatesRequest
        {
            State = query.State,
            Purpose = part == PkiListPart.Authorities ? Oadm.Sdk.Certificates.ListedCertificateUses.Ca : query.Purpose,
            NoAuthorities = part == PkiListPart.Certificates,
            Source = query.Source,
            IssuedBy = query.IssuedBy,
            Search = query.Search,
            GroupBy = query.GroupBy,
            Offset = query.Offset,
            Limit = query.Limit,
        };
        var reply = PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(PkiMethods.GetCertificates, PkiJson.Serialize(request), ct).ConfigureAwait(true));
        return new IssuedCertificateListPage(reply.Rows, reply.Total, reply.Offset)
        {
            WarningDays = reply.WarningDays,
            Issuers = reply.Issuers.Count == 0 ? null : reply.Issuers,
            Groups = reply.Groups,
        };
    }

    public async Task<IssuedCertificateDetails> GetCertificateAsync(string serialNumber, CancellationToken ct)
    {
        var reply = PkiJson.Deserialize<CertificateDetailsReply>(await ctx.InvokeAsync(PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(serialNumber)), ct).ConfigureAwait(true));
        return new IssuedCertificateDetails(reply.Certificate, reply.Revocation, reply.Certificate is null ? reply.Error ?? PkiCertificateTexts.NotListed : null);
    }
}

/// <summary>
/// The Certificates card of the PKI page (docs/spec/pki.md "Certificates"): the shared list (<see cref="IssuedCertificateListViewModel"/>:
/// pages of 500, Group by (kept on this computer), filters, search, View certificate) read from PKI, plus, for users with
/// all devices who may manage the CA, Create, Sign request and Import (toolbar), Export, Revoke and Delete (row buttons and
/// the row menu; Revoke and Delete also for several chosen rows) and Revoke all of this device. The CA rows' own actions
/// (the OADM CA's Export, Install, Back up; Remove of other CAs) are the page's (<see cref="PkiViewModel"/>). A change on
/// the server (topic <c>certificates</c>) reads the shown page again.
/// </summary>
public sealed partial class IssuedCertificatesViewModel : IssuedCertificateListViewModel
{
    /// <summary>The choices of the reason, in the page's order.</summary>
    public static IReadOnlyList<RevocationReason> Reasons => IssuedCertificateTexts.Reasons;

    public const string ConfirmMessage = IssuedCertificateTexts.ConfirmMessage;

    /// <summary>The confirmation of one certificate.</summary>
    public const string ConfirmMessageOne = IssuedCertificateTexts.ConfirmMessageOne;

    /// <summary>The progress row while Delete runs.</summary>
    public const string Deleting = "Deleting the certificates";

    private readonly ICorePluginClientContext _ctx;
    private readonly PkiClientSettingsStore _clientSettings;

    public IssuedCertificatesViewModel(ICorePluginClientContext ctx, TimeProvider? time = null, PkiClientSettingsStore? clientSettings = null, PkiListPart part = PkiListPart.All)
        : base(new PkiCertificateSource(ctx ?? throw new ArgumentNullException(nameof(ctx)), part), time)
    {
        _ctx = ctx;
        _clientSettings = clientSettings ?? new PkiClientSettingsStore();
        PageSize = PkiPluginInfo.CertificatesPageSize;

        // The CAs: a short list without groups, filters and search.
        CanGroup = part != PkiListPart.Authorities;
        ShowsFilters = part != PkiListPart.Authorities;
        ShowsUsedIn = part == PkiListPart.Authorities; // the policies that use each CA (decision 2026-10-25)
        if (CanGroup && ctx.GetLocalSetting(GroupBySetting) is { } kept && GroupChoices.FirstOrDefault(c => c.Value.ToString() == kept) is { } choice)
        {
            GroupBy = choice;
        }

        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayRevoke));
            OnPropertyChanged(nameof(MayManage));
            NotifyCommands();
        };
    }

    /// <summary>The key of the kept Group by on this computer.</summary>
    public const string GroupBySetting = "certificates.groupBy";

    /// <summary>The statuses below the state of a row (the OADM CA's trust, from the page); none without.</summary>
    public Func<IssuedCertificateInfo, IReadOnlyList<IssuedCertificateStatus>>? TrustStates { get; set; }

    /// <summary>Export of the OADM CA's row: the page's Export public certificate ("pem" or "der"); null: the row's download.</summary>
    public Func<string?, Task>? ExportOadmCa { get; set; }

    protected override IReadOnlyList<IssuedCertificateStatus> MoreStatesOf(IssuedCertificateInfo row) => TrustStates?.Invoke(row) ?? [];

    protected override void OnGroupingChanged(IssuedCertificateGrouping grouping) => _ctx.SetLocalSetting(GroupBySetting, grouping.ToString());

    /// <summary>
    /// The page's actions of a CA row (the OADM CA's Install in trusted root store and Back up; Remove of a previous or
    /// imported CA): everyday entries and red ones. Set by the page (the CA's commands are the page's).
    /// </summary>
    public Func<IssuedCertificateRow, (IReadOnlyList<IssuedCertificateAction> Everyday, IReadOnlyList<IssuedCertificateAction> Danger)>? CaActions { get; set; }

    /// <summary>
    /// A row's menu: View, Export (PEM, DER, with its key where OADM holds it), the CA's own actions, then a separator and the
    /// red ones: Revoke, Revoke all of this device, Remove, Delete. Users who may not manage the CA get View only.
    /// </summary>
    protected override IReadOnlyList<IssuedCertificateAction> ActionsOf(IssuedCertificateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        List<IssuedCertificateAction> everyday = [new("View", ViewRowCommand, row)];
        List<IssuedCertificateAction> danger = [];
        if (MayManage)
        {
            everyday.Add(new("Export", Items:
            [
                new("PEM (.crt)", ExportPemCommand, row),
                new("DER (.cer)", ExportDerCommand, row),
                .. row.HasKey ? [new IssuedCertificateAction("With its key (.p12)", ExportKeyCommand, row)] : Array.Empty<IssuedCertificateAction>(),
            ]));
            if (row.CanRevoke)
            {
                danger.Add(new("Revoke", RevokeRowCommand, row, IsDanger: true));
            }

            if (row.Source.Kind == ListedCertificateKind.Device)
            {
                danger.Add(new("Revoke all of this device", RevokeDeviceCommand, row, IsDanger: true));
            }

            if (row.CanDelete)
            {
                danger.Add(new("Delete", DeleteRowCommand, row, IsDanger: true));
            }
        }

        if (CaActions?.Invoke(row) is { } ca)
        {
            everyday.AddRange(ca.Everyday);
            danger.InsertRange(0, ca.Danger);
        }

        return danger.Count == 0 ? everyday : [.. everyday, IssuedCertificateAction.Separator, .. danger];
    }

    /// <summary>Several chosen rows: Revoke and Delete of all of them (red).</summary>
    protected override IReadOnlyList<IssuedCertificateAction> SelectionActions() => !MayManage ? [] :
    [
        new($"Revoke {Selected.Count:N0} certificates", RevokeCommand, IsDanger: true),
        new($"Delete {Selected.Count:N0} certificates", DeleteCommand, IsDanger: true),
    ];

    /// <summary>The OADM CA's trust changed: its row shows it.</summary>
    public void RefreshTrust() => RefreshRows();

    /// <summary>The policies of the page (the issuing policies of Create and Sign request); empty without.</summary>
    public Func<IReadOnlyList<PkiPolicy>> PolicySource { get; set; } = () => [];

    /// <summary>Users with all devices whose role manages the CA (the server refuses others).</summary>
    public bool MayRevoke => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>Create, Sign request, Import, Export and Delete: the same users as Revoke.</summary>
    public bool MayManage => MayRevoke;

    /// <summary>A live event of the page (the list changed on the server).</summary>
    public Task HandleEventAsync(string topic) => topic == PkiMethods.CertificatesTopic ? LoadAsync() : Task.CompletedTask;

    protected override void OnSelectionChanged() => NotifyCommands();

    protected override void OnWorkChanged() => NotifyCommands();

    protected override Task ShowMessageAsync(string title, string message) => _ctx.ShowMessageAsync(title, message);

    private void NotifyCommands()
    {
        RevokeCommand.NotifyCanExecuteChanged();
        RevokeDeviceCommand.NotifyCanExecuteChanged();
        RevokeRowCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeleteRowCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        SignRequestCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        ExportPemCommand.NotifyCanExecuteChanged();
        ExportDerCommand.NotifyCanExecuteChanged();
        ExportKeyCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ revoke

    private bool CanRevoke() => MayRevoke && !IsWorking && Selected.Any(r => r.CanRevoke);

    /// <summary>Revoke (red, row menu): the chosen rows that can still be revoked, with a reason, confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanRevoke))]
    private Task RevokeAsync() => RevokeRowsAsync([.. Selected.Where(r => r.CanRevoke)]);

    private bool CanRevokeRow(IssuedCertificateRow? row) => MayRevoke && !IsWorking && row is { CanRevoke: true };

    /// <summary>Revoke (row button): this row.</summary>
    [RelayCommand(CanExecute = nameof(CanRevokeRow))]
    private Task RevokeRowAsync(IssuedCertificateRow? row) => row is { CanRevoke: true } ? RevokeRowsAsync([row]) : Task.CompletedTask;

    private async Task RevokeRowsAsync(List<IssuedCertificateRow> rows)
    {
        if (rows.Count == 0 || Dialogs is not IIssuedCertificatesDialogs dialogs)
        {
            return;
        }

        if (await dialogs.ConfirmRevokeAsync(RevokeTitle(rows), rows.Count == 1 ? ConfirmMessageOne : ConfirmMessage).ConfigureAwait(true) is not { } reason)
        {
            return;
        }

        await SendRevokeAsync(PkiMethods.Revoke, PkiJson.Serialize(new RevokeRequest([.. rows.Select(r => r.Source.SerialNumber)], reason))).ConfigureAwait(true);
    }

    /// <summary>"Revoke the certificate of 10.0.0.48?", "Revoke the certificate nvr.example.com?" or "Revoke 3 certificates?".</summary>
    private static string RevokeTitle(List<IssuedCertificateRow> rows) =>
        rows.Count == 1 && rows[0].Source.Kind != ListedCertificateKind.Device ? $"Revoke the certificate {rows[0].Name}?" : IssuedCertificateTexts.RevokeTitle(rows);

    private bool CanRevokeDevice(IssuedCertificateRow? row) =>
        MayRevoke && !IsWorking && (row ?? (Selected.Count == 1 ? Selected[0] : null)) is { Source.Kind: ListedCertificateKind.Device };

    /// <summary>Revoke all of this device (row menu): every certificate of the row's device that is still valid.</summary>
    [RelayCommand(CanExecute = nameof(CanRevokeDevice))]
    private async Task RevokeDeviceAsync(IssuedCertificateRow? row)
    {
        row ??= Selected.Count == 1 ? Selected[0] : null;
        if (row is not { Source.Kind: ListedCertificateKind.Device } || Dialogs is not IIssuedCertificatesDialogs dialogs)
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
    /// device first, up to 20 s): the progress row above the grid meanwhile, the commands off; then the page again.
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

    // ------------------------------------------------------------------ delete

    private bool CanDelete() => MayManage && !IsWorking && Selected.Count > 0 && Selected.All(r => r.CanDelete);

    /// <summary>Delete (row menu): the chosen rows, all of which nothing uses; confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => DeleteRowsAsync([.. Selected]);

    private bool CanDeleteRow(IssuedCertificateRow? row) => MayManage && !IsWorking && row is { CanDelete: true };

    /// <summary>Delete (row button): this row.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRow))]
    private Task DeleteRowAsync(IssuedCertificateRow? row) => row is { CanDelete: true } ? DeleteRowsAsync([row]) : Task.CompletedTask;

    private async Task DeleteRowsAsync(List<IssuedCertificateRow> rows)
    {
        if (rows.Count == 0 || Dialogs is not IIssuedCertificatesDialogs dialogs)
        {
            return;
        }

        // Still valid ones created or signed here are revoked first (decision 2026-10-25: Revoke and delete).
        var revoking = rows.Count(r => r.DeleteRevokes);
        var message = rows.Count == 1 ? IssuedCertificateTexts.DeleteMessageOne : IssuedCertificateTexts.DeleteMessage;
        if (revoking > 0)
        {
            message = (rows.Count == 1 ? IssuedCertificateTexts.RevokedFirstOne(rows[0].Name) : IssuedCertificateTexts.RevokedFirst(revoking)) + " " + message;
        }

        if (!await dialogs.ConfirmDeleteAsync(IssuedCertificateTexts.DeleteTitle(rows), message, revoking > 0 ? IssuedCertificateTexts.RevokeAndDelete : "Delete")
                .ConfigureAwait(true))
        {
            return;
        }

        string? answer;
        WorkText = Deleting;
        try
        {
            var reply = PkiJson.Deserialize<DeleteCertificatesReply>(await _ctx.InvokeAsync(CertificateMethods.Delete,
                PkiJson.Serialize(new DeleteCertificatesRequest([.. rows.Select(r => r.Key)])), CancellationToken.None).ConfigureAwait(true));
            answer = reply.Error;
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
            await _ctx.ShowMessageAsync("Delete", answer).ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ create, sign request, import

    private bool CanAdd() => MayManage && !IsWorking;

    /// <summary>Create (toolbar): OADM makes the key and the certificate; the user saves it as a PKCS#12 file.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task CreateAsync()
    {
        if (Dialogs is IIssuedCertificatesDialogs dialogs)
        {
            await dialogs.ShowCreateAsync(new CreateCertificateViewModel(SubmitCreateAsync, PolicySource())).ConfigureAwait(true);
        }
    }

    /// <summary>Sign request (toolbar): the OADM CA signs a request of another system.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task SignRequestAsync()
    {
        if (Dialogs is IIssuedCertificatesDialogs dialogs)
        {
            await dialogs.ShowSignRequestAsync(new SignRequestViewModel(SubmitSignAsync, PolicySource())).ConfigureAwait(true);
        }
    }

    /// <summary>Import (toolbar): a certificate, or a PKCS#12 file with its key.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task ImportAsync()
    {
        if (Dialogs is IIssuedCertificatesDialogs dialogs)
        {
            await dialogs.ShowImportCertificateAsync(new ImportCertificateViewModel(SubmitImportAsync)).ConfigureAwait(true);
        }
    }

    /// <summary>Create: the server makes it, then the PKCS#12 file with the dialog's password is saved where the user says.</summary>
    internal async Task<bool> SubmitCreateAsync(CreateCertificateViewModel dialog, CreateCertificateRequest request, string password)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var reply = PkiJson.Deserialize<CertificateActionReply>(await _ctx.InvokeAsync(CertificateMethods.Create, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true));
        if (!reply.Ok)
        {
            dialog.SetServerErrors(reply.Errors, reply.Error, nameof(CreateCertificateViewModel.Name));
            return false;
        }

        _ = LoadAsync();

        // The certificate exists now: a cancelled save leaves it in the list (Export with key later).
        await SaveDownloadAsync(dialog.Files, "Save the certificate with its key", new DownloadCertificateRequest(reply.Id!, CertificateFormats.Pkcs12, password), PkiFileKind.Pkcs12)
            .ConfigureAwait(true);
        return true;
    }

    internal async Task<bool> SubmitSignAsync(SignRequestViewModel dialog, SignCertificateRequest request)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var reply = PkiJson.Deserialize<CertificateActionReply>(await _ctx.InvokeAsync(CertificateMethods.Sign, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true));
        if (!reply.Ok)
        {
            dialog.SetServerErrors(reply.Errors, reply.Error, nameof(SignRequestViewModel.File));
            return false;
        }

        _ = LoadAsync();
        await SaveDownloadAsync(dialog.Files, "Save the signed certificate", new DownloadCertificateRequest(reply.Id!, CertificateFormats.Pem), PkiFileKind.CertificatePem).ConfigureAwait(true);
        return true;
    }

    internal async Task<bool> SubmitImportAsync(ImportCertificateViewModel dialog, ImportCertificateRequest request)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var reply = PkiJson.Deserialize<CertificateActionReply>(await _ctx.InvokeAsync(CertificateMethods.Import, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true));
        if (!reply.Ok)
        {
            dialog.SetServerErrors(reply.Errors, reply.Error, nameof(ImportCertificateViewModel.File));
            return false;
        }

        await LoadAsync().ConfigureAwait(true);
        return true;
    }

    // ------------------------------------------------------------------ export

    private bool CanExport(IssuedCertificateRow? row) => MayManage && !IsWorking && (row ?? (Selected.Count == 1 ? Selected[0] : null)) is not null;

    private bool CanExportKey(IssuedCertificateRow? row) => MayManage && !IsWorking && (row ?? (Selected.Count == 1 ? Selected[0] : null)) is { HasKey: true };

    /// <summary>Export, PEM (.crt).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPemAsync(IssuedCertificateRow? row) => ExportAsync(row, CertificateFormats.Pem, PkiFileKind.CertificatePem);

    /// <summary>Export, DER (.cer).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportDerAsync(IssuedCertificateRow? row) => ExportAsync(row, CertificateFormats.Der, PkiFileKind.CertificateDer);

    /// <summary>Export with key (PKCS#12): only where OADM holds the key; the password dialog first.</summary>
    [RelayCommand(CanExecute = nameof(CanExportKey))]
    private async Task ExportKeyAsync(IssuedCertificateRow? row)
    {
        row ??= Selected.Count == 1 ? Selected[0] : null;
        if (row is not { HasKey: true } || Dialogs is not IIssuedCertificatesDialogs dialogs)
        {
            return;
        }

        var key = row.Key;
        await dialogs.ShowExportKeyAsync(new ExportKeyViewModel(row.Name, async (dialog, password) =>
        {
            await SaveDownloadAsync(dialog.Files, "Save the certificate with its key", new DownloadCertificateRequest(key, CertificateFormats.Pkcs12, password), PkiFileKind.Pkcs12, throwErrors: true)
                .ConfigureAwait(true);
            return true;
        })).ConfigureAwait(true);
    }

    private async Task ExportAsync(IssuedCertificateRow? row, string format, PkiFileKind kind)
    {
        row ??= Selected.Count == 1 ? Selected[0] : null;
        if (row is { IsOadmCa: true } && ExportOadmCa is { } exportCa)
        {
            await exportCa(format == CertificateFormats.Der ? "der" : "pem").ConfigureAwait(true); // the public certificate, as Export public certificate
            return;
        }

        if (row is null || Dialogs is not IIssuedCertificatesDialogs dialogs)
        {
            return;
        }

        await SaveDownloadAsync(dialogs.CertificateFiles, "Export certificate", new DownloadCertificateRequest(row.Key, format), kind).ConfigureAwait(true);
    }

    /// <summary>
    /// Downloads the file of <paramref name="request"/> and saves it where the user says (the folder of the last export first).
    /// A refusal is a message, or thrown to the dialog (<paramref name="throwErrors"/>: shown below its password).
    /// </summary>
    private async Task SaveDownloadAsync(IPkiFiles? files, string title, DownloadCertificateRequest request, PkiFileKind kind, bool throwErrors = false)
    {
        files ??= (Dialogs as IIssuedCertificatesDialogs)?.CertificateFiles;
        if (files is null)
        {
            return;
        }

        byte[] data;
        string fileName;
        try
        {
            await using var download = await _ctx.OpenDownloadAsync(CertificateMethods.Download, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true);
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer).ConfigureAwait(true);
            (data, fileName) = (buffer.ToArray(), download.FileName);
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex) when (!throwErrors)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(title, ServerErrors.Message(ex)).ConfigureAwait(true);
            return;
        }

        var settings = _clientSettings.Load();
        var folder = await files.SaveAsync(title, fileName, kind, data, settings.LastFolder).ConfigureAwait(true);
        if (folder is { Length: > 0 })
        {
            settings.LastFolder = folder;
            _clientSettings.Save(settings);
        }
    }
}
