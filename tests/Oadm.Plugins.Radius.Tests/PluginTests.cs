using System.Text.Json;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The plugin entry point: its manifest and the device-group canary (docs/spec/device-scope.md).</summary>
public sealed class PluginTests
{
    [Fact]
    public void TheManifestDescribesAnOadmProPluginOffByDefault()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Oadm.Tests.Shared.TestRepository.Path("plugins", "Oadm.Plugins.Radius", "plugin.json")));
        var root = manifest.RootElement;

        Assert.Equal(RadiusPlugin.PluginId, root.GetProperty("id").GetString());
        Assert.Equal("802.1X server", root.GetProperty("displayName").GetString());
        Assert.True(root.GetProperty("pro").GetBoolean());
        Assert.False(root.GetProperty("enabledByDefault").GetBoolean());
        Assert.Equal("0.4.0", root.GetProperty("minSdkVersion").GetString());
        Assert.Equal("full", root.GetProperty("siteSupport").GetString());
        var port = Assert.Single(root.GetProperty("sitePorts").EnumerateArray());
        Assert.Equal("udp", port.GetProperty("protocol").GetString());
        Assert.Equal(1812, port.GetProperty("port").GetInt32());

        var plugin = new RadiusPlugin();
        Assert.Equal("802.1X server", plugin.DisplayName);
        Assert.Equal(CorePluginGroup.Security, plugin.Group);
    }

    [Fact]
    public async Task TheCanaryPasses()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        var plugin = new RadiusPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(RadiusPlugin.GetStateMethod, "server-wide state, no device data")
            .RunAsync();
    }
}
