using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins;

/// <summary>
/// The server's plugin packages and which are on (PluginService.ListPackages). Loaded on every connect and after a change
/// on the Settings page; <see cref="Changed"/> lets the shell add or remove rail pages, refresh the task menus and hide the
/// toolbar entries of packages that are off. A package the server does not list counts as on (older server).
/// </summary>
public sealed partial class PluginPackageStore(IOadmApi api, ILogger<PluginPackageStore> logger)
{
    private IReadOnlyList<PluginPackageInfo> _packages = [];

    public IReadOnlyList<PluginPackageInfo> Packages => _packages;

    /// <summary>Raised on the calling thread after the list was loaded or changed.</summary>
    public event EventHandler? Changed;

    public bool IsEnabled(string? packageId) =>
        string.IsNullOrEmpty(packageId)
        || _packages.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)) is not { } package
        || package.Enabled;

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            Apply(await api.ListPluginPackagesAsync(ct).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLoadFailed(logger, ex.Message);
        }
    }

    /// <summary>Admin: turns a package on or off; throws the server's error.</summary>
    public async Task SetEnabledAsync(string packageId, bool enabled, CancellationToken ct) =>
        Apply(await api.SetPluginPackageEnabledAsync(packageId, enabled, ct).ConfigureAwait(true));

    private void Apply(IReadOnlyList<PluginPackageInfo>? packages)
    {
        _packages = packages ?? [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The plugin list could not be loaded: {Message}")]
    private static partial void LogLoadFailed(ILogger logger, string message);
}
