using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Feed;

namespace Oadm.SiteNode;

/// <summary>How a node runs: the installed service takes the defaults; tests set their own folder and fakes.</summary>
public sealed class NodeOptions
{
    /// <summary>Environment variable with the data folder (like <c>--data-dir</c>).</summary>
    public const string DataDirVariable = "OADM_SITE_NODE_DATA_DIR";

    public string DataDirectory { get; set; } = DefaultDataDirectory();

    public string Version { get; set; } = NodeStatusFeed.ProgramVersion;

    /// <summary>The default site name the server suggests.</summary>
    public string HostName { get; set; } = Environment.MachineName;

    /// <summary>The local admin channel; null = none (tests that drive the node only through gRPC).</summary>
    public AdminEndpoint? AdminEndpoint { get; set; }

    /// <summary>Write the service log to the console and logs/ (off in tests).</summary>
    public bool UseSerilog { get; set; } = true;

    public DialLimits Limits { get; set; } = DialLimits.Default;

    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Tests only: loopback counts as a camera network (the fake cards put one there). Never set by the program.</summary>
    public bool AllowLoopbackDevices { get; set; }

    /// <summary>Fakes for tests; null = the OS.</summary>
    public INetworkCards? NetworkCards { get; set; }

    public IDeviceConnector? Connector { get; set; }

    public INodeDns? Dns { get; set; }

    public IPinger? Pinger { get; set; }

    /// <summary>The mDNS browse of Browse; null = <see cref="Discovery.AxisBrowser"/> on the reached cards.</summary>
    public Discovery.IAxisBrowser? Browser { get; set; }

    /// <summary>Longest wait between two connection attempts to the server.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A connection attempt to the server (TCP connect) ends after this time; the TLS handshake has its own.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long Reset waits for the bound server to take the notice before it resets anyway (<see cref="Feed.ResetNotice"/>).</summary>
    public TimeSpan ResetWaitLimit { get; set; } = Feed.ResetNotice.DefaultWaitLimit;

    /// <summary>The installed node's data folder: %ProgramData%\OADM Site Node, /var/lib/oadm-site-node.</summary>
    public static string DefaultDataDirectory()
    {
        if (Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } fromEnvironment)
            return Path.GetFullPath(fromEnvironment);
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OADM Site Node")
            : "/var/lib/oadm-site-node";
    }

    /// <summary>The admin endpoint of a data folder: the fixed one for the installed folder, else one of its own.</summary>
    public static AdminEndpoint EndpointFor(string dataDirectory) =>
        string.Equals(Path.GetFullPath(dataDirectory), Path.GetFullPath(DefaultInstalledDirectory()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? AdminEndpoint.Default
            : AdminEndpoint.ForDataDirectory(dataDirectory);

    private static string DefaultInstalledDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OADM Site Node")
        : "/var/lib/oadm-site-node";
}
