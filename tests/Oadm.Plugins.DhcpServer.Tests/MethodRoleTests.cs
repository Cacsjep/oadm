using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>Who may call the page methods (checked by the host before InvokeAsync).</summary>
public sealed class MethodRoleTests
{
    [Fact]
    public void Reading_is_for_operators_every_change_is_admin_only_and_audited()
    {
        ICorePlugin plugin = new DhcpServerPlugin();
        Assert.Equal(UserRole.Operator, plugin.RequiredRole(DhcpServerMethods.GetState));
        Assert.False(plugin.IsAudited(DhcpServerMethods.GetState));
        foreach (var method in new[] { DhcpServerMethods.Save, DhcpServerMethods.SaveStatic, DhcpServerMethods.DeleteStatic, DhcpServerMethods.MakeStatic, DhcpServerMethods.Release })
        {
            Assert.Equal(UserRole.Admin, plugin.RequiredRole(method));
            Assert.True(plugin.IsAudited(method));
        }
    }
}
