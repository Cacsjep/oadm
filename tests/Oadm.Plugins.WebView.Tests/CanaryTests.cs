using Oadm.Sdk.Testing;

namespace Oadm.Plugins.WebView.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): the plugin has no page method that reaches a device.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice());
        var outside = ctx.Add(new FakeDevice());
        var plugin = new WebViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices("open", "no page methods: the tabs go through the host's DeviceWebService and the user's view")
            .RunAsync();
    }
}
