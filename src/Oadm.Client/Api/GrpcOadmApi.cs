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

    public IAsyncEnumerable<UserInfo> WatchSessionAsync(CancellationToken ct) =>
        ReadAll(C.Auth.WatchSession(new Empty(), cancellationToken: ct), ct);

    public async Task<UserInfo> AddUserAsync(string userName, string password, string roleId, CancellationToken ct) =>
        await C.Users.AddAsync(new AddUserRequest { UserName = userName, Password = password, RoleId = roleId }, cancellationToken: ct);

    public async Task<RoleMatrix> GetRoleMatrixAsync(CancellationToken ct) =>
        await C.Roles.GetMatrixAsync(new Empty(), cancellationToken: ct);

    public async Task<RoleMatrix> SaveRolesAsync(IReadOnlyList<RoleChange> roles, CancellationToken ct) =>
        await C.Roles.SaveRolesAsync(new SaveRolesRequest { Roles = { roles } }, cancellationToken: ct);

    public async Task<RoleMatrix> DeleteRoleAsync(string id, CancellationToken ct) =>
        await C.Roles.DeleteRoleAsync(new RoleIdRequest { Id = id }, cancellationToken: ct);

    public async Task<MyRole> GetMyRoleAsync(CancellationToken ct) =>
        await C.Roles.MineAsync(new Empty(), cancellationToken: ct);

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

    public async Task<IReadOnlyList<DeviceTag>> ListTagsAsync(CancellationToken ct) =>
        (await C.Tags.ListAsync(new Empty(), cancellationToken: ct)).Tags;

    public IAsyncEnumerable<TagList> WatchTagsAsync(CancellationToken ct) =>
        ReadAll(C.Tags.Watch(new Empty(), cancellationToken: ct), ct);

    public async Task<AddWaitingReply> AddWaitingAsync(IReadOnlyCollection<WaitingLine> lines, string source, CancellationToken ct)
    {
        var request = new AddWaitingRequest { Source = source };
        request.Lines.AddRange(lines);
        return await C.Waiting.AddAsync(request, cancellationToken: ct);
    }

    public async Task<WaitingList> ListWaitingAsync(string? after, int limit, CancellationToken ct) =>
        await C.Waiting.ListAsync(new ListWaitingRequest { After = after ?? "", Limit = limit }, cancellationToken: ct);

    public IAsyncEnumerable<WaitingSummary> WatchWaitingAsync(CancellationToken ct) =>
        ReadAll(C.Waiting.Watch(new Empty(), cancellationToken: ct), ct);

    public async Task<WaitingSummary> RemoveWaitingAsync(IReadOnlyCollection<string> ids, bool all, CancellationToken ct) =>
        await C.Waiting.RemoveAsync(WaitingIdsOf(ids, all), cancellationToken: ct);

    public async Task<WaitingSummary> TryWaitingNowAsync(IReadOnlyCollection<string> ids, bool all, CancellationToken ct) =>
        await C.Waiting.TryNowAsync(WaitingIdsOf(ids, all), cancellationToken: ct);

    private static WaitingIds WaitingIdsOf(IReadOnlyCollection<string> ids, bool all)
    {
        var message = new WaitingIds { All = all };
        if (!all)
        {
            message.Ids.AddRange(ids);
        }

        return message;
    }

    public async Task<DeviceTag> CreateTagAsync(string name, TagColor color, CancellationToken ct) =>
        await C.Tags.CreateAsync(new CreateTagRequest { Name = name, Color = color }, cancellationToken: ct);

    public async Task<DeviceTag> UpdateTagAsync(string name, string? newName, TagColor color, CancellationToken ct) =>
        await C.Tags.UpdateAsync(new UpdateTagRequest { Name = name, NewName = newName ?? "", Color = color }, cancellationToken: ct);

    public async Task<int> DeleteTagAsync(string name, CancellationToken ct) =>
        (await C.Tags.DeleteAsync(new TagName { Name = name }, cancellationToken: ct)).DevicesChanged;

    public async Task<SetDeviceTagsReply> SetDeviceTagsAsync(IReadOnlyCollection<string> deviceIds, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken ct)
    {
        var request = new SetDeviceTagsRequest();
        request.DeviceIds.AddRange(deviceIds);
        request.Add.AddRange(add);
        request.Remove.AddRange(remove);
        return await C.Tags.SetDeviceTagsAsync(request, cancellationToken: ct);
    }

    public async Task<EventCatalog> GetEventCatalogAsync(CancellationToken ct) =>
        await C.Events.GetCatalogAsync(new Empty(), cancellationToken: ct);

    public async Task<IReadOnlyList<RuleSummary>> ListRulesAsync(CancellationToken ct) =>
        (await C.Events.ListRulesAsync(new Empty(), cancellationToken: ct)).Rules;

    public IAsyncEnumerable<RuleList> WatchRulesAsync(CancellationToken ct) =>
        ReadAll(C.Events.WatchRules(new Empty(), cancellationToken: ct), ct);

    public async Task<RuleDetail> GetRuleAsync(string id, CancellationToken ct) =>
        await C.Events.GetRuleAsync(new RuleId { Id = id }, cancellationToken: ct);

    public async Task<RuleDetail> SaveRuleAsync(string definitionJson, CancellationToken ct) =>
        await C.Events.SaveRuleAsync(new SaveRuleRequest { DefinitionJson = definitionJson }, cancellationToken: ct);

    public async Task DeleteRuleAsync(string id, CancellationToken ct) =>
        await C.Events.DeleteRuleAsync(new RuleId { Id = id }, cancellationToken: ct);

    public async Task<RuleSummary> SetRuleEnabledAsync(string id, bool enabled, CancellationToken ct) =>
        await C.Events.SetRuleEnabledAsync(new SetRuleEnabledRequest { Id = id, Enabled = enabled }, cancellationToken: ct);

    public async Task<RuleDetail> DuplicateRuleAsync(string id, CancellationToken ct) =>
        await C.Events.DuplicateRuleAsync(new RuleId { Id = id }, cancellationToken: ct);

    public async Task<IReadOnlyList<ActionChoiceEntry>> ListActionChoicesAsync(string actionId, string fieldKey, CancellationToken ct) =>
        (await C.Events.ListActionChoicesAsync(new ActionChoicesRequest { ActionId = actionId, FieldKey = fieldKey }, cancellationToken: ct)).Choices;

    public async Task<EventLogPage> ListEventLogAsync(int limit, string? ruleId, CancellationToken ct) =>
        await C.Events.ListLogAsync(new ListEventLogRequest { Limit = limit, RuleId = ruleId ?? string.Empty }, cancellationToken: ct);

    public IAsyncEnumerable<EventLogPage> WatchEventLogAsync(CancellationToken ct) =>
        ReadAll(C.Events.WatchLog(new Empty(), cancellationToken: ct), ct);

    public async Task<int> ClearEventLogAsync(CancellationToken ct) =>
        (await C.Events.ClearLogAsync(new Empty(), cancellationToken: ct)).Deleted;

    public async Task<TestActionReply> TestEventActionAsync(TestActionRequest request, CancellationToken ct) =>
        await C.Events.TestActionAsync(request, cancellationToken: ct);

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

    public async Task<DeviceLogInReply> LogInDevicesAsync(IReadOnlyCollection<string> deviceIds, string userName, string password, bool saveToCredentialList, CancellationToken ct)
    {
        var request = new DeviceLogInRequest { UserName = userName, Password = password, SaveToCredentialList = saveToCredentialList };
        request.DeviceIds.AddRange(deviceIds);
        return await C.Devices.LogInAsync(request, cancellationToken: ct);
    }

    public async Task<string> GetCredentialUserNameAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct) =>
        (await C.Devices.GetCredentialUserNameAsync(ToIds(deviceIds), cancellationToken: ct)).UserName;

    public async Task<SetFirstPasswordReply> SetFirstPasswordAsync(IReadOnlyCollection<string> deviceIds, string password, CancellationToken ct)
    {
        var request = new SetFirstPasswordRequest { Password = password };
        request.DeviceIds.AddRange(deviceIds);
        return await C.Devices.SetFirstPasswordAsync(request, cancellationToken: ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPassphrasePoliciesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        var reply = await C.Devices.GetPassphrasePoliciesAsync(ToIds(deviceIds), cancellationToken: ct);
        return reply.Policies.ToDictionary(p => p.DeviceId, p => p.Policy);
    }

    public async Task<string> GetWebUiUrlAsync(string deviceId, CancellationToken ct) =>
        (await C.Devices.GetWebUiUrlAsync(new DeviceId { Id = deviceId }, cancellationToken: ct)).Url;

    public async Task<string> StartZeroConfAsync(CancellationToken ct) =>
        (await C.Discovery.StartZeroConfAsync(new Empty(), cancellationToken: ct)).SessionId;

    public async Task<string> StartRangeScanAsync(string firstAddress, string lastAddress, CancellationToken ct) =>
        (await C.Discovery.StartRangeScanAsync(new RangeScanRequest { From = firstAddress, To = lastAddress }, cancellationToken: ct)).SessionId;

    public async Task<string> ProbeAddressAsync(string address, CancellationToken ct) =>
        (await C.Discovery.ProbeAddressAsync(new ProbeAddressRequest { Address = address }, cancellationToken: ct)).SessionId;

    // The overloads with a login are the import's lines: a short first contact (import_line).
    public async Task<string> ProbeAddressAsync(string address, string? userName, string? password, CancellationToken ct) =>
        (await C.Discovery.ProbeAddressAsync(
            new ProbeAddressRequest { Address = address, UserName = userName ?? "", Password = password ?? "", ImportLine = true },
            cancellationToken: ct)).SessionId;

    public async Task<string> ProbeAddressAsync(string address, string? userName, string? password, string expectedSerial, CancellationToken ct) =>
        (await C.Discovery.ProbeAddressAsync(
            new ProbeAddressRequest { Address = address, UserName = userName ?? "", Password = password ?? "", ExpectedSerial = expectedSerial, ImportLine = true },
            cancellationToken: ct)).SessionId;

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

    public async Task<OpticsState> GetOpticsAsync(string deviceId, int sensor, CancellationToken ct) =>
        await C.LiveView.GetOpticsAsync(new OpticsRequest { DeviceId = deviceId, Sensor = sensor }, cancellationToken: ct);

    public async Task<OpticsState> SetOpticsAsync(SetOpticsRequest request, CancellationToken ct) =>
        await C.LiveView.SetOpticsAsync(request, cancellationToken: ct);

    public async Task<PtzState> GetPtzAsync(string deviceId, int camera, CancellationToken ct) =>
        await C.LiveView.GetPtzAsync(new PtzRequest { DeviceId = deviceId, Camera = camera }, cancellationToken: ct);

    public async Task MovePtzAsync(MovePtzRequest request, CancellationToken ct) =>
        await C.LiveView.MovePtzAsync(request, cancellationToken: ct);

    public async Task<RtspPortReply> GetRtspPortAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct) =>
        await C.LiveView.GetRtspPortAsync(new RtspPortRequest { DeviceIds = { deviceIds } }, cancellationToken: ct);

    public async Task<RtspPortReply> SetRtspPortAsync(IReadOnlyCollection<string> deviceIds, int port, CancellationToken ct) =>
        await C.LiveView.SetRtspPortAsync(new SetRtspPortRequest { DeviceIds = { deviceIds }, Port = port }, cancellationToken: ct);

    public async Task<OpenDeviceWebReply> OpenDeviceWebAsync(string deviceId, CancellationToken ct) =>
        await C.DeviceWeb.OpenAsync(new OpenDeviceWebRequest { DeviceId = deviceId }, cancellationToken: ct);

    public IDeviceWebCall ExchangeDeviceWeb(CancellationToken ct) => new GrpcDeviceWebCall(C.DeviceWeb.Exchange(cancellationToken: ct));

    public async Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct) =>
        (await C.Plugins.ListCorePluginsAsync(new Empty(), cancellationToken: ct)).Plugins;

    public async Task<PluginPackageList> ListPluginPackagesAsync(CancellationToken ct) =>
        await C.Plugins.ListPackagesAsync(new Empty(), cancellationToken: ct);

    public async Task<PluginPackageList> SetPluginPackageEnabledAsync(string packageId, bool enabled, CancellationToken ct) =>
        await C.Plugins.SetPackageEnabledAsync(new SetPackageEnabledRequest { Id = packageId, Enabled = enabled }, cancellationToken: ct);

    public async Task<PluginPackageList> SetPluginLicenseAsync(string packageId, string license, CancellationToken ct) =>
        await C.Plugins.SetPackageLicenseAsync(new SetPackageLicenseRequest { Id = packageId, License = license }, cancellationToken: ct);

    public async Task<PluginPackageList> RemovePluginLicenseAsync(string packageId, CancellationToken ct) =>
        await C.Plugins.RemovePackageLicenseAsync(new PackageIdRequest { Id = packageId }, cancellationToken: ct);

    public async Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        var reply = await C.Plugins.InvokeAsync(
            new InvokeRequest { PluginId = pluginId, Method = method, PayloadJson = payloadJson ?? "" },
            cancellationToken: ct);
        return string.IsNullOrEmpty(reply.PayloadJson) ? null : reply.PayloadJson;
    }

    public IAsyncEnumerable<PluginEvent> WatchCorePluginAsync(string pluginId, CancellationToken ct) =>
        ReadAll(C.Plugins.Watch(new WatchPluginRequest { PluginId = pluginId }, cancellationToken: ct), ct);

    // The calls live as long as the returned stream (not ct): the caller reads it with its own token.
    public Task<Oadm.Sdk.Plugins.PluginDownload> DownloadCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct) =>
        OpenDownloadAsync(C.Plugins.Download(new InvokeRequest { PluginId = pluginId, Method = method, PayloadJson = payloadJson ?? "" }, cancellationToken: CancellationToken.None), ct);

    public async Task DeleteUploadAsync(string fileId, CancellationToken ct) =>
        await C.Files.DeleteAsync(new FileIdRequest { FileId = fileId }, cancellationToken: ct);

    public async Task<BackupState> GetBackupStateAsync(CancellationToken ct) => await C.Backup.GetStateAsync(new Empty(), cancellationToken: ct);

    public async Task<BackupState> SetBackupScheduleAsync(BackupSchedule schedule, CancellationToken ct) => await C.Backup.SetScheduleAsync(schedule, cancellationToken: ct);

    public Task<Oadm.Sdk.Plugins.PluginDownload> CreateBackupAsync(string password, CancellationToken ct) =>
        OpenDownloadAsync(C.Backup.Create(new CreateBackupRequest { Password = password }, cancellationToken: CancellationToken.None), ct);

    public Task<Oadm.Sdk.Plugins.PluginDownload> DownloadBackupAsync(string name, CancellationToken ct) =>
        OpenDownloadAsync(C.Backup.Download(new BackupName { Name = name }, cancellationToken: CancellationToken.None), ct);

    public async Task<BackupState> DeleteBackupAsync(string name, CancellationToken ct) => await C.Backup.DeleteAsync(new BackupName { Name = name }, cancellationToken: ct);

    public async Task<BackupInfo> InspectBackupAsync(RestoreRequest request, CancellationToken ct) => await C.Backup.InspectAsync(request, cancellationToken: ct);

    public async Task<RestoreReply> RestoreBackupAsync(RestoreRequest request, CancellationToken ct) => await C.Backup.RestoreAsync(request, cancellationToken: ct);

    /// <summary>Reads the first message (name and length) of a file stream; the rest is the returned stream.</summary>
    private async Task<Oadm.Sdk.Plugins.PluginDownload> OpenDownloadAsync(AsyncServerStreamingCall<DownloadChunk> call, CancellationToken ct)
    {
        try
        {
            if (!await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "The server sent no file."));
            }

            DownloadChunk header = call.ResponseStream.Current;
            return new Oadm.Sdk.Plugins.PluginDownload(
                new DownloadChunkStream(call),
                string.IsNullOrWhiteSpace(header.FileName) ? "download" : header.FileName,
                header.Length >= 0 ? header.Length : null);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            call.Dispose();
            OnUnauthenticated("stream");
            throw;
        }
        catch
        {
            call.Dispose();
            throw;
        }
    }

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
        public RoleService.RoleServiceClient Roles { get; } = new(channel);
        public AuditService.AuditServiceClient Audit { get; } = new(channel);
        public DeviceService.DeviceServiceClient Devices { get; } = new(channel);
        public DiscoveryService.DiscoveryServiceClient Discovery { get; } = new(channel);
        public AddDevicesService.AddDevicesServiceClient AddDevices { get; } = new(channel);
        public TaskService.TaskServiceClient Tasks { get; } = new(channel);
        public SettingsService.SettingsServiceClient Settings { get; } = new(channel);
        public PluginService.PluginServiceClient Plugins { get; } = new(channel);
        public FileService.FileServiceClient Files { get; } = new(channel);
        public LiveViewService.LiveViewServiceClient LiveView { get; } = new(channel);
        public DeviceWebService.DeviceWebServiceClient DeviceWeb { get; } = new(channel);
        public TagService.TagServiceClient Tags { get; } = new(channel);
        public WaitingDeviceService.WaitingDeviceServiceClient Waiting { get; } = new(channel);
        public EventService.EventServiceClient Events { get; } = new(channel);
        public BackupService.BackupServiceClient Backup { get; } = new(channel);
    }
}
