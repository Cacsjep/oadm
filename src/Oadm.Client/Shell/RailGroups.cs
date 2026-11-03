using Oadm.Sdk.Plugins;

namespace Oadm.Client.Shell;

/// <summary>
/// The order and headers of the rail groups (<see cref="CorePluginGroup"/>), also of the Plugins page. The enum's values
/// are never renumbered, so a group added later gets its place here: Vision right after Monitoring.
/// </summary>
public static class RailGroups
{
    private static readonly CorePluginGroup[] Ordered =
    [
        CorePluginGroup.Servers,
        CorePluginGroup.Automation,
        CorePluginGroup.Security,
        CorePluginGroup.Monitoring,
        CorePluginGroup.Vision,
        CorePluginGroup.Reporting,
        CorePluginGroup.Maintenance,
        CorePluginGroup.Integrations,
        CorePluginGroup.Utilities,
        CorePluginGroup.Extensions,
    ];

    /// <summary>The position of the group in the rail; a value this client does not know goes last.</summary>
    public static int Order(CorePluginGroup group)
    {
        var index = Array.IndexOf(Ordered, group);
        return index < 0 ? Ordered.Length : index;
    }

    /// <summary>The group's header in the rail and on the Plugins page ("Monitoring", "Vision").</summary>
    public static string Title(CorePluginGroup group) => group.ToString();
}
