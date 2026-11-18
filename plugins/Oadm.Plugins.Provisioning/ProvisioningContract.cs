using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Provisioning;

/// <summary>Ids and names shared by the server part, the page and the tests.</summary>
public static class ProvisioningInfo
{
    public const string PluginId = "oadm.provisioning";
    public const string DisplayName = "Provisioning";
    public const string IconKey = "workflow";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class ProvisioningMethods
{
    /// <summary>() -> <see cref="StatusReply"/>.</summary>
    public const string GetStatus = "getStatus";

    /// <summary>() -> <see cref="ProfilesReply"/>.</summary>
    public const string ListProfiles = "listProfiles";

    /// <summary><see cref="IdRequest"/> -> <see cref="ProfileReply"/>.</summary>
    public const string GetProfile = "getProfile";

    /// <summary><see cref="ProfileRequest"/> -> <see cref="ProfileCheckResult"/>: the editor's check, the same as on save and Start.</summary>
    public const string CheckProfile = "checkProfile";

    /// <summary>() -> the node kinds (<see cref="NodeKindInfo"/>) with their default settings, for the editor's list.</summary>
    public const string NodeKinds = "nodeKinds";

    /// <summary>() -> the credential list without passwords (<see cref="Sdk.Devices.DeviceCredentialName"/>).</summary>
    public const string Credentials = "credentials";

    /// <summary><see cref="SaveProfileRequest"/> -> <see cref="ProfileReply"/>.</summary>
    public const string SaveProfile = "saveProfile";

    /// <summary><see cref="IdRequest"/> -> <see cref="ProfilesReply"/>.</summary>
    public const string DeleteProfile = "deleteProfile";

    /// <summary><see cref="IdRequest"/> -> <see cref="StatusReply"/>: the profile must have no errors.</summary>
    public const string ActivateProfile = "activateProfile";

    /// <summary>() -> <see cref="StatusReply"/> (with the warnings, e.g. a pool that overlaps the DHCP range).</summary>
    public const string Start = "start";

    /// <summary>() -> <see cref="StatusReply"/>.</summary>
    public const string Stop = "stop";

    /// <summary><see cref="CamerasRequest"/> -> <see cref="CamerasPage"/>: rows by MAC address, paged.</summary>
    public const string ListCameras = "listCameras";

    /// <summary><see cref="MacRequest"/> -> <see cref="CameraRow"/> (with its result per node).</summary>
    public const string GetCamera = "getCamera";

    /// <summary><see cref="MacsRequest"/> -> <see cref="CountReply"/>.</summary>
    public const string RunFailedAgain = "runFailedAgain";

    /// <summary><see cref="MacsRequest"/> -> <see cref="CountReply"/>: Continue on the rows that wait for approval.</summary>
    public const string Continue = "continue";

    /// <summary><see cref="MacsRequest"/> -> <see cref="CountReply"/>: rows removed (the cameras stay in OADM).</summary>
    public const string RemoveCameras = "removeCameras";

    /// <summary><see cref="SecretRequest"/> -> <see cref="SecretReply"/>: a typed password or passphrase kept for a node (its id only comes back).</summary>
    public const string StoreSecret = "storeSecret";

    /// <summary><see cref="FileRequest"/> -> <see cref="Engine.StoredFile"/>: a file the caller uploaded, kept for a node.</summary>
    public const string StoreFile = "storeFile";

    /// <summary>Event: <see cref="StatusReply"/> after Start, Stop and changes of the profiles.</summary>
    public const string StatusTopic = "status";

    /// <summary>Event: <see cref="CamerasEvent"/>, changed and removed rows (at most every 500 ms).</summary>
    public const string CamerasTopic = ProvisioningEngine.CamerasTopic;

    /// <summary>The methods that only read.</summary>
    public static bool IsRead(string method) =>
        method is GetStatus or ListProfiles or GetProfile or CheckProfile or NodeKinds or Credentials or ListCameras or GetCamera;
}

public sealed record IdRequest(Guid Id);

public sealed record ProfileRequest(Profile Profile);

/// <summary>A profile to store; <see cref="Password"/>: a typed root password for Set password (added to the credential list, never stored here).</summary>
public sealed record SaveProfileRequest(Profile Profile, string? Password = null)
{
    public override string ToString() => $"SaveProfileRequest {{ Profile = {Profile.Name}, Password = {(Password is null ? "none" : "***")} }}";
}

/// <summary>A typed password or passphrase for a node; never logged.</summary>
public sealed record SecretRequest(string? Value)
{
    public override string ToString() => "SecretRequest { Value = *** }";
}

/// <summary>The id a node names its secret by (<c>secretId</c>).</summary>
public sealed record SecretReply(Guid Id);

/// <summary>A file of the caller's uploads to keep for a node.</summary>
public sealed record FileRequest(string UploadId);

public sealed record CamerasRequest(string? After = null, int Limit = 200);

public sealed record MacRequest(string Mac);

public sealed record MacsRequest(IReadOnlyList<string> Macs);

public sealed record CountReply(int Count);

/// <summary>One profile in the list.</summary>
public sealed record ProfileSummary(Guid Id, string Name, int Nodes, bool IsActive, bool IsValid);

public sealed record ProfilesReply(IReadOnlyList<ProfileSummary> Profiles, Guid? ActiveId);

/// <summary>A profile with its check.</summary>
public sealed record ProfileReply(Profile Profile, ProfileCheckResult Check);

/// <summary>A kind of node for the editor's list.</summary>
public sealed record NodeKindInfo(NodeKind Kind, string Name, bool IsFixed, bool AlwaysStops, JsonElement DefaultSettings);

/// <summary>The page's state: started or not, the active profile, the DHCP server, the counts and the warnings.</summary>
public sealed record StatusReply
{
    public bool Started { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public string? StartedBy { get; init; }

    public Guid? ActiveProfileId { get; init; }

    public string? ActiveProfileName { get; init; }

    /// <summary>The profile the session runs (the active one as it was at Start).</summary>
    public string? SessionProfileName { get; init; }

    /// <summary>The DHCP server; null when its plugin does not run.</summary>
    public DhcpServerSummary? Dhcp { get; init; }

    public CameraCounts Counts { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Warnings the page shows (Start stays possible): a Set IP pool that overlaps the DHCP range.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The camera rows' version.</summary>
    public long Version { get; init; }
}
