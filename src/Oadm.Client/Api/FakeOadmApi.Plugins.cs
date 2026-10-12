using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake plugin list of the Plugins page: the simulated core plugins plus Restart, each one package with its rail group. The Hardening scan
/// and the other monitoring plugins are off by default like on the server (their manifests say so); turning a package off hides its rail page and its tasks.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly Dictionary<string, bool> _packageStates = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<PluginPackageInfo> FakePackageTemplates =>
    [
        Package(SnapshotReportPluginId, "Snapshot report", "Snapshots of every camera and a PDF maintenance report.", "Reporting", hasPage: true, tasks: 0),
        Package(PkiPluginId, "PKI", "Issues device certificates for HTTPS and IEEE 802.1X.", "Security", hasPage: true, tasks: 9, alwaysOn: true),
        Package(MetadataMonitorPluginId, "Metadata Monitor", "Shows the events a camera sends, live.", "Monitoring", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(HardeningScanPluginId, "Hardening scan", "Checks devices against the AXIS OS hardening guide.", "Security", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(IoMonitorPluginId, "I/O Monitor", "Shows the inputs and outputs of a device, live.", "Monitoring", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(DeviceTriggersPluginId, "Device triggers", "Uses events of the devices in event rules.", "Automation", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(WebViewPluginId, "Web View", "Opens the web interface of the devices in tabs, logged in with the stored login.", "Utilities", hasPage: true, tasks: 0),
        Package(LiveViewPluginId, "Live View", "Saved views of several cameras at once, opened in tabs.", "Monitoring", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(EdgeRecordingPluginId, "Edge Recording Export", "Downloads the recordings on the cameras' SD cards to this computer.", "Maintenance", hasPage: true, tasks: 0, enabledByDefault: false),
        Package("oadm.vapix-commander", "VAPIX Commander", "Sends VAPIX commands to many devices at once.", "Automation", hasPage: true, tasks: 0),
        Package("oadm.ntp-server", "NTP server", "Serves the time to devices from this server.", "Servers", hasPage: true, tasks: 1),
        Package("oadm.dhcp-server", "DHCP server", "Hands out IP addresses and can add new Axis devices automatically.", "Servers", hasPage: true, tasks: 0),
        Package("oadm.image-health", "Image Health Dashboard", "Shows blur, blocked and redirected views from AXIS Image Health Analytics.", "Monitoring", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(SystemReportPluginId, "System report", "Downloads the system reports of devices for Axis support.", "Reporting", hasPage: false, tasks: 0, alwaysOn: true),
        Package(RestartPluginId, "Restart", "Restarts devices.", "Extensions", hasPage: false, tasks: 1, alwaysOn: true),
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

    private static PluginPackageInfo Package(string id, string name, string description, string group, bool hasPage, int tasks,
        bool enabledByDefault = true, bool alwaysOn = false) => new()
    {
        Id = id,
        Group = group,
        DisplayName = name,
        Description = description,
        AlwaysOn = alwaysOn,
        Version = "0.1.0",
        EnabledByDefault = enabledByDefault,
        HasPage = hasPage,
        MenuTaskCount = tasks,
    };
}
