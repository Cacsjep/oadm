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

    [Fact]
    public void TheManifestKeepsTheConstructorAndDeconstructOfSdk010()
    {
        // Plugins built against SDK 0.1.0 call these exact members: a new manifest entry must never change them.
        Type[] parameters =
        [
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(string),
            typeof(Sdk.Licensing.PluginLicenseManifest),
        ];
        var type = typeof(Sdk.Plugins.PluginManifest);
        Assert.NotNull(type.GetConstructor(parameters));
        var deconstruct = Assert.Single(type.GetMethods(), m => m.Name == "Deconstruct");
        Assert.Equal(parameters, deconstruct.GetParameters().Select(p => p.ParameterType.GetElementType()));

        var manifest = new Sdk.Plugins.PluginManifest("a", "1.0", "0.1.0") { SiteSupport = SiteSupport.Full };
        Assert.Equal(SiteSupport.Full, manifest.SiteSupport);
        Assert.NotEqual(manifest, manifest with { SiteSupport = null });
    }

    private Sdk.Plugins.PluginManifest Read(string json)
    {
        var path = Path.Combine(_folder, "plugin.json");
        File.WriteAllText(path, json);
        return PluginLoader.ReadManifest(path);
    }
}
