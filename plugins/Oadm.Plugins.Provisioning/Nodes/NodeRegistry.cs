using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.VapixCommander;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>Every kind of node, by kind. A new node is one class here (README "Adding a node").</summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<NodeKind, IProvisioningNode> _byKind;

    public NodeRegistry(IEnumerable<IProvisioningNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        All = [.. nodes];
        _byKind = All.ToDictionary(n => n.Kind);
    }

    /// <summary>The nodes of this version.</summary>
    public static NodeRegistry Default { get; } = Create(CommandLibrary.Empty);

    /// <summary>Every node of this version, VAPIX commands with <paramref name="commands"/> (the plugin's copy of the Commander library).</summary>
    public static NodeRegistry Create(CommandLibrary commands) => new(
    [
        new SetPasswordNode(),
        new UpgradeOnlineNode(),
        new UpgradeFilesNode(),
        new NetworkNode(),
        new DateTimeNode(),
        new ConfigurationNode(),
        new UsersNode(),
        new CertificateNode(),
        new Dot1xNode(),
        new SdCardNode(),
        new AutofocusNode(),
        new VapixCommandsNode(commands),
    ]);

    public IReadOnlyList<IProvisioningNode> All { get; }

    /// <summary>The node of a kind; null for a kind this version does not know.</summary>
    public IProvisioningNode? Find(NodeKind kind) => _byKind.GetValueOrDefault(kind);
}
