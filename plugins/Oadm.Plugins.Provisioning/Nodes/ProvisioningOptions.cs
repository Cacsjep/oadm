namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>Timings and limits of the engine and the nodes; tests pass short ones with a fake clock.</summary>
public sealed record ProvisioningOptions
{
    /// <summary>Provisioning tasks that run at the same time (the task plugin's <c>MaxParallelDevices</c>; more wait as Queued).</summary>
    public int MaxParallel { get; init; } = 8;

    /// <summary>Cameras of DHCP leases that are checked at the same time (whether they are new, before they are added).</summary>
    public int MaxLooks { get; init; } = 8;

    /// <summary>Most cameras of DHCP leases waiting to be checked; leases of more are left out (logged).</summary>
    public int MaxWaitingLooks { get; init; } = 10_000;

    /// <summary>How often the DHCP server's acknowledged leases are read.</summary>
    public TimeSpan LeasePoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a new camera may take to answer (it starts its web server a while after DHCP).</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The wait between two tries of a camera that does not answer yet.</summary>
    public TimeSpan AnswerRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a node asks whether its task ended.</summary>
    public TimeSpan TaskPoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait for a node's task (an AXIS OS upgrade takes a while).</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long Set password waits until OADM has read the camera with its new password.</summary>
    public TimeSpan DeviceReadTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often Set password looks whether OADM has read the camera.</summary>
    public TimeSpan DevicePoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait for the camera's autofocus.</summary>
    public TimeSpan AutofocusTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
