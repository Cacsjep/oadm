using Microsoft.Extensions.Logging;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Plugins;

public interface IPlugin
{
    /// <summary>Stable id, e.g. "oadm.restart".</summary>
    string Id { get; }

    /// <summary>
    /// Name shown in menus and the toolbar, e.g. "Restart" or "Upgrade firmware" (the tasks pane shows
    /// <see cref="ITaskPlugin.GetTaskName"/> of task plugins).
    /// Task plugins: at most <see cref="TaskPluginNames.MaxDisplayNameLength"/> characters and no
    /// trailing "..." (the host never appends one and strips it defensively); the loader logs a
    /// warning and shortens longer names with an ellipsis.
    /// </summary>
    string DisplayName { get; }
    string? IconKey { get; }
}

public interface ITaskPlugin : IPlugin
{
    /// <summary>
    /// Submenu of the device context menu this task appears in. Reuse a <see cref="TaskGroups"/>
    /// constant when one fits; a new name creates a new submenu. Default <see cref="TaskGroups.General"/>.
    /// </summary>
    string Group => TaskGroups.General;

    bool ShowInToolbar { get; }
    /// <summary>When true the client opens the matching ITaskPluginDialog before running.</summary>
    bool RequiresDialog { get; }
    /// <summary>
    /// Synchronous filter for the context menu. Plugins that change the device MUST check the
    /// required API versions here via <c>device.Apis.Supports(...)</c> (cached from the last full
    /// refresh) and again in <see cref="ExecuteAsync"/> against a fresh
    /// <c>ctx.Vapix.GetApiListAsync</c> with <c>Require(...)</c> before the first write.
    /// </summary>
    bool CanRun(IDeviceInfo device);

    /// <summary>
    /// Why <see cref="CanRun"/> is false for this device, in plain language: what is missing and what to do, e.g.
    /// "Needs AXIS OS 11.11 or later (this device has 10.12.338)" or "Needs the Time API". The context menu shows the
    /// task greyed out with this text as tooltip. Called only when CanRun is false, with cached data only (cheap, no
    /// request). A detail about this one device goes at the end in parentheses starting with "this device" (see
    /// <see cref="TaskSupportReasons.ThisDevice"/>): the host leaves it out when it sums up several devices. Null:
    /// <see cref="TaskSupportReasons.Default"/>. Helpers: <see cref="TaskSupportReasons"/>. Exceptions are caught by the
    /// host (default text, logged once). Default: null.
    /// </summary>
    string? NotSupportedReason(IDeviceInfo device) => null;

    /// <summary>
    /// How many tasks of this plugin may run at the same time (one task = one device); further tasks
    /// wait in Queued. Can only lower the server setting Tasks.MaxParallelPerPlugin (default 16): the
    /// engine uses the smaller of the two. Null: the server setting. Firmware upgrades use 4.
    /// </summary>
    int? MaxParallelDevices => null;

    /// <summary>
    /// False hides the plugin from the device context menu and the toolbar (it is not listed by
    /// TaskService.ListTaskPlugins). Used by tasks a core plugin starts itself from its page, e.g. the
    /// VAPIX Commander rollout. Default true.
    /// </summary>
    bool ShowInMenus => true;

    /// <summary>
    /// The name of one task in the tasks pane: exactly what this task does with this payload, e.g.
    /// "Add user joe", "Set static IP 10.0.0.60", "Upgrade firmware to 12.11.77", not the menu name.
    /// At most <see cref="TaskPluginNames.MaxTaskNameLength"/> characters (the host shortens longer names
    /// with an ellipsis and logs a warning). Never include secrets such as passwords: the name is persisted
    /// and shown to every user. Called once per Run (all devices of a run share the name); return
    /// <see cref="IPlugin.DisplayName"/> when the payload cannot be read. Default: the display name.
    /// </summary>
    string GetTaskName(string? payloadJson) => DisplayName;

    /// <summary>
    /// Runs once per device. Return normally for Done, call <see cref="ITaskExecutionContext.ReportWarning"/>
    /// for Done with warnings, throw for Failed. <paramref name="payloadJson"/> comes from the dialog;
    /// it is kept in memory only and never persisted, so it may carry secrets such as passwords.
    /// </summary>
    Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);
}

public interface ICorePlugin : IPlugin
{
    /// <summary>Task plugins contributed by this core plugin, e.g. PKI contributes "Deploy certificate".</summary>
    IReadOnlyList<ITaskPlugin> TaskPlugins { get; }
    Task StartAsync(ICorePluginContext ctx, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    /// <summary>Backend for the plugin's UI page (or, without a page, for its client parts such as a toolbar button).</summary>
    Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// False: the plugin has no page in the client's navigation rail (no rail entry at all); its client part is e.g. a
    /// toolbar plugin that calls <see cref="InvokeAsync"/> through <c>IToolbarContext.InvokePluginAsync</c>. Default true.
    /// </summary>
    bool HasPage => true;

    /// <summary>
    /// The group of the plugin's page in the client's navigation rail (a small group header; empty groups are not shown).
    /// Default <see cref="CorePluginGroup.Extensions"/>.
    /// </summary>
    CorePluginGroup Group => CorePluginGroup.Extensions;

    /// <summary>
    /// The role a user needs to call <paramref name="method"/> through the plugin's page (PluginService.Invoke). The host
    /// checks it before <see cref="InvokeAsync"/> and answers PERMISSION_DENIED for a missing role. Methods that change
    /// the server's configuration (settings, keys, services) return <see cref="UserRole.Admin"/>; reads and device
    /// work stay <see cref="UserRole.Operator"/> (the default).
    /// </summary>
    UserRole RequiredRole(string method) => UserRole.Operator;

    /// <summary>
    /// Whether a call of <paramref name="method"/> is written to the server's audit log (user, client, plugin, method).
    /// Default: the methods that need <see cref="UserRole.Admin"/>.
    /// </summary>
    bool IsAudited(string method) => RequiredRole(method) == UserRole.Admin;
}

/// <summary>Role of an OADM user. Admin may do everything; Operator works with devices and tasks.</summary>
public enum UserRole
{
    Operator = 0,
    Admin = 1,
}

public interface ITaskExecutionContext
{
    Guid TaskId { get; }
    /// <summary>Pre-authenticated for the current device.</summary>
    IVapixClient Vapix { get; }
    ILogger Logger { get; }
    /// <summary>Set when the task was contributed by a core plugin.</summary>
    ICorePlugin? Owner { get; }

    /// <summary>Files uploaded by the dialog (firmware, ACAP packages), referenced by id in the payload.</summary>
    IUploadedFiles Files { get; }

    /// <summary>
    /// Overall progress of the task with an optional status message. Optional when the plugin uses steps:
    /// without explicit calls the progress is derived from the steps (each step weighs the same; the running
    /// one counts with its own <see cref="ITaskStep.ReportProgress"/>). Once called, this value wins.
    /// </summary>
    void ReportProgress(int percent, string? message = null);

    /// <summary>
    /// Announces the steps the task will (probably) run, in order, so the user sees them as Pending from the
    /// start. Optional: <see cref="BeginStep"/> also adds steps that were not planned. Planned steps that never
    /// run end as Skipped. Name steps in the imperative, short and specific ("Upload firmware").
    /// </summary>
    void PlanSteps(params string[] names)
    {
    }

    /// <summary>
    /// Starts the named step (activating the planned step of that name) and returns it; see
    /// <see cref="ITaskStep"/> for the end rules. Every device request and every wait is its own step.
    /// Use <c>using var step = ctx.BeginStep("...")</c> or the helper
    /// <see cref="TaskStepExtensions.StepAsync{T}"/>. A running step is completed when the next one begins.
    /// </summary>
    ITaskStep BeginStep(string name) => new TaskStepList(onWarning: ReportWarning).Begin(name);

    /// <summary>The device finishes as "Done with warnings" (unless it fails); the message is logged.</summary>
    void ReportWarning(string message);

    /// <summary>Entry in this task's log, per device, shown in the task details and persisted. Never log secrets.</summary>
    void Log(TaskLogLevel level, string message);

    /// <summary>
    /// The device no longer accepts the credentials OADM stored (factory default, password reset):
    /// the server deletes them and refreshes the device, which then shows PasswordNotSet or
    /// CredentialsRequired. Logged in the task log (without secrets).
    /// </summary>
    void MarkCredentialsInvalid() => throw new NotSupportedException("This context cannot change stored credentials.");

    /// <summary>
    /// Stores new credentials for the current device (encrypted, never logged), e.g. after the plugin
    /// changed the password of the account OADM uses. Afterwards <see cref="Vapix"/> returns a client
    /// with the new credentials; re-read it instead of keeping the old instance.
    /// </summary>
    Task UpdateCredentialsAsync(string userName, string password, CancellationToken ct) =>
        throw new NotSupportedException("This context cannot change stored credentials.");

    /// <summary>
    /// A VAPIX client for the current device at another address (e.g. the static address the plugin just set),
    /// with the stored credentials, the device's scheme and its pinned certificate. The caller owns the client:
    /// dispose it when it implements <see cref="IDisposable"/>. Read requests only until the identity is verified.
    /// </summary>
    Task<IVapixClient> CreateClientForAsync(string address, CancellationToken ct) =>
        throw new NotSupportedException("This context cannot create clients for other addresses.");

    /// <summary>
    /// Moves OADM's device record to <paramref name="newAddress"/> so the device stays managed after it was
    /// re-addressed. The server first checks that the device answering there has the same serial number
    /// (throws <see cref="DeviceIdentityException"/> otherwise, the record is unchanged), keeps credentials and
    /// certificate pin, publishes the change and queues a full refresh; afterwards <see cref="Vapix"/> uses the
    /// new address. Returns false when nothing was changed because OADM reaches the device by host name
    /// (setting Devices.UseHostName) or the record already has that address.
    /// </summary>
    Task<bool> UpdateDeviceAddressAsync(string newAddress, CancellationToken ct) =>
        throw new NotSupportedException("This context cannot change the device address.");

    /// <summary>
    /// Updates how OADM connects to the current device after the plugin changed its web server: with
    /// <paramref name="scheme"/> "https" the device must present the certificate with the SHA-256 fingerprint
    /// <paramref name="expectedFingerprintSha256"/> (hex, separators ignored; null = accept and pin the presented one),
    /// with "http" OADM connects over plain HTTP (Digest only). The server connects with the new scheme, verifies the
    /// serial number like <see cref="UpdateDeviceAddressAsync"/>, then stores scheme, certificate pin and certificate
    /// details (so the device never shows CertificateChanged), publishes the change and queues a full refresh;
    /// afterwards <see cref="Vapix"/> uses the new connection. Throws <see cref="DeviceIdentityException"/> when the
    /// device does not answer with the new scheme, has another serial number or presents another certificate; the
    /// record is then unchanged.
    /// </summary>
    Task UpdateDeviceTlsAsync(string scheme, string? expectedFingerprintSha256, CancellationToken ct) =>
        throw new NotSupportedException("This context cannot change the device connection.");
}

/// <summary>The device answering at an address is not the expected one (different or missing serial number).</summary>
public sealed class DeviceIdentityException : Exception
{
    public DeviceIdentityException()
    {
    }

    public DeviceIdentityException(string message)
        : base(message)
    {
    }

    public DeviceIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum TaskLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public interface ICorePluginContext
{
    IDeviceRepository Devices { get; }
    IVapixClientFactory Vapix { get; }
    ITaskRunner Tasks { get; }
    IPluginSettings Settings { get; }
    ILogger Logger { get; }

    /// <summary>
    /// Folder the plugin was loaded from (plugin.json, assemblies, data files such as a command library).
    /// Null for plugins registered in code. Use it instead of <c>Assembly.Location</c>.
    /// </summary>
    string? PluginDirectory => null;

    /// <summary>
    /// Encrypts secrets the plugin stores in <see cref="Settings"/> with the server's master key
    /// (AES-256-GCM, like device passwords). Null when the host offers no protector: then do not store secrets.
    /// </summary>
    ISecretProtector? Secrets => null;

    /// <summary>
    /// Live events for the plugin's client page (<c>ICorePluginClientContext.WatchEventsAsync</c>). Null when the host
    /// cannot push events: the page then polls through InvokeAsync.
    /// </summary>
    IPluginEvents? Events => null;

    /// <summary>
    /// Certificate authorities this plugin wants the server to trust when it rates device certificates (Trusted in the
    /// device grid). Null when the host does not support it.
    /// </summary>
    ITrustAnchors? TrustAnchors => null;

    /// <summary>
    /// Event streams of managed devices (RTSP metadata, opened by the server with the stored credentials, which never
    /// reach the plugin). Null when the host does not offer them.
    /// </summary>
    IDeviceEventStreams? EventStreams => null;

    /// <summary>
    /// The host firewall for the ports the plugin serves (NTP UDP 123, DHCP UDP 67): open the rule while the service is
    /// enabled, close it when disabled or stopped (<see cref="FirewallRuleKeeper"/>). Only offered by the Windows server
    /// running as the installed service; null elsewhere (Linux, macOS, console runs, tests).
    /// </summary>
    IFirewallRules? Firewall => null;

    /// <summary>
    /// A private folder for the plugin's working files (temporary downloads, built archives) inside the server's data
    /// folder (<c>&lt;datafolder&gt;/plugin-data/&lt;plugin id&gt;</c>), created by the host on first use and protected like
    /// the data folder. The plugin cleans up what it writes. Null on hosts without one (use a folder of your own then).
    /// </summary>
    string? DataDirectory => null;

    /// <summary>
    /// Adds Axis devices the plugin saw on the network (DHCP leases) through the add page's pipeline and follows managed
    /// devices to new addresses (<see cref="IDeviceAutoAdd"/>). Null when the host does not offer it.
    /// </summary>
    IDeviceAutoAdd? AutoAdd => null;
}

/// <summary>Server-side encryption for secrets a plugin persists. Values never leave the server.</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>; <paramref name="purpose"/> is bound as associated data (e.g. a record id).</summary>
    string Protect(string plaintext, string purpose);

    /// <summary>Decrypts a value of <see cref="Protect"/> with the same purpose. Throws when it was tampered with.</summary>
    string Unprotect(string protectedValue, string purpose);
}

/// <summary>Settings store namespaced per plugin.</summary>
public interface IPluginSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string? valueJson, CancellationToken ct);
}

/// <summary>
/// Content of plugin.json next to the plugin assemblies. <paramref name="EnabledByDefault"/> false: the plugin is off
/// until an administrator turns it on (Plugins page). <paramref name="AlwaysOn"/> true: core functionality that cannot be
/// turned off (the stored choice is ignored). <paramref name="Description"/>: one short sentence for the Plugins page.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Version,
    string MinSdkVersion,
    string? DisplayName = null,
    bool EnabledByDefault = true,
    bool AlwaysOn = false,
    string? Description = null);

/// <summary>
/// Well-known context menu groups (<see cref="ITaskPlugin.Group"/>). Plugins reuse these so related
/// tasks of different plugins share one submenu; any other non-empty name creates its own submenu.
/// </summary>
/// <summary>Groups of the core plugin pages in the client's navigation rail, shown in this order (user decision 2026-10-08).</summary>
public enum CorePluginGroup
{
    /// <summary>Network services OADM runs for the devices: NTP server, DHCP server.</summary>
    Servers = 0,

    /// <summary>Running work on many devices: VAPIX Commander.</summary>
    Automation = 1,

    /// <summary>Certificates and hardening: PKI, Hardening scan.</summary>
    Security = 2,

    /// <summary>Watching devices live: Metadata Monitor.</summary>
    Monitoring = 3,

    /// <summary>Reports for customers and support: Snapshot report.</summary>
    Reporting = 4,

    /// <summary>Upkeep of the installation, e.g. backups and scheduled checks.</summary>
    Maintenance = 5,

    /// <summary>Connections to other systems, e.g. a video management system or a ticket system.</summary>
    Integrations = 6,

    /// <summary>Small helpers and tools.</summary>
    Utilities = 7,

    /// <summary>Everything else, the default for third-party plugins.</summary>
    Extensions = 8,
}

public static class TaskGroups
{
    public const string Applications = "Applications";
    public const string General = "General";
    public const string Maintenance = "Maintenance";
    public const string Network = "Network";
    public const string Security = "Security";
    public const string Users = "Users";
    public const string Video = "Video";

    /// <summary>Longest group name; longer ones are shortened like display names.</summary>
    public const int MaxLength = 32;
}

/// <summary>Display name rules for task plugins, applied by the server loader and the client menu.</summary>
public static class TaskPluginNames
{
    /// <summary>Longest task plugin display name (menu entries and toolbar buttons stay readable).</summary>
    public const int MaxDisplayNameLength = 32;

    /// <summary>Longest task name (<see cref="ITaskPlugin.GetTaskName"/>) shown in the tasks pane.</summary>
    public const int MaxTaskNameLength = 48;

    /// <summary>The ellipsis used when a name is shortened.</summary>
    public const string Ellipsis = "…";

    /// <summary>Trims the name and removes trailing "..." / "…" (the host never shows dialog dots).</summary>
    public static string StripEllipsis(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        while (true)
        {
            if (text.EndsWith("...", StringComparison.Ordinal))
            {
                text = text[..^3].TrimEnd();
            }
            else if (text.EndsWith(Ellipsis, StringComparison.Ordinal))
            {
                text = text[..^1].TrimEnd();
            }
            else
            {
                return text;
            }
        }
    }

    /// <summary>
    /// <see cref="StripEllipsis"/>, then shortens names over <paramref name="maxLength"/> characters to
    /// <paramref name="maxLength"/> - 1 characters plus "…".
    /// </summary>
    public static string Normalize(string? name, int maxLength = MaxDisplayNameLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        var text = StripEllipsis(name);
        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + Ellipsis;
    }

    /// <summary>The group name to show: trimmed and shortened, <see cref="TaskGroups.General"/> when empty.</summary>
    public static string NormalizeGroup(string? group)
    {
        var text = Normalize(group, TaskGroups.MaxLength);
        return text.Length == 0 ? TaskGroups.General : text;
    }
}

public static class SdkInfo
{
    public const string Version = "0.1.0";
}
