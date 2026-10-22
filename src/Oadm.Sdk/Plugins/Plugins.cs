using Microsoft.Extensions.Logging;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Network;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Plugins;

/// <summary>What every plugin has: an id, a name and an icon.</summary>
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
    /// <summary>An icon of the client by key without "Icon." ("restart", "network"); null for none. See the UI kit.</summary>
    string? IconKey { get; }
}

/// <summary>
/// An entry of the device context menu that runs on the server, one task per selected device. Implement it in a
/// <c>*.Server.dll</c> with a public parameterless constructor, or contribute it through <see cref="ICorePlugin.TaskPlugins"/>.
/// </summary>
public interface ITaskPlugin : IPlugin
{
    /// <summary>
    /// Submenu of the device context menu this task appears in. Reuse a <see cref="TaskGroups"/>
    /// constant when one fits; a new name creates a new submenu. Default <see cref="TaskGroups.General"/>.
    /// </summary>
    string Group => TaskGroups.General;

    /// <summary>Also a button in the Devices toolbar, enabled when the task can run on the whole selection.</summary>
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
    /// Finer rights inside this task for choices that differ in risk, e.g. Users "add", "update" and "remove". Empty (the
    /// default): the task has one permission, "task:&lt;id&gt;". With permissions declared there is no plain "task:&lt;id&gt;":
    /// the menu entry shows when the role has at least one of them, and each run needs the ones
    /// <see cref="RequiredPermissions"/> names.
    /// </summary>
    IReadOnlyList<PermissionDefinition> Permissions => [];

    /// <summary>
    /// The keys of <see cref="Permissions"/> one run needs, from its payload (the run is refused before any task starts
    /// when the role lacks one). Called only for a plugin that declares permissions; null, empty or an exception means
    /// every declared permission (fail closed). Read the payload only, never contact devices.
    /// </summary>
    IReadOnlyList<string>? RequiredPermissions(string? payloadJson) => null;

    /// <summary>
    /// Whether this task works for devices at a site; null (the default) = the package's <c>siteSupport</c> in plugin.json.
    /// Set it only when one task of a package differs.
    /// </summary>
    Sites.SiteSupport? SiteSupport => null;

    /// <summary>
    /// Runs once per device. Return normally for Done, call <see cref="ITaskExecutionContext.ReportWarning"/>
    /// for Done with warnings, throw for Failed. <paramref name="payloadJson"/> comes from the dialog;
    /// it is kept in memory only and never persisted, so it may carry secrets such as passwords.
    /// </summary>
    Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);
}

/// <summary>
/// A service that runs inside the server while it is turned on, usually with a page in the navigation rail
/// (<c>ICorePluginPage</c> in the client part). It can contribute task plugins, actions and events.
/// </summary>
public interface ICorePlugin : IPlugin
{
    /// <summary>Task plugins contributed by this core plugin, e.g. PKI contributes "Deploy certificate".</summary>
    IReadOnlyList<ITaskPlugin> TaskPlugins { get; }
    /// <summary>
    /// Called when the server starts and when an administrator turns the plugin on. Keep <paramref name="ctx"/> for later.
    /// Return fast: never wait for a device or the network here, start background work instead.
    /// </summary>
    Task StartAsync(ICorePluginContext ctx, CancellationToken ct);
    /// <summary>Called when the server stops and when the plugin is turned off: stop background work and release ports.</summary>
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

    /// <summary>
    /// Named rights of this plugin for the permission matrix, e.g. VAPIX Commander "send" and "rollout". A plugin with a
    /// page also gets <see cref="PermissionKeys.Show"/> automatically (on for Operator, for Viewer on monitoring and
    /// reporting pages); declare "show" only to change those defaults, its name and text are the host's. Empty (the
    /// default): the host adds <see cref="PermissionKeys.Manage"/> for the methods that need <see cref="UserRole.Admin"/>.
    /// </summary>
    IReadOnlyList<PermissionDefinition> Permissions => [];

    /// <summary>
    /// The permission a user needs to call <paramref name="method"/>: a key of <see cref="Permissions"/>, or
    /// <see cref="PermissionKeys.Show"/>. Null (the default) falls back to <see cref="RequiredRole"/>: Operator methods need
    /// "show" (or only a login for a plugin without a page), Admin methods need "manage". An unknown key or an exception
    /// means Administrator only (fail closed).
    /// </summary>
    string? RequiredPermission(string method) => null;
}

/// <summary>Role of an OADM user. Admin may do everything; Operator works with devices and tasks.</summary>
public enum UserRole
{
    /// <summary>Works with devices and tasks.</summary>
    Operator = 0,
    /// <summary>May also change the server: settings, users, plugins.</summary>
    Admin = 1,
}

/// <summary>What the server gives one task while it runs on one device.</summary>
public interface ITaskExecutionContext
{
    /// <summary>The id of this task (one device).</summary>
    Guid TaskId { get; }
    /// <summary>Pre-authenticated for the current device.</summary>
    IVapixClient Vapix { get; }
    /// <summary>The server log. For what the user should read, use <see cref="Log"/>.</summary>
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
    /// (added by its DNS name or a typed name) or the record already has that address.
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

    /// <summary>
    /// The license of the plugin's package (plugin.json <c>license</c>): check <see cref="IPluginLicense.IsValid"/>,
    /// <see cref="IPluginLicense.Has"/> or <see cref="IPluginLicense.Limit"/> before work that needs it. Null on hosts
    /// without licenses.
    /// </summary>
    IPluginLicense? License => null;

    /// <summary>
    /// The server's OADM PRO license, live (<see cref="IProLicense.Changed"/>): whether PRO is active, the plan, the devices
    /// and the last day. OADM only checks; a PRO plugin decides what runs without it. Null on hosts without licenses.
    /// </summary>
    IProLicense? Pro => null;

    /// <summary>
    /// Network checks towards this task's device, routed like it (through its site or on the server's own network): the
    /// address-in-use check, the device's host name, OADM's address towards the device. Null on hosts without it.
    /// </summary>
    Sites.IDeviceNetwork? Network => null;
}

/// <summary>The device answering at an address is not the expected one (different or missing serial number).</summary>
public sealed class DeviceIdentityException : Exception
{
    /// <summary>The device at the address is not the expected one.</summary>
    public DeviceIdentityException()
    {
    }

    /// <summary>The device at the address is not the expected one, with the message for the user.</summary>
    public DeviceIdentityException(string message)
        : base(message)
    {
    }

    /// <summary>The device at the address is not the expected one, with the message and the cause.</summary>
    public DeviceIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The level of a task log entry (<see cref="ITaskExecutionContext.Log"/>).</summary>
public enum TaskLogLevel
{
    /// <summary>What the task did.</summary>
    Info = 0,
    /// <summary>Something the user should check; the task still succeeds.</summary>
    Warning = 1,
    /// <summary>What failed.</summary>
    Error = 2,
}

/// <summary>What the server gives a core plugin in <see cref="ICorePlugin.StartAsync"/>.</summary>
public interface ICorePluginContext
{
    /// <summary>Every managed device.</summary>
    IDeviceRepository Devices { get; }
    /// <summary>Logged-in VAPIX clients for the managed devices.</summary>
    IVapixClientFactory Vapix { get; }
    /// <summary>Starts and cancels tasks, e.g. of a task plugin the core plugin contributes.</summary>
    ITaskRunner Tasks { get; }
    /// <summary>The plugin's own settings in the server database.</summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// The plugin's own records in the server database, one row per record (data that grows with the devices); null on
    /// hosts before OADM 4.1.
    /// </summary>
    IPluginRecords? Records => null;
    /// <summary>The server log.</summary>
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

    /// <summary>
    /// The license of the plugin's package (plugin.json <c>license</c>), live: <see cref="IPluginLicense.Changed"/> fires
    /// when an administrator enters or removes a license and when it or the trial runs out. OADM only checks; the plugin
    /// decides what runs without a valid license. Null on hosts without licenses.
    /// </summary>
    IPluginLicense? License => null;

    /// <summary>
    /// The server's OADM PRO license, live (<see cref="IProLicense.Changed"/>): whether PRO is active, the plan, the devices
    /// and the last day. OADM only checks; a PRO plugin decides what runs without it. Null on hosts without licenses.
    /// </summary>
    IProLicense? Pro => null;

    /// <summary>
    /// Publishes the plugin's events to the server's event rules (the plugin implements
    /// <see cref="Events.IEventSource"/> to declare them). Null when the host has no event rules or the plugin declares no
    /// events.
    /// </summary>
    Events.IEventPublisher? EventPublisher => null;

    /// <summary>
    /// Receives every event published to the server, the same the event rules see (<see cref="Events.IEventSubscriptions"/>).
    /// Null when the host has no event rules.
    /// </summary>
    Events.IEventSubscriptions? EventSubscriptions => null;

    /// <summary>
    /// Network checks routed like a device or a site, and listeners on the server's own network
    /// (<see cref="Sites.IPluginNetwork"/>). Null on hosts without it.
    /// </summary>
    Sites.IPluginNetwork? Network => null;

    /// <summary>The bound sites and relays into them (<see cref="Sites.ISites"/>). Null on hosts without sites.</summary>
    Sites.ISites? Sites => null;

    /// <summary>
    /// Services core plugins offer each other (<see cref="IPluginServices"/>), e.g. the OADM CA of the PKI plugin. Null on
    /// hosts without them.
    /// </summary>
    IPluginServices? Services => null;
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
    /// <summary>The JSON stored under <paramref name="key"/>; null when nothing is stored.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct);
    /// <summary>Stores <paramref name="valueJson"/> under <paramref name="key"/>; null removes it.</summary>
    Task SetAsync(string key, string? valueJson, CancellationToken ct);
}

/// <summary>
/// Content of plugin.json next to the plugin assemblies. <paramref name="EnabledByDefault"/> false: the plugin is off
/// until an administrator turns it on (Plugins page). <paramref name="AlwaysOn"/> true: core functionality that cannot be
/// turned off (the stored choice is ignored). <paramref name="Description"/>: one short sentence for the Plugins page.
/// </summary>
/// <param name="Id">The plugin id ("acme.hello"); also the folder name.</param>
/// <param name="Version">The plugin's version.</param>
/// <param name="MinSdkVersion">The oldest SDK the plugin works with (<see cref="SdkInfo.Version"/>).</param>
/// <param name="DisplayName">The name on the Plugins page.</param>
/// <param name="EnabledByDefault">False: off until an administrator turns it on.</param>
/// <param name="AlwaysOn">True: core functionality that cannot be turned off.</param>
/// <param name="Description">One short sentence for the Plugins page.</param>
/// <param name="License">The plugin is sold with a license (public key, features, trial days); null = free.</param>
/// <remarks>
/// The positional parameters are those of SDK 0.1.0 (constructor and <c>Deconstruct</c> stay binary compatible); later
/// entries are init properties.
/// </remarks>
public sealed record PluginManifest(
    string Id,
    string Version,
    string MinSdkVersion,
    string? DisplayName = null,
    bool EnabledByDefault = true,
    bool AlwaysOn = false,
    string? Description = null,
    PluginLicenseManifest? License = null)
{
    /// <summary>Whether the package works with devices at a site; null (not declared) counts as <see cref="Sites.SiteSupport.None"/>.</summary>
    public Sites.SiteSupport? SiteSupport { get; init; }

    /// <summary>Ports the package listens on at sites (relays), known in advance; null = none.</summary>
    public IReadOnlyList<Sites.SitePort>? SitePorts { get; init; }

    /// <summary>
    /// An OADM PRO plugin: listed for everyone, but the host turns it on only with an active OADM PRO license or trial and
    /// turns it off when the license ends (<see cref="Licensing.IProLicense"/>).
    /// </summary>
    public bool Pro { get; init; }
}

/// <summary>
/// Well-known context menu groups (<see cref="ITaskPlugin.Group"/>). Plugins reuse these so related
/// tasks of different plugins share one submenu; any other non-empty name creates its own submenu.
/// </summary>
/// <summary>Groups of the core plugin pages in the client's navigation rail, shown in this order.</summary>
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

/// <summary>Well-known context menu groups (<see cref="ITaskPlugin.Group"/>). Related tasks of different plugins share one submenu.</summary>
public static class TaskGroups
{
    /// <summary>Applications (ACAP) and their licenses.</summary>
    public const string Applications = "Applications";
    /// <summary>The default for tasks without a group.</summary>
    public const string General = "General";
    /// <summary>Restart, firmware, date and time, configuration.</summary>
    public const string Maintenance = "Maintenance";
    /// <summary>Network settings and addresses.</summary>
    public const string Network = "Network";
    /// <summary>Certificates, HTTPS, IEEE 802.1X.</summary>
    public const string Security = "Security";
    /// <summary>User accounts on the devices.</summary>
    public const string Users = "Users";
    /// <summary>Video and image settings.</summary>
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

/// <summary>The version of this SDK.</summary>
public static class SdkInfo
{
    /// <summary>The SDK version; a plugin whose <c>minSdkVersion</c> is newer is not loaded.</summary>
    public const string Version = "0.2.0";
}
