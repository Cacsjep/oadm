using Oadm.Sdk.Plugins;

namespace Oadm.Client.Shell;

/// <summary>
/// The order and headers of the rail groups (<see cref="CorePluginGroup"/>), also of the Plugins page. The order is the
/// SDK's <see cref="CorePluginGroups.RailOrder"/>, shared with the server's permission matrix.
/// </summary>
public static class RailGroups
{
    /// <summary>The position of the group in the rail (<see cref="CorePluginGroups.RailOrder"/>); a value this client does not know goes last.</summary>
    public static int Order(CorePluginGroup group) => CorePluginGroups.RailOrder(group);

    /// <summary>The group's header in the rail and on the Plugins page ("Monitoring", "Vision").</summary>
    public static string Title(CorePluginGroup group) => group.ToString();
}
