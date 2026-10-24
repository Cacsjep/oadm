using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Client;

/// <summary>The tabs of the page.</summary>
public enum RadiusTab
{
    Server,
    Sessions,
    Log,
    Revoked,
}

/// <summary>
/// The 802.1X server page (docs/spec/radius-server.md "What the user sees"). Fixed tabs: Server (General with Advanced,
/// Switches, VLANs; one Save for General and VLANs with the status left of it), Sessions (live), Log, Revoked. Nothing of a
/// tab is read before it opens. Users limited to a device group see only Sessions and Log (with their devices' rows; the
/// server filters); users with all devices whose role may manage the plugin change the settings, the switches, the log and
/// the revocation list. <see cref="Activate"/> reads and watches the live events while the page is shown.
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
        Switches = new SwitchesViewModel(ctx, () => MayManage, () => _state, () => _sites);
        Vlans = new VlansViewModel(() => MayManage);
        Sessions = new SessionsViewModel(ctx, _time);
        Log = new LogViewModel(ctx, () => MayManage, _time);
        Revoked = new RevokedViewModel(ctx, () => MayManage, _time);
        Tab = HasAllDevices ? RadiusTab.Server : RadiusTab.Sessions;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.IsFormValid) or nameof(SettingsViewModel.FormError))
            {
                NotifySave();
            }
        };
        Vlans.ValidityChanged += (_, _) => NotifySave();
        _ctx.PermissionsChanged += (_, _) => NotifyRole();
    }

    public SettingsViewModel Settings { get; }

    public SwitchesViewModel Switches { get; }

    public VlansViewModel Vlans { get; }

    public SessionsViewModel Sessions { get; }

    public LogViewModel Log { get; }

    public RevokedViewModel Revoked { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerTab), nameof(IsSessionsTab), nameof(IsLogTab), nameof(IsRevokedTab))]
    public partial RadiusTab Tab { get; set; }

    public bool IsServerTab => Tab == RadiusTab.Server;

    public bool IsSessionsTab => Tab == RadiusTab.Sessions;

    public bool IsLogTab => Tab == RadiusTab.Log;

    public bool IsRevokedTab => Tab == RadiusTab.Revoked;

    /// <summary>The user reaches every device: Server and Revoked show (server-wide), else only Sessions and Log.</summary>
    public bool HasAllDevices => _ctx.HasAllDevices;

    /// <summary>Users with all devices whose role may manage the plugin (the server refuses others anyway).</summary>
    public bool MayManage => _ctx.HasAllDevices && (_state?.HasAllDevices ?? true) && _ctx.Has(PermissionKeys.Of(RadiusPluginInfo.PluginId, PermissionKeys.Manage));

    /// <summary>The first read of the Server tab (the overlay).</summary>
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
    public string? SaveTip => Settings.FormError ?? Vlans.Error;

    public void Activate()
    {
        Deactivate();

        // No events came in while the page was away: the lists read before are read again when their tab opens.
        Sessions.MarkStale();
        Log.MarkStale();
        _active = new CancellationTokenSource();
        var ct = _active.Token;
        _ = _ctx.RunAsync(OnEvent, null, ct);
        _ = OpenTabAsync(Tab, ct);

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

    [RelayCommand]
    private Task ShowServerTabAsync() => ShowTabAsync(RadiusTab.Server);

    [RelayCommand]
    private Task ShowSessionsTabAsync() => ShowTabAsync(RadiusTab.Sessions);

    [RelayCommand]
    private Task ShowLogTabAsync() => ShowTabAsync(RadiusTab.Log);

    [RelayCommand]
    private Task ShowRevokedTabAsync() => ShowTabAsync(RadiusTab.Revoked);

    internal Task ShowTabAsync(RadiusTab tab)
    {
        if (!HasAllDevices && tab is RadiusTab.Server or RadiusTab.Revoked)
        {
            return Task.CompletedTask;
        }

        Tab = tab;
        return OpenTabAsync(tab, _active?.Token ?? CancellationToken.None);
    }

    /// <summary>Reads a tab when it opens (the Server tab every time, the lists the first time; events keep them).</summary>
    internal Task OpenTabAsync(RadiusTab tab, CancellationToken ct) => tab switch
    {
        RadiusTab.Server => LoadServerAsync(ct),
        RadiusTab.Sessions => Sessions.NeedsLoad ? Sessions.LoadAsync(ct) : Task.CompletedTask,
        RadiusTab.Log => Log.NeedsLoad ? Log.LoadAsync(ct) : Task.CompletedTask,
        _ => Revoked.LoadAsync(ct),
    };

    /// <summary>The state, the switches and the VLANs (the overlay over the cards).</summary>
    internal async Task LoadServerAsync(CancellationToken ct)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var state = RadiusJson.Deserialize<RadiusState>(await _ctx.InvokeAsync(RadiusMethods.GetState, null, ct).ConfigureAwait(true));
            ApplyState(state, form: true);
            await Switches.LoadAsync(ct).ConfigureAwait(true);
            _sites = await ReadSitesAsync(ct).ConfigureAwait(true);
            var vlans = RadiusJson.Deserialize<VlanSettings>(await _ctx.InvokeAsync(RadiusMethods.GetVlans, null, ct).ConfigureAwait(true));
            Vlans.SetChoices(_sites, _ctx.Devices.SelectMany(d => d.Tags).Concat(vlans.Rules.Where(r => r.Kind == VlanRuleKinds.Tag).Select(r => r.RefId)));
            Vlans.Apply(vlans);
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

    private bool CanSave() => MayManage && !IsSaving && Settings.IsFormValid && Vlans.IsValid;

    /// <summary>
    /// Save: General with Advanced and VLANs (the switches apply at once in their dialog). Both are checked with the server's
    /// own rules (<see cref="RadiusRules"/>) before the first call, so a refusal of the second leaves nothing half saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Settings.ShowAllErrors();
        if (Vlans.Enabled)
        {
            Vlans.ShowAllErrors(); // collapsed while off: nothing to show there
        }

        if (!Settings.IsFormValid || !Vlans.IsValid)
        {
            return;
        }

        ActionError = null;
        var vlans = Vlans.ToSettings();
        var settings = Settings.ToSettings(vlans.Enabled, vlans.DefaultVlan);
        if (_state is { } known && RadiusRules.Validate(settings, known.CipherSuitesListed) is { } settingsError)
        {
            if (!Settings.ShowServerError(settingsError.Message))
            {
                ActionError = settingsError.Message;
            }

            return;
        }

        if (RadiusRules.Validate(vlans) is { } vlanError)
        {
            if (!(vlans.Enabled && Vlans.ShowError(vlanError)))
            {
                ActionError = vlanError.Message;
            }

            return;
        }

        IsSaving = true;
        try
        {
            var state = RadiusJson.Deserialize<RadiusState>(await _ctx.InvokeAsync(RadiusMethods.SaveSettings, RadiusJson.Serialize(settings), CancellationToken.None).ConfigureAwait(true));
            ApplyState(state, form: true); // saved, whatever the VLANs answer
            await _ctx.InvokeAsync(RadiusMethods.SaveVlans, RadiusJson.Serialize(vlans), CancellationToken.None).ConfigureAwait(true);
            Vlans.Apply(vlans);
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
        Switches.NotifyRole();
        Vlans.NotifyRole();
        Log.NotifyRole();
        Revoked.NotifyRole();
        NotifySave();
        if (!HasAllDevices && Tab is RadiusTab.Server or RadiusTab.Revoked)
        {
            Tab = RadiusTab.Sessions;
            _ = OpenTabAsync(Tab, _active?.Token ?? CancellationToken.None);
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
            case RadiusTopics.Revoked:
                if (Revoked.IsLoaded)
                {
                    _ = Revoked.LoadAsync();
                }

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
