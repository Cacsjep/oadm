using System.Text.Json;

namespace Oadm.Plugins.Provisioning.Model;

/// <summary>The kinds of nodes a profile is made of (docs/spec/provisioning.md "Nodes"). Stored by name, never renamed.</summary>
public enum NodeKind
{
    /// <summary>Set password (fixed, first): the first root password from the credential list.</summary>
    SetPassword = 0,

    /// <summary>Add to OADM (fixed, second): adds the camera with that login and the tags.</summary>
    AddToOadm = 1,

    /// <summary>Wait for approval: the camera waits until a person presses Continue on its row.</summary>
    WaitForApproval = 2,

    /// <summary>Set IP: the next free address of the node's own pool, written as a fixed address.</summary>
    SetIp = 3,

    /// <summary>Date and time: NTP servers and time zone, through the Date and time task.</summary>
    DateTime = 4,
}

/// <summary>One node of a profile: its kind, its settings (the kind's own record as JSON) and whether the flow goes on after an error.</summary>
public sealed record FlowNode
{
    /// <summary>The node's id within its profile ("n1"): letters, digits and hyphens, at most 32 characters.</summary>
    public string Id { get; init; } = string.Empty;

    public NodeKind Kind { get; init; }

    /// <summary>On error the flow goes on with the next node (default). Ignored for Set password, which always stops.</summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>The settings record of the kind (<see cref="Nodes.IProvisioningNode"/>); null: the kind's defaults.</summary>
    public JsonElement? Settings { get; init; }
}

/// <summary>A profile: a name and a straight chain of nodes, Set password and Add to OADM first.</summary>
public sealed record Profile
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<FlowNode> Nodes { get; init; } = [];
}

/// <summary>The stored profiles and which one is active (plugin setting <c>profiles</c>).</summary>
public sealed record ProfileBook
{
    public IReadOnlyList<Profile> Profiles { get; init; } = [];

    /// <summary>The active profile; null: none.</summary>
    public Guid? ActiveId { get; init; }
}

/// <summary>Settings of Set password: the credential list entry of root whose password is set (never the password itself).</summary>
public sealed record SetPasswordSettings(Guid? CredentialId = null);

/// <summary>Settings of Add to OADM: the tags the camera gets.</summary>
public sealed record AddToOadmSettings(IReadOnlyList<string>? Tags = null);

/// <summary>Settings of Wait for approval: none.</summary>
public sealed record WaitForApprovalSettings;

/// <summary>Settings of Set IP: the pool (first and last address), subnet mask, router and DNS servers.</summary>
public sealed record SetIpSettings(
    string? First = null,
    string? Last = null,
    string? SubnetMask = null,
    string? Gateway = null,
    IReadOnlyList<string>? DnsServers = null);

/// <summary>Settings of Date and time: the time zone and the NTP servers (none: the servers DHCP gives).</summary>
public sealed record DateTimeSettings(
    string? TimeZone = null,
    IReadOnlyList<string>? NtpServers = null,
    bool Nts = false);
