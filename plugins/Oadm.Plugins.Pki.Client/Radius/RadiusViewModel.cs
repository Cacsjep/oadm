using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Client.Servers;
using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// The 802.1X server page (docs/spec/radius-server.md "What the user sees"), one page without tabs: Server (with Save and
/// "N policies apply" with Edit policies: the rules are PKI's policies) and Switches, then Sessions (live) and Log. Users
/// limited to a device group see only Sessions and Log (with their devices' rows; the server filters); users with all
/// devices whose role may manage the plugin change the settings, the switches and the log. <see cref="Activate"/> reads
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
        Switches = new SwitchesViewModel(ctx, () => MayManage, () => _state, () => _sites);
        Sessions = new SessionsViewModel(ctx, _time);
        Log = new LogViewModel(ctx, () => MayManage, _time);
        Server = new ServerSwitchViewModel(ctx, RadiusPluginInfo.PluginId, RadiusPluginInfo.DisplayName,
            "Switches get no answer from OADM until it is on again: cameras on 802.1X ports may lose their network access.");
        Server.RunningChanged += (_, _) => OnServerRunningChanged();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.IsFormValid) or nameof(SettingsViewModel.FormError))
            {
                NotifySave();
            }
        };
        _ctx.PermissionsChanged += (_, _) => NotifyRole();
    }

    /// <summary>The plugin id of PKI, whose page holds the policies.</summary>
    public const string PkiPluginId = "oadm.pki";

    public SettingsViewModel Settings { get; }

    public SwitchesViewModel Switches { get; }

    public SessionsViewModel Sessions { get; }

    public LogViewModel Log { get; }

    /// <summary>Turn on / Turn off of the server (PKI): the cards show only while it runs.</summary>
    public ServerSwitchViewModel Server { get; }

    /// <summary>
    /// The server-wide cards (Server, Switches) are read and shown: users with all devices only (fail closed: the server
    /// also answers a group user with empty settings).
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

    /// <summary>"6 policies apply": PKI's policies the server applies (802.1X server, Allow CA, VLAN, Block, Revoke).</summary>
    [ObservableProperty]
    public partial string PoliciesText { get; set; } = string.Empty;

    /// <summary>"1 policy applies", "6 policies apply", "No policies apply".</summary>
    public static string PoliciesApplyText(int count) => count switch
    {
        0 => "No policies apply",
        1 => "1 policy applies",
        _ => string.Create(CultureInfo.CurrentCulture, $"{count:N0} policies apply"),
    };

    /// <summary>Edit policies: the policies are on the PKI page.</summary>
    [RelayCommand]
    private Task EditInPkiAsync() => _ctx.OpenAsync("plugin:" + PkiPluginId);

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

    /// <summary>The switch first, then everything of the page while the server runs.</summary>
    internal async Task LoadAllAsync(CancellationToken ct)
    {
        _readingSwitch = true;
        try
        {
            await Server.LoadAsync(ct).ConfigureAwait(true);
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
            _readingSwitch = false;
        }

        if (Server.Running && !ct.IsCancellationRequested)
        {
            await LoadPageAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>The switch is being read by <see cref="LoadAllAsync"/>, which reads the page itself.</summary>
    private bool _readingSwitch;

    /// <summary>The server started (read the page) or stopped (the page shows only the switch; lists are read again later).</summary>
    private void OnServerRunningChanged()
    {
        if (_readingSwitch)
        {
            return;
        }

        if (Server.Running && _active is { } active)
        {
            _ = LoadPageAsync(active.Token);
        }
        else if (!Server.Running)
        {
            Sessions.MarkStale();
            Log.MarkStale();
        }
    }

    /// <summary>Everything of the page: the cards (users with all devices), then the sessions and the log.</summary>
    internal async Task LoadPageAsync(CancellationToken ct)
    {
        if (HasAllDevices)
        {
            await LoadServerAsync(ct).ConfigureAwait(true);
        }

        await Task.WhenAll(
            Sessions.NeedsLoad ? Sessions.LoadAsync(ct) : Task.CompletedTask,
            Log.NeedsLoad ? Log.LoadAsync(ct) : Task.CompletedTask).ConfigureAwait(true);
    }

    /// <summary>The state and the switches (the overlay over the cards).</summary>
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
        if (RadiusRules.Validate(settings) is { } settingsError)
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

    private void ClearServerWide() => Switches.Apply([]);

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
        PoliciesText = state.HasAllDevices ? PoliciesApplyText(state.PoliciesApply) : string.Empty;
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
        Log.NotifyRole();
        NotifySave();
        if (!HasAllDevices && Switches.Rows.Count > 0)
        {
            ClearServerWide(); // nothing read with all devices stays for a user limited to a group
        }
    }

    /// <summary>A live event of the server (applied on the UI thread).</summary>
    internal void OnEvent(PluginEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Server.OnEvent(e))
        {
            return; // turned on or off, or the license changed
        }

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
