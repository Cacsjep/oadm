using System.Globalization;

namespace Oadm.Plugins.Provisioning.Model;

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

/// <summary>The camera a provisioning task works on: its OADM device.</summary>
/// <param name="Serial">The serial number ("ACCC8E010203").</param>
/// <param name="Address">The address OADM reaches it at when the task started.</param>
/// <param name="Model">The product number, when known.</param>
/// <param name="DeviceId">The OADM device.</param>
public sealed record CameraInfo(string Serial, string Address, string? Model, Guid? DeviceId)
{
    /// <summary>The MAC address of the serial number, "AC:CC:8E:01:02:03" (the serial when it is not 12 hex digits).</summary>
    public string Mac => Serial.Length == 12 && ulong.TryParse(Serial, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        ? string.Join(':', Enumerable.Range(0, 6).Select(i => Serial.Substring(i * 2, 2).ToUpperInvariant()))
        : Serial;
}

/// <summary>Whether provisioning is on (plugin setting <c>state</c>): survives a restart.</summary>
public sealed record ProvisioningState
{
    public bool On { get; init; }

    /// <summary>The administrator who turned it on.</summary>
    public string? OnBy { get; init; }

    public DateTimeOffset? OnAt { get; init; }

    /// <summary>The last DHCP acknowledgement read.</summary>
    public long AckAfter { get; init; }
}

/// <summary>
/// The payload of one provisioning task (memory only, never stored): the profile as it was when the task started, the nodes to
/// run (null: all, in order; Run again: the failed ones and those that never ran) and whether the camera is checked first.
/// </summary>
public sealed record RunPayload
{
    public Profile Profile { get; init; } = new();

    public IReadOnlyList<int>? Nodes { get; init; }

    /// <summary>Check first that the camera has no password yet (false for Run again after the check passed).</summary>
    public bool Look { get; init; } = true;

    /// <summary>
    /// Run again: the nodes and the check of each camera by device id (one run, one audit entry, for every camera of the same
    /// profile); null: <see cref="Nodes"/> and <see cref="Look"/> for every camera.
    /// </summary>
    public IReadOnlyDictionary<Guid, DevicePlan>? Plans { get; init; }

    /// <summary>The payload of one camera: its own plan when it has one.</summary>
    public RunPayload For(Guid deviceId) =>
        Plans is not null && Plans.TryGetValue(deviceId, out var plan) ? this with { Nodes = plan.Nodes, Look = plan.Look, Plans = null } : this;
}

/// <summary>What Run again runs on one camera (<see cref="RunPayload.Plans"/>).</summary>
public sealed record DevicePlan
{
    /// <summary>The nodes to run; null: all, in order.</summary>
    public IReadOnlyList<int>? Nodes { get; init; }

    /// <summary>Check first that the camera has no password yet.</summary>
    public bool Look { get; init; } = true;
}

/// <summary>
/// The last provisioning run of one device (records collection <c>runs</c>, key the device id): its task, the profile it ran
/// (by the key of its snapshot) and each node's result, for Run again. Never a secret.
/// </summary>
public sealed record RunRecord
{
    public Guid TaskId { get; init; }

    /// <summary>The key of the profile's snapshot (records collection <c>snapshots</c>).</summary>
    public string Snapshot { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    /// <summary>The camera passed the check that it has no password yet.</summary>
    public bool Looked { get; init; }

    /// <summary>One per node of the profile; null where it has not run.</summary>
    public IReadOnlyList<NodeResult?> Results { get; init; } = [];

    public DateTimeOffset Started { get; init; }

    /// <summary>
    /// The run did not end well: the camera did not pass the check yet, or a node is red or never ran (also while its task
    /// waits or runs). Run again can start it; an automatic start keeps it unless the camera was reset since.
    /// </summary>
    public bool CanRunAgain() => !Looked || Failed().Count > 0;

    /// <summary>The nodes Run again runs: the red ones and those that never ran, in order.</summary>
    public IReadOnlyList<int> Failed()
    {
        var nodes = new List<int>();
        for (var i = 0; i < Results.Count; i++)
        {
            if (Results[i] is null or { State: NodeState.Error })
            {
                nodes.Add(i);
            }
        }

        return nodes;
    }
}
