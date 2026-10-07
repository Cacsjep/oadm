using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// The client's only door to the server. Wraps the generated gRPC clients so view models can be
/// tested against <see cref="FakeOadmApi"/>. Message types are the generated contract types.
/// </summary>
public interface IOadmApi
{
    /// <summary>Address shown in the UI, e.g. "http://localhost:5080" or "fake".</summary>
    string ServerAddress { get; }

    /// <summary>Points the API at a different server. Running streams fail and are re-established by the caller.</summary>
    void SetServerAddress(string address);

    // DeviceService
    Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken ct);
    IAsyncEnumerable<DeviceChanged> WatchDevicesAsync(CancellationToken ct);
    Task RemoveDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct);
    Task RefreshDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct);
    Task SetCredentialsAsync(IReadOnlyCollection<string> deviceIds, string userName, string password, CancellationToken ct);
    Task<string> GetWebUiUrlAsync(string deviceId, CancellationToken ct);

    // DiscoveryService
    Task<string> StartZeroConfAsync(CancellationToken ct);
    Task<string> StartRangeScanAsync(string firstAddress, string lastAddress, CancellationToken ct);
    IAsyncEnumerable<DiscoveredDevice> WatchDiscoveredAsync(string sessionId, CancellationToken ct);
    Task StopDiscoveryAsync(string sessionId, CancellationToken ct);

    // AddDevicesService
    Task<AddPlan> PrepareAddAsync(string sessionId, IReadOnlyCollection<string> discoveredIds, CancellationToken ct);
    Task<CommitReply> CommitAddAsync(CommitRequest request, CancellationToken ct);

    // TaskService
    Task<IReadOnlyList<TaskPluginInfo>> ListTaskPluginsAsync(CancellationToken ct);
    Task<string> RunTaskAsync(string pluginId, IReadOnlyCollection<string> deviceIds, string? payloadJson, string owner, CancellationToken ct);
    Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken ct);
    IAsyncEnumerable<TaskChanged> WatchTasksAsync(CancellationToken ct);
    Task CancelTaskAsync(string taskId, CancellationToken ct);
    Task DeleteTaskAsync(string taskId, CancellationToken ct);

    /// <summary>Cancels running tasks, then deletes every task. Returns the number deleted.</summary>
    Task<int> DeleteAllTasksAsync(CancellationToken ct);

    // SettingsService
    Task<ServerSettings> GetSettingsAsync(CancellationToken ct);
    Task<ServerSettings> SetSettingsAsync(ServerSettings settings, CancellationToken ct);

    // LiveViewService
    /// <summary>Encoded access units of a device's camera, relayed by the server.</summary>
    IAsyncEnumerable<LiveViewFrame> WatchLiveViewAsync(LiveViewRequest request, CancellationToken ct);

    /// <summary>Video sources (view areas, sensors, encoder channels) of a device.</summary>
    Task<IReadOnlyList<LiveViewSource>> ListLiveViewSourcesAsync(string deviceId, CancellationToken ct);

    // PluginService
    Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct);
    Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct);
}
