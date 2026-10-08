using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>Who may call the page methods (checked by the host before InvokeAsync).</summary>
public sealed class MethodRoleTests
{
    [Fact]
    public void Only_reading_the_state_is_for_operators()
    {
        ICorePlugin plugin = new PkiPlugin();
        Assert.Equal(UserRole.Operator, plugin.RequiredRole(PkiMethods.GetState));
        foreach (var method in new[]
                 {
                     PkiMethods.Generate, PkiMethods.Import, PkiMethods.PreviewReplace, PkiMethods.ExportPublic, PkiMethods.ExportPrevious,
                     PkiMethods.RemovePrevious, PkiMethods.Backup, PkiMethods.InstallServerTrust, PkiMethods.SaveSettings, PkiMethods.ImportRadiusCa,
                 })
        {
            Assert.Equal(UserRole.Admin, plugin.RequiredRole(method));
            Assert.True(plugin.IsAudited(method));
        }

        // The Security tasks are task plugins: no role beyond a login.
        Assert.NotEmpty(plugin.TaskPlugins);
    }
}
