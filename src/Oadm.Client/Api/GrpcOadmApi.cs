using System.Runtime.CompilerServices;

using Grpc.Core;
using Grpc.Net.Client;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>Real <see cref="IOadmApi"/> over gRPC (h2c by default, http://localhost:5080).</summary>
public sealed class GrpcOadmApi : IOadmApi, IDisposable
{
    private readonly Lock _gate = new();
    private GrpcChannel _channel;
    private Clients _clients;

    public GrpcOadmApi(string address)
    {
        ServerAddress = Normalize(address);
        _channel = GrpcChannel.ForAddress(ServerAddress);
        _clients = new Clients(_channel);
    }

    public string ServerAddress { get; private set; }

    public void SetServerAddress(string address)
    {
        string normalized = Normalize(address);
        GrpcChannel old;
        lock (_gate)
        {
            if (normalized == ServerAddress)
            {
                return;
            }

            old = _channel;
            ServerAddress = normalized;
            _channel = GrpcChannel.ForAddress(normalized);
            _clients = new Clients(_channel);
        }

        old.Dispose();
    }

    public void Dispose() => _channel.Dispose();

    private Clients C
    {
        get
        {
            lock (_gate)
            {
                return _clients;
            }
        }
    }

    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken ct) =>
        (await C.Devices.ListAsync(new Empty(), cancellationToken: ct)).Devices;

    public IAsyncEnumerable<DeviceChanged> WatchDevicesAsync(CancellationToken ct) =>
        ReadAll(C.Devices.Watch(new Empty(), cancellationToken: ct), ct);

    public async Task RemoveDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct) =>
        await C.Devices.RemoveAsync(ToIds(deviceIds), cancellationToken: ct);

    public async Task RefreshDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct) =>
        await C.Devices.RefreshAsync(ToIds(deviceIds), cancellationToken: ct);

    public async Task SetCredentialsAsync(IReadOnlyCollection<string> deviceIds, string userName, string password, CancellationToken ct)
    {
        var request = new SetCredentialsRequest { UserName = userName, Password = password };
        request.DeviceIds.AddRange(deviceIds);
        await C.Devices.SetCredentialsAsync(request, cancellationToken: ct);
    }

    public async Task<string> GetWebUiUrlAsync(string deviceId, CancellationToken ct) =>
        (await C.Devices.GetWebUiUrlAsync(new DeviceId { Id = deviceId }, cancellationToken: ct)).Url;

    public async Task<string> StartZeroConfAsync(CancellationToken ct) =>
        (await C.Discovery.StartZeroConfAsync(new Empty(), cancellationToken: ct)).SessionId;

    public async Task<string> StartRangeScanAsync(string firstAddress, string lastAddress, CancellationToken ct) =>
        (await C.Discovery.StartRangeScanAsync(new RangeScanRequest { From = firstAddress, To = lastAddress }, cancellationToken: ct)).SessionId;

    public IAsyncEnumerable<DiscoveredDevice> WatchDiscoveredAsync(string sessionId, CancellationToken ct) =>
        ReadAll(C.Discovery.WatchDiscovered(new DiscoverySession { SessionId = sessionId }, cancellationToken: ct), ct);

    public async Task StopDiscoveryAsync(string sessionId, CancellationToken ct) =>
        await C.Discovery.StopAsync(new DiscoverySession { SessionId = sessionId }, cancellationToken: ct);

    public async Task<AddPlan> PrepareAddAsync(string sessionId, IReadOnlyCollection<string> discoveredIds, CancellationToken ct)
    {
        var request = new PrepareRequest { SessionId = sessionId };
        request.DiscoveredIds.AddRange(discoveredIds);
        return await C.AddDevices.PrepareAsync(request, cancellationToken: ct);
    }

    public async Task<CommitReply> CommitAddAsync(CommitRequest request, CancellationToken ct) =>
        await C.AddDevices.CommitAsync(request, cancellationToken: ct);

    public async Task<IReadOnlyList<TaskPluginInfo>> ListTaskPluginsAsync(CancellationToken ct) =>
        (await C.Tasks.ListTaskPluginsAsync(new Empty(), cancellationToken: ct)).Plugins;

    public async Task<string> RunTaskAsync(string pluginId, IReadOnlyCollection<string> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        var request = new RunTaskRequest { PluginId = pluginId, PayloadJson = payloadJson ?? "", Owner = owner };
        request.DeviceIds.AddRange(deviceIds);
        return (await C.Tasks.RunAsync(request, cancellationToken: ct)).TaskId;
    }

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken ct) =>
        (await C.Tasks.ListAsync(new Empty(), cancellationToken: ct)).Tasks;

    public IAsyncEnumerable<TaskChanged> WatchTasksAsync(CancellationToken ct) =>
        ReadAll(C.Tasks.Watch(new Empty(), cancellationToken: ct), ct);

    public async Task CancelTaskAsync(string taskId, CancellationToken ct) =>
        await C.Tasks.CancelAsync(new TaskIdRequest { TaskId = taskId }, cancellationToken: ct);

    public async Task DeleteTaskAsync(string taskId, CancellationToken ct) =>
        await C.Tasks.DeleteAsync(new TaskIdRequest { TaskId = taskId }, cancellationToken: ct);

    public async Task<ServerSettings> GetSettingsAsync(CancellationToken ct) =>
        await C.Settings.GetAsync(new Empty(), cancellationToken: ct);

    public async Task<ServerSettings> SetSettingsAsync(ServerSettings settings, CancellationToken ct) =>
        await C.Settings.SetAsync(settings, cancellationToken: ct);

    public async Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct) =>
        (await C.Plugins.ListCorePluginsAsync(new Empty(), cancellationToken: ct)).Plugins;

    public async Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        var reply = await C.Plugins.InvokeAsync(
            new InvokeRequest { PluginId = pluginId, Method = method, PayloadJson = payloadJson ?? "" },
            cancellationToken: ct);
        return string.IsNullOrEmpty(reply.PayloadJson) ? null : reply.PayloadJson;
    }

    private static DeviceIds ToIds(IEnumerable<string> ids)
    {
        var message = new DeviceIds();
        message.Ids.AddRange(ids);
        return message;
    }

    private static async IAsyncEnumerable<T> ReadAll<T>(AsyncServerStreamingCall<T> call, [EnumeratorCancellation] CancellationToken ct)
    {
        using (call)
        {
            await foreach (T item in call.ResponseStream.ReadAllAsync(ct))
            {
                yield return item;
            }
        }
    }

    internal static string Normalize(string address)
    {
        string trimmed = (address ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return Infrastructure.ClientSettings.DefaultServerAddress;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    private sealed class Clients(GrpcChannel channel)
    {
        public DeviceService.DeviceServiceClient Devices { get; } = new(channel);
        public DiscoveryService.DiscoveryServiceClient Discovery { get; } = new(channel);
        public AddDevicesService.AddDevicesServiceClient AddDevices { get; } = new(channel);
        public TaskService.TaskServiceClient Tasks { get; } = new(channel);
        public SettingsService.SettingsServiceClient Settings { get; } = new(channel);
        public PluginService.PluginServiceClient Plugins { get; } = new(channel);
    }
}
