using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>VAPIX Commander is an Operator page; sending and rollouts go to the audit log.</summary>
public sealed class MethodRoleTests
{
    [Fact]
    public void Every_method_is_for_operators_and_send_and_rollout_are_audited()
    {
        ICorePlugin plugin = new VapixCommanderPlugin(Samples.Directory);
        foreach (var method in new[] { CommanderMethods.ListLibrary, CommanderMethods.Save, CommanderMethods.TryRequest, CommanderMethods.Rollout, CommanderMethods.CheckCompatibility })
        {
            Assert.Equal(UserRole.Operator, plugin.RequiredRole(method));
        }

        Assert.True(plugin.IsAudited(CommanderMethods.TryRequest));
        Assert.True(plugin.IsAudited(CommanderMethods.Rollout));
        Assert.False(plugin.IsAudited(CommanderMethods.ListLibrary));
        Assert.False(plugin.IsAudited(CommanderMethods.CheckCompatibility));
    }
}
