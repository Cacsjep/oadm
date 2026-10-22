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
/// in rail order. The header shows <see cref="ToString"/>.
/// </summary>
public sealed record PluginGroupKey(CorePluginGroup Group)
{
    public int Order => (int)Group;

    public override string ToString() => Group.ToString();

    /// <summary>The group the server reports ("Monitoring"); unknown or empty is Extensions, like the rail.</summary>
    public static PluginGroupKey Parse(string? group) =>
        new(Enum.TryParse(group, ignoreCase: true, out CorePluginGroup parsed) && Enum.IsDefined(parsed) ? parsed : CorePluginGroup.Extensions);
}

/// <summary>One plugin package of the Plugins page; the check box turns it on or off at once.</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    private bool _applying;

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
        _applying = true;
        IsEnabled = info.Enabled;
        _applying = false;
    }

    public string Id { get; }

    /// <summary>The group the page shows the package under (the grid groups by this property).</summary>
    public PluginGroupKey Group { get; }

    public string Name { get; }

    /// <summary>One sentence from plugin.json.</summary>
    public string Description { get; }


    public bool OffByDefault { get; }

    /// <summary>Core functionality listed only for its license: the check box stays on.</summary>
    public bool AlwaysOn { get; }

    /// <summary>"Works at sites", "Only this server's network"; empty from a server without sites.</summary>
    public string SiteText { get; }

    /// <summary>The ports it listens on at sites: "UDP 123"; empty for none.</summary>
    public string SitePortsText { get; }

    public bool HasSitePorts => SitePortsText.Length > 0;

    /// <summary>The texts of the Sites column (plugin.json siteSupport).</summary>
    internal static string SiteTextOf(PluginPackageInfo info) => info.SiteSupport switch
    {
        "full" => "Works at sites",
        "none" => "Only this server's network",
        _ => string.Empty,
    };

    /// <summary>The license as the server checked it; NotRequired for a free plugin.</summary>
    public PluginLicenseInfo License { get; }

    /// <summary>The plugin is sold with a license: the License column shows its state and the buttons.</summary>
    public bool NeedsLicense => License.Status != LicenseStatus.NotRequired;

    /// <summary>An OADM PRO plugin: the License column shows "OADM PRO".</summary>
    public bool IsPro { get; }

    /// <summary>A PRO plugin while OADM PRO is not active: it stays off, the check box is locked, Activate opens Settings.</summary>
    public bool ProLocked { get; }

    public bool ProActive => IsPro && !ProLocked;

    /// <summary>The tooltip of the OADM PRO chip.</summary>
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

    /// <summary>The detail below the status: who it is for and until when, or why it is not valid.</summary>
    public string LicenseDetail => License.Reason;

    public bool LicenseOk => License.Status == LicenseStatus.Valid;

    public bool LicenseWarning => License.Status == LicenseStatus.Trial;

    public bool LicenseError => NeedsLicense && !LicenseOk && !LicenseWarning;

    /// <summary>A license is stored (Remove applies); a trial or a missing license has none.</summary>
    public bool HasLicense => License.Data is not null;

    /// <summary>"Enter license", or "Change license" when one is stored.</summary>
    public string EnterLicenseText => HasLicense ? "Change license" : "Enter license";

    public bool CanEditLicense => NeedsLicense && _owner.IsAdmin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public string StateText => IsEnabled ? "On" : "Off";

    public bool CanToggle => _owner.IsAdmin && !IsBusy && !AlwaysOn && !ProLocked;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _ = ToggleAsync(value);
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanEditLicense));
        OnPropertyChanged(nameof(CanActivatePro));
    }

    internal void Set(bool enabled)
    {
        _applying = true;
        IsEnabled = enabled;
        _applying = false;
    }

    private async Task ToggleAsync(bool enabled)
    {
        IsBusy = true;
        try
        {
            await _owner.SetEnabledAsync(this, enabled).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// Rail page "Plugins" (administrators only): every plugin package of the server that can be turned off, with its state
/// and description; packages that are always on (core functionality) are not listed. Administrators turn a package on
/// or off at once (PluginService.SetPackageEnabled). A package's rail page, context menu entries and toolbar entries follow through
/// <see cref="PluginPackageStore.Changed"/>.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly PluginPackageStore _store;
    private readonly UserSession? _session;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private readonly ILogger<PluginsViewModel> _logger;

    public PluginsViewModel(PluginPackageStore store, ILogger<PluginsViewModel> logger, UserSession? session = null, IDialogService? dialogs = null, IClipboardService? clipboard = null,
        Devices.DeviceStore? devices = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _session = session;
        _dialogs = dialogs;
        _clipboard = clipboard;
        store.Changed += (_, _) => Rebuild();
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

    /// <summary>The server has sites: the page shows the Sites column (what works at a site), like the device grid's Site column.</summary>
    [ObservableProperty]
    public partial bool HasSites { get; private set; }

    /// <summary>At least one plugin is sold with a license or is a PRO plugin: the page shows the License column.</summary>
    public bool HasLicensedPlugins => Plugins.Any(p => p.NeedsLicense || p.IsPro);

    /// <summary>OADM PRO is active on the server: PRO plugins can be turned on.</summary>
    public bool ProActive => _store.ProActive;

    /// <summary>Asks the shell to open a host page (<c>HostPages</c> key): Activate opens Settings, where the License card is.</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>Activate of a PRO plugin: opens Settings (License card).</summary>
    [RelayCommand]
    private void ActivatePro() => NavigateRequested?.Invoke(this, Oadm.Sdk.Client.HostPages.Settings);

    public Oadm.Sdk.Client.Collections.RangeObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    /// <summary>
    /// The groups that have packages, in rail order (the page groups like the navigation rail).
    /// Set with every rebuild, before <see cref="Plugins"/> changes.
    /// </summary>
    public IReadOnlyList<PluginGroupKey> Groups { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool IsEmpty => Plugins.Count == 0;

    /// <summary>This server's ID, which plugin licenses name; empty for an older server.</summary>
    public string ServerId => _store.ServerId;

    public bool HasServerId => ServerId.Length > 0;

    /// <summary>The server ID shows here only while a plugin needs a license (the Settings page always shows it).</summary>
    public bool ShowServerId => HasServerId && HasLicensedPlugins;

    [RelayCommand]
    private async Task CopyServerIdAsync()
    {
        if (_clipboard is not null && HasServerId)
        {
            await _clipboard.SetTextAsync(ServerId).ConfigureAwait(true);
        }
    }

    /// <summary>Opens "License of X": paste or choose the file, the server checks and stores it.</summary>
    [RelayCommand]
    private async Task EnterLicenseAsync(PluginRowViewModel? row)
    {
        if (row is null || _dialogs is null || !row.CanEditLicense)
        {
            return;
        }

        ErrorText = null;
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

        try
        {
            await _store.RemoveLicenseAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"The license of {row.Name} could not be removed: {Message(ex)}";
        }
    }

    internal async Task SetEnabledAsync(PluginRowViewModel row, bool enabled)
    {
        try
        {
            await _store.SetEnabledAsync(row.Id, enabled, CancellationToken.None).ConfigureAwait(true);
            ErrorText = null;
            LogChanged(_logger, row.Id, enabled ? "on" : "off");
        }
        catch (Exception ex)
        {
            row.Set(!enabled);
            ErrorText = string.Create(CultureInfo.CurrentCulture, $"{row.Name} could not be turned {(enabled ? "on" : "off")}: {Message(ex)}");
        }
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
        OnPropertyChanged(nameof(ServerId));
        OnPropertyChanged(nameof(HasServerId));
        OnPropertyChanged(nameof(HasLicensedPlugins));
        OnPropertyChanged(nameof(ShowServerId));
    }

    /// <summary>gRPC errors carry the user message in Status.Detail.</summary>
    private static string Message(Exception ex) =>
        ex is Grpc.Core.RpcException rpc && !string.IsNullOrEmpty(rpc.Status.Detail) ? rpc.Status.Detail : ex.Message;

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} turned {State}")]
    private static partial void LogChanged(ILogger logger, string packageId, string state);
}
