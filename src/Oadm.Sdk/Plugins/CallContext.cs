using Oadm.Sdk.Devices;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// The user's call of a plugin method (SDK 0.4.0, OADM 4.2): who calls, what the role allows, and the devices the user may
/// reach. A user can be limited to a device group; <see cref="Devices"/>, <see cref="Vapix"/>, <see cref="Tasks"/> and
/// <see cref="EventStreams"/> see only the devices of that group. Device ids outside it are treated like unknown ids:
/// <see cref="IDeviceRepository.FindAsync"/> answers null, the others throw <see cref="DevicesNotAvailableException"/>
/// (the host answers NOT_FOUND, "N devices are no longer available.") and nothing runs.
/// </summary>
/// <remarks>
/// During the call of a user with a device group, the context's own <c>ctx.Devices</c>, <c>ctx.Vapix</c>, <c>ctx.Tasks</c> and
/// <c>ctx.EventStreams</c> refuse (fail closed): use the call. Background work never carries a user; work a method starts
/// for the user goes through <see cref="ICorePluginContext.Jobs"/>, which keeps the owner's call.
/// </remarks>
public interface ICallContext
{
    /// <summary>The user's name, as logged in.</summary>
    string UserName { get; }

    /// <summary>
    /// An opaque id of the user's login session (never the token): the audience of events for one client
    /// (<see cref="PluginEventAudience.Session"/>) and the owner of jobs.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// The key of the user's devices for state a plugin keeps per group (sweeps, scans): <see cref="AllDevicesScopeKey"/> for
    /// users with all devices, else the device group's id. Never empty.
    /// </summary>
    string DeviceScopeKey { get; }

    /// <summary>The <see cref="DeviceScopeKey"/> of users with all devices.</summary>
    const string AllDevicesScopeKey = "all";

    /// <summary>True when the user's role grants <paramref name="permission"/> ("&lt;plugin id&gt;:&lt;key&gt;" or a host key).</summary>
    bool Has(string permission);

    /// <summary>The user may reach every device (administrators always). False for a user limited to a device group.</summary>
    bool HasAllDevices { get; }

    /// <summary>The devices of the user's view: <see cref="IDeviceRepository.ListAsync"/> lists only them.</summary>
    IDeviceRepository Devices { get; }

    /// <summary>Logged-in VAPIX clients, only for devices of the user's view.</summary>
    IVapixClientFactory Vapix { get; }

    /// <summary>Starts tasks for the user, only on devices of the user's view (the whole run is refused otherwise).</summary>
    ITaskRunner Tasks { get; }

    /// <summary>Event streams of devices of the user's view; null when the host does not offer them.</summary>
    IDeviceEventStreams? EventStreams { get; }

    /// <summary>True when <paramref name="device"/> is in the user's view (a few hash lookups, no request).</summary>
    bool Contains(IDeviceInfo device);
}

/// <summary>
/// The full device access of a core plugin (<see cref="ICorePluginContext.System"/>): every managed device. For background
/// work (sweeps, servers such as NTP, DHCP, EST) and, during a user's call, only in methods that declare why
/// (<see cref="ICorePlugin.SystemAccessReason"/>). Never hand its results to a user without filtering them by the call.
/// </summary>
public interface ISystemAccess
{
    /// <summary>Every managed device.</summary>
    IDeviceRepository Devices { get; }

    /// <summary>Logged-in VAPIX clients for every managed device.</summary>
    IVapixClientFactory Vapix { get; }

    /// <summary>Starts and cancels tasks on any device (owner: the plugin).</summary>
    ITaskRunner Tasks { get; }

    /// <summary>Event streams of every managed device; null when the host does not offer them.</summary>
    IDeviceEventStreams? EventStreams { get; }
}

/// <summary>The default <see cref="ICorePluginContext.System"/> of hosts before SDK 0.4.0: the context's own members.</summary>
internal sealed class ContextSystemAccess(ICorePluginContext ctx) : ISystemAccess
{
    public IDeviceRepository Devices => ctx.Devices;

    public IVapixClientFactory Vapix => ctx.Vapix;

    public ITaskRunner Tasks => ctx.Tasks;

    public IDeviceEventStreams? EventStreams => ctx.EventStreams;
}

/// <summary>
/// The device groups as a plugin sees them (<see cref="ICorePluginContext.DeviceScopes"/>, SDK 0.4.0), for objects a plugin
/// keeps for a group (dashboards, device triggers, saved views: stored with <see cref="ICallContext.DeviceScopeKey"/> of
/// the user who made them). Background work of such an object uses only the devices of its owner, and a deleted group's
/// objects are off (fail closed) until an administrator gives them another owner.
/// </summary>
public interface IDeviceScopes
{
    /// <summary>True for <see cref="ICallContext.AllDevicesScopeKey"/> and for the key of a group that exists; false for a deleted group.</summary>
    bool Exists(string scopeKey);

    /// <summary>True when <paramref name="device"/> is in the devices of <paramref name="scopeKey"/> (false for a deleted group).</summary>
    bool Contains(string scopeKey, IDeviceInfo device);

    /// <summary>The devices of <paramref name="scopeKey"/> (none for a deleted group).</summary>
    IDeviceRepository Devices(string scopeKey);

    /// <summary>The display name of a scope ("All devices" or the group's name); null for a deleted group.</summary>
    string? NameOf(string scopeKey);

    /// <summary>Raised (without a caller) with the key of a group whose rules changed or that was deleted.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>Who receives a plugin event (<see cref="IPluginEvents.Publish(string, string?, PluginEventAudience)"/>).</summary>
public enum PluginEventAudienceKind
{
    /// <summary>Watchers whose user has all devices (what the old <c>Publish(topic, json)</c> reaches).</summary>
    AllDevices = 0,

    /// <summary>Watchers whose user can see the one device.</summary>
    Device = 1,

    /// <summary>
    /// A JSON array with one element per device: every watcher gets the elements of the devices it can see, in order; a
    /// watcher that sees none gets nothing.
    /// </summary>
    DeviceRows = 2,

    /// <summary>The watchers of one user (every session).</summary>
    User = 3,

    /// <summary>The watchers of one login session (<see cref="ICallContext.SessionId"/>).</summary>
    Session = 4,
}

/// <summary>
/// The audience of a plugin event (SDK 0.4.0): the host delivers the event only to the page watchers it covers, so an event
/// about a device never reaches a user who cannot see the device.
/// </summary>
public sealed class PluginEventAudience
{
    private PluginEventAudience(PluginEventAudienceKind kind, IReadOnlyList<Guid> deviceIds, string? userName, string? sessionId)
    {
        Kind = kind;
        DeviceIds = deviceIds;
        UserName = userName;
        SessionId = sessionId;
    }

    /// <summary>Who receives it.</summary>
    public PluginEventAudienceKind Kind { get; }

    /// <summary>The device (<see cref="PluginEventAudienceKind.Device"/>) or the device of each row (<see cref="PluginEventAudienceKind.DeviceRows"/>).</summary>
    public IReadOnlyList<Guid> DeviceIds { get; }

    /// <summary>The user of <see cref="PluginEventAudienceKind.User"/>.</summary>
    public string? UserName { get; }

    /// <summary>The session of <see cref="PluginEventAudienceKind.Session"/>.</summary>
    public string? SessionId { get; }

    /// <summary>Watchers whose user has all devices (server-wide state: NTP, DHCP, PKI pages).</summary>
    public static PluginEventAudience AllDevices { get; } = new(PluginEventAudienceKind.AllDevices, [], null, null);

    /// <summary>Watchers whose user can see <paramref name="deviceId"/>.</summary>
    public static PluginEventAudience Device(Guid deviceId) => new(PluginEventAudienceKind.Device, [deviceId], null, null);

    /// <summary>
    /// The payload is a JSON array with one element per entry of <paramref name="rowDeviceIds"/> (same order); each watcher
    /// gets the elements of the devices it can see.
    /// </summary>
    public static PluginEventAudience DeviceRows(IReadOnlyList<Guid> rowDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(rowDeviceIds);
        return new(PluginEventAudienceKind.DeviceRows, [.. rowDeviceIds], null, null);
    }

    /// <summary>Every watcher of <paramref name="userName"/>.</summary>
    public static PluginEventAudience User(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        return new(PluginEventAudienceKind.User, [], userName, null);
    }

    /// <summary>The watchers of one session (<see cref="ICallContext.SessionId"/>).</summary>
    public static PluginEventAudience Session(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new(PluginEventAudienceKind.Session, [], null, sessionId);
    }

    /// <summary>The session of <paramref name="caller"/>: events of work the user started.</summary>
    public static PluginEventAudience Of(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return Session(caller.SessionId);
    }
}

/// <summary>
/// Work a plugin method starts for a user (a report, a scan, an export, a monitor), owned by the user who started it
/// (<see cref="ICorePluginContext.Jobs"/>, SDK 0.4.0). The host runs it without the caller (background), keeps the owner's
/// call for its devices, and answers ids of another user like unknown ids, so every id-taking method and download looks
/// the job up through <see cref="IPluginJobs.Find"/>.
/// </summary>
public interface IPluginJobs
{
    /// <summary>
    /// Starts <paramref name="work"/> for <paramref name="owner"/> outside the call. The work reaches devices through
    /// <see cref="IPluginJob.Owner"/> (the owner's view, also after the call ended) and stops when its token is cancelled
    /// (<see cref="IPluginJob.Cancel"/>, the plugin stopping). Returns the job; send its <see cref="IPluginJob.Id"/> to the page.
    /// </summary>
    /// <param name="owner">The call that starts the job.</param>
    /// <param name="kind">The plugin's name for the kind of work ("report", "scan", "export").</param>
    /// <param name="work">The work; exceptions end the job (logged), never the plugin.</param>
    IPluginJob Start(ICallContext owner, string kind, Func<IPluginJob, CancellationToken, Task> work);

    /// <summary>The job with <paramref name="jobId"/> when it belongs to the user of <paramref name="caller"/>, else null (also for unknown ids).</summary>
    IPluginJob? Find(ICallContext caller, string jobId);

    /// <summary>The jobs of the user of <paramref name="caller"/>, newest first.</summary>
    IReadOnlyList<IPluginJob> List(ICallContext caller);
}

/// <summary>One job of <see cref="IPluginJobs"/>.</summary>
public interface IPluginJob
{
    /// <summary>A random id (never guessable), unique on the server.</summary>
    string Id { get; }

    /// <summary>The plugin's kind of work ("report").</summary>
    string Kind { get; }

    /// <summary>The call of the user who started it: its devices are the job's devices.</summary>
    ICallContext Owner { get; }

    /// <summary>Ends when the work ended (ran to completion, failed or was cancelled).</summary>
    Task Completion { get; }

    /// <summary>Asks the work to stop (its token is cancelled).</summary>
    void Cancel();
}
