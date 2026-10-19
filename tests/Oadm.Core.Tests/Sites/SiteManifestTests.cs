using Oadm.Core.Plugins;
using Oadm.Sdk.Sites;

namespace Oadm.Core.Tests.Sites;

public sealed class SiteManifestTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-site-manifest-" + Guid.NewGuid().ToString("N"));

    public SiteManifestTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void SiteSupportAndPortsAreRead()
    {
        var manifest = Read("""
            { "id": "acme.ntp", "version": "1.0.0", "minSdkVersion": "0.1.0",
              "siteSupport": "full", "sitePorts": [ { "protocol": "udp", "port": 123, "reason": "NTP server" } ] }
            """);

        Assert.Equal(SiteSupport.Full, manifest.SiteSupport);
        Assert.Equal([new SitePort(SiteProtocol.Udp, 123, "NTP server")], manifest.SitePorts);
    }

    [Fact]
    public void AManifestWithoutSiteSupportDeclaresNothing()
    {
        var manifest = Read("""{ "id": "acme.old", "version": "1.0.0", "minSdkVersion": "0.1.0" }""");

        Assert.Null(manifest.SiteSupport);
        Assert.Null(manifest.SitePorts);
    }

    private Sdk.Plugins.PluginManifest Read(string json)
    {
        var path = Path.Combine(_folder, "plugin.json");
        File.WriteAllText(path, json);
        return PluginLoader.ReadManifest(path);
    }
}
