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
        Package(DashboardsPluginId, "Dashboards", "Live values of device triggers and live video on dashboards.", "Monitoring", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(EdgeRecordingPluginId, "Edge Recording Export", "Downloads the recordings on the cameras' SD cards to this computer.", "Maintenance", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(EdgeStoragePluginId, "Edge storage", "Shows the SD cards of the cameras and formats, checks and encrypts them.", "Maintenance", hasPage: true, tasks: 0, enabledByDefault: false),
        Package("oadm.vapix-commander", "VAPIX Commander", "Sends VAPIX commands to many devices at once.", "Automation", hasPage: true, tasks: 0),
        Package("oadm.ntp-server", "NTP server", "Serves the time to devices from this server.", "Servers", hasPage: true, tasks: 1),
        Package("oadm.dhcp-server", "DHCP server", "Hands out IP addresses and can add new Axis devices automatically.", "Servers", hasPage: true, tasks: 0),
        Package("oadm.image-health", "Image Health Dashboard", "Shows blur, blocked and redirected views from AXIS Image Health Analytics.", "Monitoring", hasPage: true, tasks: 0, enabledByDefault: false),
        Package(SystemReportPluginId, "System report", "Downloads the system reports of devices for Axis support.", "Reporting", hasPage: false, tasks: 0, alwaysOn: true),
        Package(RestartPluginId, "Restart", "Restarts devices.", "Extensions", hasPage: false, tasks: 1, alwaysOn: true),
        Package(SampleLicensedPluginId, "Site reports", "A sample commercial plugin: monthly reports per site.", "Extensions", hasPage: false, tasks: 0),
    ];

    public Task<PluginPackageList> ListPluginPackagesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(FakePackageList());
        }
    }

    public Task<PluginPackageList> SetPluginLicenseAsync(string packageId, string license, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (!string.Equals(packageId, SampleLicensedPluginId, StringComparison.OrdinalIgnoreCase))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "This plugin needs no license."));
            }

            var info = Oadm.Sdk.Licensing.LicenseFile.Verify(license, SampleLicenseKeys.PublicKey, packageId, FakeServerId, DateOnly.FromDateTime(DateTime.Now));
            if (info.Status != Oadm.Sdk.Licensing.LicenseStatus.Valid)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, info.Reason));
            }

            _sampleLicense = license;
            return Task.FromResult(FakePackageList());
        }
    }

    public Task<PluginPackageList> RemovePluginLicenseAsync(string packageId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _sampleLicense = null;
            return Task.FromResult(FakePackageList());
        }
    }

    /// <summary>The server ID of fake mode.</summary>
    public const string FakeServerId = "7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37";

    /// <summary>A sample commercial plugin of fake mode (trial with 12 days left until a license is entered).</summary>
    public const string SampleLicensedPluginId = "example.site-reports";

    /// <summary>The vendor key pair of the sample plugin: tests sign licenses with its private key.</summary>
    public static (string PrivateKeyPem, string PublicKey) SampleLicenseKeys { get; } = Oadm.Sdk.Licensing.LicenseFile.CreateKeys();

    private string? _sampleLicense;

    private PluginPackageList FakePackageList()
    {
        var list = new PluginPackageList { ServerId = FakeServerId };
        list.Packages.AddRange(FakePackages());
        return list;
    }

    /// <summary>The license state of the sample commercial plugin, checked like the server does.</summary>
    private PluginLicenseState SampleLicenseState()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var state = new PluginLicenseState();
        state.AvailableFeatures.AddRange(["reports", "export"]);
        if (_sampleLicense is not null)
        {
            var info = Oadm.Sdk.Licensing.LicenseFile.Verify(_sampleLicense, SampleLicenseKeys.PublicKey, SampleLicensedPluginId, FakeServerId, today);
            state.Status = (PluginLicenseStatus)(int)info.Status;
            state.Reason = info.Reason;
            if (info.Data is { } data)
            {
                state.Customer = data.Customer;
                state.Issued = Oadm.Sdk.Licensing.LicenseFile.Day(data.Issued);
                state.Expires = data.Expires is { } expires ? Oadm.Sdk.Licensing.LicenseFile.Day(expires) : string.Empty;
                state.Features.AddRange(data.Features);
                foreach (var (name, value) in data.Limits)
                {
                    state.Limits[name] = value;
                }
            }

            return state;
        }

        var ends = Oadm.Sdk.Licensing.LicenseFile.Day(today.AddDays(12));
        state.Status = PluginLicenseStatus.PluginLicenseTrial;
        state.Reason = $"Trial until {ends}.";
        state.TrialEnds = ends;
        return state;
    }

    public Task<PluginPackageList> SetPluginPackageEnabledAsync(string packageId, bool enabled, CancellationToken ct)
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
            return Task.FromResult(FakePackageList());
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
            if (string.Equals(p.Id, SampleLicensedPluginId, StringComparison.OrdinalIgnoreCase))
            {
                p.License = SampleLicenseState();
            }

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
