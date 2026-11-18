using Oadm.Plugins.Provisioning.Model;

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
    public static NodeRegistry Default { get; } = new(
    [
        new SetPasswordNode(),
        new AddToOadmNode(),
        new WaitForApprovalNode(),
        new SetIpNode(),
        new DateTimeNode(),
    ]);

    public IReadOnlyList<IProvisioningNode> All { get; }

    /// <summary>The node of a kind; null for a kind this version does not know.</summary>
    public IProvisioningNode? Find(NodeKind kind) => _byKind.GetValueOrDefault(kind);
}

/// <summary>Timings and limits of the engine and the nodes; tests pass short ones with a fake clock.</summary>
public sealed record ProvisioningOptions
{
    /// <summary>Cameras whose flow runs at the same time (more are Queued). A camera waiting for approval holds none.</summary>
    public int MaxParallel { get; init; } = 8;

    /// <summary>Most camera rows of a session; leases of more cameras are left out (logged).</summary>
    public int MaxCameras { get; init; } = 10_000;

    /// <summary>How often the DHCP server's acknowledged leases are read.</summary>
    public TimeSpan LeasePoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a new camera may take to answer after its lease (it starts its web server a while after DHCP).</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The wait between two tries of a camera that does not answer yet.</summary>
    public TimeSpan AnswerRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a node asks whether its task ended.</summary>
    public TimeSpan TaskPoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait for a node's task (an AXIS OS upgrade takes a while).</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long Add to OADM waits until OADM has read the new device.</summary>
    public TimeSpan DeviceReadTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often Add to OADM looks whether OADM has read the device.</summary>
    public TimeSpan DevicePoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Changes reach the page and the database at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
