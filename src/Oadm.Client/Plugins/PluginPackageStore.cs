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

    /// <summary>The server ID licenses name (Plugins page); empty for an older server.</summary>
    public string ServerId { get; private set; } = string.Empty;

    /// <summary>OADM PRO is active on the server: PRO plugins can be turned on.</summary>
    public bool ProActive { get; private set; }

    /// <summary>
    /// The license of a package as the server checked it (<c>ICorePluginClientContext.LicenseOf</c>); NotRequired for a free
    /// plugin, null for a package the server does not list.
    /// </summary>
    public Oadm.Sdk.Licensing.PluginLicenseInfo? LicenseOf(string packageId) =>
        _packages.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)) is { } package
            ? ToInfo(package.License)
            : null;

    /// <summary>The SDK view of the server's license state.</summary>
    public static Oadm.Sdk.Licensing.PluginLicenseInfo ToInfo(PluginLicenseState? state)
    {
        if (state is null || state.Status == PluginLicenseStatus.PluginLicenseNotRequired)
        {
            return Oadm.Sdk.Licensing.PluginLicenseInfo.NotRequired;
        }

        static DateOnly? Day(string text) =>
            DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day : null;
        var data = state.Customer.Length == 0
            ? null
            : new Oadm.Sdk.Licensing.LicenseData(string.Empty, string.Empty, state.Customer, Day(state.Issued) ?? default, Day(state.Expires),
                [.. state.Features], state.Limits.ToDictionary(l => l.Key, l => l.Value, StringComparer.OrdinalIgnoreCase));
        return new Oadm.Sdk.Licensing.PluginLicenseInfo((Oadm.Sdk.Licensing.LicenseStatus)(int)state.Status, state.Reason, data, Day(state.TrialEnds));
    }

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

    /// <summary>Admin: enters a license; throws the server's error (the reason in Status.Detail).</summary>
    public async Task SetLicenseAsync(string packageId, string license, CancellationToken ct) =>
        Apply(await api.SetPluginLicenseAsync(packageId, license, ct).ConfigureAwait(true));

    /// <summary>Admin: removes a license; throws the server's error.</summary>
    public async Task RemoveLicenseAsync(string packageId, CancellationToken ct) =>
        Apply(await api.RemovePluginLicenseAsync(packageId, ct).ConfigureAwait(true));

    private void Apply(PluginPackageList? list)
    {
        _packages = list?.Packages ?? [];
        ServerId = list?.ServerId ?? string.Empty;
        ProActive = list?.ProActive ?? false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The plugin list could not be loaded: {Message}")]
    private static partial void LogLoadFailed(ILogger logger, string message);
}
