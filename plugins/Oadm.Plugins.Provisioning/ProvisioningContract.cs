using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Provisioning;

/// <summary>Ids and names shared by the server part, the client part and the tests.</summary>
public static class ProvisioningInfo
{
    public const string PluginId = "oadm.provisioning";
    public const string DisplayName = "Provisioning";
    public const string IconKey = "workflow";

    /// <summary>The task "Provision with &lt;profile&gt;" (one per camera; hidden from the menus, only Provisioning starts it).</summary>
    public const string TaskPluginId = "oadm.provisioning.run";
}

/// <summary>Backend methods (<c>InvokeAsync</c>) of the client part and live event topics.</summary>
public static class ProvisioningMethods
{
    /// <summary>() -> <see cref="StateReply"/>: whether provisioning is on and a new camera gets its task. Every signed-in user (the Add devices page).</summary>
    public const string State = "state";

    /// <summary>() -> <see cref="StatusReply"/>: on or off, who turned it on, the active profile, why On is refused, the warnings.</summary>
    public const string GetStatus = "getStatus";

    /// <summary>() -> <see cref="ProfilesReply"/>.</summary>
    public const string ListProfiles = "listProfiles";

    /// <summary><see cref="IdRequest"/> -> <see cref="ProfileReply"/>.</summary>
    public const string GetProfile = "getProfile";

    /// <summary><see cref="ProfileRequest"/> -> <see cref="ProfileCheckResult"/>: the editor's check, the same as on save and activate.</summary>
    public const string CheckProfile = "checkProfile";

    /// <summary>() -> the node kinds (<see cref="NodeKindInfo"/>) with their default settings, for the editor's list.</summary>
    public const string NodeKinds = "nodeKinds";

    /// <summary>() -> the credential list without passwords (<see cref="Sdk.Devices.DeviceCredentialName"/>).</summary>
    public const string Credentials = "credentials";

    /// <summary><see cref="SaveProfileRequest"/> -> <see cref="ProfileReply"/>.</summary>
    public const string SaveProfile = "saveProfile";

    /// <summary><see cref="IdRequest"/> -> <see cref="ProfilesReply"/>. Refused for the active profile while provisioning is on.</summary>
    public const string DeleteProfile = "deleteProfile";

    /// <summary><see cref="IdRequest"/> -> <see cref="StatusReply"/>: the profile must have no errors.</summary>
    public const string ActivateProfile = "activateProfile";

    /// <summary>() -> <see cref="StatusReply"/>: needs an active profile without errors.</summary>
    public const string TurnOn = "turnOn";

    /// <summary>() -> <see cref="StatusReply"/>: no new task starts; running ones run to their end.</summary>
    public const string TurnOff = "turnOff";

    /// <summary><see cref="DeviceIdsRequest"/> -> <see cref="Engine.RunAgainReply"/>: the failed provisioning of these cameras again, only the failed steps.</summary>
    public const string RunAgain = "runAgain";

    /// <summary><see cref="DeviceIdRequest"/> -> <see cref="RunReply"/>: the last provisioning run of one camera; NOT_FOUND without one.</summary>
    public const string GetRun = "getRun";

    /// <summary><see cref="SecretRequest"/> -> <see cref="SecretReply"/>: a typed password or passphrase kept for a node (its id only comes back).</summary>
    public const string StoreSecret = "storeSecret";

    /// <summary><see cref="FileRequest"/> -> <see cref="Engine.StoredFile"/>: a file the caller uploaded, kept for a node.</summary>
    public const string StoreFile = "storeFile";

    /// <summary>() -> the VAPIX Commander commands a VAPIX commands node can run (<see cref="ProfileCommand"/>): no dangerous ones, none with a password or a file.</summary>
    public const string VapixCommands = "vapixCommands";

    /// <summary>Event: <see cref="StatusReply"/> after On, Off and changes of the profiles (to users with the "show" right).</summary>
    public const string StatusTopic = "status";

    /// <summary>The methods that only read.</summary>
    public static bool IsRead(string method) =>
        method is State or GetStatus or ListProfiles or GetProfile or CheckProfile or NodeKinds or Credentials or VapixCommands or GetRun;
}

/// <summary>A command of the VAPIX Commander library for a VAPIX commands node: its id, name, category and fields.</summary>
public sealed record ProfileCommand(string Id, string Name, string Category, string? Description, IReadOnlyList<VapixCommander.CommandField> Fields);

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

/// <summary>Cameras by their OADM device ids.</summary>
public sealed record DeviceIdsRequest(IReadOnlyList<Guid> DeviceIds);

/// <summary>One camera by its OADM device id.</summary>
public sealed record DeviceIdRequest(Guid DeviceId);

/// <summary>
/// Whether provisioning is on, and whether a factory-new camera gets its task now (<paramref name="Ready"/>: on and the active
/// profile has no errors; the Add devices page shows Ready for provisioning only then).
/// </summary>
public sealed record StateReply(bool On, bool Ready);

/// <summary>
/// The last provisioning run of a camera: the profile, whether the camera passed the check that it is new, each node's name
/// and result (null where it did not run) and whether Run again would start.
/// </summary>
public sealed record RunReply(Guid DeviceId, Guid TaskId, string ProfileName, DateTimeOffset Started, bool Checked, IReadOnlyList<RunNodeReply> Nodes, bool CanRunAgain);

/// <summary>One node of a run: its name and how it ended (null: not run).</summary>
public sealed record RunNodeReply(string Name, NodeResult? Result);

/// <summary>One profile in the list.</summary>
public sealed record ProfileSummary(Guid Id, string Name, int Nodes, bool IsActive, bool IsValid);

public sealed record ProfilesReply(IReadOnlyList<ProfileSummary> Profiles, Guid? ActiveId);

/// <summary>A profile with its check.</summary>
public sealed record ProfileReply(Profile Profile, ProfileCheckResult Check);

/// <summary>A kind of node for the editor's list.</summary>
public sealed record NodeKindInfo(NodeKind Kind, string Name, bool IsFixed, bool AlwaysStops, JsonElement DefaultSettings);

/// <summary>The state of provisioning for the Provisioning management dialog and the Devices toolbar.</summary>
public sealed record StatusReply
{
    /// <summary>Provisioning is on: every factory-new camera OADM finds gets its task.</summary>
    public bool On { get; init; }

    /// <summary>The administrator who turned it on.</summary>
    public string? OnBy { get; init; }

    public DateTimeOffset? OnAt { get; init; }

    public Guid? ActiveProfileId { get; init; }

    public string? ActiveProfileName { get; init; }

    /// <summary>The active profile has no errors (only then do tasks start).</summary>
    public bool ActiveProfileValid { get; init; }

    /// <summary>Why On is refused now ("Choose the active profile first."); null when On works or provisioning is on.</summary>
    public string? TurnOnRefusal { get; init; }

    /// <summary>The DHCP server; null when its plugin does not run.</summary>
    public DhcpServerSummary? Dhcp { get; init; }

    /// <summary>Provisioning tasks that run or wait now.</summary>
    public int RunningTasks { get; init; }

    /// <summary>Warnings (On still works): a pool that overlaps the DHCP range; the active profile has errors while on.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
