using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>Who may call the page methods (checked by the host before InvokeAsync).</summary>
public sealed class MethodRoleTests
{
    [Fact]
    public void Reading_is_for_operators_saving_is_admin_only_and_audited()
    {
        ICorePlugin plugin = new NtpServerPlugin();
        Assert.Equal(UserRole.Operator, plugin.RequiredRole(NtpServerMethods.GetState));
        Assert.Equal(UserRole.Admin, plugin.RequiredRole(NtpServerMethods.Save));
        Assert.True(plugin.IsAudited(NtpServerMethods.Save));
        Assert.False(plugin.IsAudited(NtpServerMethods.GetState));
    }
}
