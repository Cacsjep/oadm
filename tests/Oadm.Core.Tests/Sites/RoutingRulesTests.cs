using System.Text.RegularExpressions;

using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// The routing rules the compiler cannot check (CLAUDE.md "# Sites", Server: Routing): connection options come from one helper,
/// and every exception to the banned-API analyzer is a reviewed one.
/// </summary>
public sealed partial class RoutingRulesTests
{
    /// <summary>The files that may use banned network APIs, each with a justification in its SuppressMessage.</summary>
    private static readonly string[] AllowedBannedApiUsers =
    [
        "src/Oadm.Core/Sites/DeviceRouter.cs",
        "src/Oadm.Core/Sites/RoutedDns.cs",
        // IDeviceNetwork and IPluginNetwork on the server's own network: plugins' pings, probes, DNS and listeners.
        "src/Oadm.Core/Sites/LocalNetwork.cs",
        "src/Oadm.Core/Vapix/VapixClient.cs",
        "src/Oadm.Core/Discovery/VapixDeviceProbe.cs",
        "src/Oadm.Core/Discovery/Mdns/MdnsBrowser.cs",
        "src/Oadm.Core/Settings/ServerSettings.cs",
        "src/Oadm.Contracts/Security/OadmChannel.cs",
        "src/Oadm.Server/Auth/ServerTlsCertificate.cs",
        // The site transport: links site nodes open (the listener, the server's HTTP/2 client on a link).
        "src/Oadm.Server/Sites/NodeLink.cs",
        "src/Oadm.Server/Sites/SiteListener.cs",
        "src/Oadm.Client/DeviceWeb/DeviceWebRelay.cs",
        "plugins/Oadm.Plugins.DhcpServer/Serving/DhcpSockets.cs",
        "plugins/Oadm.Plugins.Notifications/SendHttpAction.cs",
        "plugins/Oadm.Plugins.Notifications/SendTcpAction.cs",
        "plugins/Oadm.Plugins.NtpServer/Serving/NtpUdpServer.cs",
        "plugins/Oadm.Plugins.NtpServer/Upstream/UpstreamClient.cs",
        "plugins/Oadm.Plugins.NtpServer/Upstream/UpstreamResolver.cs",
    ];

    [Fact]
    public void OnlyDeviceConnectionsBuildsConnectionOptions()
    {
        var offenders = SourceFiles()
            .Where(f => !f.EndsWith("src/Oadm.Core/Sites/DeviceConnections.cs", StringComparison.Ordinal))
            .Where(f => NewOptions().IsMatch(File.ReadAllText(TestRepository.Path(f))))
            .ToList();

        // A failure here: build the options with DeviceConnections.For(device | discovered), so they carry the route.
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryBannedApiSuppressionIsAReviewedOne()
    {
        var suppressing = SourceFiles()
            .Where(f => File.ReadAllText(TestRepository.Path(f)).Contains("RS0030", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        // A failure here: a new file opens its own sockets, asks DNS or builds an HTTP handler. Device connections go
        // through DeviceRouter; anything else needs a justification and an entry in this list.
        Assert.Equal(AllowedBannedApiUsers.Order(StringComparer.Ordinal), suppressing);
        Assert.All(suppressing, f => Assert.Contains("Justification = \"", File.ReadAllText(TestRepository.Path(f)), StringComparison.Ordinal));
    }

    [Fact]
    public void TheAnalyzerCoversTheRepositoryAndThePluginSdk()
    {
        var root = File.ReadAllText(TestRepository.Path("Directory.Build.props"));
        var sdk = File.ReadAllText(TestRepository.Path("src", "Oadm.Plugin.Sdk", "targets", "Oadm.Plugin.props"));
        var versions = File.ReadAllText(TestRepository.Path("src", "Oadm.Plugin.Sdk", "targets", "Oadm.Plugin.Versions.props"));
        var banned = File.ReadAllLines(TestRepository.Path("src", "Oadm.Plugin.Sdk", "targets", "BannedSymbols.txt"));

        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", root, StringComparison.Ordinal);
        Assert.Contains("src/Oadm.Plugin.Sdk/targets/BannedSymbols.txt", root, StringComparison.Ordinal);
        Assert.Contains("<WarningsNotAsErrors>$(WarningsNotAsErrors);RS0030</WarningsNotAsErrors>", sdk, StringComparison.Ordinal);
        // The repository and the plugin SDK use the same analyzer version.
        var version = AnalyzerVersion().Match(root).Groups[1].Value;
        Assert.Contains($"<OadmBannedApiAnalyzersVersion>{version}</OadmBannedApiAnalyzersVersion>", versions, StringComparison.Ordinal);
        foreach (var symbol in (string[])["T:System.Net.Sockets.Socket;", "T:System.Net.Sockets.TcpClient;", "T:System.Net.Sockets.UdpClient;",
            "T:System.Net.NetworkInformation.Ping;", "T:System.Net.Dns;", "T:System.Net.Http.HttpClientHandler;", "T:System.Net.Http.SocketsHttpHandler;",
            "M:System.Net.WebSockets.ClientWebSocket.ConnectAsync(System.Uri,System.Threading.CancellationToken);",
            "M:System.Net.WebSockets.ClientWebSocket.ConnectAsync(System.Uri,System.Net.Http.HttpMessageInvoker,System.Threading.CancellationToken);",
            "M:System.Net.Http.HttpClient.#ctor;", "T:System.Net.WebRequest;", "T:System.Net.HttpWebRequest;"])
        {
            Assert.Contains(banned, l => l.StartsWith(symbol, StringComparison.Ordinal));
        }
    }

    /// <summary>Every C# file of src/ and plugins/ (repository-relative, forward slashes), build output left out.</summary>
    private static List<string> SourceFiles() =>
        [.. ((string[])["src", "plugins"])
            .SelectMany(top => Directory.EnumerateFiles(TestRepository.Path(top), "*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(TestRepository.Root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))];

    [GeneratedRegex(@"new\s+VapixConnectionOptions\b|VapixConnectionOptions\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex NewOptions();

    [GeneratedRegex(@"BannedApiAnalyzers"" Version=""([^""]+)""")]
    private static partial Regex AnalyzerVersion();
}
