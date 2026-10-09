using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Settings;

/// <summary>One plugin package of the Settings page's plugin list; the check box turns it on or off at once.</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    private bool _applying;

    internal PluginRowViewModel(PluginsViewModel owner, PluginPackageInfo info, string adds)
    {
        _owner = owner;
        Id = info.Id;
        Name = info.DisplayName;
        Version = info.Version;
        Adds = adds;
        OffByDefault = !info.EnabledByDefault;
        _applying = true;
        IsEnabled = info.Enabled;
        _applying = false;
    }

    public string Id { get; }

    public string Name { get; }

    public string Version { get; }

    /// <summary>What the plugin adds: "Page", "9 context menu entries", "Toolbar button".</summary>
    public string Adds { get; }

    public bool OffByDefault { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public string StateText => IsEnabled ? "On" : "Off";

    public bool CanToggle => _owner.IsAdmin && !IsBusy;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _ = ToggleAsync(value);
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

    internal void NotifyRole() => OnPropertyChanged(nameof(CanToggle));

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
/// Settings page, card "Plugins": every plugin package of the server with its state. Administrators turn a package on
/// or off at once (PluginService.SetPackageEnabled); operators see the list read-only. A package's rail page, context
/// menu entries and toolbar entries follow through <see cref="PluginPackageStore.Changed"/>.
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly PluginPackageStore _store;
    private readonly IClientPluginRegistry? _registry;
    private readonly UserSession? _session;
    private readonly ILogger<PluginsViewModel> _logger;

    public PluginsViewModel(PluginPackageStore store, ILogger<PluginsViewModel> logger, IClientPluginRegistry? registry = null, UserSession? session = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _registry = registry;
        _session = session;
        store.Changed += (_, _) => Rebuild();
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    OnPropertyChanged(nameof(Hint));
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

    public Oadm.Sdk.Client.Collections.RangeObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    /// <summary>The line above the list.</summary>
    public string Hint => IsAdmin
        ? "A plugin that is off shows no page, menu entry or toolbar button."
        : "Only administrators can turn plugins on or off.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool IsEmpty => Plugins.Count == 0;

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
        Plugins.ReplaceAll(_store.Packages
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PluginRowViewModel(this, p, AddsText(p))));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string AddsText(PluginPackageInfo package)
    {
        var parts = new List<string>();
        if (package.HasPage)
        {
            parts.Add("Page");
        }

        if (package.MenuTaskCount > 0)
        {
            parts.Add(package.MenuTaskCount == 1
                ? "1 context menu entry"
                : string.Create(CultureInfo.CurrentCulture, $"{package.MenuTaskCount} context menu entries"));
        }

        if (_registry?.ToolbarPlugins.Any(t => string.Equals(_registry.PackageOf(t), package.Id, StringComparison.OrdinalIgnoreCase)) == true)
        {
            parts.Add("Toolbar button");
        }

        return string.Join(", ", parts);
    }

    /// <summary>gRPC errors carry the user message in Status.Detail.</summary>
    private static string Message(Exception ex) =>
        ex is Grpc.Core.RpcException rpc && !string.IsNullOrEmpty(rpc.Status.Detail) ? rpc.Status.Detail : ex.Message;

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} turned {State}")]
    private static partial void LogChanged(ILogger logger, string packageId, string state);
}
