using System.Runtime.CompilerServices;

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

using Oadm.Contracts.Security;
using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Real <see cref="IOadmApi"/> over gRPC: HTTP/2 over TLS with the pinned server certificate
/// (<see cref="ServerCertificatePinning"/>; https://localhost:5080 by default, http:// addresses without TLS for
/// development), the session token on every call.
/// </summary>
public sealed class GrpcOadmApi : IOadmApi, IDisposable
{
    /// <summary>Data chunk size of <see cref="UploadFileAsync"/>.</summary>
    public const int UploadChunkSize = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly ServerCertificatePinning _pinning;
    private readonly HttpMessageHandler? _handler;
    private GrpcChannel _channel;
    private Clients _clients;
    private volatile string? _token;

    /// <param name="address">"localhost:5080", "https://server:5080" or "http://..." (no TLS).</param>
    /// <param name="pinning">Pinned server certificates; null keeps them in memory only.</param>
    /// <param name="handler">Test hook: HTTP handler of an in-process server.</param>
    public GrpcOadmApi(string address, ServerCertificatePinning? pinning = null, HttpMessageHandler? handler = null)
    {
        _pinning = pinning ?? new ServerCertificatePinning(new InMemoryPinStore());
        _handler = handler;
        ServerAddress = Normalize(address);
        (_channel, _clients) = Open(ServerAddress);
    }

    public string ServerAddress { get; private set; }

    /// <summary>The address as a URI (key of the pinned certificate).</summary>
    public Uri ServerUri => new(ServerAddress);

    public ServerCertificatePinning Pinning => _pinning;

    public string? AccessToken
    {
        get => _token;
        set => _token = value;
    }

    public event EventHandler? SessionEnded;

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
            (_channel, _clients) = Open(normalized);
        }

        old.Dispose();
    }

    /// <summary>A new connection to the same server (after the user trusted its certificate the old one stays refused).</summary>
    public void Reconnect()
    {
        GrpcChannel old;
        lock (_gate)
        {
            old = _channel;
            (_channel, _clients) = Open(ServerAddress);
        }

        old.Dispose();
    }

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct) =>
        await C.Auth.StatusAsync(new Empty(), cancellationToken: ct);

    public async Task<LoginReply> LoginAsync(string userName, string password, bool remember, CancellationToken ct) =>
        await C.Auth.LoginAsync(new LoginRequest { UserName = userName, Password = password, Remember = remember }, cancellationToken: ct);

    public async Task<LoginReply> CreateFirstAdminAsync(string userName, string password, string? setupCode, bool remember, CancellationToken ct) =>
        await C.Auth.CreateFirstAdminAsync(
            new CreateFirstAdminRequest { UserName = userName, Password = password, SetupCode = setupCode ?? "", Remember = remember },
            cancellationToken: ct);

    public async Task LogoutAsync(CancellationToken ct) => await C.Auth.LogoutAsync(new Empty(), cancellationToken: ct);

    public async Task<UserInfo> GetCurrentUserAsync(CancellationToken ct) => await C.Auth.MeAsync(new Empty(), cancellationToken: ct);

    public async Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct) =>
        (await C.Users.ListAsync(new Empty(), cancellationToken: ct)).Users;

    public async Task<UserInfo> AddUserAsync(string userName, string password, UserRole role, CancellationToken ct) =>
        await C.Users.AddAsync(new AddUserRequest { UserName = userName, Password = password, Role = role }, cancellationToken: ct);

    public async Task<UserInfo> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct) =>
        await C.Users.UpdateAsync(request, cancellationToken: ct);

    public async Task DeleteUserAsync(string id, CancellationToken ct) =>
        await C.Users.DeleteAsync(new UserIdRequest { Id = id }, cancellationToken: ct);

    public async Task<AuditList> ListAuditAsync(int limit, CancellationToken ct) =>
        await C.Audit.ListAsync(new ListAuditRequest { Limit = limit }, cancellationToken: ct);

    public void Dispose() => _channel.Dispose();

    private (GrpcChannel Channel, Clients Clients) Open(string address)
    {
        var (channel, invoker) = OadmChannel.Create(new Uri(address), _pinning, () => _token, _handler);
        return (channel, new Clients(invoker.Intercept(new SessionWatcher(this))));
    }

    private void OnUnauthenticated(string method)
    {
        if (!method.StartsWith("/oadm.v1.AuthService/", StringComparison.Ordinal) && _token is not null)
        {
            SessionEnded?.Invoke(this, EventArgs.Empty);
        }
    }

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
        ReadAll(C.Devices.Watch(new WatchDevicesRequest { SnapshotEndMarker = true }, cancellationToken: ct), ct);

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

    public async Task<string> ProbeAddressAsync(string address, CancellationToken ct) =>
        (await C.Discovery.ProbeAddressAsync(new ProbeAddressRequest { Address = address }, cancellationToken: ct)).SessionId;

    public IAsyncEnumerable<DiscoveredDevice> WatchDiscoveredAsync(string sessionId, CancellationToken ct) =>
        ReadAll(C.Discovery.WatchDiscovered(new DiscoverySession { SessionId = sessionId }, cancellationToken: ct), ct);

    public async Task StopDiscoveryAsync(string sessionId, CancellationToken ct) =>
        await C.Discovery.StopAsync(new DiscoverySession { SessionId = sessionId }, cancellationToken: ct);

    public async Task StopScanAsync(string sessionId, CancellationToken ct) =>
        await C.Discovery.StopScanAsync(new DiscoverySession { SessionId = sessionId }, cancellationToken: ct);

    public async Task<CommitReply> CommitAddAsync(CommitRequest request, CancellationToken ct) =>
        await C.AddDevices.CommitAsync(request, cancellationToken: ct);

    public async Task<DiscoveredDevice> RetryAuthAsync(RetryAuthRequest request, CancellationToken ct) =>
        await C.AddDevices.RetryAuthAsync(request, cancellationToken: ct);

    public async Task<IReadOnlyList<TaskPluginInfo>> ListTaskPluginsAsync(CancellationToken ct) =>
        (await C.Tasks.ListTaskPluginsAsync(new ListTaskPluginsRequest { Compact = true }, cancellationToken: ct)).Plugins;

    public async Task<IReadOnlyList<string>> RunTaskAsync(string pluginId, IReadOnlyCollection<string> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        var request = new RunTaskRequest { PluginId = pluginId, PayloadJson = payloadJson ?? "", Owner = owner };
        request.DeviceIds.AddRange(deviceIds);
        RunTaskReply reply = await C.Tasks.RunAsync(request, cancellationToken: ct);
#pragma warning disable CS0612 // task_id is the deprecated single id of older servers.
        return reply.TaskIds.Count > 0 ? reply.TaskIds : [reply.TaskId];
#pragma warning restore CS0612
    }

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken ct) =>
        (await C.Tasks.ListAsync(new ListTasksRequest { Limit = Oadm.Client.Tasks.TaskStore.MaxTasks }, cancellationToken: ct)).Tasks;

    public IAsyncEnumerable<TaskChanged> WatchTasksAsync(CancellationToken ct) =>
        ReadAll(C.Tasks.Watch(new WatchTasksRequest { SnapshotLimit = Oadm.Client.Tasks.TaskStore.MaxTasks, SnapshotEndMarker = true }, cancellationToken: ct), ct);

    public async Task CancelTaskAsync(string taskId, CancellationToken ct) =>
        await C.Tasks.CancelAsync(new TaskIdRequest { TaskId = taskId }, cancellationToken: ct);

    public async Task DeleteTaskAsync(string taskId, CancellationToken ct) =>
        await C.Tasks.DeleteAsync(new TaskIdRequest { TaskId = taskId }, cancellationToken: ct);

    public async Task<int> DeleteAllTasksAsync(CancellationToken ct) =>
        (await C.Tasks.DeleteAllAsync(new Empty(), cancellationToken: ct)).Deleted;

    public async Task<IReadOnlyList<TaskLogEntry>> GetTaskLogAsync(string taskId, CancellationToken ct) =>
        (await C.Tasks.GetLogAsync(new TaskIdRequest { TaskId = taskId }, cancellationToken: ct)).Entries;

    public async Task<string?> QueryTaskPluginAsync(string pluginId, string deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        var reply = await C.Tasks.QueryAsync(
            new TaskQueryRequest { PluginId = pluginId, DeviceId = deviceId, Method = method, PayloadJson = payloadJson ?? "" },
            cancellationToken: ct);
        return string.IsNullOrEmpty(reply.PayloadJson) ? null : reply.PayloadJson;
    }

    public async Task<UploadedFileInfo> UploadFileAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, UploadChunkSize, useAsync: true);
        long size = file.Length;
        using AsyncClientStreamingCall<UploadChunk, UploadedFileInfo> call = C.Files.Upload(cancellationToken: ct);
        await call.RequestStream.WriteAsync(
            new UploadChunk { Header = new UploadHeader { Name = Path.GetFileName(path), Size = size } }, ct);
        progress?.Report(0);

        byte[] buffer = new byte[UploadChunkSize];
        long sent = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        {
            await call.RequestStream.WriteAsync(new UploadChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }, ct);
            sent += read;
            progress?.Report(size == 0 ? 1 : (double)sent / size);
        }

        await call.RequestStream.CompleteAsync();
        UploadedFileInfo info = await call.ResponseAsync;
        progress?.Report(1);
        return info;
    }

    public async Task<ServerSettings> GetSettingsAsync(CancellationToken ct) =>
        await C.Settings.GetAsync(new Empty(), cancellationToken: ct);

    public async Task<ServerSettings> SetSettingsAsync(ServerSettings settings, CancellationToken ct) =>
        await C.Settings.SetAsync(settings, cancellationToken: ct);

    public async Task<IReadOnlyList<CredentialEntry>> ListCredentialsAsync(CancellationToken ct) =>
        (await C.Settings.ListCredentialsAsync(new Empty(), cancellationToken: ct)).Entries;

    public async Task<CredentialEntry> AddCredentialAsync(string userName, string password, CancellationToken ct) =>
        await C.Settings.AddCredentialAsync(new AddCredentialRequest { UserName = userName, Password = password }, cancellationToken: ct);

    public async Task RemoveCredentialAsync(string id, CancellationToken ct) =>
        await C.Settings.RemoveCredentialAsync(new CredentialEntryId { Id = id }, cancellationToken: ct);

    public async Task<string> RevealCredentialAsync(string id, CancellationToken ct) =>
        (await C.Settings.RevealCredentialAsync(new CredentialEntryId { Id = id }, cancellationToken: ct)).Password;

    public IAsyncEnumerable<LiveViewFrame> WatchLiveViewAsync(LiveViewRequest request, CancellationToken ct) =>
        ReadAll(C.LiveView.Watch(request, cancellationToken: ct), ct);

    public async Task<IReadOnlyList<LiveViewSource>> ListLiveViewSourcesAsync(string deviceId, CancellationToken ct) =>
        (await C.LiveView.ListSourcesAsync(new LiveViewSourcesRequest { DeviceId = deviceId }, cancellationToken: ct)).Sources;

    public async Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct) =>
        (await C.Plugins.ListCorePluginsAsync(new Empty(), cancellationToken: ct)).Plugins;

    public async Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        var reply = await C.Plugins.InvokeAsync(
            new InvokeRequest { PluginId = pluginId, Method = method, PayloadJson = payloadJson ?? "" },
            cancellationToken: ct);
        return string.IsNullOrEmpty(reply.PayloadJson) ? null : reply.PayloadJson;
    }

    public IAsyncEnumerable<PluginEvent> WatchCorePluginAsync(string pluginId, CancellationToken ct) =>
        ReadAll(C.Plugins.Watch(new WatchPluginRequest { PluginId = pluginId }, cancellationToken: ct), ct);

    private static DeviceIds ToIds(IEnumerable<string> ids)
    {
        var message = new DeviceIds();
        message.Ids.AddRange(ids);
        return message;
    }

    private async IAsyncEnumerable<T> ReadAll<T>(AsyncServerStreamingCall<T> call, [EnumeratorCancellation] CancellationToken ct)
    {
        using (call)
        {
            IAsyncEnumerator<T> items = call.ResponseStream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    try
                    {
                        if (!await items.MoveNextAsync())
                        {
                            break;
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
                    {
                        OnUnauthenticated("stream");
                        throw;
                    }

                    yield return items.Current;
                }
            }
            finally
            {
                await items.DisposeAsync();
            }
        }
    }

    /// <summary>"localhost:5080" -> "https://localhost:5080" (TLS); explicit http:// stays (no TLS, development).</summary>
    internal static string Normalize(string address) =>
        OadmChannel.NormalizeAddress(address, "https://" + Infrastructure.ClientSettings.DefaultServerAddress).ToString().TrimEnd('/');

    /// <summary>Reports UNAUTHENTICATED answers of unary calls (session ended).</summary>
    private sealed class SessionWatcher(GrpcOadmApi owner) : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            AsyncUnaryCall<TResponse> call = continuation(request, context);
            return new AsyncUnaryCall<TResponse>(Watch(call.ResponseAsync, context.Method.FullName), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            AsyncClientStreamingCall<TRequest, TResponse> call = continuation(context);
            return new AsyncClientStreamingCall<TRequest, TResponse>(call.RequestStream, Watch(call.ResponseAsync, context.Method.FullName),
                call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
        }

        private async Task<TResponse> Watch<TResponse>(Task<TResponse> response, string method)
        {
            try
            {
                return await response.ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                owner.OnUnauthenticated(method);
                throw;
            }
        }
    }

    private sealed class Clients(CallInvoker channel)
    {
        public AuthService.AuthServiceClient Auth { get; } = new(channel);
        public UserService.UserServiceClient Users { get; } = new(channel);
        public AuditService.AuditServiceClient Audit { get; } = new(channel);
        public DeviceService.DeviceServiceClient Devices { get; } = new(channel);
        public DiscoveryService.DiscoveryServiceClient Discovery { get; } = new(channel);
        public AddDevicesService.AddDevicesServiceClient AddDevices { get; } = new(channel);
        public TaskService.TaskServiceClient Tasks { get; } = new(channel);
        public SettingsService.SettingsServiceClient Settings { get; } = new(channel);
        public PluginService.PluginServiceClient Plugins { get; } = new(channel);
        public FileService.FileServiceClient Files { get; } = new(channel);
        public LiveViewService.LiveViewServiceClient LiveView { get; } = new(channel);
    }
}
