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

/// <summary>
/// The PKI page (docs/spec/pki.md "The PKI page"): the tab Certificates with the list of the CAs (Create CA, Import, and per
/// CA Install in trusted root store, Back up, Delete) and the list of every other certificate, the tab Policies, and the
/// server tabs the host hands over (<see cref="Tabs"/>). Decision 2026-10-26: there is no OADM CA; every CA is an ordinary
/// root or intermediate CA. <see cref="Activate"/> while the page is shown (reads everything and watches the live events).
/// </summary>
public sealed partial class PkiViewModel : ValidatingViewModel, IDisposable
{
    /// <summary>Wait before watching again after the event stream ended or failed.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public const string TrustTooltip = "Firefox and Java use their own certificate stores: import the exported certificate there separately.";

    private readonly ICorePluginClientContext _ctx;
    private readonly ITrustStoreInstaller _clientTrust;
    private readonly PkiClientSettingsStore _clientSettings;
    private readonly Dictionary<string, IssuedCertificateStatus?> _clientTrustStates = new(StringComparer.OrdinalIgnoreCase); // root SHA-256 -> badge (null: not installed)
    private readonly Dictionary<string, IssuedCertificateStatus> _serverWork = new(StringComparer.OrdinalIgnoreCase); // root SHA-256 -> an install that runs or failed
    private CancellationTokenSource? _active;

    /// <summary>
    /// The role may manage the certificate authority ("oadm.pki:manage": CA actions and settings) and the user has all
    /// devices (the CA is server-wide; users limited to a device group only read the page).
    /// </summary>
    public bool MayManage => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>The role may install a root CA in the server's trusted root store ("oadm.pki:trustServer"), for users with all devices.</summary>
    public bool MayTrustServer => _ctx.HasAllDevices && _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(PkiPluginInfo.PluginId, PkiPermissions.TrustServer));

    /// <param name="ctx">Page context of the host.</param>
    /// <param name="clientTrust">This computer's trusted root store (<see cref="TrustStoreInstallers.ForClient"/>); tests pass a fake.</param>
    /// <param name="clientSettings">Where the last folder is remembered.</param>
    /// <param name="time">The clock of the policies' highlight; tests pass a fake.</param>
    public PkiViewModel(ICorePluginClientContext ctx, ITrustStoreInstaller clientTrust, PkiClientSettingsStore? clientSettings = null, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Policies = new PoliciesViewModel(ctx, time);
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayManage));
            OnPropertyChanged(nameof(MayTrustServer));
            CreateCaCommand.NotifyCanExecuteChanged();
            Policies.NotifyRole();
            RebuildTabs(); // Policies only for users with all devices
        };
        _clientTrust = clientTrust ?? throw new ArgumentNullException(nameof(clientTrust));
        _clientSettings = clientSettings ?? new PkiClientSettingsStore();
        Authorities = new IssuedCertificatesViewModel(ctx, clientSettings: _clientSettings, part: PkiListPart.Authorities)
        {
            PolicySource = () => [.. Policies.List.Rows.Select(r => r.Policy)],
            TrustStates = TrustStatesOf,
            CaActions = CaActionsOf,
        };
        Certificates = new IssuedCertificatesViewModel(ctx, clientSettings: _clientSettings, part: PkiListPart.Certificates)
        {
            PolicySource = () => [.. Policies.List.Rows.Select(r => r.Policy)],
            TrustStates = TrustStatesOf,
            CaActions = CaActionsOf,
        };
        InitTabs();
    }

    /// <summary>The dialogs and file pickers of the page view.</summary>
    public IPkiDialogs? Dialogs { get; set; }

    /// <summary>The last state of the server.</summary>
    public PkiState? State { get; private set; }

    // ---------------------------------------------------------------- status

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOk), nameof(IsStatusWarning), nameof(IsStatusError), nameof(IsStatusAccent), nameof(ShowCaStatus), nameof(ShowCaNotice))]
    public partial string StatusKind { get; set; } = "accent";

    /// <summary>
    /// The PKI status stands above the CA list only when the server cannot keep a CA or the first-start CA could not be made
    /// (decision 2026-10-26: each CA row shows its own state; a page read that failed says so too).
    /// </summary>
    public bool ShowCaStatus => !IsGenerating && StatusKind == ServiceStatus.Error && (IsUnavailable || !HasCa);

    /// <summary>Something stands above the CA list: the first-start CA is being made, or <see cref="ShowCaStatus"/>.</summary>
    public bool ShowCaNotice => IsGenerating || ShowCaStatus;

    public bool IsStatusOk => StatusKind == ServiceStatus.Ok;

    public bool IsStatusWarning => StatusKind == ServiceStatus.Warning;

    public bool IsStatusError => StatusKind == ServiceStatus.Error;

    public bool IsStatusAccent => StatusKind == "accent";

    /// <summary>The first-start root CA exists (the status line speaks of it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaStatus), nameof(ShowCaNotice))]
    public partial bool HasCa { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaStatus), nameof(ShowCaNotice))]
    [NotifyCanExecuteChangedFor(nameof(CreateCaCommand), nameof(ImportCommand))]
    public partial bool IsGenerating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaStatus), nameof(ShowCaNotice))]
    [NotifyCanExecuteChangedFor(nameof(CreateCaCommand), nameof(ImportCommand))]
    public partial bool IsUnavailable { get; set; }

    public bool IsActive => _active is not null;

    // ---------------------------------------------------------------- policies and certificates

    /// <summary>The Certificates card: every certificate but the CAs.</summary>
    public IssuedCertificatesViewModel Certificates { get; }

    /// <summary>The Certificate authorities card: every CA of PKI's list with its actions (decision 2026-10-25).</summary>
    public IssuedCertificatesViewModel Authorities { get; }

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

    /// <summary>Reads the whole state.</summary>
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
            return Task.WhenAll(Authorities.HandleEventAsync(item.Topic), Certificates.HandleEventAsync(item.Topic));
        }

        if (item.Topic == PolicyMethods.Topic)
        {
            return Policies.LoadAsync(_active?.Token ?? CancellationToken.None);
        }

        return item.Topic == PkiMethods.StateTopic && !string.IsNullOrEmpty(item.PayloadJson)
            ? ApplyStateAsync(PkiJson.Deserialize<PkiState>(item.PayloadJson))
            : Task.CompletedTask;
    }

    /// <summary>Shows a state of the server (also used by tests and the screenshots); a change of the CAs reads the CA list again.</summary>
    public async Task ApplyStateAsync(PkiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var authoritiesChanged = State is null || !State.Authorities.Select(a => a.Id + a.Fingerprint).SequenceEqual(state.Authorities.Select(a => a.Id + a.Fingerprint), StringComparer.Ordinal);
        State = state;
        SetStatus(state.Status.Kind, state.Status.Text, state.Status.Detail);
        IsGenerating = state.IsGenerating;
        IsUnavailable = state.Unavailable;
        HasCa = state.Ca is not null;
        await CheckClientTrustAsync(state).ConfigureAwait(true);
        Authorities.RefreshTrust();
        if (authoritiesChanged && IsActive)
        {
            await Authorities.LoadAsync().ConfigureAwait(true);
        }
    }

    // ---------------------------------------------------------------- the CA card's commands

    private bool CanCreateCa() => MayManage && !IsGenerating && !IsUnavailable;

    /// <summary>Create CA (toolbar of the CA card): a root CA or an intermediate CA signed by a CA with its key.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateCa))]
    private async Task CreateCaAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var issuers = (State?.Authorities ?? []).Where(a => a.Issues).Select(a => new Choice(a.Id, a.Name)).ToList();
        await Dialogs.ShowCreateCaAsync(new CreateCaViewModel(SubmitCreateCaAsync, issuers)).ConfigureAwait(true);
    }

    /// <summary>Create CA: the server makes it (field errors under the fields), then the state and the CA list.</summary>
    internal async Task<bool> SubmitCreateCaAsync(CreateCaViewModel dialog, CreateCaRequest request)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.CreateAuthority, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true));
        var done = await FinishAsync(dialog, reply, PkiFields.CommonName).ConfigureAwait(true);
        if (done)
        {
            await LoadListsAsync().ConfigureAwait(true);
        }

        return done;
    }

    [RelayCommand(CanExecute = nameof(CanImportWithKey))]
    private async Task ImportAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        await Dialogs.ShowImportAsync(new ImportCaViewModel(SubmitImportAsync)).ConfigureAwait(true);
    }

    /// <summary>A CA with its key can be kept only with the server's master key.</summary>
    private bool CanImportWithKey() => !IsUnavailable && !IsGenerating;

    [RelayCommand]
    private async Task ImportAuthorityAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        await Dialogs.ShowImportAuthorityAsync(new ImportAuthorityViewModel(SubmitImportAuthorityAsync)).ConfigureAwait(true);
    }

    /// <summary>Import CA certificate: the server checks the file (errors under it), then the state.</summary>
    internal async Task<bool> SubmitImportAuthorityAsync(ImportAuthorityViewModel dialog, ImportAuthorityRequest request)
    {
        ArgumentNullException.ThrowIfNull(dialog);
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

        await LoadListsAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Import of a CA with its key (decision 2026-10-25): the server checks the file (field errors) and adds the CA to the list;
    /// it never replaces another CA, so nothing is asked.
    /// </summary>
    internal async Task<bool> SubmitImportAsync(ImportCaViewModel dialog, ImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        dialog.IsBusy = true;
        var reply = PkiJson.Deserialize<PkiReply>(await _ctx.InvokeAsync(PkiMethods.Import, PkiJson.Serialize(request), CancellationToken.None).ConfigureAwait(true));
        var done = await FinishAsync(dialog, reply, PkiFields.File).ConfigureAwait(true);
        if (done)
        {
            await LoadListsAsync().ConfigureAwait(true);
        }

        return done;
    }

    // ---------------------------------------------------------------- a CA row's actions

    /// <summary>
    /// The CA entries of a row's menu: Install in trusted root store of a root CA (its own permission), Back up of a CA with
    /// its key, and Delete (red) of every CA (decision 2026-10-26). A row that cannot be read offers Delete only.
    /// </summary>
    private (IReadOnlyList<IssuedCertificateAction> Everyday, IReadOnlyList<IssuedCertificateAction> Danger) CaActionsOf(IssuedCertificateRow row)
    {
        List<IssuedCertificateAction> everyday = [];
        List<IssuedCertificateAction> danger = [];
        if (!row.IsCa)
        {
            return (everyday, danger);
        }

        if (row.IsRootCa && MayTrustServer)
        {
            everyday.Add(new("Install in trusted root store", InstallTrustCommand, row));
        }

        if (row.Source.CanIssue && !row.Source.Unreadable && MayManage)
        {
            everyday.Add(new("Back up", BackupCommand, row));
        }

        if (MayManage)
        {
            danger.Add(new("Delete", DeleteCaCommand, row, IsDanger: true));
        }

        return (everyday, danger);
    }

    /// <summary>The confirmation of Delete of a CA: its certificates stay in the list (decision 2026-10-26).</summary>
    public const string DeleteCaMessage = "Its certificates stay in the list. This cannot be undone.";

    /// <summary>"Delete the CA Contoso Root?".</summary>
    public static string DeleteCaTitle(string name) => $"Delete the CA {name}?";

    private bool CanDeleteCa(IssuedCertificateRow? row) => MayManage && row is { IsCa: true };

    /// <summary>Delete of a CA row (red), confirmed; the server refuses one a policy or a service names or that signed a listed CA, with the reason.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCa))]
    private async Task DeleteCaAsync(IssuedCertificateRow? row)
    {
        if (row is not { IsCa: true } || !await _ctx.ConfirmAsync(DeleteCaTitle(row.Name), DeleteCaMessage, "Delete").ConfigureAwait(true))
        {
            return;
        }

        var reply = await CallAsync<PkiReply>("Delete CA", PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(row.Key)).ConfigureAwait(true);
        if (reply?.Error is { } error)
        {
            await _ctx.ShowMessageAsync("Delete CA", error).ConfigureAwait(true);
        }

        if (reply?.State is { } state)
        {
            await ApplyStateAsync(state).ConfigureAwait(true);
        }

        await LoadListsAsync().ConfigureAwait(true);
    }

    /// <summary>Back up of a CA with its key: the password dialog, then the PKCS#12 file.</summary>
    [RelayCommand]
    private async Task BackupAsync(IssuedCertificateRow? row)
    {
        if (Dialogs is null || row is not { IsCa: true } || row.Source.AuthorityId is not { } id)
        {
            return;
        }

        await Dialogs.ShowBackupAsync(new BackupViewModel((dialog, password) => SubmitBackupAsync(dialog, password, id))).ConfigureAwait(true);
    }

    /// <summary>Backup: the server builds the PKCS#12, then the save dialog.</summary>
    internal async Task<bool> SubmitBackupAsync(BackupViewModel dialog, string password, string? authorityId = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var reply = PkiJson.Deserialize<FileReply>(await _ctx.InvokeAsync(PkiMethods.Backup, PkiJson.Serialize(new BackupRequest(password, authorityId)), CancellationToken.None).ConfigureAwait(true));
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

    /// <summary>Text of the confirmation before the install: the CA's SHA-256 fingerprint, to compare with the CA's View.</summary>
    internal static string InstallTrustMessage(string name, string fingerprint) =>
        $"The OADM server and this computer will trust every certificate {name} issues. Install only when the "
        + $"fingerprint is the one shown in View.\n\nSHA-256 fingerprint:\n{CaCertificates.ColonFingerprint(fingerprint)}";

    private bool CanInstallTrust(IssuedCertificateRow? row) => MayTrustServer && row is { IsRootCa: true } && !_serverWork.ContainsKey(FingerprintOf(row.Source) ?? string.Empty);

    /// <summary>
    /// Install in trusted root store of a root CA (decision 2026-10-26: every root CA): confirmed with its fingerprint, then the
    /// server's machine root store and this computer's; the row's badges show the work and the result.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallTrust))]
    private async Task InstallTrustAsync(IssuedCertificateRow? row)
    {
        if (row is not { IsRootCa: true } || FingerprintOf(row.Source) is not { } fingerprint
            || State?.RootTrust.FirstOrDefault(r => string.Equals(r.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) is not { } root
            || !await _ctx.ConfirmAsync("Install in trusted root store", InstallTrustMessage(row.Name, fingerprint), "Install").ConfigureAwait(true))
        {
            return;
        }

        _serverWork[fingerprint] = new IssuedCertificateStatus("Installing on the server", "accent");
        Authorities.RefreshTrust();
        try
        {
            var reply = PkiJson.Deserialize<InstallReply>(await _ctx.InvokeAsync(PkiMethods.InstallServerTrust, PkiJson.Serialize(new InstallTrustRequest(fingerprint)), CancellationToken.None)
                .ConfigureAwait(true));
            _serverWork.Remove(fingerprint);
            if (reply.Error is { } error && !reply.Installed)
            {
                _serverWork[fingerprint] = new IssuedCertificateStatus("Failed on the server", ServiceStatus.Error, error);
            }

            if (reply.State is { } state)
            {
                State = state;
            }

            if (State?.Simulated == true)
            {
                return; // fake mode: nothing is installed on this computer
            }

            _clientTrustStates[fingerprint] = new IssuedCertificateStatus("Installing on this computer", "accent");
            Authorities.RefreshTrust();
            using var certificate = Certificate(root.CertificatePem);
            var result = await Task.Run(() => _clientTrust.InstallAsync(certificate, CancellationToken.None)).ConfigureAwait(true);
            _clientTrustStates[fingerprint] = result.Installed
                ? new IssuedCertificateStatus(TrustedHere, ServiceStatus.Ok, TrustedHereTip)
                : new IssuedCertificateStatus("Failed on this computer", ServiceStatus.Error, result.Error);
        }
#pragma warning disable CA1031 // Shown in the badges.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _serverWork[fingerprint] = new IssuedCertificateStatus("Failed on the server", ServiceStatus.Error, PkiDialogViewModel.Message(ex));
        }
        finally
        {
            InstallTrustCommand.NotifyCanExecuteChanged();
            Authorities.RefreshTrust();
        }
    }

    /// <summary>The badge of a root CA once it is in the server's root store.</summary>
    public const string TrustedByServer = "OS root store: server";

    /// <summary>The badge of a root CA once it is in this computer's root store.</summary>
    public const string TrustedHere = "OS root store: client";

    public const string TrustedByServerTip = "Installed in the operating system's trusted root store of the computer running the OADM server.";

    public const string TrustedHereTip = "Installed in the operating system's trusted root store of this computer.";

    /// <summary>
    /// A root CA row's statuses (tinted badges next to its type, decision 2026-10-25, every root CA since 2026-10-26): "OS root
    /// store: server", "OS root store: client" once installed; the work or a failure in its own words (a failure's reason as
    /// tooltip); nothing while it is not installed (the row menu has Install).
    /// </summary>
    private List<IssuedCertificateStatus> TrustStatesOf(Oadm.Sdk.Certificates.IssuedCertificateInfo row)
    {
        if (row is not { Kind: Oadm.Sdk.Certificates.ListedCertificateKind.Authority, IsRootCa: true, Unreadable: false } || FingerprintOf(row) is not { } fingerprint)
        {
            return [];
        }

        List<IssuedCertificateStatus> states = [];
        if (_serverWork.TryGetValue(fingerprint, out var work))
        {
            states.Add(work);
        }
        else if (State?.RootTrust.FirstOrDefault(r => string.Equals(r.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) is { } root)
        {
            if (root.ServerInstalled == true)
            {
                states.Add(new IssuedCertificateStatus(TrustedByServer, ServiceStatus.Ok, TrustedByServerTip));
            }
            else if (root.ServerError is { } error)
            {
                states.Add(new IssuedCertificateStatus("Failed on the server", ServiceStatus.Error, error));
            }
        }

        if (_clientTrustStates.GetValueOrDefault(fingerprint) is { } client)
        {
            states.Add(client);
        }

        return states;
    }

    /// <summary>The SHA-256 of a CA row (its id "ca:&lt;SHA-256&gt;"); null for another row.</summary>
    private static string? FingerprintOf(Oadm.Sdk.Certificates.IssuedCertificateInfo row) =>
        row.Id is { } id && id.StartsWith("ca:", StringComparison.Ordinal) ? id[3..] : null;

    /// <summary>
    /// Checks once per root CA whether this computer's root store holds it (events arrive often; an install checks again).
    /// Fake mode installs nothing here.
    /// </summary>
    private async Task CheckClientTrustAsync(PkiState state)
    {
        if (state.Simulated)
        {
            return;
        }

        foreach (var root in state.RootTrust.Where(r => !_clientTrustStates.ContainsKey(r.Fingerprint)))
        {
            try
            {
                using var certificate = Certificate(root.CertificatePem);
                var installed = await Task.Run(() => _clientTrust.IsInstalledAsync(certificate, CancellationToken.None)).ConfigureAwait(true);
                _clientTrustStates[root.Fingerprint] = installed ? new IssuedCertificateStatus(TrustedHere, ServiceStatus.Ok, TrustedHereTip) : null;
            }
#pragma warning disable CA1031 // Unknown is shown as not installed.
            catch (Exception)
#pragma warning restore CA1031
            {
                _clientTrustStates[root.Fingerprint] = null;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Reads the CAs and the certificates again.</summary>
    private async Task LoadListsAsync(CancellationToken ct = default)
    {
        await Authorities.LoadAsync(ct).ConfigureAwait(true);
        await Certificates.LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A page call whose failure (the connection, a refusal) goes into a message titled <paramref name="title"/>, never the
    /// generic crash message; null then.
    /// </summary>
    private async Task<T?> CallAsync<T>(string title, string method, object payload)
        where T : class
    {
        try
        {
            return PkiJson.Deserialize<T>(await _ctx.InvokeAsync(method, PkiJson.Serialize(payload), CancellationToken.None).ConfigureAwait(true));
        }
#pragma warning disable CA1031 // Shown in a message.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(title, ServerErrors.Message(ex)).ConfigureAwait(true);
            return null;
        }
    }

    private async Task<bool> FinishAsync(PkiDialogViewModel dialog, PkiReply reply, string fallbackField)
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
                    await LoadListsAsync(ct).ConfigureAwait(true); // events may have been missed while not watching
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

    private void SetStatus(string kind, string text, string? detail)
    {
        StatusKind = kind;
        StatusText = text;
        StatusDetail = detail;
    }

    private static X509Certificate2 Certificate(string pem)
    {
        var certificates = CaCertificates.ReadPemCertificates(pem);
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
