using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>Who may call the page methods (checked by the host before InvokeAsync).</summary>
public sealed class MethodRoleTests
{
    [Fact]
    public void Every_method_is_for_operators_and_nothing_is_audited()
    {
        ICorePlugin plugin = new HardeningScanPlugin();
        foreach (var method in HardeningMethods.All)
        {
            Assert.Equal(UserRole.Operator, plugin.RequiredRole(method));
            Assert.False(plugin.IsAudited(method));
        }
    }

    [Fact]
    public async Task Unknown_methods_and_a_stopped_plugin_are_refused()
    {
        var plugin = new HardeningScanPlugin();
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.InvokeAsync(HardeningMethods.GetState, null, CancellationToken.None));
        await plugin.StartAsync(new TestCoreContext(new FakeDevices(), new FakeVapixFactory(), new MemorySettings(), null), CancellationToken.None);
        await using var _ = plugin;
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("setSsh", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(HardeningMethods.StartScan, "{not json", CancellationToken.None));
        Assert.Empty(plugin.TaskPlugins);
        Assert.Equal("clipboardCheck", plugin.IconKey);
    }
}
