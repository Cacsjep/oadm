using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake plugin list of the Settings page: the simulated core plugins plus Restart, each one package. The Hardening scan
/// is off by default like on the server (its manifest says so); turning a package off hides its rail page and its tasks.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly Dictionary<string, bool> _packageStates = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<PluginPackageInfo> FakePackageTemplates =>
    [
        Package(SnapshotReportPluginId, "Snapshot report", hasPage: true, tasks: 0),
        Package(PkiPluginId, "PKI", hasPage: true, tasks: 9),
        Package(MetadataMonitorPluginId, "Metadata Monitor", hasPage: true, tasks: 0),
        Package(HardeningScanPluginId, "Hardening scan", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(SystemReportPluginId, "System report", hasPage: false, tasks: 0),
        Package(RestartPluginId, "Restart", hasPage: false, tasks: 1),
    ];

    public Task<IReadOnlyList<PluginPackageInfo>> ListPluginPackagesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<PluginPackageInfo>>(FakePackages());
        }
    }

    public Task<IReadOnlyList<PluginPackageInfo>> SetPluginPackageEnabledAsync(string packageId, bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (!FakePackageTemplates.Any(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Unknown plugin '{packageId}'."));
            }

            _packageStates[packageId] = enabled;
            return Task.FromResult<IReadOnlyList<PluginPackageInfo>>(FakePackages());
        }
    }

    /// <summary>Whether a plugin id (core plugin or task) belongs to a package that is on. Call under the lock.</summary>
    private bool IsFakePluginEnabled(string pluginId)
    {
        PluginPackageInfo? package = FakePackageTemplates.FirstOrDefault(p =>
            string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase)
            || pluginId.StartsWith(p.Id + ".", StringComparison.OrdinalIgnoreCase));
        return package is null || _packageStates.GetValueOrDefault(package.Id, package.EnabledByDefault);
    }

    private List<PluginPackageInfo> FakePackages() =>
    [
        .. FakePackageTemplates.Select(p =>
        {
            p.Enabled = _packageStates.GetValueOrDefault(p.Id, p.EnabledByDefault);
            return p;
        }),
    ];

    private static PluginPackageInfo Package(string id, string name, bool hasPage, int tasks, bool enabledByDefault = true) => new()
    {
        Id = id,
        DisplayName = name,
        Version = "0.1.0",
        EnabledByDefault = enabledByDefault,
        HasPage = hasPage,
        MenuTaskCount = tasks,
    };
}
