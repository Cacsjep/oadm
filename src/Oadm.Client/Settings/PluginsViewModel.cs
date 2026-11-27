using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Settings;

/// <summary>
/// A group of the Plugins page: the rail group of the package (<see cref="CorePluginGroup"/>, "Servers", "Monitoring"),
/// in rail order. The heading shows <see cref="ToString"/>.
/// </summary>
public sealed record PluginGroupKey(CorePluginGroup Group)
{
    public int Order => Shell.RailGroups.Order(Group);

    public override string ToString() => Shell.RailGroups.Title(Group);

    /// <summary>The icon of the group's tiles (a theme icon key without "Icon.").</summary>
    public string IconKey => Group switch
    {
        CorePluginGroup.Servers => "server",
        CorePluginGroup.Automation => "zap",
        CorePluginGroup.Security => "shield",
        CorePluginGroup.Monitoring => "activity",
        CorePluginGroup.Vision => "eye",
        CorePluginGroup.Integrations => "network",
        CorePluginGroup.Reporting => "file",
        CorePluginGroup.Maintenance => "wrench",
        CorePluginGroup.Utilities => "app",
        _ => "plugin",
    };

    /// <summary>The group the server reports ("Monitoring"); unknown or empty is Extensions, like the rail.</summary>
    public static PluginGroupKey Parse(string? group) =>
        new(Enum.TryParse(group, ignoreCase: true, out CorePluginGroup parsed) && Enum.IsDefined(parsed) ? parsed : CorePluginGroup.Extensions);
}

/// <summary>One plugin package of the Plugins page, a tile: one click on it turns the package on or off at once.</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;

    internal PluginRowViewModel(PluginsViewModel owner, PluginPackageInfo info)
    {
        _owner = owner;
        Id = info.Id;
        Group = PluginGroupKey.Parse(info.Group);
        Name = info.DisplayName;
        Description = info.Description;
        OffByDefault = !info.EnabledByDefault;
        AlwaysOn = info.AlwaysOn;
        License = PluginPackageStore.ToInfo(info.License);
        SiteText = SiteTextOf(info);
        SitePortsText = string.Join(", ", info.SitePorts.Select(p => $"{(p.Protocol == "udp" ? "UDP" : "TCP")} {p.Port}"));
        IsPro = info.Pro;
        ProLocked = info.Pro && !owner.ProActive;
        if (owner.SwitchingTo(Id) is { } target)
        {
            // A rebuild while this package is being switched: the tile keeps its ring and the state it is going to.
            IsEnabled = target;
            IsBusy = true;
        }
        else
        {
            IsEnabled = info.Enabled;
        }

        ErrorText = owner.ErrorOf(Id);
        SearchWords = owner.Words;
    }

    public string Id { get; }

    /// <summary>The group the page shows the package under.</summary>
    public PluginGroupKey Group { get; }

    public string Name { get; }

    /// <summary>One sentence from plugin.json.</summary>
    public string Description { get; }

    public bool OffByDefault { get; }

    /// <summary>Core functionality listed only for its license: it stays on.</summary>
    public bool AlwaysOn { get; }

    /// <summary>"Works at sites", "Only this server's network"; empty from a server without sites.</summary>
    public string SiteText { get; }

    /// <summary>The ports it listens on at sites: "UDP 123"; empty for none.</summary>
    public string SitePortsText { get; }

    public bool HasSitePorts => SitePortsText.Length > 0;

    /// <summary>"Works at sites (UDP 123)": the line of the tile while the server has sites.</summary>
    public string SiteLine => HasSitePorts ? $"{SiteText} ({SitePortsText})" : SiteText;

    public bool ShowSiteLine => _owner.HasSites && SiteText.Length > 0;

    /// <summary>The texts of the sites line (plugin.json siteSupport).</summary>
    internal static string SiteTextOf(PluginPackageInfo info) => info.SiteSupport switch
    {
        "full" => "Works at sites",
        "none" => "Only this server's network",
        _ => string.Empty,
    };

    /// <summary>The license as the server checked it; NotRequired for a free plugin.</summary>
    public PluginLicenseInfo License { get; }

    /// <summary>The plugin is sold with a license: the tile shows its state and the license buttons.</summary>
    public bool NeedsLicense => License.Status != LicenseStatus.NotRequired;

    /// <summary>An OADM PRO plugin.</summary>
    public bool IsPro { get; }

    /// <summary>A PRO plugin while OADM PRO is not active: it stays off, the tile says why and a click explains it.</summary>
    public bool ProLocked { get; }

    public bool ProActive => IsPro && !ProLocked;

    /// <summary>The reason line of a locked PRO plugin.</summary>
    public string ProDetail => ProLocked ? ProLicenseTexts.NeedsPro : "OADM PRO is active.";

    public bool CanActivatePro => ProLocked && _owner.IsAdmin;

    /// <summary>"Licensed", "Trial", "Expired" ... for the status chip.</summary>
    public string LicenseText => License.Status switch
    {
        LicenseStatus.Valid => "Licensed",
        LicenseStatus.Trial => "Trial",
        LicenseStatus.TrialEnded => "Trial ended",
        LicenseStatus.Missing => "No license",
        LicenseStatus.Expired => "Expired",
        LicenseStatus.WrongServer => "Other server",
        LicenseStatus.Invalid => "Not valid",
        _ => string.Empty,
    };

    /// <summary>The tooltip of the status: who it is for and until when, or why it is not valid.</summary>
    public string LicenseDetail => License.Reason;

    public bool LicenseOk => License.Status == LicenseStatus.Valid;

    public bool LicenseWarning => License.Status == LicenseStatus.Trial;

    public bool LicenseError => NeedsLicense && !LicenseOk && !LicenseWarning;

    /// <summary>A license is stored (Remove applies); a trial or a missing license has none.</summary>
    public bool HasLicense => License.Data is not null;

    /// <summary>"Enter license", or "Change license" when one is stored.</summary>
    public string EnterLicenseText => HasLicense ? "Change license" : "Enter license";

    public bool CanEditLicense => NeedsLicense && _owner.IsAdmin;

    /// <summary>On (the accent tile with the check) or off; while a switch runs, the state it is going to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileTip), nameof(ShowCheck))]
    public partial bool IsEnabled { get; private set; }

    /// <summary>A turn on or off is running: the tile shows a ring in place of its check and takes no second click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle), nameof(ShowCheck))]
    public partial bool IsBusy { get; private set; }

    public bool CanToggle => _owner.IsAdmin && !IsBusy && !AlwaysOn && !ProLocked;

    /// <summary>The check top right: on, and no switch running.</summary>
    public bool ShowCheck => IsEnabled && !IsBusy;

    /// <summary>"Click to turn on" or "Click to turn off"; a locked PRO plugin says why inside the tile instead.</summary>
    public string? TileTip => ProLocked ? null : IsEnabled ? "Click to turn off" : "Click to turn on";

    /// <summary>Why the last turn on or off or license removal failed: in red inside the tile, kept over a reload of the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>The search words, marked in the name and the description.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SearchWords { get; private set; }

    /// <summary>Whether every search word occurs in the name, the description or the group (case does not matter).</summary>
    public bool Matches(IReadOnlyList<string> words)
    {
        foreach (var word in words)
        {
            if (!Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                && !Description.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                && !Group.ToString().Contains(word, StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A click on the tile, Space or Enter: on or off; a locked PRO plugin explains what it needs instead.</summary>
    /// <remarks>Concurrent runs allowed, so the tile never looks disabled while a switch runs; the page ignores the click.</remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleAsync() => _owner.ToggleAsync(this);

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanEditLicense));
        OnPropertyChanged(nameof(CanActivatePro));
    }

    internal void NotifySites() => OnPropertyChanged(nameof(ShowSiteLine));

    internal void Set(bool enabled) => IsEnabled = enabled;

    internal void SetBusy(bool busy) => IsBusy = busy;

    internal void SetError(string? error) => ErrorText = error;

    internal void SetWords(IReadOnlyList<string> words) => SearchWords = words;
}

/// <summary>A quick filter of the Plugins page.</summary>
public enum PluginFilter
{
    All,
    On,
    Off,

    /// <summary>The installed optional plugins (add-ons, docs/spec/add-ons.md).</summary>
    Optional,
}

/// <summary>One quick filter pill: its name, how many tiles it shows with the current search, whether it is chosen.</summary>
public sealed partial class PluginFilterPill : ObservableObject
{
    private readonly PluginsViewModel _owner;

    internal PluginFilterPill(PluginsViewModel owner, PluginFilter filter, string label)
    {
        _owner = owner;
        Filter = filter;
        Label = label;
    }

    public PluginFilter Filter { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial int Count { get; internal set; }

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    [RelayCommand]
    private void Select() => _owner.SelectedFilter = Filter;
}

/// <summary>The tiles of one group under its heading, as the search and the filter leave them.</summary>
public sealed record PluginTileGroup(PluginGroupKey Key, IReadOnlyList<PluginRowViewModel> Tiles)
{
    public string Title => Key.ToString();
}

/// <summary>
/// Rail page "Plugins" (administrators only): every plugin package of the server that can be turned off, as tiles under
/// the rail groups, with a search and the quick filters All, On, Off and Optional; packages that are always on (core
/// functionality) are not listed. A click on a tile turns its package on or off at once (PluginService.SetPackageEnabled).
/// A package's rail page, context menu entries and toolbar entries follow through <see cref="PluginPackageStore.Changed"/>.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly PluginPackageStore _store;
    private readonly UserSession? _session;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private readonly ILogger<PluginsViewModel> _logger;

    /// <summary>The packages being turned on or off, with the state they go to (kept over a rebuild of the rows).</summary>
    private readonly Dictionary<string, bool> _switching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last error of a package's tile (kept over a rebuild of the rows, cleared by its next click).</summary>
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    public PluginsViewModel(PluginPackageStore store, ILogger<PluginsViewModel> logger, UserSession? session = null, IDialogService? dialogs = null, IClipboardService? clipboard = null,
        Devices.DeviceStore? devices = null, Plugins.AddOns.AddOnsViewModel? addOns = null)
    {
        AddOns = addOns;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _session = session;
        _dialogs = dialogs;
        _clipboard = clipboard;
        Filters =
        [
            new PluginFilterPill(this, PluginFilter.All, "All"),
            new PluginFilterPill(this, PluginFilter.On, "On"),
            new PluginFilterPill(this, PluginFilter.Off, "Off"),
            new PluginFilterPill(this, PluginFilter.Optional, "Optional"),
        ];
        store.Changed += (_, _) => Rebuild();
        addOns?.AddOns.CollectionChanged += (_, _) => Refilter();
        if (devices is not null)
        {
            HasSites = devices.Sites.HasSites;
            devices.Sites.PropertyChanged += (_, _) => HasSites = devices.Sites.HasSites;
        }

        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    foreach (PluginRowViewModel row in Plugins)
                    {
                        row.NotifyRole();
                    }
                }
            };
        }

        Rebuild();
    }

    public bool IsAdmin => _session?.IsAdmin ?? true;

    /// <summary>The Optional plugins card below the tiles (docs/spec/add-ons.md "Plugins page"); null hides it.</summary>
    public Plugins.AddOns.AddOnsViewModel? AddOns { get; }

    /// <summary>The server has sites: each tile says whether its plugin works at a site, like the device grid's Site column.</summary>
    [ObservableProperty]
    public partial bool HasSites { get; private set; }

    partial void OnHasSitesChanged(bool value)
    {
        foreach (PluginRowViewModel row in Plugins)
        {
            row.NotifySites();
        }
    }

    /// <summary>At least one plugin is sold with a license or is a PRO plugin.</summary>
    public bool HasLicensedPlugins => Plugins.Any(p => p.NeedsLicense || p.IsPro);

    /// <summary>OADM PRO is active on the server: PRO plugins can be turned on.</summary>
    public bool ProActive => _store.ProActive;

    /// <summary>Asks the shell to open a host page (<c>HostPages</c> key): Activate opens Settings, where OADM PRO is.</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>Activate of a PRO plugin: opens Settings (OADM PRO).</summary>
    [RelayCommand]
    private void ActivatePro() => NavigateRequested?.Invoke(this, Oadm.Sdk.Client.HostPages.Settings);

    /// <summary>Every listed package, in rail group order and by name.</summary>
    public Oadm.Sdk.Client.Collections.RangeObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    /// <summary>The groups that have packages, in rail order. Set with every rebuild, before <see cref="Plugins"/> changes.</summary>
    public IReadOnlyList<PluginGroupKey> Groups { get; private set; } = [];

    /// <summary>The tiles the search and the filter leave, under their group headings (groups without one are left out).</summary>
    public Oadm.Sdk.Client.Collections.RangeObservableCollection<PluginTileGroup> TileGroups { get; } = [];

    /// <summary>The quick filters All, On, Off, Optional with their counts.</summary>
    public IReadOnlyList<PluginFilterPill> Filters { get; }

    /// <summary>What the search box holds: every word must occur in a tile's name, description or group.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    partial void OnSearchTextChanged(string? value) => Refilter();

    /// <summary>The chosen quick filter (All at the start).</summary>
    [ObservableProperty]
    public partial PluginFilter SelectedFilter { get; set; }

    partial void OnSelectedFilterChanged(PluginFilter value) => Refilter();

    /// <summary>The search words (<see cref="SearchText"/> split at white space).</summary>
    public IReadOnlyList<string> Words { get; private set; } = [];

    /// <summary>"12 of 22 on": the live status of the whole page, at the right end of its header.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>The search or the filter leaves no tile (and there are plugins).</summary>
    [ObservableProperty]
    public partial bool NothingFound { get; private set; }

    public bool IsEmpty => Plugins.Count == 0;

    /// <summary>
    /// Reads the plugin list and the add-ons again (the page opened): the PRO lock follows a license that ended or changed
    /// elsewhere.
    /// </summary>
    public Task RefreshAsync() => Task.WhenAll(_store.LoadAsync(CancellationToken.None), AddOns?.LoadAsync(CancellationToken.None) ?? Task.CompletedTask);

    /// <summary>Opens "License of X": paste or choose the file, the server checks and stores it.</summary>
    [RelayCommand]
    private async Task EnterLicenseAsync(PluginRowViewModel? row)
    {
        if (row is null || _dialogs is null || !row.CanEditLicense)
        {
            return;
        }

        SetError(row, null);
        await _dialogs.ShowPluginLicenseAsync(new PluginLicenseViewModel(_store, row.Id, row.Name, _dialogs, _clipboard)).ConfigureAwait(true);
    }

    /// <summary>Removes a stored license after a confirmation.</summary>
    [RelayCommand]
    private async Task RemoveLicenseAsync(PluginRowViewModel? row)
    {
        if (row is null || _dialogs is null || !row.CanEditLicense || !row.HasLicense)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Remove license", $"Remove the license of {row.Name}? The plugin may stop working.", "Remove license").ConfigureAwait(true))
        {
            return;
        }

        string id = row.Id;
        _errors.Remove(id);
        string? error = null;
        try
        {
            await _store.RemoveLicenseAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = $"The license could not be removed: {Message(ex)}";
        }

        SetError(Current(id) ?? row, error);
    }

    /// <summary>The state a package is being switched to, null while no switch runs for it.</summary>
    internal bool? SwitchingTo(string id) => _switching.TryGetValue(id, out bool target) ? target : null;

    /// <summary>The error a package's tile shows, null for none.</summary>
    internal string? ErrorOf(string id) => _errors.GetValueOrDefault(id);

    /// <summary>
    /// A click on a tile: turns its package on or off; a locked PRO plugin asks to open Settings, where OADM PRO is,
    /// and changes nothing. A click while a switch runs does nothing.
    /// </summary>
    internal async Task ToggleAsync(PluginRowViewModel row)
    {
        if (!IsAdmin || row.IsBusy || row.AlwaysOn)
        {
            return;
        }

        if (row.ProLocked)
        {
            if (_dialogs is null
                || await _dialogs.ConfirmAsync("OADM PRO", $"{row.Name} needs an OADM PRO license or trial. Start the trial or import a license in Settings.", "Open Settings").ConfigureAwait(true))
            {
                ActivatePro();
            }

            return;
        }

        await SwitchAsync(row, !row.IsEnabled).ConfigureAwait(true);
    }

    /// <summary>
    /// Turns a package on or off. One switch per package at a time: a click while one runs sends nothing. The tile shows
    /// the state it goes to with a ring until the server answered; on failure it goes back and the error shows in the tile.
    /// </summary>
    internal async Task SwitchAsync(PluginRowViewModel row, bool enabled)
    {
        string id = row.Id;
        if (!_switching.TryAdd(id, enabled))
        {
            return;
        }

        _errors.Remove(id);
        row.SetError(null);
        row.Set(enabled);
        row.SetBusy(true);
        string? error = null;
        try
        {
            await _store.SetEnabledAsync(id, enabled, CancellationToken.None).ConfigureAwait(true);
            LogChanged(_logger, id, enabled ? "on" : "off");
        }
        catch (Exception ex)
        {
            error = string.Create(CultureInfo.CurrentCulture, $"Could not be turned {(enabled ? "on" : "off")}: {Message(ex)}");
        }
        finally
        {
            _switching.Remove(id);
        }

        // The rows may have been rebuilt meanwhile: the current tile of the package shows the server's state again.
        PluginRowViewModel current = Current(id) ?? row;
        PluginPackageInfo? info = _store.Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        current.Set(info?.Enabled ?? (error is null ? enabled : !enabled));
        current.SetBusy(false);
        SetError(current, error);
        Refilter();
    }

    private PluginRowViewModel? Current(string id) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private void SetError(PluginRowViewModel row, string? error)
    {
        if (error is null)
        {
            _errors.Remove(row.Id);
        }
        else
        {
            _errors[row.Id] = error;
        }

        row.SetError(error);
    }

    private void Rebuild()
    {
        var rows = _store.Packages
            .Where(p => !p.AlwaysOn || p.License is { Status: not PluginLicenseStatus.PluginLicenseNotRequired })
            .Select(p => new PluginRowViewModel(this, p))
            .OrderBy(r => r.Group.Order)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Groups = [.. rows.Select(r => r.Group).Distinct()];
        OnPropertyChanged(nameof(Groups));
        Plugins.ReplaceAll(rows);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasLicensedPlugins));
        Refilter();
    }

    /// <summary>
    /// Applies the search and the filter in one pass over the rows (O(n)): the counts of the pills (with the search), the
    /// tiles per group and the page's "x of y on".
    /// </summary>
    private void Refilter()
    {
        Words = WordsOf(SearchText);
        var optional = AddOns is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : AddOns.AddOns.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int all = 0, on = 0, off = 0, opt = 0, enabled = 0;
        var groups = new List<PluginTileGroup>();
        List<PluginRowViewModel>? tiles = null;
        PluginGroupKey? group = null;
        foreach (PluginRowViewModel row in Plugins)
        {
            row.SetWords(Words);
            if (row.IsEnabled)
            {
                enabled++;
            }

            if (!row.Matches(Words))
            {
                continue;
            }

            bool isOptional = optional.Contains(row.Id);
            all++;
            on += row.IsEnabled ? 1 : 0;
            off += row.IsEnabled ? 0 : 1;
            opt += isOptional ? 1 : 0;
            bool shown = SelectedFilter switch
            {
                PluginFilter.On => row.IsEnabled,
                PluginFilter.Off => !row.IsEnabled,
                PluginFilter.Optional => isOptional,
                _ => true,
            };
            if (!shown)
            {
                continue;
            }

            if (group != row.Group)
            {
                group = row.Group;
                tiles = [];
                groups.Add(new PluginTileGroup(group, tiles));
            }

            tiles!.Add(row);
        }

        foreach (PluginFilterPill pill in Filters)
        {
            pill.IsSelected = pill.Filter == SelectedFilter;
            pill.Count = pill.Filter switch
            {
                PluginFilter.On => on,
                PluginFilter.Off => off,
                PluginFilter.Optional => opt,
                _ => all,
            };
        }

        TileGroups.ReplaceAll(groups);
        CountText = Plugins.Count == 0 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{enabled} of {Plugins.Count} on");
        NothingFound = Plugins.Count > 0 && groups.Count == 0;
    }

    /// <summary>
    /// The search words: the text split at white space; a word of more than 3 letters ending in "s" is searched without it,
    /// so "plates" finds "plate" and "cards" finds "card".
    /// </summary>
    public static IReadOnlyList<string> WordsOf(string? search) =>
    [
        .. (search ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Length > 3 && w[^1] is 's' or 'S' ? w[..^1] : w),
    ];

    /// <summary>gRPC errors carry the user message in Status.Detail.</summary>
    private static string Message(Exception ex) =>
        ex is Grpc.Core.RpcException rpc && !string.IsNullOrEmpty(rpc.Status.Detail) ? rpc.Status.Detail : ex.Message;

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} turned {State}")]
    private static partial void LogChanged(ILogger logger, string packageId, string state);
}
