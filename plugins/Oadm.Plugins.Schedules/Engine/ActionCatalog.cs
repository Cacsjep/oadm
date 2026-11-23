using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.VapixCommander;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>A right a schedule's action needs: the permission key and its name in the permission matrix.</summary>
/// <param name="Permission">"task:oadm.firmware:upgrade", "oadm.vapix-commander:rollout".</param>
/// <param name="Name">"Upgrade AXIS OS".</param>
public sealed record ActionRight(string Permission, string Name);

/// <summary>
/// The actions of a schedule (docs/spec/schedules.md "Actions"): Provisioning's nodes of the kinds of <see cref="ActionKinds.All"/>
/// only, with their step names, starting estimates and the right each needs. Never Network, Users, SD card, Date and time or
/// Autofocus: a kind outside the list has no node here and is refused.
/// </summary>
public sealed class ActionCatalog
{
    private readonly Dictionary<NodeKind, IProvisioningNode> _byKind;

    public ActionCatalog(CommandLibrary commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        IProvisioningNode[] nodes =
        [
            new UpgradeOnlineNode(),
            new UpgradeFilesNode(),
            new ConfigurationNode(),
            new RestartNode(),
            new CertificateNode(),
            new Dot1xNode(),
            new VapixCommandsNode(commands),
        ];
        All = [.. ActionKinds.All.Select(k => nodes.Single(n => n.Kind == k))];
        _byKind = All.ToDictionary(n => n.Kind);
    }

    /// <summary>The actions without the VAPIX Commander library (tests, a host without the plugin's folder).</summary>
    public static ActionCatalog Default { get; } = new(CommandLibrary.Empty);

    /// <summary>The actions in the order of <see cref="ActionKinds.All"/>.</summary>
    public IReadOnlyList<IProvisioningNode> All { get; }

    /// <summary>The node of an action kind; null for a kind a schedule cannot run.</summary>
    public IProvisioningNode? Find(NodeKind kind) => _byKind.GetValueOrDefault(kind);

    /// <summary>The VAPIX commands node (its library).</summary>
    public VapixCommandsNode CommandsNode => (VapixCommandsNode)_byKind[NodeKind.VapixCommands];

    /// <summary>"This action cannot run in a schedule."</summary>
    public const string NotAnAction = "This action cannot run in a schedule.";

    /// <summary>The starting estimate per device of an action (the table of the spec).</summary>
    public static TimeSpan StartingEstimate(NodeKind kind) => kind switch
    {
        NodeKind.UpgradeOnline or NodeKind.UpgradeFiles => TimeSpan.FromMinutes(10),
        NodeKind.Restart => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromMinutes(1),
    };

    public static readonly ActionRight Upgrade = new(PermissionKeys.Task(FirmwareTask.PluginId, "upgrade"), "Upgrade AXIS OS");
    public static readonly ActionRight Configure = new(PermissionKeys.Task(DeviceConfig.ConfigureIds.Configure, DeviceConfig.ConfigurePermissions.Configure), "Configure devices");
    public static readonly ActionRight Restart = new(PermissionKeys.Task(RestartNode.RestartTaskId), "Restart");
    public static readonly ActionRight Https = new(PermissionKeys.Task(CertificateNode.HttpsTaskId), "Enable HTTPS");
    public static readonly ActionRight Est = new(PermissionKeys.Task(CertificateNode.EstTaskId, "enroll"), "Set up certificate enrollment");
    public static readonly ActionRight Dot1x = new(PermissionKeys.Task(Dot1xNode.Dot1xTaskId), "Enable IEEE 802.1X");
    public static readonly ActionRight Commands = new(PermissionKeys.Of("oadm.vapix-commander", "rollout"), "Run VAPIX commands on many devices");

    /// <summary>The right of the action's task; null for a kind a schedule cannot run (refused, fail closed).</summary>
    public static ActionRight? RightOf(FlowNode action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            NodeKind.UpgradeOnline or NodeKind.UpgradeFiles => Upgrade,
            NodeKind.Configuration => Configure,
            NodeKind.Restart => Restart,
            NodeKind.Certificate => NodeSettings.Read<CertificateSettings>(action.Settings)?.Mode == CertificateMode.Est ? Est : Https,
            NodeKind.Dot1x => Dot1x,
            NodeKind.VapixCommands => Commands,
            _ => null,
        };
    }

    /// <summary>The first right of <paramref name="actions"/> (and of the plugin's "manage") <paramref name="caller"/> lacks; null when it has all.</summary>
    public static ActionRight? Missing(ICallContext caller, IEnumerable<FlowNode> actions)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(actions);
        if (!caller.Has(PermissionKeys.Of(SchedulesInfo.PluginId, PermissionKeys.Manage)))
        {
            return Manage;
        }

        foreach (var action in actions)
        {
            if (RightOf(action) is not { } right)
            {
                return new ActionRight(string.Empty, action.Kind.ToString());
            }

            if (!caller.Has(right.Permission))
            {
                return right;
            }
        }

        return null;
    }

    /// <summary>The plugin's own right to change and run schedules.</summary>
    public static readonly ActionRight Manage = new(PermissionKeys.Of(SchedulesInfo.PluginId, PermissionKeys.Manage), "Change and run schedules");
}
