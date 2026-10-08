using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// The client's only door to the server. Wraps the generated gRPC clients so view models can be
/// tested against <see cref="FakeOadmApi"/>. Message types are the generated contract types.
/// </summary>
public interface IOadmApi
{
    /// <summary>Address shown in the UI, e.g. "https://localhost:5080" or "fake".</summary>
    string ServerAddress { get; }

    /// <summary>Points the API at a different server. Running streams fail and are re-established by the caller.</summary>
    void SetServerAddress(string address);

    /// <summary>The session token sent as "authorization: Bearer" with every call; null before login.</summary>
    string? AccessToken { get; set; }

    /// <summary>A call outside AuthService was answered UNAUTHENTICATED: the session ended (expired, revoked, user disabled).</summary>
    event EventHandler? SessionEnded;

    // AuthService (open: status, login, first administrator)
    Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct);
    Task<LoginReply> LoginAsync(string userName, string password, bool remember, CancellationToken ct);
    Task<LoginReply> CreateFirstAdminAsync(string userName, string password, string? setupCode, bool remember, CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
    Task<UserInfo> GetCurrentUserAsync(CancellationToken ct);

    // UserService (Admin only)
    Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct);
    Task<UserInfo> AddUserAsync(string userName, string password, UserRole role, CancellationToken ct);
    Task<UserInfo> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct);
    Task DeleteUserAsync(string id, CancellationToken ct);

    // AuditService (Admin only)
    /// <summary>The newest <paramref name="limit"/> audit entries, newest first, and the total count.</summary>
    Task<AuditList> ListAuditAsync(int limit, CancellationToken ct);

    // DeviceService
    Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken ct);
    /// <summary>Snapshot (one ADDED per device), then one SNAPSHOT_END, then live changes.</summary>
    IAsyncEnumerable<DeviceChanged> WatchDevicesAsync(CancellationToken ct);
    Task RemoveDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct);
    Task RefreshDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct);
    Task SetCredentialsAsync(IReadOnlyCollection<string> deviceIds, string userName, string password, CancellationToken ct);
    Task<string> GetWebUiUrlAsync(string deviceId, CancellationToken ct);

    /// <summary>
    /// "Log in" for devices whose stored credentials are rejected: the server tries the credential on every device and
    /// stores it for those that accept it. One call for the selection; one result per device.
    /// </summary>
    Task<DeviceLogInReply> LogInDevicesAsync(IReadOnlyCollection<string> deviceIds, string userName, string password, bool saveToCredentialList, CancellationToken ct);

    /// <summary>The user name stored for all these devices when they share one; empty otherwise.</summary>
    Task<string> GetCredentialUserNameAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct);

    // DiscoveryService
    Task<string> StartZeroConfAsync(CancellationToken ct);
    Task<string> StartRangeScanAsync(string firstAddress, string lastAddress, CancellationToken ct);

    /// <summary>Probes one entered address (IP or host name, optional port and scheme); returns the session id.</summary>
    Task<string> ProbeAddressAsync(string address, CancellationToken ct);

    /// <summary>
    /// Probes one address with credentials for that device only (a line of an imported device list): the
    /// server tries them first, then the credential list; they stay in server memory. Null = none.
    /// </summary>
    Task<string> ProbeAddressAsync(string address, string? userName, string? password, CancellationToken ct);
    IAsyncEnumerable<DiscoveredDevice> WatchDiscoveredAsync(string sessionId, CancellationToken ct);
    Task StopDiscoveryAsync(string sessionId, CancellationToken ct);

    /// <summary>Ends the scan of a session early (Stop button); the devices found and the session stay.</summary>
    Task StopScanAsync(string sessionId, CancellationToken ct);

    // AddDevicesService
    Task<CommitReply> CommitAddAsync(CommitRequest request, CancellationToken ct);

    /// <summary>Logs in to a discovered device with the entered credentials; returns the updated device.</summary>
    Task<DiscoveredDevice> RetryAuthAsync(RetryAuthRequest request, CancellationToken ct);

    // TaskService
    Task<IReadOnlyList<TaskPluginInfo>> ListTaskPluginsAsync(CancellationToken ct);
    /// <summary>Starts one task per device (sharing a batch id) and returns the task ids.</summary>
    Task<IReadOnlyList<string>> RunTaskAsync(string pluginId, IReadOnlyCollection<string> deviceIds, string? payloadJson, string owner, CancellationToken ct);
    Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken ct);
    /// <summary>Snapshot (every active task plus the newest <see cref="Oadm.Client.Tasks.TaskStore.MaxTasks"/>), then one SNAPSHOT_END, then live changes.</summary>
    IAsyncEnumerable<TaskChanged> WatchTasksAsync(CancellationToken ct);
    Task CancelTaskAsync(string taskId, CancellationToken ct);
    Task DeleteTaskAsync(string taskId, CancellationToken ct);

    /// <summary>Cancels running tasks, then deletes every task. Returns the number deleted.</summary>
    Task<int> DeleteAllTasksAsync(CancellationToken ct);

    /// <summary>The task's log, oldest first.</summary>
    Task<IReadOnlyList<TaskLogEntry>> GetTaskLogAsync(string taskId, CancellationToken ct);

    /// <summary>Read-only query of a task plugin for its dialog (server-side ITaskPluginQuery). Null when the plugin returned nothing.</summary>
    Task<string?> QueryTaskPluginAsync(string pluginId, string deviceId, string method, string? payloadJson, CancellationToken ct);

    // FileService
    /// <summary>Uploads a local file in 256 KB chunks; <paramref name="progress"/> receives 0..1.</summary>
    Task<UploadedFileInfo> UploadFileAsync(string path, IProgress<double>? progress, CancellationToken ct);

    // SettingsService
    Task<ServerSettings> GetSettingsAsync(CancellationToken ct);
    Task<ServerSettings> SetSettingsAsync(ServerSettings settings, CancellationToken ct);

    /// <summary>The credential list (ids and user names only; a password comes back only through <see cref="RevealCredentialAsync"/>).</summary>
    Task<IReadOnlyList<CredentialEntry>> ListCredentialsAsync(CancellationToken ct);
    Task<CredentialEntry> AddCredentialAsync(string userName, string password, CancellationToken ct);
    Task RemoveCredentialAsync(string id, CancellationToken ct);

    /// <summary>The stored password of one credential list entry, on explicit request (eye button). NOT_FOUND for an unknown id.</summary>
    Task<string> RevealCredentialAsync(string id, CancellationToken ct);

    // LiveViewService
    /// <summary>Encoded access units of a device's camera, relayed by the server.</summary>
    IAsyncEnumerable<LiveViewFrame> WatchLiveViewAsync(LiveViewRequest request, CancellationToken ct);

    /// <summary>Video sources (view areas, sensors, encoder channels) of a device.</summary>
    Task<IReadOnlyList<LiveViewSource>> ListLiveViewSourcesAsync(string deviceId, CancellationToken ct);

    // PluginService
    Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct);
    Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct);

    /// <summary>Live events of a core plugin (PluginService.Watch) until cancelled. Default: none (the page polls).</summary>
    IAsyncEnumerable<PluginEvent> WatchCorePluginAsync(string pluginId, CancellationToken ct) => AsyncEnumerable.Empty<PluginEvent>();
}
