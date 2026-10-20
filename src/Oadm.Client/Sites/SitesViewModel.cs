using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;
using Oadm.Contracts.Sites;

namespace Oadm.Client.Sites;

/// <summary>
/// One row of the Sites page: a bound site, or a node that connected and waits to be bound. Rows are replaced as a whole
/// by every list of the server.
/// </summary>
public sealed class SiteRowViewModel
{
    private readonly string _search;

    public SiteRowViewModel(Site site, bool mayChange)
    {
        ArgumentNullException.ThrowIfNull(site);
        Site = site;
        IsBound = site.Id.Length > 0;
        StateText = SiteTexts.State(site.State);
        IsOk = site.State == SiteState.Online;
        IsError = site.State == SiteState.Offline;
        IsRemoved = site.State == SiteState.Removed;
        IsWarning = site.State is SiteState.NotBound or SiteState.Removed;
        IsInfo = site.State == SiteState.Waiting;
        IsGreyed = site.State is SiteState.BoundElsewhere or SiteState.BindingClosed;
        Detail = site.Message.Length > 0 ? site.Message : SiteTexts.DefaultDetail(site.State);
        StateTip = Detail.Length > 0 ? StateText + ": " + Detail : StateText;
        NodeAddressText = site.NodeAddress;
        DeviceCountText = IsBound ? site.DeviceCount.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        RoundTripText = site.RoundTripMs > 0 ? site.RoundTripMs.ToString("N0", CultureInfo.CurrentCulture) + " ms" : string.Empty;
        ConnectionsText = IsBound && site.State == SiteState.Online
            ? (site.ShortConnections + site.PermanentConnections).ToString("N0", CultureInfo.CurrentCulture)
            : string.Empty;
        PortsWaitingText = site.PortsWaiting > 0 ? site.PortsWaiting.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        LargeTransfersText = site.LargeTransfers > 0 ? site.LargeTransfers.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        VersionText = site.UpdateAvailable ? site.Version + " · Update available" : site.Version;
        LastSeenText = site.LastSeen is { } seen && seen.Seconds > 0
            ? seen.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;
        LastSeenSortKey = site.LastSeen?.ToDateTime() ?? DateTime.MinValue;
        CanBind = mayChange && site.State == SiteState.Waiting && !IsBound;
        CanManage = mayChange && IsBound && !IsRemoved;
        CanReplace = mayChange && IsBound;
        CanRemove = mayChange && IsBound;
        RemoveTip = IsRemoved ? "Remove devices" : "Remove site";
        _search = string.Join(' ', site.Name, NodeAddressText, StateText, site.Version, site.NodeId);
    }

    public Site Site { get; }

    /// <summary>The key of the row: the site id, or the node id of a waiting node.</summary>
    public string Key => IsBound ? Site.Id : "node:" + Site.NodeId;

    public string Name => Site.Name;

    public bool IsBound { get; }

    public string StateText { get; }

    public string Detail { get; }

    /// <summary>The state with its reason in full ("Offline: The site node does not answer."): the cell's tooltip.</summary>
    public string StateTip { get; }

    /// <summary>A site removed with its devices kept: only Replace with and Remove devices.</summary>
    public bool IsRemoved { get; }

    public bool IsOk { get; }

    public bool IsError { get; }

    public bool IsWarning { get; }

    public bool IsInfo { get; }

    /// <summary>A connected node that cannot be bound (bound to another server, binding closed): shown dimmed with the reason.</summary>
    public bool IsGreyed { get; }

    public double RowOpacity => IsGreyed ? 0.5 : 1;

    public string NodeAddressText { get; }

    public string DeviceCountText { get; }

    public int DeviceCount => Site.DeviceCount;

    public string RoundTripText { get; }

    public int RoundTripMs => Site.RoundTripMs;

    public string ConnectionsText { get; }

    public string PortsWaitingText { get; }

    public int PortsWaiting => Site.PortsWaiting;

    public string LargeTransfersText { get; }

    public int LargeTransfers => Site.LargeTransfers;

    public string VersionText { get; }

    public string LastSeenText { get; }

    public DateTime LastSeenSortKey { get; }

    public bool CanBind { get; }

    /// <summary>Rename: a bound site that is not removed.</summary>
    public bool CanManage { get; }

    /// <summary>Replace with: a bound site, also a removed one with devices.</summary>
    public bool CanReplace { get; }

    /// <summary>Remove site, or Remove devices of a removed site.</summary>
    public bool CanRemove { get; }

    /// <summary>"Remove site", or "Remove devices" for a removed site.</summary>
    public string RemoveTip { get; }

    public bool Matches(string text) => _search.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Rail page "Sites" (Servers): the bound sites and the site nodes waiting to be bound, live through WatchSites while the
/// page is shown. Reading needs "See sites"; Accept site nodes, Bind, Rename, Replace with and Remove site are
/// Administrator only.
/// </summary>
public sealed partial class SitesViewModel : ObservableObject, IDisposable
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly UserSession? _session;
    private List<SiteRowViewModel> _all = [];
    private SiteList _list = new();
    private CancellationTokenSource? _active;

    /// <summary>A list of the server arrived for this session; until then nothing is shown as the server's state.</summary>
    private bool _received;

    /// <summary>The session the shown list belongs to (<see cref="UserSession.SignInCount"/>); another one starts empty.</summary>
    private int _shownSession = -1;

    public SitesViewModel(IOadmApi api, IDialogService dialogs, IUiDispatcher ui, UserSession? session = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _session = session;
        if (session is not null)
        {
            session.PropertyChanged += OnSessionChanged;
        }
    }

    public RangeObservableCollection<SiteRowViewModel> Rows { get; } = [];

    /// <summary>Administrators change sites (never grantable); without a session, in tests: yes.</summary>
    public bool MayChange => _session?.IsAdmin ?? true;

    /// <summary>Accept site nodes (the server's setting); setting it asks the server (the check box of the toolbar).</summary>
    public bool Accept
    {
        get => _list.Accept;
        set => _ = SetAcceptAsync(value);
    }

    /// <summary>"Accept site nodes: on" for roles that may not change it.</summary>
    public string AcceptText => _list.Accept ? "Accepting site nodes" : "Not accepting site nodes";

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Until the first list arrived.</summary>
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty] public partial bool IsChangingAccept { get; private set; }

    /// <summary>A refused action or a failed read, directly below the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>The outcome of the last action (Replace with), below the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeText { get; private set; }

    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);

    public bool ShowEmpty => !IsLoading && Rows.Count == 0;

    /// <summary>
    /// Accept site nodes is off and there is nothing to list: the page shows what sites are and Accept site nodes instead
    /// of the list (like Compliance while it is off).
    /// </summary>
    public bool ShowOffState => _received && !IsLoading && !_list.Accept && _all.Count == 0;

    public bool ShowList => !ShowOffState;

    /// <summary>Turn off site nodes in the toolbar (administrators, while it is on).</summary>
    public bool ShowTurnOff => MayChange && _list.Accept;

    /// <summary>Accept site nodes in the toolbar (administrators, while it is off but sites are listed).</summary>
    public bool ShowAcceptButton => MayChange && !_list.Accept;

    public string EmptyText => _all.Count > 0
        ? "No site matches the search."
        : !_received ? string.Empty // the error below the list says why
        : _list.Accept ? SiteTexts.AcceptOnHint(_list.ListenAddress) : SiteTexts.AcceptOffHint;

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowOffState));
        OnPropertyChanged(nameof(ShowList));
    }

    /// <summary>The page is shown: read and watch the list.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        int session = _session?.SignInCount ?? 0;
        if (session != _shownSession)
        {
            // Another login (maybe another server) since the page was last shown: nothing of the earlier one stays.
            _shownSession = session;
            Clear();
        }

        _active = new CancellationTokenSource();
        _ = WatchAsync(_active.Token);
    }

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose()
    {
        Deactivate();
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionChanged;
        }
    }

    /// <summary>Applies a list of the server (watch stream, tests).</summary>
    public void Apply(SiteList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _received = true;
        Show(list);
        IsLoading = false;
    }

    // Back to "loading": the list, the rows and the texts of an earlier session go.
    private void Clear()
    {
        _received = false;
        _list = new SiteList();
        _all = [];
        ErrorText = null;
        NoticeText = null;
        SearchText = string.Empty;
        IsLoading = true;
        RaiseListChanged(acceptChanged: true);
        Filter();
    }

    private void Show(SiteList list)
    {
        var acceptChanged = list.Accept != _list.Accept;
        _list = list;
        _all = [.. list.Sites.Select(s => new SiteRowViewModel(s, MayChange))];
        RaiseListChanged(acceptChanged);
        Filter();
    }

    private void RaiseListChanged(bool acceptChanged)
    {
        if (acceptChanged)
        {
            OnPropertyChanged(nameof(Accept));
            OnPropertyChanged(nameof(AcceptText));
            OnPropertyChanged(nameof(ShowTurnOff));
            OnPropertyChanged(nameof(ShowAcceptButton));
        }

        OnPropertyChanged(nameof(ShowOffState));
        OnPropertyChanged(nameof(ShowList));
    }

    /// <summary>Accept site nodes on or off (Administrator; the check box in the toolbar).</summary>
    public async Task SetAcceptAsync(bool accept)
    {
        if (!MayChange || accept == _list.Accept || IsChangingAccept)
        {
            OnPropertyChanged(nameof(Accept));
            return;
        }

        IsChangingAccept = true;
        try
        {
            Apply(await _api.SetSiteAcceptAsync(accept, CancellationToken.None).ConfigureAwait(true));
            ErrorText = null;
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException)
        {
            ErrorText = "Accept site nodes could not be changed: " + Message(ex);
            OnPropertyChanged(nameof(Accept)); // the check box goes back
        }
        finally
        {
            IsChangingAccept = false;
        }
    }

    /// <summary>Accept site nodes (the button of the empty page and of the toolbar while it is off).</summary>
    [RelayCommand]
    private Task AcceptAsync() => SetAcceptAsync(true);

    /// <summary>Turn off site nodes (red, left of the search, confirmed): nodes that connect to the server are cut off.</summary>
    [RelayCommand]
    private async Task TurnOffAsync()
    {
        ErrorText = null;
        if (!await _dialogs.ConfirmAsync(
            "Turn off site nodes",
            "Turn off site nodes? Site nodes are disconnected and their devices show Site offline.",
            "Turn off").ConfigureAwait(true))
        {
            return;
        }

        await SetAcceptAsync(false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BindAsync(SiteRowViewModel? row)
    {
        if (row is not { CanBind: true })
        {
            return;
        }

        var form = SiteFormViewModel.Bind(_api, row.Site, [.. _all.Select(r => r.Site)]);
        if (await _dialogs.ShowSiteFormAsync(form).ConfigureAwait(true))
        {
            NoticeText = null;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RenameAsync(SiteRowViewModel? row)
    {
        if (row is not { CanManage: true })
        {
            return;
        }

        if (await _dialogs.ShowSiteFormAsync(SiteFormViewModel.Rename(_api, row.Site)).ConfigureAwait(true))
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReplaceAsync(SiteRowViewModel? row)
    {
        if (row is not { CanReplace: true })
        {
            return;
        }

        var others = ReplaceTargets(row);
        if (others.Count == 0)
        {
            ErrorText = _all.Any(r => r.IsBound && !r.IsRemoved && r.Site.Id != row.Site.Id)
                ? "The site that replaces " + row.Name + " must be online."
                : "Bind the new site node first, then replace " + row.Name + " with it.";
            return;
        }

        var form = SiteFormViewModel.Replace(_api, row.Site, others);
        if (await _dialogs.ShowSiteFormAsync(form).ConfigureAwait(true) && form.ReplaceReply is { } reply)
        {
            NoticeText = SiteTexts.Replaced(reply);
            ErrorText = null;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(SiteRowViewModel? row)
    {
        if (row is not { CanRemove: true })
        {
            return;
        }

        var form = SiteFormViewModel.Remove(_api, row.Site);
        if (await _dialogs.ShowSiteFormAsync(form).ConfigureAwait(true) && form.RemoveReply is { } reply)
        {
            NoticeText = null;
            ErrorText = reply.NodeAnswered ? null : SiteTexts.NodeDidNotAnswer;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The sites Replace with can move the devices to: what the server accepts (SiteAdmin.ReplaceAsync), another bound site
    /// that is online (not removed, not Not bound, not offline).
    /// </summary>
    internal List<Site> ReplaceTargets(SiteRowViewModel row) =>
        [.. _all.Where(r => r.IsBound && r.Site.State == SiteState.Online && r.Site.Id != row.Site.Id).Select(r => r.Site)];

    private async Task ReloadAsync()
    {
        try
        {
            Apply(await _api.ListSitesAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException)
        {
            // The watch stream brings the change too.
        }
    }

    private void Filter()
    {
        var text = SearchText.Trim();
        var rows = text.Length == 0 ? _all : _all.Where(r => r.Matches(text)).ToList();
        Rows.ReplaceAll(rows);
        var bound = _all.Count(r => r.IsBound);
        var online = _all.Count(r => r.IsOk);
        var waiting = _all.Count(r => !r.IsBound && r.IsInfo);
        var status = string.Create(CultureInfo.CurrentCulture, $"{bound:N0} {(bound == 1 ? "site" : "sites")} · {online:N0} online");
        if (waiting > 0)
        {
            status += string.Create(CultureInfo.CurrentCulture, $" · {waiting:N0} waiting to be bound");
        }

        StatusText = rows.Count == _all.Count ? status : status + string.Create(CultureInfo.CurrentCulture, $" · {rows.Count:N0} shown");
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var list in _api.WatchSitesAsync(ct).ConfigureAwait(false))
                {
                    _ui.Post(() =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return; // the page closed or another session started: a list of the old watch never shows
                        }

                        Apply(list);
                        ErrorText = ErrorText == ReadError ? null : ErrorText;
                    });
                    delay = TimeSpan.FromSeconds(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                _ui.Post(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        IsLoading = false;
                        ErrorText = ex.Status.Detail;
                    }
                });
            }
#pragma warning disable CA1031 // The connection dropped: watch again after a short delay.
            catch (Exception)
#pragma warning restore CA1031
            {
                _ui.Post(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        IsLoading = false;
                        ErrorText ??= ReadError;
                    }
                });
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
        }
    }

    private const string ReadError = "The sites could not be read. Trying again.";

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserSession.IsAdmin))
        {
            OnPropertyChanged(nameof(MayChange));
            OnPropertyChanged(nameof(ShowTurnOff));
            OnPropertyChanged(nameof(ShowAcceptButton));
            if (_received)
            {
                Show(_list); // row actions follow the role; nothing to show before the server sent a list
            }
        }
    }

    internal static string Message(Exception ex) => ex is RpcException rpc ? rpc.Status.Detail : "The server did not answer. Try again.";
}
