using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake plugin list of the Plugins page: the simulated core plugins plus Restart, each one package. The Hardening scan
/// is off by default like on the server (its manifest says so); turning a package off hides its rail page and its tasks.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly Dictionary<string, bool> _packageStates = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<PluginPackageInfo> FakePackageTemplates =>
    [
        Package(SnapshotReportPluginId, "Snapshot report", "Snapshots of every camera and a PDF maintenance report.", hasPage: true, tasks: 0),
        Package(PkiPluginId, "PKI", "Issues device certificates for HTTPS and IEEE 802.1X.", hasPage: true, tasks: 9, alwaysOn: true),
        Package(MetadataMonitorPluginId, "Metadata Monitor", "Shows the events a camera sends, live.", hasPage: true, tasks: 0),
        Package(HardeningScanPluginId, "Hardening scan", "Checks devices against the AXIS OS hardening guide.", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(SystemReportPluginId, "System report", "Downloads the system reports of devices for Axis support.", hasPage: false, tasks: 0, alwaysOn: true),
        Package(RestartPluginId, "Restart", "Restarts devices.", hasPage: false, tasks: 1, alwaysOn: true),
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

            if (FakePackageTemplates.First(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)) is { AlwaysOn: true } always)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{always.DisplayName} is always on."));
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
        return package is null || package.AlwaysOn || _packageStates.GetValueOrDefault(package.Id, package.EnabledByDefault);
    }

    private List<PluginPackageInfo> FakePackages() =>
    [
        .. FakePackageTemplates.Select(p =>
        {
            p.Enabled = p.AlwaysOn || _packageStates.GetValueOrDefault(p.Id, p.EnabledByDefault);
            return p;
        }),
    ];

    private static PluginPackageInfo Package(string id, string name, string description, bool hasPage, int tasks,
        bool enabledByDefault = true, bool alwaysOn = false) => new()
    {
        Id = id,
        DisplayName = name,
        Description = description,
        AlwaysOn = alwaysOn,
        Version = "0.1.0",
        EnabledByDefault = enabledByDefault,
        HasPage = hasPage,
        MenuTaskCount = tasks,
    };
}
