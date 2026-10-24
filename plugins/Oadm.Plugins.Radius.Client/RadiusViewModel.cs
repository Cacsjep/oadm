using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// The 802.1X server page (docs/spec/radius-server.md "What the user sees"), one page without tabs: Server (with Save and
/// the Advanced dialog), Trusted CAs, Switches and Policies, then Sessions (live) and Log. Users limited to a device group
/// see only Sessions and Log (with their devices' rows; the server filters); users with all devices whose role may manage
/// the plugin change the settings, the trusted CAs, the switches, the policies and the log. <see cref="Activate"/> reads
/// everything and watches the live events while the page is shown.
/// </summary>
public sealed partial class RadiusViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _active;
    private ITimer? _tick;
    private RadiusState? _state;
    private IReadOnlyList<SiteRef> _sites = [];
    private int _loadVersion;

    public RadiusViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
        Settings = new SettingsViewModel(() => MayManage);
        TrustedCas = new TrustedCasViewModel(ctx, () => MayManage);
        Switches = new SwitchesViewModel(ctx, () => MayManage, () => _state, () => _sites);
        Policies = new PoliciesViewModel(ctx, () => MayManage, () => _ctx.Has(PermissionKeys.Of(PkiPluginId, PermissionKeys.Manage)));
        Sessions = new SessionsViewModel(ctx, _time);
        Log = new LogViewModel(ctx, () => MayManage, _time);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.IsFormValid) or nameof(SettingsViewModel.FormError))
            {
                NotifySave();
            }
        };
        _ctx.PermissionsChanged += (_, _) => NotifyRole();
    }

    /// <summary>The plugin id of PKI, whose "Manage the certificate authority" a revoke policy needs too.</summary>
    public const string PkiPluginId = "oadm.pki";

    public SettingsViewModel Settings { get; }

    public TrustedCasViewModel TrustedCas { get; }

    public SwitchesViewModel Switches { get; }

    public PoliciesViewModel Policies { get; }

    public SessionsViewModel Sessions { get; }

    public LogViewModel Log { get; }

    /// <summary>The Advanced dialog (the page view).</summary>
    public IAdvancedDialog? AdvancedDialog { get; set; }

    /// <summary>
    /// The server-wide cards (Server, Trusted CAs, Switches, Policies) are read and shown: users with all devices only (fail
    /// closed: the server also answers a group user with empty settings).
    /// </summary>
    public bool HasAllDevices => _ctx.HasAllDevices && (_state?.HasAllDevices ?? true);

    /// <summary>Users with all devices whose role may manage the plugin (the server refuses others anyway).</summary>
    public bool MayManage => _ctx.HasAllDevices && (_state?.HasAllDevices ?? true) && _ctx.Has(PermissionKeys.Of(RadiusPluginInfo.PluginId, PermissionKeys.Manage));

    /// <summary>The first read of the cards (the overlay).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>"Running on port 1812", "Port 1812 is used by another program", ... left of Save.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    /// <summary>The status chip's color: ok, neutral, warning or error (<see cref="ServiceStatus"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusOk), nameof(StatusWarning), nameof(StatusError))]
    public partial string StatusKind { get; set; } = ServiceStatus.Neutral;

    public bool StatusOk => StatusKind == ServiceStatus.Ok;

    public bool StatusWarning => StatusKind == ServiceStatus.Warning;

    public bool StatusError => StatusKind == ServiceStatus.Error;

    /// <summary>Sites whose node has not allowed the port yet, one line each.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSiteProblems))]
    public partial string SitesText { get; set; } = string.Empty;

    public bool HasSiteProblems => SitesText.Length > 0;

    /// <summary>"Server certificate valid until 2027-10-24"; empty without one.</summary>
    [ObservableProperty]
    public partial string CertificateText { get; set; } = string.Empty;

    /// <summary>A save that failed for no field: below the Save row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    public partial string? ActionError { get; set; }

    public bool HasActionError => !string.IsNullOrEmpty(ActionError);

    /// <summary>Why Save is off, for its tooltip.</summary>
    public string? SaveTip => Settings.FormError;

    public void Activate()
    {
        Deactivate();

        // No events came in while the page was away: the lists read before are read again when their tab opens.
        Sessions.MarkStale();
        Log.MarkStale();
        _active = new CancellationTokenSource();
        var ct = _active.Token;
        _ = _ctx.RunAsync(OnEvent, null, ct);
        _ = LoadAllAsync(ct);

        // Every second on the UI thread: the durations of the open sessions, ended ones leave after 10 s.
        var ui = SynchronizationContext.Current;
        _tick = _time.CreateTimer(_ =>
        {
            if (ui is null)
            {
                Sessions.Tick();
            }
            else
            {
                ui.Post(_ => Sessions.Tick(), null);
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Deactivate()
    {
        _tick?.Dispose();
        _tick = null;
        _active?.Cancel();
        _active?.Dispose();
        _active = null;
    }

    public void Dispose()
    {
        Deactivate();
    }

    /// <summary>Everything of the page: the cards (users with all devices), then the sessions and the log.</summary>
    internal async Task LoadAllAsync(CancellationToken ct)
    {
        if (HasAllDevices)
        {
            await LoadServerAsync(ct).ConfigureAwait(true);
        }

        await Task.WhenAll(
            Sessions.NeedsLoad ? Sessions.LoadAsync(ct) : Task.CompletedTask,
            Log.NeedsLoad ? Log.LoadAsync(ct) : Task.CompletedTask).ConfigureAwait(true);
    }

    /// <summary>The state, the trusted CAs, the switches and the policies (the overlay over the cards).</summary>
    internal async Task LoadServerAsync(CancellationToken ct)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var state = RadiusJson.Deserialize<RadiusState>(await _ctx.InvokeAsync(RadiusMethods.GetState, null, ct).ConfigureAwait(true));
            ApplyState(state, form: true);
            if (!HasAllDevices)
            {
                ClearServerWide(); // a user limited to a group: nothing server-wide (the server sends none either)
                return;
            }

            await Switches.LoadAsync(ct).ConfigureAwait(true);
            _sites = await ReadSitesAsync(ct).ConfigureAwait(true);
            await TrustedCas.LoadAsync(ct).ConfigureAwait(true);
            Policies.SetSites(_sites);
            await Policies.LoadAsync(ct).ConfigureAwait(true);
            if (!HasAllDevices)
            {
                ClearServerWide(); // the role changed during the read
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The page shows the reason instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = ServerErrors.Message(ex);
            StatusKind = ServiceStatus.Error;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    private bool CanSave() => MayManage && !IsSaving && Settings.IsFormValid;

    /// <summary>Save of the Server card (Listen on, Port, Log entries kept), checked with the server's own rules first.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Settings.ShowAllErrors();
        if (!Settings.IsFormValid)
        {
            return;
        }

        ActionError = null;
        var settings = Settings.ToSettings();
        if (_state is { } known && RadiusRules.Validate(settings, known.CipherSuitesListed) is { } settingsError)
        {
            if (!Settings.ShowServerError(settingsError.Message))
            {
                ActionError = settingsError.Message;
            }

            return;
        }

        IsSaving = true;
        try
        {
            var state = RadiusJson.Deserialize<RadiusState>(await _ctx.InvokeAsync(RadiusMethods.SaveSettings, RadiusJson.Serialize(settings), CancellationToken.None).ConfigureAwait(true));
            ApplyState(state, form: true);
        }
#pragma warning disable CA1031 // Shown below the field it belongs to, else below the Save row.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ServerErrors.Message(ex);
            if (!Settings.ShowServerError(message))
            {
                ActionError = message;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanOpenAdvanced() => _state is { CipherSuitesListed.Count: > 0 } && HasAllDevices;

    /// <summary>Advanced: the TLS settings and the largest EAP packet in their own dialog (saved there; the Server form stays).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenAdvanced))]
    private async Task OpenAdvancedAsync()
    {
        if (_state is not { } state || AdvancedDialog is not { } dialog)
        {
            return;
        }

        var advanced = NewAdvanced(state);
        if (await dialog.ShowAdvancedAsync(advanced).ConfigureAwait(true) && advanced.Saved is { } saved)
        {
            ApplyState(saved, form: false);
            Settings.ApplySaved(saved.Settings);
        }
    }

    /// <summary>The Advanced dialog of the saved settings (read only without the manage permission).</summary>
    public AdvancedViewModel NewAdvanced(RadiusState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new AdvancedViewModel(_ctx, state.Settings, state.CipherSuitesListed) { MayManage = MayManage };
    }

    private void ClearServerWide()
    {
        TrustedCas.Clear();
        Policies.Clear();
        Switches.Apply([]);
    }

    private void ApplyState(RadiusState state, bool form)
    {
        _state = state;
        StatusText = state.StatusText;
        StatusKind = state.StatusKind;
        SitesText = string.Join(Environment.NewLine, state.SitesNotAllowed.Select(s => $"{s.SiteName}: allow the 802.1X server in the OADM Site Node app"));
        CertificateText = state.ServerCertificate is { } certificate
            ? "Server certificate valid until " + certificate.NotAfterUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        Log.Kept = state.Settings.LogKept;
        if (form)
        {
            Settings.Apply(state);
        }

        NotifyRole();
    }

    private void NotifySave()
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveTip));
    }

    private void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(HasAllDevices));
        Settings.NotifyRole();
        TrustedCas.NotifyRole();
        Switches.NotifyRole();
        Policies.NotifyRole();
        Log.NotifyRole();
        NotifySave();
        OpenAdvancedCommand.NotifyCanExecuteChanged();
        if (!HasAllDevices && (Policies.Rows.Count > 0 || TrustedCas.Cas.Count > 0 || Switches.Rows.Count > 0))
        {
            ClearServerWide(); // nothing read with all devices stays for a user limited to a group
        }
    }

    /// <summary>A live event of the server (applied on the UI thread).</summary>
    internal void OnEvent(PluginEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e.Topic)
        {
            case RadiusTopics.State:
                ApplyState(RadiusJson.Deserialize<RadiusState>(e.PayloadJson), form: false);
                break;
            case RadiusTopics.Switches:
                if (HasAllDevices)
                {
                    Switches.Apply(RadiusJson.Deserialize<SwitchList>(e.PayloadJson).Switches);
                }

                break;
            case RadiusTopics.Sessions:
                Sessions.Apply(RadiusJson.Deserialize<IReadOnlyList<SessionRow>>(e.PayloadJson));
                break;
            case RadiusTopics.Log:
                Log.Add(RadiusJson.Deserialize<IReadOnlyList<LogRow>>(e.PayloadJson));
                break;
            case RadiusTopics.LogCleared:
                Log.Cleared();
                break;
        }
    }

    /// <summary>
    /// The sites of the server (listSites); a server part without it: the sites of the devices and switches this client knows.
    /// </summary>
    private async Task<IReadOnlyList<SiteRef>> ReadSitesAsync(CancellationToken ct)
    {
        try
        {
            return RadiusJson.Deserialize<SiteList>(await _ctx.InvokeAsync(RadiusPageMethods.ListSites, null, ct).ConfigureAwait(true)).Sites;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Older server part: the sites the page knows.
        catch (Exception)
#pragma warning restore CA1031
        {
            return
            [
                .. _ctx.Devices.Where(d => d.SiteId is not null).Select(d => new SiteRef(d.SiteId!.Value, d.SiteName ?? string.Empty))
                    .Concat(Switches.Rows.Where(r => r.Source.SiteId is not null).Select(r => new SiteRef(r.Source.SiteId!.Value, r.Source.SiteName)))
                    .DistinctBy(s => s.SiteId),
            ];
        }
    }
}
