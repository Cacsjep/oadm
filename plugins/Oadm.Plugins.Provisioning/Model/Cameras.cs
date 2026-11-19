namespace Oadm.Plugins.Provisioning.Model;

/// <summary>Where a camera's flow stands.</summary>
public enum CameraState
{
    /// <summary>Waiting for one of the places (at most 8 cameras at once).</summary>
    Queued = 0,

    /// <summary>A node runs (or waits for approval).</summary>
    Running = 1,

    Done = 2,

    DoneWithWarnings = 3,

    Failed = 4,

    /// <summary>The camera has a password already (or is no Axis camera): never changed.</summary>
    NotNew = 5,
}

/// <summary>How a node ended for one camera.</summary>
public enum NodeState
{
    Success = 0,

    Warning = 1,

    Error = 2,

    /// <summary>Does not apply to this camera (no SD card, no remote focus): counts as a success.</summary>
    NotNeeded = 3,
}

/// <summary>The end of one node for one camera, with its plain message ("Password set.").</summary>
public sealed record NodeResult(NodeState State, string Message)
{
    public static NodeResult Success(string message) => new(NodeState.Success, message);

    public static NodeResult Warning(string message) => new(NodeState.Warning, message);

    public static NodeResult Error(string message) => new(NodeState.Error, message);

    public static NodeResult NotNeeded(string message) => new(NodeState.NotNeeded, "Not needed: " + message);
}

/// <summary>
/// One camera of the session (one row of the page), stored per MAC address. <see cref="Results"/> follows the nodes of the
/// session's profile; null where the node has not run.
/// </summary>
public sealed record CameraRow
{
    /// <summary>"AC:CC:8E:01:02:03".</summary>
    public string Mac { get; init; } = string.Empty;

    /// <summary>The serial number OADM uses (the MAC address without separators).</summary>
    public string Serial { get; init; } = string.Empty;

    /// <summary>The address the camera answers at now (the DHCP address, the fixed one after Set IP).</summary>
    public string Address { get; init; } = string.Empty;

    public string? Model { get; init; }

    /// <summary>The OADM device once added.</summary>
    public Guid? DeviceId { get; init; }

    public CameraState State { get; init; }

    /// <summary>The node that runs or ran last (index in the profile); -1: none yet.</summary>
    public int NodeIndex { get; init; } = -1;

    /// <summary>The name of that node ("Set IP").</summary>
    public string? NodeName { get; init; }

    /// <summary>The camera waits for Continue (Wait for approval).</summary>
    public bool WaitingForApproval { get; init; }

    public IReadOnlyList<NodeResult?> Results { get; init; } = [];

    /// <summary>The row's own text ("Not new, left as it is.").</summary>
    public string? Message { get; init; }

    /// <summary>When the camera's lease was seen first.</summary>
    public DateTimeOffset Seen { get; init; }

    public DateTimeOffset Updated { get; init; }

    /// <summary>Rises with every change of the row; an event with an older one is stale.</summary>
    public long Version { get; init; }
}

/// <summary>The provisioning session while it is started (plugin setting <c>session</c>): survives a restart until Stop.</summary>
public sealed record Session
{
    public DateTimeOffset StartedAt { get; init; }

    public string StartedBy { get; init; } = string.Empty;

    /// <summary>The active profile as it was at Start: changes to it apply at the next Start.</summary>
    public Profile Profile { get; init; } = new();

    /// <summary>The Set IP addresses reserved in this session, by MAC address.</summary>
    public IReadOnlyDictionary<string, string> Reserved { get; init; } = new Dictionary<string, string>();

    /// <summary>The last DHCP acknowledgement read.</summary>
    public long AckAfter { get; init; }
}
