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
/// One row of the Sites page: a bound site, or a node that waits to be bound (connected, or found by Search or Add
/// manually). Rows are replaced as a whole by every list of the server.
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
        IsWarning = site.State == SiteState.NotBound;
        IsInfo = site.State == SiteState.Waiting;
        IsGreyed = site.State is SiteState.BoundElsewhere or SiteState.BindingClosed;
        Detail = site.Message.Length > 0 ? site.Message : SiteTexts.DefaultDetail(site.State);
        NodeAddressText = SiteTexts.Address(site.NodeAddress, site.NodePort);
        DeviceCountText = IsBound ? site.DeviceCount.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        RoundTripText = site.RoundTripMs > 0 ? site.RoundTripMs.ToString("N0", CultureInfo.CurrentCulture) + " ms" : string.Empty;
        ConnectionsText = IsBound && site.State == SiteState.Online
            ? string.Create(CultureInfo.CurrentCulture, $"{site.ShortConnections:N0} / {site.PermanentConnections:N0}")
            : string.Empty;
        PortsWaitingText = site.PortsWaiting > 0 ? site.PortsWaiting.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        LargeTransfersText = site.LargeTransfers > 0 ? site.LargeTransfers.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        VersionText = site.UpdateAvailable ? site.Version + " · Update available" : site.Version;
        LastSeenText = site.LastSeen is { } seen && seen.Seconds > 0
            ? seen.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;
        LastSeenSortKey = site.LastSeen?.ToDateTime() ?? DateTime.MinValue;
        CanBind = mayChange && site.State == SiteState.Waiting && !IsBound;
        CanManage = mayChange && IsBound;
        CanChangeAddress = CanManage && site.Mode == SiteMode.Inbound;
        _search = string.Join(' ', site.Name, NodeAddressText, StateText, site.Version, site.NodeId);
    }

    public Site Site { get; }

    /// <summary>The key of the row: the site id, or the node id of a waiting node.</summary>
    public string Key => IsBound ? Site.Id : "node:" + Site.NodeId;

    public string Name => Site.Name;

    public bool IsBound { get; }

    public string StateText { get; }

    public string Detail { get; }

    public bool IsOk { get; }

    public bool IsError { get; }

    public bool IsWarning { get; }

    public bool IsInfo { get; }

    /// <summary>A found node that cannot be bound (bound to another server, binding closed): shown dimmed with the reason.</summary>
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

    public bool CanManage { get; }

    public bool CanChangeAddress { get; }

    public bool Matches(string text) => _search.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Rail page "Sites" (Servers): the bound sites and the site nodes waiting to be bound, live through WatchSites while the
/// page is shown. Reading needs "See sites"; Accept site nodes, Search, Add manually, Bind, Rename, Change address,
/// Replace with and Remove site are Administrator only.
/// </summary>
public sealed partial class SitesViewModel : ObservableObject, IDisposable
{
    /// <summary>How long Search looks for nodes that wait for the server.</summary>
    public const int SearchSeconds = 10;

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly UserSession? _session;
    private readonly Dictionary<string, Site> _found = new(StringComparer.Ordinal);
    private List<SiteRowViewModel> _all = [];
    private SiteList _list = new();
    private CancellationTokenSource? _active;
    private CancellationTokenSource? _search;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchNodesCommand))]
    public partial bool IsSearching { get; private set; }

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

    public string EmptyText => _all.Count > 0
        ? "No site matches the search."
        : _list.Accept ? SiteTexts.AcceptOnHint(_list.ListenAddress) : SiteTexts.AcceptOffHint;

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    /// <summary>The page is shown: read and watch the list.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
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

        _search?.Cancel();
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
        var acceptChanged = list.Accept != _list.Accept;
        _list = list;
        IsLoading = false;
        foreach (var site in list.Sites.Where(s => s.Id.Length > 0 || s.NodeId.Length > 0))
        {
            _found.Remove(site.NodeId);
        }

        var rows = new List<SiteRowViewModel>(list.Sites.Count + _found.Count);
        rows.AddRange(list.Sites.Select(s => new SiteRowViewModel(s, MayChange)));
        rows.AddRange(_found.Values.Select(s => new SiteRowViewModel(s, MayChange)));
        _all = rows;
        if (acceptChanged)
        {
            OnPropertyChanged(nameof(Accept));
            OnPropertyChanged(nameof(AcceptText));
        }

        Filter();
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

    private bool CanSearch => MayChange && !IsSearching;

    /// <summary>Search: nodes that wait for the server on the server's network (inbound), for <see cref="SearchSeconds"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchNodesAsync()
    {
        IsSearching = true;
        _search = new CancellationTokenSource();
        var found = 0;
        try
        {
            await foreach (var node in _api.FindSiteNodesAsync(SearchSeconds, null, _search.Token).ConfigureAwait(true))
            {
                if (node.Node is { } site && AddFound(site))
                {
                    found++;
                }
            }

            NoticeText = found switch
            {
                0 => "No site node waits for this server.",
                1 => "1 site node found.",
                _ => string.Create(CultureInfo.CurrentCulture, $"{found:N0} site nodes found."),
            };
            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException)
        {
            ErrorText = "The search failed: " + Message(ex);
        }
        finally
        {
            _search.Dispose();
            _search = null;
            IsSearching = false;
        }
    }

    /// <summary>Add manually: a node that waits for the server at an address.</summary>
    [RelayCommand(CanExecute = nameof(MayChange))]
    private async Task AddManuallyAsync()
    {
        var form = SiteFormViewModel.AddManually(_api);
        if (await _dialogs.ShowSiteFormAsync(form).ConfigureAwait(true) && form.FoundNode is { } node)
        {
            AddFound(node);
            NoticeText = null;
        }
    }

    [RelayCommand]
    private async Task BindAsync(SiteRowViewModel? row)
    {
        if (row is not { CanBind: true })
        {
            return;
        }

        var form = SiteFormViewModel.Bind(_api, row.Site);
        if (await _dialogs.ShowSiteFormAsync(form).ConfigureAwait(true))
        {
            _found.Remove(row.Site.NodeId);
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
    private async Task ChangeAddressAsync(SiteRowViewModel? row)
    {
        if (row is not { CanChangeAddress: true })
        {
            return;
        }

        if (await _dialogs.ShowSiteFormAsync(SiteFormViewModel.ChangeAddress(_api, row.Site)).ConfigureAwait(true))
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReplaceAsync(SiteRowViewModel? row)
    {
        if (row is not { CanManage: true })
        {
            return;
        }

        var others = _all.Where(r => r.IsBound && r.Site.Id != row.Site.Id).Select(r => r.Site).ToList();
        if (others.Count == 0)
        {
            ErrorText = "Bind the new site node first, then replace " + row.Name + " with it.";
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
        if (row is not { CanManage: true })
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

    private bool AddFound(Site site)
    {
        if (site.NodeId.Length == 0 || _list.Sites.Any(s => s.NodeId == site.NodeId))
        {
            return false;
        }

        _found[site.NodeId] = site;
        Apply(_list);
        return true;
    }

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
                    IsLoading = false;
                    ErrorText = ex.Status.Detail;
                });
            }
#pragma warning disable CA1031 // The connection dropped: watch again after a short delay.
            catch (Exception)
#pragma warning restore CA1031
            {
                _ui.Post(() =>
                {
                    IsLoading = false;
                    ErrorText ??= ReadError;
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
            AddManuallyCommand.NotifyCanExecuteChanged();
            SearchNodesCommand.NotifyCanExecuteChanged();
            Apply(_list); // row actions follow the role
        }
    }

    internal static string Message(Exception ex) => ex is RpcException rpc ? rpc.Status.Detail : "The server did not answer. Try again.";
}
