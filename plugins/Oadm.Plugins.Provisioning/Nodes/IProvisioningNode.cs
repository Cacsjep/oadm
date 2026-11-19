using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// One kind of node of a profile (README "Adding a node"): its name, its settings check (one error, the same in the editor
/// and before a profile is active) and its run on one camera. A node never throws for a device problem: it answers a
/// <see cref="NodeResult"/> with a plain message. Most nodes start the existing task of their area (<see cref="TaskNode{TSettings}"/>).
/// </summary>
public interface IProvisioningNode
{
    NodeKind Kind { get; }

    /// <summary>The node's name on the canvas and in the camera table ("Set IP").</summary>
    string Name { get; }

    /// <summary>Fixed nodes (Set password, Add to OADM) are always first, in this order, and cannot be removed.</summary>
    bool IsFixed => false;

    /// <summary>An error stops the camera's flow whatever <see cref="FlowNode.ContinueOnError"/> says (Set password).</summary>
    bool AlwaysStops => false;

    /// <summary>The node works on the OADM device, so it runs only after Add to OADM gave the camera its device.</summary>
    bool NeedsDevice => true;

    /// <summary>The settings a new node of this kind starts with.</summary>
    object DefaultSettings { get; }

    /// <summary>The one error of the settings, or null when they are fine. Cheap: the editor runs it on every change.</summary>
    string? Check(JsonElement? settings, ProfileCheckContext context);

    /// <summary>A warning about the settings (they still work), or null.</summary>
    string? Warning(JsonElement? settings, ProfileCheckContext context) => null;

    /// <summary>Runs the node on one camera. Never throws for a device problem (only for cancellation).</summary>
    Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct);
}

/// <summary>What the profile check knows besides the profile: the credential list and the DHCP server.</summary>
/// <param name="Credentials">The credential list entries by id with their user names; null when the host cannot tell.</param>
/// <param name="Dhcp">The DHCP server's state; null when it is not running or not installed.</param>
public sealed record ProfileCheckContext(IReadOnlyDictionary<Guid, string>? Credentials, DhcpServerSummary? Dhcp)
{
    public static ProfileCheckContext Empty { get; } = new(null, null);

    /// <summary>The files the vault keeps, by id; null when the host cannot tell (a file is then checked when the node runs).</summary>
    public IReadOnlyDictionary<Guid, StoredFile>? Files { get; init; }

    /// <summary>The secrets the vault keeps; null when the host cannot tell.</summary>
    public IReadOnlySet<Guid>? Secrets { get; init; }
}

/// <summary>What a node may use while it runs (the engine implements it).</summary>
public interface INodeHost
{
    /// <summary>The server's add page pipeline: first password, add with a login, credential list.</summary>
    IDeviceAutoAdd AutoAdd { get; }

    /// <summary>Starts the tasks of other plugins and follows their outcome.</summary>
    ITaskRunner Tasks { get; }

    /// <summary>The managed devices.</summary>
    IDeviceRepository Devices { get; }

    /// <summary>VAPIX of the managed devices (Autofocus, the template camera of Configuration).</summary>
    IVapixClientFactory Vapix { get; }

    /// <summary>
    /// The tasks of the administrator who started provisioning, with their MyAxis sign-in (Upgrade AXIS OS online); null
    /// after a server restart until provisioning is started again.
    /// </summary>
    ITaskRunner? StarterTasks { get; }

    /// <summary>The typed secrets and the files the nodes name by id.</summary>
    NodeVault Vault { get; }

    TimeProvider Time { get; }

    ProvisioningOptions Options { get; }

    /// <summary>The owner of the tasks the nodes start ("Provisioning").</summary>
    string Owner { get; }

    /// <summary>Takes and reserves the next free address of the pool for the camera (or the one it has already).</summary>
    Task<AddressTake> TakeAddressAsync(string mac, SetIpSettings pool, CancellationToken ct);

    /// <summary>Waits outside the camera's place until Continue is pressed on its row; returns who pressed it.</summary>
    Task<string> WaitForApprovalAsync(string mac, CancellationToken ct);

    /// <summary>The camera is an OADM device now.</summary>
    void DeviceAdded(string mac, Guid deviceId, string? model);

    /// <summary>The camera answers at another address now.</summary>
    void AddressChanged(string mac, string address);
}

/// <summary>An address of a Set IP pool: the address, or null when the pool is used up, and the addresses skipped as in use.</summary>
public sealed record AddressTake(string? Address, IReadOnlyList<string> InUse, bool Reused);

/// <summary>One node's run on one camera.</summary>
public sealed record NodeRun(INodeHost Host, CameraRow Camera, Profile Profile, FlowNode Node)
{
    /// <summary>The node's settings as <typeparamref name="T"/> (missing: the defaults; unreadable: null).</summary>
    public T? SettingsAs<T>()
        where T : class => NodeSettings.Read<T>(Node.Settings);

    /// <summary>The credential list entry of the profile's Set password node.</summary>
    public Guid? CredentialId =>
        Profile.Nodes.FirstOrDefault(n => n.Kind == NodeKind.SetPassword) is { } node ? NodeSettings.Read<SetPasswordSettings>(node.Settings)?.CredentialId : null;
}

/// <summary>Reads and writes the settings records of the nodes.</summary>
public static class NodeSettings
{
    public static JsonSerializerOptions Json => ProvisioningJson.Options;

    /// <summary>The settings as <typeparamref name="T"/>: missing settings are the record's defaults, unreadable ones null.</summary>
    public static T? Read<T>(JsonElement? settings)
        where T : class
    {
        if (settings is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element)
        {
            return JsonSerializer.Deserialize<T>("{}", Json);
        }

        try
        {
            return element.Deserialize<T>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement Write(object settings) => JsonSerializer.SerializeToElement(settings, settings.GetType(), Json);
}
