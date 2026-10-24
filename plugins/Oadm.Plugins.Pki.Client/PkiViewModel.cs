using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.TrustStore;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Client;

/// <summary>One choice of a select field.</summary>
public sealed record Choice(string Value, string Label);

/// <summary>One row of "Previous certificate authorities".</summary>
public sealed class PreviousCaRowViewModel(PreviousCaInfo info)
{
    public string Id { get; } = info.Id;

    public string Name { get; } = info.CommonName;

    public string ValidUntil { get; } = info.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string Replaced { get; } = info.ReplacedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string Tooltip { get; } = info.Devices switch
    {
        0 => info.Subject,
        1 => info.Subject + " · 1 device still uses a certificate of this CA",
        _ => $"{info.Subject} · {info.Devices} devices still use certificates of this CA",
    };
}

/// <summary>
/// The PKI page (docs/spec/pki.md "The PKI page"): the tab Certificates with Policies and the list of every certificate,
/// the CAs among them with the OADM CA's actions (trusted root store, export, backup, new CA, import, remove), and the server
/// tabs the host hands over (<see cref="Tabs"/>). <see cref="Activate"/> while the page is shown (reads everything and
/// watches the live events).
/// </summary>
public sealed partial class PkiViewModel : ValidatingViewModel, IDisposable
{
    /// <summary>Wait before watching again after the event stream ended or failed.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public const string TrustTooltip = "Firefox and Java use their own certificate stores: import the exported certificate there separately.";

    private readonly ICorePluginClientContext _ctx;
    private readonly ITrustStoreInstaller _clientTrust;
    private readonly PkiClientSettingsStore _clientSettings;
    private CancellationTokenSource? _active;
    private string? _clientCheckedCaId;

    /// <param name="ctx">Page context of the host.</param>
    /// <param name="clientTrust">This computer's trusted root store (<see cref="TrustStoreInstallers.ForClient"/>); tests pass a fake.</param>
    /// <param name="clientSettings">Where the last folder is remembered.</param>
    /// <summary>
    /// The role may manage the certificate authority ("oadm.pki:manage": CA actions and settings) and the user has all
    /// devices (the CA is server-wide; users limited to a device group only read the page).
    /// </summary>
    public bool MayManage => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>The role may install the CA in the server's trusted root store ("oadm.pki:trustServer"), for users with all devices.</summary>
    public bool MayTrustServer => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, PkiPermissions.TrustServer));

    public PkiViewModel(ICorePluginClientContext ctx, ITrustStoreInstaller clientTrust, PkiClientSettingsStore? clientSettings = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Policies = new PoliciesViewModel(ctx);
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayManage));
            OnPropertyChanged(nameof(MayTrustServer));
            Policies.NotifyRole();
        };
        _clientTrust = clientTrust ?? throw new ArgumentNullException(nameof(clientTrust));
        _clientSettings = clientSettings ?? new PkiClientSettingsStore();
        Certificates = new IssuedCertificatesViewModel(ctx, clientSettings: _clientSettings)
        {
            PolicySource = () => [.. Policies.List.Rows.Select(r => r.Policy)],
            TrustStates = TrustStatesOf,
            ExportOadmCa = ExportAsync,
            CaActions = CaActionsOf,
        };
        InitTabs();
    }

    /// <summary>The dialogs and file pickers of the page view.</summary>
    public IPkiDialogs? Dialogs { get; set; }

    public ObservableCollection<PreviousCaRowViewModel> PreviousCas { get; } = [];

    /// <summary>The last state of the server.</summary>
    public PkiState? State { get; private set; }

    // ---------------------------------------------------------------- header status

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOk), nameof(IsStatusWarning), nameof(IsStatusError), nameof(IsStatusAccent), nameof(ShowCaStatus), nameof(ShowCaNotice))]
    public partial string StatusKind { get; set; } = "accent";

    /// <summary>
    /// The PKI status stands above the list: a CA whose key cannot be read, that expired or ends soon, a failed start. Not
    /// while it is fine (its row says it), without a CA (<see cref="NoCaText"/>) or while one is created (the progress row).
    /// </summary>
    public bool ShowCaStatus => HasCa && !IsGenerating && StatusKind is ServiceStatus.Warning or ServiceStatus.Error;

    /// <summary>Something about the OADM CA stands above the list: it is being created, there is none, or <see cref="ShowCaStatus"/>.</summary>
    public bool ShowCaNotice => IsGenerating || HasNoCa || ShowCaStatus;

    public bool IsStatusOk => StatusKind == ServiceStatus.Ok;

    public bool IsStatusWarning => StatusKind == ServiceStatus.Warning;

    public bool IsStatusError => StatusKind == ServiceStatus.Error;

    public bool IsStatusAccent => StatusKind == "accent";

    // ---------------------------------------------------------------- certificate authority card

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCa), nameof(ShowCaStatus), nameof(ShowCaNotice))]
    [NotifyCanExecuteChangedFor(nameof(InstallTrustCommand), nameof(ExportCommand), nameof(BackupCommand))]
    public partial bool HasCa { get; set; }

    public bool HasNoCa => !HasCa;

    [ObservableProperty]
    public partial string NoCaText { get; set; } = "Loading the certificate authority.";

    [ObservableProperty]
    public partial string CaName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CaSourceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KeyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Fingerprint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChain))]
    public partial string? ChainText { get; set; }

    public bool HasChain => !string.IsNullOrEmpty(ChainText);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand))]
    public partial bool IsKeyUnreadable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaStatus), nameof(ShowCaNotice))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(ImportCommand), nameof(InstallTrustCommand), nameof(BackupCommand))]
    public partial bool IsGenerating { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(ImportCommand), nameof(InstallTrustCommand), nameof(BackupCommand), nameof(ExportCommand))]
    public partial bool IsUnavailable { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallTrustCommand))]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial string ServerTrustText { get; set; } = "Checking the server";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerTrustOk), nameof(IsServerTrustError), nameof(IsServerTrustAccent))]
    public partial string ServerTrustKind { get; set; } = ServiceStatus.Neutral;

    public bool IsServerTrustOk => ServerTrustKind == ServiceStatus.Ok;

    public bool IsServerTrustError => ServerTrustKind == ServiceStatus.Error;

    public bool IsServerTrustAccent => ServerTrustKind == "accent";

    [ObservableProperty]
    public partial string? ServerTrustTip { get; set; }

    [ObservableProperty]
    public partial string ClientTrustText { get; set; } = "Checking this computer";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClientTrustOk), nameof(IsClientTrustError), nameof(IsClientTrustAccent))]
    public partial string ClientTrustKind { get; set; } = ServiceStatus.Neutral;

    public bool IsClientTrustOk => ClientTrustKind == ServiceStatus.Ok;

    public bool IsClientTrustError => ClientTrustKind == ServiceStatus.Error;

    public bool IsClientTrustAccent => ClientTrustKind == "accent";

    [ObservableProperty]
    public partial string? ClientTrustTip { get; set; }


    public bool IsActive => _active is not null;

    // ---------------------------------------------------------------- policies and certificates

    /// <summary>The Certificates card: every certificate the OADM CA issued, revoke.</summary>
    public IssuedCertificatesViewModel Certificates { get; }

    /// <summary>The Policies card.</summary>
    public PoliciesViewModel Policies { get; }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Page shown: read the state and watch the live events.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        _ = RunAsync(_active.Token);
    }

    /// <summary>Page hidden: stop watching.</summary>
    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose() => Deactivate();

    /// <summary>Reads the whole state; the settings form is filled the first time (later reads keep what the user typed).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _ctx.InvokeAsync(PkiMethods.GetState, null, ct).ConfigureAwait(true);
            if (json is not null)
            {
                await ApplyStateAsync(PkiJson.Deserialize<PkiState>(json)).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any server error is shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus(ServiceStatus.Error, "Cannot read the PKI state", PkiDialogViewModel.Message(ex));
        }
    }

    /// <summary>Applies one live event (also used by tests).</summary>
    public Task HandleEventAsync(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Topic == PkiMethods.CertificatesTopic)
        {
            return Certificates.HandleEventAsync(item.Topic);
        }

        if (item.Topic == PolicyMethods.Topic)
        {
            return Policies.LoadAsync(_active?.Token ?? CancellationToken.None);
        }

        return item.Topic == PkiMethods.StateTopic && !string.IsNullOrEmpty(item.PayloadJson)
            ? ApplyStateAsync(PkiJson.Deserialize<PkiState>(item.PayloadJson))
            : Task.CompletedTask;
    }

    /// <summary>Shows a state of the server (also used by tests and the screenshots).</summary>
    public async Task ApplyStateAsync(PkiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        SetStatus(state.Status.Kind, state.Status.Text, state.Status.Detail);
        IsGenerating = state.IsGenerating;
        IsUnavailable = state.Unavailable;
        IsKeyUnreadable = state.KeyUnreadable;
        if (state.Ca is { } ca)
        {
            HasCa = true;
            CaName = ca.CommonName;
            CaSourceText = ca.Source == CaSource.Imported ? "Imported" : "Generated";
            ValidText = $"{ca.NotBeforeUtc:yyyy-MM-dd} to {ca.NotAfterUtc:yyyy-MM-dd}";
            KeyText = state.KeyUnreadable ? ca.KeyType + " · the key cannot be read" : ca.KeyType;
            Fingerprint = ca.Fingerprint;
            ChainText = ca.IsIntermediate && ca.ChainSubjects.Count > 0 ? "Issued by " + string.Join(", issued by ", ca.ChainSubjects) : null;
        }
        else
        {
            HasCa = false;
            NoCaText = state.Unavailable ? "The server cannot keep a certificate authority (no secret protection)."
                : state.IsGenerating ? "Creating the certificate authority."
                : NoCaYet;
        }

        ApplyServerTrust(state.ServerTrustInstalled, state.ServerTrustError);

        PreviousCas.Clear();
        foreach (var previous in state.PreviousCas)
        {
            PreviousCas.Add(new PreviousCaRowViewModel(previous));
        }


        await CheckClientTrustAsync(state).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Text of the confirmation before the install: the CA's SHA-256 fingerprint, to compare with the page.</summary>
    internal static string InstallTrustMessage(CaInfo ca) =>
        $"The OADM server and this computer will trust every certificate {ca.CommonName} issues. Install only when the "
        + $"fingerprint is the one shown on this page.\n\nSHA-256 fingerprint:\n{ca.Fingerprint}";

    [RelayCommand(CanExecute = nameof(CanInstallTrust))]
    private async Task InstallTrustAsync()
    {
        if (State?.Ca is { } toInstall
            && !await _ctx.ConfirmAsync("Install in trusted root store", InstallTrustMessage(toInstall), "Install").ConfigureAwait(true))
        {
            return;
        }

        IsInstalling = true;
        try
        {
            ServerTrustText = "Installing on the server";
            ServerTrustKind = "accent";
            var reply = PkiJson.Deserialize<InstallReply>(await _ctx.InvokeAsync(PkiMethods.InstallServerTrust, null, CancellationToken.None).ConfigureAwait(true));
            ApplyServerTrust(reply.Installed, reply.Error);
            if (reply.State is { } state)
            {
                State = state;
            }

            if (State is not { Ca: { } ca } current)
            {
                return;
            }

            if (current.Simulated)
            {
                SetClientTrust(ServiceStatus.Neutral, "Not installed on this computer", "Fake mode: nothing is installed on this computer.");
                return;
            }

            SetClientTrust("accent", "Installing on this computer", null);
            using var certificate = Certificate(ca);
            var result = await Task.Run(() => _clientTrust.InstallAsync(certificate, CancellationToken.None)).ConfigureAwait(true);
            if (result.Installed)
            {
                SetClientTrust(ServiceStatus.Ok, "Installed on this computer", TrustTooltip);
            }
            else
            {
                SetClientTrust(ServiceStatus.Error, "Failed on this computer", result.Error);
            }

            _clientCheckedCaId = ca.Id;
        }
#pragma warning disable CA1031 // Shown in the chips.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ApplyServerTrust(false, PkiDialogViewModel.Message(ex));
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private bool CanInstallTrust() => HasCa && !IsInstalling && !IsGenerating && !IsUnavailable;

    /// <summary>Exports the public certificate: "pem" (.crt) or "der" (.cer).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        var der = string.Equals(format, "der", StringComparison.OrdinalIgnoreCase);
        var reply = PkiJson.Deserialize<FileReply>(await _ctx.InvokeAsync(PkiMethods.ExportPublic, PkiJson.Serialize(new ExportRequest(der ? "der" : "pem")), CancellationToken.None).ConfigureAwait(true));
        await SaveFileAsync("Export public certificate", reply, der ? PkiFileKind.CertificateDer : PkiFileKind.CertificatePem).ConfigureAwait(true);
    }

    private bool CanExport(string? format) => HasCa && !IsUnavailable;

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        await Dialogs.ShowBackupAsync(new BackupViewModel(SubmitBackupAsync)).ConfigureAwait(true);
    }

    private bool CanBackup() => HasCa && !IsKeyUnreadable && !IsGenerating && !IsUnavailable;

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task GenerateAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var name = State?.Ca is { Source: CaSource.Generated } ca ? ca.CommonName : "OADM Root CA";
        await Dialogs.ShowGenerateAsync(new GenerateCaViewModel(SubmitGenerateAsync, name)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task ImportAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        await Dialogs.ShowImportAsync(new ImportCaViewModel(SubmitImportAsync)).ConfigureAwait(true);
    }

    private bool CanReplace() => !IsGenerating && !IsUnavailable;

    private async Task ExportPreviousAsync(PreviousCaRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var reply = PkiJson.Deserialize<FileReply>(await _ctx.InvokeAsync(PkiMethods.ExportPrevious, PkiJson.Serialize(new ExportPreviousRequest(row.Id, "pem")), CancellationToken.None).ConfigureAwait(true));
        await SaveFileAsync("Export previous CA certificate", reply, PkiFileKind.CertificatePem).ConfigureAwait(true);
    }

    private async Task RemovePreviousAsync(PreviousCaRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirmed = await _ctx.ConfirmAsync(
            "Remove previous CA",
            $"OADM no longer counts certificates issued by {row.Name} as trusted. Devices that still use them show the certificate as untrusted until they are renewed. Remove {row.Name}?",
            "Remove").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.RemovePrevious, PkiJson.Serialize(new RemovePreviousRequest(row.Id)), CancellationToken.None).ConfigureAwait(true));
        if (reply.Error is { } error)
        {
            await _ctx.ShowMessageAsync("Remove previous CA", error).ConfigureAwait(true);
        }

        if (reply.State is { } state)
        {
            await ApplyStateAsync(state).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ImportAuthorityAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        await Dialogs.ShowImportAuthorityAsync(new ImportAuthorityViewModel(SubmitImportAuthorityAsync)).ConfigureAwait(true);
    }

    /// <summary>Import CA of the list: the server checks the file (errors under it), then the state.</summary>
    internal async Task<bool> SubmitImportAuthorityAsync(ImportAuthorityViewModel dialog, ImportAuthorityRequest request)
    {
        var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.ImportAuthority, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true));
        if (!reply.Ok)
        {
            var message = reply.Errors?.Values.FirstOrDefault() ?? reply.Error ?? "The CA could not be imported.";
            dialog.SetServerErrors(null, message, nameof(ImportAuthorityViewModel.File));
            return false;
        }

        if (reply.State is { } state)
        {
            await ApplyStateAsync(state).ConfigureAwait(true);
        }

        return true;
    }

    private bool CanRemoveCa(IssuedCertificateRow? row) => MayManage && row is { IsRemovableCa: true };

    /// <summary>
    /// Remove of a CA row (red): a previous OADM CA (its certificates are no longer trusted) or an imported CA, confirmed;
    /// the server refuses one a policy or a service uses, with the reason. The list is read again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveCa))]
    private async Task RemoveCaAsync(IssuedCertificateRow? row)
    {
        if (row is not { IsRemovableCa: true } || row.Source.AuthorityId is not { } id)
        {
            return;
        }

        if (row.AuthorityKind == Oadm.Sdk.Certificates.CertificateAuthorityKind.PreviousOadm)
        {
            if (PreviousCas.FirstOrDefault(p => string.Equals(PkiService.PreviousAuthorityId(p.Id), id, StringComparison.OrdinalIgnoreCase)) is { } previous)
            {
                await RemovePreviousAsync(previous).ConfigureAwait(true);
            }
        }
        else if (await _ctx.ConfirmAsync("Remove CA", $"Services can no longer use {row.Name}. Remove {row.Name}?", "Remove").ConfigureAwait(true))
        {
            var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.RemoveAuthority, PkiJson.Serialize(new RemoveAuthorityRequest(id)), CancellationToken.None).ConfigureAwait(true));
            if (reply.Error is { } error)
            {
                await _ctx.ShowMessageAsync("Remove CA", error).ConfigureAwait(true);
            }

            if (reply.State is { } state)
            {
                await ApplyStateAsync(state).ConfigureAwait(true);
            }
        }

        await Certificates.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The CA entries of a row's menu: the OADM CA's Install in trusted root store (its own permission) and Back up; Remove
    /// (red) of a previous or imported CA.
    /// </summary>
    private (IReadOnlyList<IssuedCertificateAction> Everyday, IReadOnlyList<IssuedCertificateAction> Danger) CaActionsOf(IssuedCertificateRow row)
    {
        List<IssuedCertificateAction> everyday = [];
        List<IssuedCertificateAction> danger = [];
        if (row.IsOadmCa && MayTrustServer)
        {
            everyday.Add(new("Install in trusted root store", InstallTrustCommand));
        }

        if (row.IsOadmCa && MayManage)
        {
            everyday.Add(new("Back up", BackupCommand));
        }

        if (row.IsRemovableCa && MayManage)
        {
            danger.Add(new("Remove", RemoveCaCommand, row, IsDanger: true));
        }

        return (everyday, danger);
    }

    /// <summary>The line without a CA: what to do first.</summary>
    public const string NoCaYet = "There is no OADM CA yet. Choose New OADM CA under Create, or import one under Import.";

    /// <summary>The OADM CA row's statuses: whether it is in the trusted root store of the server and of this computer.</summary>
    private List<IssuedCertificateStatus> TrustStatesOf(Oadm.Sdk.Certificates.IssuedCertificateInfo row)
    {
        if (row is not { Kind: Oadm.Sdk.Certificates.ListedCertificateKind.Authority, AuthorityKind: Oadm.Sdk.Certificates.CertificateAuthorityKind.Oadm })
        {
            return [];
        }

        return [new IssuedCertificateStatus(ServerTrustText, ServerTrustKind, ServerTrustTip), new IssuedCertificateStatus(ClientTrustText, ClientTrustKind, ClientTrustTip)];
    }

    partial void OnServerTrustTextChanged(string value) => Certificates?.RefreshTrust();

    partial void OnClientTrustTextChanged(string value) => Certificates?.RefreshTrust();

    partial void OnServerTrustTipChanged(string? value) => Certificates?.RefreshTrust();

    partial void OnClientTrustTipChanged(string? value) => Certificates?.RefreshTrust();

    // ---------------------------------------------------------------- dialog backends

    /// <summary>Generate: the replace confirmation first (field rules are the server's), then the new CA.</summary>
    internal async Task<bool> SubmitGenerateAsync(GenerateCaViewModel dialog, GenerateRequest request)
    {
        if (HasCa && !await ConfirmReplaceAsync(dialog).ConfigureAwait(true))
        {
            return false;
        }

        dialog.IsBusy = true;
        var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.Generate, PkiJson.Serialize(request with { Confirmed = true }), CancellationToken.None).ConfigureAwait(true));
        return await FinishReplaceAsync(dialog, reply, PkiFields.CommonName).ConfigureAwait(true);
    }

    /// <summary>Import: the server checks the file first (field errors), then the replace confirmation, then the import.</summary>
    internal async Task<bool> SubmitImportAsync(ImportCaViewModel dialog, ImportRequest request)
    {
        dialog.IsBusy = true;
        var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.Import, PkiJson.Serialize(request with { Confirmed = false }), CancellationToken.None).ConfigureAwait(true));
        if (reply.NeedsConfirmation)
        {
            dialog.IsBusy = false;
            if (!await dialog.ConfirmAsync(ReplaceTitle, ReplaceMessage(reply.DevicesWithCurrentCa), "Replace").ConfigureAwait(true))
            {
                return false;
            }

            dialog.IsBusy = true;
            reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.Import, PkiJson.Serialize(request with { Confirmed = true }), CancellationToken.None).ConfigureAwait(true));
        }

        return await FinishReplaceAsync(dialog, reply, PkiFields.File).ConfigureAwait(true);
    }

    /// <summary>Backup: the server builds the PKCS#12, then the save dialog.</summary>
    internal async Task<bool> SubmitBackupAsync(BackupViewModel dialog, string password)
    {
        var reply = PkiJson.Deserialize<FileReply>(await _ctx.InvokeAsync(PkiMethods.Backup, PkiJson.Serialize(new BackupRequest(password)), CancellationToken.None).ConfigureAwait(true));
        if (reply.Errors is { Count: > 0 } || reply.Error is not null)
        {
            dialog.SetServerErrors(reply.Errors, reply.Error, PkiFields.Password);
            return false;
        }

        var files = dialog.Files ?? Dialogs?.Files;
        if (files is null || reply.DataBase64 is null)
        {
            return false;
        }

        var settings = _clientSettings.Load();
        var folder = await files.SaveAsync("Save the CA backup", reply.FileName ?? "ca.pfx", PkiFileKind.Backup, Convert.FromBase64String(reply.DataBase64), settings.LastFolder).ConfigureAwait(true);
        if (folder is null)
        {
            return false;
        }

        Remember(settings, folder);
        return true;
    }

    public const string ReplaceTitle = "Replace the certificate authority";

    /// <summary>The replace confirmation; the count line only when devices are affected.</summary>
    public static string ReplaceMessage(int devices) => devices switch
    {
        0 => "Replace the CA?",
        1 => "1 device has certificates from the current CA. It keeps working until it is renewed. Replace the CA?",
        _ => string.Create(CultureInfo.InvariantCulture, $"{devices} devices have certificates from the current CA. They keep working until they are renewed. Replace the CA?"),
    };

    // ---------------------------------------------------------------- helpers

    private async Task<bool> ConfirmReplaceAsync(PkiDialogViewModel dialog)
    {
        var preview = PkiJson.Deserialize<PreviewReplaceReply>(await _ctx.InvokeAsync(PkiMethods.PreviewReplace, null, CancellationToken.None).ConfigureAwait(true));
        return await dialog.ConfirmAsync(ReplaceTitle, ReplaceMessage(preview.DevicesWithCurrentCa), "Replace").ConfigureAwait(true);
    }

    private async Task<bool> FinishReplaceAsync(PkiDialogViewModel dialog, PkiReply reply, string fallbackField)
    {
        if (!reply.Ok)
        {
            dialog.SetServerErrors(reply.Errors, reply.Error, fallbackField);
            return false;
        }

        if (reply.State is { } state)
        {
            await ApplyStateAsync(state).ConfigureAwait(true);
        }

        return true;
    }

    private async Task SaveFileAsync(string title, FileReply reply, PkiFileKind kind)
    {
        if (reply.Error is { } error)
        {
            await _ctx.ShowMessageAsync(title, error).ConfigureAwait(true);
            return;
        }

        if (Dialogs?.Files is not { } files || reply.DataBase64 is null)
        {
            return;
        }

        var settings = _clientSettings.Load();
        var folder = await files.SaveAsync(title, reply.FileName ?? "ca.crt", kind, Convert.FromBase64String(reply.DataBase64), settings.LastFolder).ConfigureAwait(true);
        if (folder is not null)
        {
            Remember(settings, folder);
        }
    }

    private void Remember(PkiClientSettings settings, string folder)
    {
        if (folder.Length > 0)
        {
            settings.LastFolder = folder;
            _clientSettings.Save(settings);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = _ctx.WatchEventsAsync(ct).GetAsyncEnumerator(ct);
                try
                {
                    // Subscribe first, then read the state: nothing published in between is lost.
                    var next = events.MoveNextAsync();
                    await LoadAsync(ct).ConfigureAwait(true);
                    await Policies.LoadAsync(ct).ConfigureAwait(true);
                    await Certificates.LoadAsync(ct).ConfigureAwait(true); // events may have been missed while not watching
                    while (await next.ConfigureAwait(true))
                    {
                        await HandleEventAsync(events.Current).ConfigureAwait(true);
                        next = events.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The connection dropped: read again and watch again after a short delay.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(true);
                }

                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // page hidden
        }
    }

    private void ApplyServerTrust(bool? installed, string? error)
    {
        if (installed == true)
        {
            SetServerTrust(ServiceStatus.Ok, "Installed on the server", null);
        }
        else if (!string.IsNullOrEmpty(error))
        {
            SetServerTrust(ServiceStatus.Error, "Failed on the server", error);
        }
        else if (installed == false)
        {
            SetServerTrust(ServiceStatus.Neutral, "Not installed on the server", null);
        }
        else
        {
            SetServerTrust(ServiceStatus.Neutral, "Not checked on the server", null);
        }
    }

    private async Task CheckClientTrustAsync(PkiState state)
    {
        if (state.Ca is not { } ca)
        {
            SetClientTrust(ServiceStatus.Neutral, "Not installed on this computer", null);
            _clientCheckedCaId = null;
            return;
        }

        if (state.Simulated)
        {
            SetClientTrust(ServiceStatus.Neutral, "Not installed on this computer", "Fake mode: nothing is installed on this computer.");
            return;
        }

        if (ca.Id == _clientCheckedCaId)
        {
            return; // checked for this CA already (events arrive often)
        }

        _clientCheckedCaId = ca.Id;
        try
        {
            using var certificate = Certificate(ca);
            var installed = await Task.Run(() => _clientTrust.IsInstalledAsync(certificate, CancellationToken.None)).ConfigureAwait(true);
            SetClientTrust(installed ? ServiceStatus.Ok : ServiceStatus.Neutral, installed ? "Installed on this computer" : "Not installed on this computer", TrustTooltip);
        }
#pragma warning disable CA1031 // Unknown is shown as not installed with the reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetClientTrust(ServiceStatus.Neutral, "Not installed on this computer", ex.Message);
        }
    }

    private void SetServerTrust(string kind, string text, string? tip)
    {
        ServerTrustKind = kind;
        ServerTrustText = text;
        ServerTrustTip = tip;
    }

    private void SetClientTrust(string kind, string text, string? tip)
    {
        ClientTrustKind = kind;
        ClientTrustText = text;
        ClientTrustTip = tip;
    }

    private void SetStatus(string kind, string text, string? detail)
    {
        StatusKind = kind;
        StatusText = text;
        StatusDetail = detail;
    }

    private static X509Certificate2 Certificate(CaInfo ca)
    {
        var certificates = CaCertificates.ReadPemCertificates(ca.CertificatePem);
        if (certificates.Count == 0)
        {
            throw new CryptographicException("The CA certificate cannot be read.");
        }

        foreach (var extra in certificates.Skip(1))
        {
            extra.Dispose();
        }

        return certificates[0];
    }

}
