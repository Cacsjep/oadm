using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// In-process stand-in for the server: realistic sample devices and tasks, simulated discovery,
/// automatic login results of the add page, credential list and the Restart task. Start the client with <c>--fake</c> to use it.
/// </summary>
public sealed partial class FakeOadmApi : IOadmApi, IDisposable
{
    public const string RestartPluginId = "oadm.restart";
    public const string IdentifyPluginId = "oadm.identify";

    private readonly Lock _gate = new();
    private readonly List<Device> _devices = [];
    private readonly List<TaskInfo> _tasks = [];
    private readonly Dictionary<string, List<TaskLogEntry>> _taskLogs = [];
    private readonly Dictionary<string, UploadedFileInfo> _uploads = [];
    private readonly Dictionary<string, FakeJob> _jobs = [];
    private readonly Dictionary<string, FakeSession> _sessions = [];
    private readonly List<CredentialEntry> _credentials = [];
    private readonly Dictionary<string, string> _credentialPasswords = [];
    private readonly Broadcast<DeviceChanged> _deviceEvents = new();
    private readonly Broadcast<TaskChanged> _taskEvents = new();
    private readonly List<TaskPluginInfo> _pluginTemplates;
    private readonly TimeSpan _tick;
    private readonly CancellationTokenSource _cts = new();
    private ServerSettings _settings = new()
    {
        PollingIntervalSeconds = 60,
        FullRefreshMinutes = 10,
        ScanParallelism = 32,
        ScanTimeoutMs = 1500,
        ZeroConfSeconds = 30,
        MaxParallelTasksPerPlugin = 16,
        ServerName = "acs",
        ListenUrl = "https://0.0.0.0:5080",
        UseHostName = false,
        ServerVersion = FakeServerVersion,
    };
    private bool _online = true;

    /// <summary>Version the fake server reports (About and licenses).</summary>
    public const string FakeServerVersion = "0.0.0-fake";

    /// <param name="tick">Simulation step. Tests pass a few milliseconds.</param>
    /// <param name="seedSampleData">False starts with an empty device and task table.</param>
    public FakeOadmApi(TimeSpan? tick = null, bool seedSampleData = true)
    {
        _tick = tick ?? TimeSpan.FromMilliseconds(250);
        _pluginTemplates =
        [
            new TaskPluginInfo { Id = RestartPluginId, DisplayName = "Restart", IconKey = "restart", ShowInToolbar = true, Group = "Maintenance" },
            new TaskPluginInfo { Id = IdentifyPluginId, DisplayName = "Identify (flash LED)", IconKey = "identify", Group = "General" },
        ];
        if (seedSampleData)
        {
            Seed();
            AddCredentialLocked("root", "Fake-root-pass1");
            AddCredentialLocked("operator", "Fake-operator-pass1");
        }

        _ = Task.Run(() => SimulationLoopAsync(_cts.Token));
    }

    public string ServerAddress => "fake";

    /// <summary>Task plugins as returned by ListTaskPlugins. Tests may add entries.</summary>
    public IList<TaskPluginInfo> PluginTemplates => _pluginTemplates;

    /// <summary>Answers QueryTaskPlugin (pluginId, deviceId, method, payload); null: UNIMPLEMENTED like a plugin without queries.</summary>
    public Func<string, string, string, string?, string?>? QueryHandler { get; set; }

    /// <summary>Files received by UploadFileAsync, by id.</summary>
    public IReadOnlyDictionary<string, UploadedFileInfo> Uploads
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, UploadedFileInfo>(_uploads);
            }
        }
    }

    public void SetServerAddress(string address)
    {
        // nothing to reconnect
    }

    /// <summary>Simulates the server going away (calls fail with Unavailable, streams break) and coming back.</summary>
    public void SetOnline(bool online)
    {
        lock (_gate)
        {
            _online = online;
        }

        if (!online)
        {
            var error = new RpcException(new Status(StatusCode.Unavailable, "fake server offline"));
            _deviceEvents.Fail(error);
            _taskEvents.Fail(error);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        lock (_gate)
        {
            foreach (FakeSession session in _sessions.Values)
            {
                session.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- devices

    public Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<Device>>(_devices.Select(d => d.Clone()).ToList());
        }
    }

    public async IAsyncEnumerable<DeviceChanged> WatchDevicesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<DeviceChanged> channel;
        List<Device> snapshot;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _deviceEvents.Subscribe();
            snapshot = _devices.Select(d => d.Clone()).ToList();
        }

        try
        {
            foreach (Device device in snapshot)
            {
                yield return new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = device };
            }

            yield return new DeviceChanged { Kind = DeviceChanged.Types.Kind.SnapshotEnd };

            await foreach (DeviceChanged change in channel.Reader.ReadAllAsync(ct))
            {
                yield return change;
            }
        }
        finally
        {
            _deviceEvents.Unsubscribe(channel);
        }
    }

    public Task RemoveDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            foreach (string id in deviceIds)
            {
                Device? device = _devices.Find(d => d.Id == id);
                if (device is not null)
                {
                    _devices.Remove(device);
                    _deviceEvents.Publish(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Removed, Device = new Device { Id = id } });
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task RefreshDevicesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            foreach (Device device in _devices.Where(d => deviceIds.Contains(d.Id)))
            {
                if (device.Status != DeviceStatus.Unreachable)
                {
                    device.LastSeen = Timestamp.FromDateTime(DateTime.UtcNow);
                }

                PublishUpdated(device);
            }
        }

        return Task.CompletedTask;
    }

    public Task SetCredentialsAsync(IReadOnlyCollection<string> deviceIds, string userName, string password, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            foreach (Device device in _devices.Where(d => deviceIds.Contains(d.Id)))
            {
                device.HasCredentials = password.Length > 0;
                if (device.Status == DeviceStatus.CredentialsRequired && IsAcceptedPassword(password))
                {
                    device.Status = DeviceStatus.Ok;
                }

                PublishUpdated(device);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string> GetWebUiUrlAsync(string deviceId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            Device device = _devices.Find(d => d.Id == deviceId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "device not found"));
            string host = device.UseHostName && device.HostName.Length > 0 ? device.HostName : device.Address;
            return Task.FromResult($"{device.Scheme}://{host}/");
        }
    }

    // ---------------------------------------------------------------- discovery

    public Task<string> StartZeroConfAsync(CancellationToken ct) => Task.FromResult(NewSession(FakeSessionKind.Scan, null, null, null));

    public Task<string> StartRangeScanAsync(string firstAddress, string lastAddress, CancellationToken ct)
    {
        if (!IPAddress.TryParse(firstAddress, out IPAddress? first) || !IPAddress.TryParse(lastAddress, out IPAddress? last))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid address range"));
        }

        return Task.FromResult(NewSession(FakeSessionKind.Range, first, last, null));
    }

    public Task<string> ProbeAddressAsync(string address, CancellationToken ct)
    {
        string entered = (address ?? "").Trim();
        if (entered.Length == 0 || entered.Contains(' ', StringComparison.Ordinal) || entered.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{entered}' is not a valid IP address or host name."));
        }

        return Task.FromResult(NewSession(FakeSessionKind.Manual, null, null, entered));
    }

    /// <summary>
    /// Like the server: every device first arrives "checking" (auth pending), the automatic login
    /// result follows a moment later. Mixed results: authenticated, login failed, factory default,
    /// unreachable and already added devices.
    /// </summary>
    public async IAsyncEnumerable<DiscoveredDevice> WatchDiscoveredAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        FakeSession session;
        lock (_gate)
        {
            ThrowIfOffline();
            session = _sessions.GetValueOrDefault(sessionId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "unknown discovery session"));
        }

        if (session.Finished)
        {
            // Like the server: a finished session replays its devices (with their current login result) and ends.
            List<DiscoveredDevice> known;
            lock (_gate)
            {
                known = session.Found.Values.Select(d => d.Clone()).ToList();
            }

            foreach (DiscoveredDevice device in known)
            {
                yield return device;
            }

            yield return new DiscoveredDevice { ScanFinished = true, ProgressPercent = 100 };
            yield break;
        }

        List<(DiscoveredDevice Found, DiscoveredDevice Auth)> candidates = BuildDiscoveryCandidates(session);
        if (session.Kind == FakeSessionKind.Scan)
        {
            foreach ((DiscoveredDevice found, _) in candidates)
            {
                await Task.Delay(_tick, ct);
                yield return Remember(session, found);
            }

            foreach ((_, DiscoveredDevice auth) in candidates)
            {
                await Task.Delay(_tick, ct);
                yield return Remember(session, auth);
            }

            // mDNS browses until the zero-conf time limit or Stop; later login results (a retry on another
            // device) still arrive meanwhile.
            session.ScanEnd.CancelAfter(ZeroConfDuration ?? TimeSpan.FromSeconds(Math.Max(1, _settings.ZeroConfSeconds)));
            using var browsing = CancellationTokenSource.CreateLinkedTokenSource(ct, session.ScanEnd.Token);
            while (true)
            {
                DiscoveredDevice? update = null;
                try
                {
                    update = await session.Updates.Reader.ReadAsync(browsing.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // time limit or Stop
                }

                if (update is null)
                {
                    break;
                }

                yield return update;
            }

            session.Finished = true;
            yield return new DiscoveredDevice { ScanFinished = true, ProgressPercent = 100 };
        }
        else
        {
            const int steps = 10;
            int reached = 0;
            for (int step = 1; step <= steps && !session.ScanEnd.IsCancellationRequested; step++)
            {
                await Task.Delay(_tick, ct);
                int percent = step * 100 / steps;
                reached = percent;
                foreach ((DiscoveredDevice found, _) in candidates.Where((_, i) => (i * steps / Math.Max(1, candidates.Count)) + 1 == step))
                {
                    found.ProgressPercent = percent;
                    yield return Remember(session, found);
                }

                yield return new DiscoveredDevice { ProgressPercent = percent };
            }

            yield return new DiscoveredDevice { ScanFinished = true, ProgressPercent = reached };
            foreach ((_, DiscoveredDevice auth) in candidates.Where(c => session.Found.ContainsKey(c.Auth.DiscoveredId)))
            {
                await Task.Delay(_tick, ct);
                yield return Remember(session, auth);
            }

            session.Finished = true;
        }
    }

    /// <summary>Zero-conf time limit of the fake; null = the Discovery.ZeroConfSeconds setting.</summary>
    public TimeSpan? ZeroConfDuration { get; set; }

    public Task StopDiscoveryAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out FakeSession? session))
            {
                session.Stopped = true;
                session.ScanEnd.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    public Task StopScanAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (_sessions.TryGetValue(sessionId, out FakeSession? session))
            {
                session.ScanEnd.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- add devices

    public Task<DiscoveredDevice> RetryAuthAsync(RetryAuthRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            if (string.IsNullOrWhiteSpace(request.UserName) || request.Password.Length == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Enter a user name and a password."));
            }

            FakeSession session = _sessions.GetValueOrDefault(request.SessionId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "unknown discovery session"));
            DiscoveredDevice found = session.Found.GetValueOrDefault(request.DiscoveredId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"Discovered device '{request.DiscoveredId}' is unknown."));
            if (found.Status == DeviceStatus.PasswordNotSet)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "The device has no password yet (factory default); set its first password instead."));
            }

            if (found.AlreadyManaged || _devices.Exists(d => d.Serial == found.Serial))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The device {found.Serial} has already been added."));
            }

            DiscoveredDevice result = found.Clone();
            result.ProgressPercent = 100;
            if (IsAcceptedPassword(request.Password))
            {
                string user = request.UserName.Trim();
                result.AuthState = AuthState.Authenticated;
                result.AuthUserName = user;
                result.AuthDetail = "";
                result.CredentialId = "entered";
                if (request.SaveToCredentialList)
                {
                    CredentialEntry entry = AddCredentialLocked(user, request.Password);
                    result.CredentialId = "list:" + entry.Id;
                }

                // Like the server: the working credential is tried on every other device of the page whose login failed.
                foreach (FakeSession other in request.RelatedSessionIds.Append(request.SessionId).Distinct()
                    .Select(id => _sessions.GetValueOrDefault(id)).OfType<FakeSession>())
                {
                    foreach (DiscoveredDevice failed in other.Found.Values.Where(d => d.AuthState == AuthState.LoginFailed && d.DiscoveredId != result.DiscoveredId).ToList())
                    {
                        DiscoveredDevice updated = failed.Clone();
                        updated.AuthState = AuthState.Authenticated;
                        updated.AuthUserName = user;
                        updated.AuthDetail = "";
                        updated.CredentialId = result.CredentialId;
                        other.Found[updated.DiscoveredId] = updated;
                        other.Updates.Writer.TryWrite(updated.Clone());
                    }
                }
            }
            else
            {
                result.AuthState = AuthState.LoginFailed;
                result.AuthUserName = "";
                result.CredentialId = "";
                result.AuthDetail = "The user name or password is wrong.";
            }

            session.Found[result.DiscoveredId] = result;
            return Task.FromResult(result.Clone());
        }
    }

    public Task<CommitReply> CommitAddAsync(CommitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            FakeSession session = _sessions.GetValueOrDefault(request.SessionId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "unknown discovery session"));
            foreach (string password in request.InitialPasswords.Values.Append(request.InitialRootPassword).Where(p => p.Length > 0))
            {
                if (password.Length > 64 || password.Any(c => c < 0x20 || c > 0x7E))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Password must be 1-64 printable ASCII characters."));
                }
            }

            var reply = new CommitReply();
            DeviceCredentials? forAll = request.Credentials.FirstOrDefault(c => c.DiscoveredId.Length == 0);
            foreach (string id in request.DiscoveredIds)
            {
                if (!session.Found.TryGetValue(id, out DiscoveredDevice? found) || found.AlreadyManaged
                    || _devices.Exists(d => d.Serial == found.Serial))
                {
                    reply.Results.Add(new CommitResult { DiscoveredId = id });
                    continue;
                }

                DeviceCredentials? credentials = request.Credentials.FirstOrDefault(c => c.DiscoveredId == id) ?? forAll;
                string initial = request.InitialPasswords.TryGetValue(id, out string? own) && own.Length > 0 ? own : request.InitialRootPassword;
                DeviceStatus status = found.Status switch
                {
                    DeviceStatus.PasswordNotSet => initial.Length > 0 ? DeviceStatus.Ok : DeviceStatus.PasswordNotSet,
                    _ when credentials is not null => IsAcceptedPassword(credentials.Password) ? DeviceStatus.Ok : DeviceStatus.CredentialsRequired,
                    _ when found.AuthState == AuthState.Authenticated => DeviceStatus.Ok,
                    DeviceStatus.Unreachable => DeviceStatus.Unreachable,
                    _ => DeviceStatus.CredentialsRequired,
                };
                string address = found.EnteredAddress.Length > 0 ? found.EnteredAddress : found.Address;
                Device device = CreateDevice(found.Serial, address, found.Model, "12.11.77", status);
                device.HostName = found.HostName;
                device.UseHostName = _settings.UseHostName && found.HostName.Length > 0; // the server setting, not the request
                device.HasCredentials = credentials is not null || initial.Length > 0 || found.AuthState == AuthState.Authenticated;
                _devices.Add(device);
                reply.DeviceIds.Add(device.Id);
                reply.Results.Add(new CommitResult { DiscoveredId = id, DeviceId = device.Id, Status = status });
                _deviceEvents.Publish(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = device.Clone() });
                found.AlreadyManaged = true;
                found.AuthState = AuthState.AlreadyAdded;
            }

            // Adding devices is not a task: devices just appear and fill in.
            return Task.FromResult(reply);
        }
    }

    private string NewSession(FakeSessionKind kind, IPAddress? from, IPAddress? to, string? entered)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var session = new FakeSession(Guid.NewGuid().ToString("N"), kind, from, to, entered);
            _sessions[session.Id] = session;
            return session.Id;
        }
    }

    private DiscoveredDevice Remember(FakeSession session, DiscoveredDevice message)
    {
        lock (_gate)
        {
            if (session.Found.TryGetValue(message.DiscoveredId, out DiscoveredDevice? known)
                && (known.AuthState is AuthState.AlreadyAdded || (known.AuthState == AuthState.Authenticated && known.CredentialId.Length > 0 && message.AuthState != AuthState.Authenticated)))
            {
                return known.Clone(); // a retry or an add already decided this device
            }

            session.Found[message.DiscoveredId] = message.Clone();
            return message.Clone();
        }
    }

    // ---------------------------------------------------------------- tasks

    public Task<IReadOnlyList<TaskPluginInfo>> ListTaskPluginsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var result = new List<TaskPluginInfo>();
            foreach (TaskPluginInfo template in _pluginTemplates)
            {
                TaskPluginInfo info = template.Clone();
                info.RunnableDeviceIds.Clear();
                info.RunnableDeviceIds.AddRange(_devices.Where(d => d.Status == DeviceStatus.Ok).Select(d => d.Id));
                result.Add(info);
            }

            return Task.FromResult<IReadOnlyList<TaskPluginInfo>>(result);
        }
    }

    /// <summary>Like the server: one task per device, all sharing a batch id.</summary>
    public Task<IReadOnlyList<string>> RunTaskAsync(string pluginId, IReadOnlyCollection<string> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            TaskPluginInfo plugin = _pluginTemplates.Find(p => p.Id == pluginId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"task plugin {pluginId} not found"));
            Action<TaskInfo, int>? onProgress = pluginId == RestartPluginId ? SimulateRestart : null;
            var ids = new List<string>();
            foreach (TaskInfo task in AddBatch(pluginId, FakeTaskName(pluginId, plugin.DisplayName), owner, TaskState.Queued, 0, deviceIds.Distinct()))
            {
                _jobs[task.Id] = new FakeJob(task, pluginId == RestartPluginId ? 4 : 25, onProgress, StepsOf(pluginId));
                ids.Add(task.Id);
            }

            return Task.FromResult<IReadOnlyList<string>>(ids);
        }
    }

    public Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<TaskInfo>>(_tasks.Select(t => t.Clone()).ToList());
        }
    }

    public async IAsyncEnumerable<TaskChanged> WatchTasksAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<TaskChanged> channel;
        List<TaskInfo> snapshot;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _taskEvents.Subscribe();
            snapshot = _tasks.Select(t => t.Clone()).ToList();
        }

        try
        {
            foreach (TaskInfo task in snapshot)
            {
                yield return new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = task };
            }

            yield return new TaskChanged { Kind = TaskChanged.Types.Kind.SnapshotEnd };

            await foreach (TaskChanged change in channel.Reader.ReadAllAsync(ct))
            {
                yield return change;
            }
        }
        finally
        {
            _taskEvents.Unsubscribe(channel);
        }
    }

    public Task CancelTaskAsync(string taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            TaskInfo? task = _tasks.Find(t => t.Id == taskId);
            if (task is not null && task.State is TaskState.Queued or TaskState.Running)
            {
                _jobs.Remove(taskId);
                Finish(task, TaskState.Cancelled, "Cancelled by user");
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            TaskInfo? task = _tasks.Find(t => t.Id == taskId);
            if (task is not null)
            {
                _jobs.Remove(taskId);
                _tasks.Remove(task);
                _taskLogs.Remove(taskId);
                _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Removed, Task = new TaskInfo { Id = taskId } });
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteAllTasksAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            foreach (TaskInfo task in _tasks.Where(t => t.State is TaskState.Queued or TaskState.Running).ToList())
            {
                _jobs.Remove(task.Id);
                Finish(task, TaskState.Cancelled, "Cancelled by user");
            }

            List<TaskInfo> all = [.. _tasks];
            _tasks.Clear();
            foreach (TaskInfo task in all)
            {
                _taskLogs.Remove(task.Id);
                _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Removed, Task = new TaskInfo { Id = task.Id } });
            }

            return Task.FromResult(all.Count);
        }
    }

    // ---------------------------------------------------------------- settings and core plugins

    public Task<IReadOnlyList<TaskLogEntry>> GetTaskLogAsync(string taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (!_tasks.Exists(t => t.Id == taskId))
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"task {taskId} not found"));
            }

            return Task.FromResult<IReadOnlyList<TaskLogEntry>>(
                _taskLogs.TryGetValue(taskId, out List<TaskLogEntry>? log) ? log.Select(e => e.Clone()).ToList() : []);
        }
    }

    public Task<string?> QueryTaskPluginAsync(string pluginId, string deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        Func<string, string, string, string?, string?> handler = QueryHandler
            ?? throw new RpcException(new Status(StatusCode.Unimplemented, $"Task plugin '{pluginId}' does not support queries."));
        return Task.FromResult(handler(pluginId, deviceId, method, payloadJson));
    }

    public async Task<UploadedFileInfo> UploadFileAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, GrpcOadmApi.UploadChunkSize, useAsync: true);
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[GrpcOadmApi.UploadChunkSize];
        long size = file.Length;
        long read = 0;
        int n;
        progress?.Report(0);
        while ((n = await file.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            read += n;
            progress?.Report(size == 0 ? 1 : (double)read / size);
        }

        var info = new UploadedFileInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileName(path),
            Size = read,
            Sha256 = Convert.ToHexString(sha.GetHashAndReset()),
        };
        lock (_gate)
        {
            _uploads[info.Id] = info;
        }

        progress?.Report(1);
        return info;
    }

    public Task<ServerSettings> GetSettingsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(_settings.Clone());
        }
    }

    public Task<ServerSettings> SetSettingsAsync(ServerSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ThrowIfOffline();
            if (settings.PollingIntervalSeconds < 5)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Polling interval must be at least 5 seconds"));
            }

            if (settings.FullRefreshMinutes is < 1 or > 1440)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Full refresh interval must be between 1 and 1440 minutes"));
            }

            if (settings.HasMaxParallelTasksPerPlugin && settings.MaxParallelTasksPerPlugin is < 1 or > 256)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "'Tasks.MaxParallelPerPlugin' must be an integer between 1 and 256."));
            }

            bool useHostName = settings.HasUseHostName ? settings.UseHostName : _settings.UseHostName; // unset keeps it, like the server
            int maxParallel = settings.HasMaxParallelTasksPerPlugin ? settings.MaxParallelTasksPerPlugin : _settings.MaxParallelTasksPerPlugin;
            _settings = settings.Clone();
            _settings.UseHostName = useHostName;
            _settings.MaxParallelTasksPerPlugin = maxParallel;
            _settings.ServerVersion = FakeServerVersion;
            return Task.FromResult(_settings.Clone());
        }
    }

    public Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<CorePluginInfo>>([.. FakeCorePlugins]);
        }
    }

    /// <summary>Core plugins simulated in fake mode (FakeOadmApi.SnapshotReport.cs, FakeOadmApi.Pki.cs, FakeOadmApi.MetadataMonitor.cs, FakeOadmApi.HardeningScan.cs).</summary>
    private static IEnumerable<CorePluginInfo> FakeCorePlugins =>
    [
        new CorePluginInfo { Id = SnapshotReportPluginId, DisplayName = "Snapshot report", IconKey = "snapshot" },
        new CorePluginInfo { Id = PkiPluginId, DisplayName = "PKI", IconKey = "shield" },
        new CorePluginInfo { Id = MetadataMonitorPluginId, DisplayName = "Metadata Monitor", IconKey = "activity" },
        new CorePluginInfo { Id = HardeningScanPluginId, DisplayName = "Hardening scan", IconKey = "clipboardCheck" },
    ];

    public Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct) => pluginId switch
    {
        SnapshotReportPluginId => InvokeSnapshotReportAsync(method, payloadJson, ct),
        PkiPluginId => InvokePkiAsync(method, payloadJson),
        MetadataMonitorPluginId => InvokeMetadataMonitorAsync(method, payloadJson),
        HardeningScanPluginId => InvokeHardeningScanAsync(method, payloadJson),
        _ => throw new RpcException(new Status(StatusCode.NotFound, $"Unknown core plugin '{pluginId}'.")),
    };

    // ---------------------------------------------------------------- live view

    /// <summary>
    /// Sources like the real devices report them: the P3265-V has two enabled view areas (as
    /// 10.0.0.48), the P3727-PLE four sensors plus a quad view, everything else one camera.
    /// </summary>
    public Task<IReadOnlyList<LiveViewSource>> ListLiveViewSourcesAsync(string deviceId, CancellationToken ct)
    {
        string model;
        lock (_gate)
        {
            ThrowIfOffline();
            model = _devices.Find(d => d.Id == deviceId)?.Model
                ?? throw new RpcException(new Status(StatusCode.NotFound, "device not found"));
        }

        static LiveViewSource Source(int camera, string name, int sensor, int w, int h) =>
            new() { Camera = camera, Name = name, Sensor = sensor, MaxWidth = w, MaxHeight = h };

        IReadOnlyList<LiveViewSource> sources = model switch
        {
            _ when model.Contains("P3265", StringComparison.Ordinal) => [Source(1, "View Area 1", 0, 1920, 1080), Source(2, "View Area 2", 0, 1920, 1080)],
            _ when model.Contains("P3727", StringComparison.Ordinal) =>
                [Source(1, "Camera 1", 0, 2592, 1944), Source(2, "Camera 2", 1, 2592, 1944), Source(3, "Camera 3", 2, 2592, 1944), Source(4, "Camera 4", 3, 2592, 1944), Source(5, "Quad view", 0, 3840, 2880)],
            _ => [Source(1, "Camera", 0, 1920, 1080)],
        };
        return Task.FromResult(sources);
    }

    /// <summary>
    /// Replays 1.5 s of real camera video (recorded from an AXIS P3265-V at 640x360, 10 fps) in a
    /// loop: H.265 when the viewer accepts it, else H.264. Each loop starts with a keyframe.
    /// </summary>
    public async IAsyncEnumerable<LiveViewFrame> WatchLiveViewAsync(LiveViewRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            if (_devices.All(d => d.Id != request.DeviceId))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "device not found"));
            }
        }

        var sources = await ListLiveViewSourcesAsync(request.DeviceId, ct).ConfigureAwait(false);
        var camera = request.Camera == 0 ? sources[0].Camera : request.Camera;
        if (sources.All(s => s.Camera != camera))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The device has no video source {camera}."));
        }

        var codec = request.AcceptedCodecs.Contains(VideoCodec.H265) ? VideoCodec.H265 : VideoCodec.H264;
        var units = FakeLiveVideo.Load(codec);
        var fps = request.Fps > 0 ? Math.Min(request.Fps, 30) : 10;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
        long timestamp = 0;
        for (var i = 0; ; i = (i + 1) % units.Count)
        {
            var (keyframe, data) = units[i];
            yield return new LiveViewFrame
            {
                Codec = codec,
                Keyframe = keyframe,
                Data = Google.Protobuf.ByteString.CopyFrom(data),
                RtpTimestamp = timestamp,
                Width = 640,
                Height = 360,
                Captured = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Camera = camera,
            };
            timestamp += 90000 / fps;
            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                yield break;
            }

            lock (_gate)
            {
                ThrowIfOffline();
            }
        }
    }

    // ---------------------------------------------------------------- simulation

    private static string OwnerName => $"{Environment.UserName}@{Environment.MachineName}";

    /// <summary>Fake credential check: the password "wrong" is rejected, anything else non-empty works.</summary>
    private static bool IsAcceptedPassword(string password) => password.Length > 0 && password != "wrong";

    private async Task SimulationLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                lock (_gate)
                {
                    if (_online)
                    {
                        AdvanceJobs();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    private void AdvanceJobs()
    {
        foreach (FakeJob job in _jobs.Values.ToList())
        {
            TaskInfo task = job.Task;
            if (task.State == TaskState.Queued)
            {
                task.State = TaskState.Running;
                task.Started = Timestamp.FromDateTime(DateTime.UtcNow);
                foreach (TaskDeviceResult r in task.Devices)
                {
                    r.State = TaskState.Running;
                }
            }

            if (++job.Ticks % job.TicksPerStep != 0)
            {
                continue;
            }

            task.Progress = Math.Min(100, task.Progress + job.Step);
            foreach (TaskDeviceResult r in task.Devices.Where(r => r.State == TaskState.Running))
            {
                r.Progress = task.Progress;
            }

            SetSteps(task, job.StepNames, task.Progress);
            job.OnProgress?.Invoke(task, task.Progress);
            if (task.Progress >= 100)
            {
                _jobs.Remove(task.Id);
                bool anyFailed = task.Devices.Any(r => r.State == TaskState.Failed);
                Finish(task, anyFailed ? TaskState.Failed : TaskState.Done, null);
            }
            else
            {
                _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = task.Clone() });
            }
        }
    }

    private void SimulateRestart(TaskInfo task, int progress)
    {
        foreach (TaskDeviceResult result in task.Devices)
        {
            Device? device = _devices.Find(d => d.Id == result.DeviceId);
            if (device is null)
            {
                continue;
            }

            // The device message follows the simulated steps (SetSteps); this only moves the device.
            if (progress is >= 24 and < 28)
            {
                AddLog(task, result.DeviceId, TaskLogLevel.Info, "Restart requested, waiting for the device to go offline");
                device.Status = DeviceStatus.Unreachable;
                PublishUpdated(device);
            }
            else if (progress is >= 80 and < 84)
            {
                AddLog(task, result.DeviceId, TaskLogLevel.Info, "Device is back online");
                device.Status = DeviceStatus.Ok;
                device.LastSeen = Timestamp.FromDateTime(DateTime.UtcNow);
                PublishUpdated(device);
            }
        }
    }

    private void Finish(TaskInfo task, TaskState state, string? message)
    {
        if (state is TaskState.Cancelled or TaskState.Failed)
        {
            EndSteps(task, state == TaskState.Cancelled ? "Cancelled." : message ?? "Failed.", state == TaskState.Cancelled
                ? "Not run: the task was cancelled."
                : "Not run: an earlier step failed.");
        }

        task.State = state;
        task.Finished = Timestamp.FromDateTime(DateTime.UtcNow);
        if (state == TaskState.Done)
        {
            task.Progress = 100;
        }

        foreach (TaskDeviceResult result in task.Devices.Where(r => r.State is TaskState.Running or TaskState.Queued))
        {
            result.State = state;
            result.Progress = state == TaskState.Done ? 100 : result.Progress;
            if (message is not null)
            {
                result.Message = message;
            }

            AddLog(task, result.DeviceId, state == TaskState.Failed ? TaskLogLevel.Error : TaskLogLevel.Info,
                message ?? (state == TaskState.Done ? "Done" : state.ToString()));
        }

        _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = task.Clone() });
    }

    private void AddLog(TaskInfo task, string? deviceId, TaskLogLevel level, string message, DateTime? timeUtc = null)
    {
        if (!_taskLogs.TryGetValue(task.Id, out List<TaskLogEntry>? log))
        {
            log = [];
            _taskLogs[task.Id] = log;
        }

        log.Add(new TaskLogEntry
        {
            Time = Timestamp.FromDateTime(timeUtc ?? DateTime.UtcNow),
            DeviceId = deviceId ?? "",
            Level = level,
            Message = message,
        });
    }

    private List<TaskInfo> AddBatch(string pluginId, string name, string owner, TaskState state, int progress, IEnumerable<string> deviceIds)
    {
        string batchId = Guid.NewGuid().ToString();
        return [.. deviceIds.Select(id => AddTask(pluginId, name, owner, state, progress, id, batchId))];
    }

    /// <summary>A task targets exactly one device.</summary>
    private TaskInfo AddTask(string pluginId, string name, string owner, TaskState state, int progress, string deviceId, string? batchId = null)
    {
        DateTime now = DateTime.UtcNow;
        var task = new TaskInfo
        {
            Id = Guid.NewGuid().ToString(),
            PluginId = pluginId,
            Name = name,
            State = state,
            Owner = owner,
            Created = Timestamp.FromDateTime(now),
            Progress = progress,
            DeviceId = deviceId,
        };
        task.BatchId = batchId ?? task.Id;
        if (state != TaskState.Queued)
        {
            task.Started = Timestamp.FromDateTime(now);
        }

        task.Devices.Add(new TaskDeviceResult { DeviceId = deviceId, State = state, Progress = progress });

        _tasks.Add(task);
        _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = task.Clone() });
        return task;
    }

    private void PublishUpdated(Device device) =>
        _deviceEvents.Publish(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = device.Clone() });

    private void ThrowIfOffline()
    {
        if (!_online)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "fake server offline"));
        }
    }

    /// <summary>Each device as first seen (login pending) and with its automatic login result.</summary>
    private List<(DiscoveredDevice Found, DiscoveredDevice Auth)> BuildDiscoveryCandidates(FakeSession session)
    {
        var list = new List<DiscoveredDevice>();
        lock (_gate)
        {
            // some managed devices answer too and show up greyed out
            foreach (Device managed in _devices.Take(3))
            {
                list.Add(Discovered(managed.Serial, managed.Address, managed.Model, DeviceStatus.Ok, AuthState.AlreadyAdded, alreadyManaged: true));
            }
        }

        list.Add(Discovered("ACCC8E5F6071", "10.0.0.92", "AXIS Q6075-E", DeviceStatus.CredentialsRequired, AuthState.Authenticated, user: "root"));
        list.Add(Discovered("B8A44FB4C5D6", "10.0.0.94", "AXIS P1455-LE", DeviceStatus.CredentialsRequired, AuthState.Authenticated, user: "root"));
        list.Add(Discovered("B8A44F6610AB", "10.0.0.96", "AXIS M3088-V", DeviceStatus.CredentialsRequired, AuthState.Authenticated, user: "operator"));
        list.Add(Discovered("ACCC8E8192A3", "10.0.0.93", "AXIS C1310-E Mk II", DeviceStatus.CredentialsRequired, AuthState.LoginFailed,
            detail: "None of the 2 known credentials worked."));
        list.Add(Discovered("B8A44F5A6B7C", "10.0.0.97", "AXIS P1468-LE", DeviceStatus.CredentialsRequired, AuthState.LoginFailed,
            detail: "None of the 2 known credentials worked."));
        list.Add(Discovered("B8A44F7788AA", "10.0.0.90", "AXIS M3215-LVE", DeviceStatus.PasswordNotSet, AuthState.PasswordNotSet, policy: "none"));
        list.Add(Discovered("B8A44F99CC01", "10.0.0.91", "AXIS P3268-LV", DeviceStatus.PasswordNotSet, AuthState.PasswordNotSet, policy: "complex"));
        list.Add(Discovered("ACCC8E2B3C4D", "10.0.0.95", "", DeviceStatus.Unreachable, AuthState.Unreachable,
            detail: "The device did not answer on HTTPS or HTTP."));

        switch (session.Kind)
        {
            case FakeSessionKind.Range:
            {
                // place the finds inside the requested range; only devices that answer are found
                list.RemoveAll(d => d.AlreadyManaged || d.Status == DeviceStatus.Unreachable);
                uint from = ToUInt(session.From!);
                uint to = Math.Max(from, ToUInt(session.To!));
                uint span = to - from + 1;
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].Source = DiscoverySource.RangeScan;
                    list[i].Address = FromUInt(from + (uint)((i * 7 + 3) % span));
                }

                break;
            }

            case FakeSessionKind.Manual:
            {
                string entered = session.Entered!;
                string host = entered.Contains("://", StringComparison.Ordinal) ? entered[(entered.IndexOf("://", StringComparison.Ordinal) + 3)..] : entered;
                DiscoveredDevice? known = list.Find(d => d.Address == host.Split(':')[0]);
                if (known is null && host.Contains("99", StringComparison.Ordinal))
                {
                    list.Clear(); // nothing answers there
                    break;
                }

                if (known is null || known.Status == DeviceStatus.Unreachable)
                {
                    string serial = "B8A44F" + (Math.Abs(StringComparer.Ordinal.GetHashCode(host)) % 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
                    known = Discovered(serial, IPAddress.TryParse(host.Split(':')[0], out _) ? host.Split(':')[0] : "10.0.0.120", "AXIS P3265-V",
                        DeviceStatus.CredentialsRequired, AuthState.Authenticated, user: "root");
                }

                known.Source = DiscoverySource.Manual;
                known.EnteredAddress = host;
                known.HostName = IPAddress.TryParse(host.Split(':')[0], out _) ? "" : host;
                list.Clear();
                list.Add(known);
                break;
            }
        }

        var result = new List<(DiscoveredDevice, DiscoveredDevice)>();
        foreach (DiscoveredDevice auth in list)
        {
            auth.DiscoveredId = auth.Serial;
            DiscoveredDevice found = auth.Clone();
            if (!found.AlreadyManaged)
            {
                found.AuthState = found.Status == DeviceStatus.Unreachable ? AuthState.Unreachable : AuthState.Pending;
                found.AuthUserName = "";
                found.CredentialId = "";
                found.AuthDetail = found.AuthState == AuthState.Unreachable ? auth.AuthDetail : "";
                found.PassphrasePolicy = "";
            }

            result.Add((found, auth));
        }

        return result;

        static DiscoveredDevice Discovered(string serial, string address, string model, DeviceStatus status, AuthState auth,
            bool alreadyManaged = false, string user = "", string detail = "", string policy = "") => new()
        {
            Serial = serial,
            Address = address,
            HostName = "axis-" + serial.ToLowerInvariant() + ".local",
            Model = model,
            Status = status,
            AlreadyManaged = alreadyManaged,
            Source = DiscoverySource.Mdns,
            Scheme = "https",
            ProductType = model.Length == 0 ? "" : SampleProductType(model),
            Category = model.Length == 0 ? DeviceCategory.Unknown : SampleCategory(model),
            AuthState = auth,
            AuthUserName = user,
            CredentialId = user.Length == 0 ? "" : "list:fake-" + user,
            AuthDetail = detail,
            PassphrasePolicy = policy,
        };
    }

    // ---------------------------------------------------------------- credential list

    public Task<IReadOnlyList<CredentialEntry>> ListCredentialsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<CredentialEntry>>(_credentials.Select(c => c.Clone()).ToList());
        }
    }

    public Task<CredentialEntry> AddCredentialAsync(string userName, string password, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (string.IsNullOrWhiteSpace(userName))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The user name must be 1-64 characters."));
            }

            if (string.IsNullOrEmpty(password) || password.Length > 64 || password.Any(c => c < 0x20 || c > 0x7E))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Password must be 1-64 printable ASCII characters."));
            }

            return Task.FromResult(AddCredentialLocked(userName.Trim(), password).Clone());
        }
    }

    public Task RemoveCredentialAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (_credentials.RemoveAll(c => c.Id == id) == 0)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Credential '{id}' not found."));
            }

            _credentialPasswords.Remove(id);
            return Task.CompletedTask;
        }
    }

    public Task<string> RevealCredentialAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return _credentialPasswords.TryGetValue(id, out string? password)
                ? Task.FromResult(password)
                : throw new RpcException(new Status(StatusCode.NotFound, $"Credential '{id}' not found."));
        }
    }

    private CredentialEntry AddCredentialLocked(string userName, string password)
    {
        var entry = new CredentialEntry
        {
            Id = Guid.NewGuid().ToString(),
            UserName = userName,
            Created = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        _credentials.Add(entry);
        _credentialPasswords[entry.Id] = password;
        return entry;
    }

    private static uint ToUInt(IPAddress address)
    {
        byte[] b = address.MapToIPv4().GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string FromUInt(uint value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}");

    private void Seed()
    {
        Device p3265 = CreateDevice("B8A44F631339", "10.0.0.48", "AXIS P3265-V", "12.11.77", DeviceStatus.Ok);
        _devices.Add(p3265);
        _devices.Add(CreateDevice("ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II", "11.11.124", DeviceStatus.Ok));
        _devices.Add(CreateDevice("ACCC8EF00D11", "10.0.0.22", "AXIS Q6135-LE", "12.6.94", DeviceStatus.Ok));
        _devices.Add(CreateDevice("B8A44F2E7A90", "10.0.0.23", "AXIS P1468-LE", "12.11.77", DeviceStatus.CredentialsRequired));
        _devices.Add(CreateDevice("00408CA1B2C3", "10.0.0.30", "AXIS A9188", "11.11.124", DeviceStatus.Unreachable));
        _devices.Add(CreateDevice("B8A44F3C4D5E", "10.0.0.31", "AXIS M4317-PLVE", "12.11.77", DeviceStatus.Ok));
        _devices.Add(CreateDevice("ACCC8E77E3A1", "10.0.0.32", "AXIS P3727-PLE", "11.8.64", DeviceStatus.Ok));
        _devices.Add(CreateDevice("B8A44F4A5B6C", "10.0.0.33", "AXIS Q3538-LVE", "12.11.77", DeviceStatus.CertificateChanged));
        _devices.Add(CreateDevice("ACCC8E9081F2", "10.0.0.34", "AXIS C1310-E Mk II", "11.11.124", DeviceStatus.Ok));
        _devices.Add(CreateDevice("B8A44F5D6E7F", "10.0.0.35", "AXIS D2110-VE", "12.6.94", DeviceStatus.Ok));
        _devices.Add(CreateDevice("B8A44F11AA22", "10.0.0.40", "AXIS F9111", "12.11.77", DeviceStatus.PasswordNotSet));
        Device q1656 = CreateDevice("ACCC8E3344BB", "10.0.0.41", "AXIS Q1656-LE", "12.6.94", DeviceStatus.Ok);
        q1656.DhcpEnabled = false;
        q1656.HttpsEnabled = false;
        q1656.Scheme = "http";
        ClearCertificate(q1656);
        _devices.Add(q1656);
        _devices[2].Dot1XEnabled = true;
        _devices[9].Dot1XEnabled = true;

        // Certificate mix: self-signed defaults (CreateDevice), a private CA that is trusted by the
        // server OS, one not in the trust store, one expiring soon, one expired, HTTP only above.
        const string issuingCa = "CN=OADM Lab Issuing CA, O=Example Corp";
        SetCertificate(_devices[0], CertificateTrust.SelfSigned, 300);
        SetCertificate(_devices[1], CertificateTrust.Trusted, 245, issuingCa);
        SetCertificate(_devices[2], CertificateTrust.Trusted, 512, issuingCa);
        SetCertificate(_devices[5], CertificateTrust.Trusted, 12, issuingCa);
        SetCertificate(_devices[6], CertificateTrust.Untrusted, 88, "CN=Site Camera CA, O=Example Corp");
        SetCertificate(_devices[8], CertificateTrust.Expired, -3);
        ClearCertificate(_devices[10]); // factory default: never fully refreshed

        DateTime now = DateTime.UtcNow;

        // One run on several devices (one task per device), including one that was removed since.
        foreach (TaskInfo done in AddBatch(RestartPluginId, "Restart device", OwnerName, TaskState.Done, 100,
            [_devices[0].Id, _devices[9].Id, "5f3c9a1e-7b2d-4c8e-9a6f-0d1e2f3a4b5c"]))
        {
            done.Started = Timestamp.FromDateTime(now.AddHours(-2));
            SetSteps(done, RestartSteps, 100);
        }

        TaskInfo failed = AddTask(RestartPluginId, "Restart device", "admin@SECURITY-PC", TaskState.Failed, 100, _devices[4].Id);
        failed.Started = Timestamp.FromDateTime(now.AddMinutes(-40));
        failed.Devices[0].Message = "Device did not come back within 3 minutes";
        SetSteps(failed, RestartSteps, 70);
        EndSteps(failed, "Device did not come back within 3 minutes.", "Not run: an earlier step failed.");
        AddLog(failed, failed.DeviceId, TaskLogLevel.Info, "Restart requested, waiting for the device to go offline", now.AddMinutes(-40));
        AddLog(failed, failed.DeviceId, TaskLogLevel.Error, "Device did not come back within 3 minutes", now.AddMinutes(-37));

        List<TaskInfo> identifyRun = AddBatch(IdentifyPluginId, "Identify device (flash LED)", OwnerName, TaskState.Done, 100, [_devices[2].Id, _devices[3].Id]);
        TaskInfo flashed = identifyRun[0];
        TaskInfo warned = identifyRun[1];
        foreach (TaskInfo t in identifyRun)
        {
            t.Started = Timestamp.FromDateTime(now.AddMinutes(-32));
            t.Finished = Timestamp.FromDateTime(now.AddMinutes(-31));
        }

        flashed.Devices[0].Message = "LED flashed";
        SetSteps(flashed, IdentifySteps, 100);
        SetSteps(warned, IdentifySteps, 100);
        warned.Steps[1].State = TaskStepState.Warning;
        warned.Steps[1].Detail = "LED not available, used the status indicator instead";
        AddLog(flashed, flashed.DeviceId, TaskLogLevel.Info, "LED flashed", now.AddMinutes(-32).AddSeconds(4));
        warned.State = TaskState.DoneWithWarnings;
        warned.Devices[0].State = TaskState.DoneWithWarnings;
        warned.Devices[0].Message = "LED not available, used the status indicator instead";
        AddLog(warned, null, TaskLogLevel.Info, "Started", now.AddMinutes(-32));
        AddLog(warned, warned.DeviceId, TaskLogLevel.Warning, "LED not available, used the status indicator instead", now.AddMinutes(-32).AddSeconds(5));
        AddLog(warned, warned.DeviceId, TaskLogLevel.Info, "Status indicator flashed", now.AddMinutes(-31));

        TaskInfo cancelled = AddTask(RestartPluginId, "Restart device", OwnerName, TaskState.Cancelled, 30, _devices[7].Id);
        cancelled.Started = Timestamp.FromDateTime(now.AddMinutes(-25));
        SetSteps(cancelled, RestartSteps, 30);
        EndSteps(cancelled, "Cancelled.", "Not run: the task was cancelled.");

        foreach (TaskInfo running in AddBatch(RestartPluginId, "Restart device", OwnerName, TaskState.Running, 36, [_devices[1].Id, _devices[5].Id]))
        {
            running.Started = Timestamp.FromDateTime(now.AddSeconds(-50));
            SetSteps(running, RestartSteps, running.Progress);
            _jobs[running.Id] = new FakeJob(running, 1, null, RestartSteps);
        }

        TaskInfo identify = AddTask(IdentifyPluginId, "Identify device (flash LED)", "admin@SECURITY-PC", TaskState.Running, 64, p3265.Id);
        identify.Started = Timestamp.FromDateTime(now.AddSeconds(-20));
        SetSteps(identify, IdentifySteps, identify.Progress);
        _jobs[identify.Id] = new FakeJob(identify, 1, null, IdentifySteps);

        // A firmware upgrade in the middle of its upload: many small steps, byte progress on the running one.
        TaskInfo firmware = AddTask(FirmwarePluginId, "Upgrade firmware to 12.11.77", OwnerName, TaskState.Running, 42, _devices[2].Id);
        firmware.Started = Timestamp.FromDateTime(now.AddSeconds(-95));
        SetSteps(firmware, FirmwareSteps, firmware.Progress);
        AddLog(firmware, firmware.DeviceId, TaskLogLevel.Info, "Compatible: fwmgr 1.4, AXIS OS 12.6.94 can be upgraded to 12.11.77", now.AddSeconds(-94));
        AddLog(firmware, firmware.DeviceId, TaskLogLevel.Info, "Uploading AXIS_OS_Q6135-LE_12_11_77.bin (82 MB, SHA-256 verified)", now.AddSeconds(-74));
        _jobs[firmware.Id] = new FakeJob(firmware, 1, null, FirmwareSteps) { TicksPerStep = 8 };

        // Finished tasks of other plugins: the task name says exactly what the task did (ITaskPlugin.GetTaskName).
        (string PluginId, string Name, int Device, int MinutesAgo)[] history =
        [
            ("oadm.users", "Add user joe", 0, 95),
            ("oadm.users", "Remove users guest, temp", 9, 90),
            ("oadm.network", "Set static IP 10.0.0.60", 3, 80),
            ("oadm.acap", "Upgrade AXIS Object Analytics to 1.4.2", 2, 70),
            ("oadm.vapix-commander.run", "Read brand parameters +2 more", 5, 60),
        ];
        foreach ((string pluginId, string name, int index, int minutesAgo) in history)
        {
            TaskInfo task = AddTask(pluginId, name, OwnerName, TaskState.Done, 100, _devices[index].Id);
            task.Started = Timestamp.FromDateTime(now.AddMinutes(-minutesAgo));
            task.Finished = Timestamp.FromDateTime(now.AddMinutes(-minutesAgo).AddSeconds(30));
            SetSteps(task, GenericSteps, 100);
        }
    }

    private Device CreateDevice(string serial, string address, string model, string firmware, DeviceStatus status) => WithSampleApis(new Device
    {
        Id = Guid.NewGuid().ToString(),
        Serial = serial,
        Address = address,
        HostName = "axis-" + serial.ToLowerInvariant(),
        Model = model,
        FirmwareVersion = firmware,
        DhcpEnabled = true,
        HttpsEnabled = true,
        Dot1XEnabled = false,
        UpnpFriendlyName = $"{model} - {serial}",
        ServerName = _settings.ServerName,
        Status = status,
        Scheme = "https",
        LastSeen = Timestamp.FromDateTime(DateTime.UtcNow),
        HasCredentials = status != DeviceStatus.PasswordNotSet,
        ProductType = SampleProductType(model),
        Category = SampleCategory(model),
        HasVideo = SampleCategory(model) is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom,
        CertTrust = CertificateTrust.SelfSigned,
        CertNotAfter = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(1000 + (serial[^1] % 7 * 61)).AddHours(1)),
        CertSubject = "CN=axis-" + serial.ToLowerInvariant(),
        CertIssuer = "CN=axis-" + serial.ToLowerInvariant(),
        CertNameMatches = false,
    });

    /// <summary>A typical AXIS OS 12 API list (subset of apidiscovery getApiList) so plugin CanRun checks work in fake mode.</summary>
    private static Device WithSampleApis(Device device)
    {
        device.Apis.Add(new DeviceApi { Id = "basic-device-info", Version = "1.3", Name = "Basic device information", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "user-management", Version = "1.2", Name = "User management", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "network-settings", Version = "1.37", Name = "Network settings", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "fwmgr", Version = "1.10", Name = "Firmware management", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "packagemanager", Version = "1.4", Name = "Package manager", Status = "official" });
        device.Apis.Add(new DeviceApi { Id = "time-service", Version = "1.1", Name = "Time service", Status = "official" });
        return device;
    }

    /// <summary>ProdType like the real devices report it (AXIS P3265-V says "Dome Camera").</summary>
    internal static string SampleProductType(string model) => model switch
    {
        _ when model.Contains("P3265", StringComparison.Ordinal) => "Dome Camera",
        _ when model.Contains("C1310", StringComparison.Ordinal) => "Network Horn Speaker",
        _ when model.Contains("D2110", StringComparison.Ordinal) => "Security Radar",
        _ when model.Contains("A9188", StringComparison.Ordinal) => "Network I/O Relay Module",
        _ when model.Contains("Q6135", StringComparison.Ordinal) => "PTZ Network Camera",
        _ => "Network Camera",
    };

    internal static DeviceCategory SampleCategory(string model) => SampleProductType(model) switch
    {
        "Network Horn Speaker" => DeviceCategory.Speaker,
        "Security Radar" => DeviceCategory.Radar,
        "Network I/O Relay Module" => DeviceCategory.IoModule,
        _ => DeviceCategory.Camera,
    };

    /// <summary>Sample certificate: <paramref name="issuer"/> null means self-signed (issuer = subject).</summary>
    private static void SetCertificate(Device device, CertificateTrust trust, int daysLeft, string? issuer = null)
    {
        string subject = "CN=axis-" + device.Serial.ToLowerInvariant();
        device.CertTrust = trust;
        device.CertNotAfter = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(daysLeft).AddHours(daysLeft < 0 ? -1 : 1));
        device.CertSubject = issuer is null ? subject : "CN=" + device.Address + ", O=Example Corp";
        device.CertIssuer = issuer ?? subject;
        device.CertNameMatches = issuer is not null;
    }

    private static void ClearCertificate(Device device)
    {
        device.CertTrust = CertificateTrust.Unknown;
        device.CertNotAfter = null;
        device.CertSubject = string.Empty;
        device.CertIssuer = string.Empty;
        device.ClearCertNameMatches();
    }

    private sealed class FakeJob(TaskInfo task, int step, Action<TaskInfo, int>? onProgress, IReadOnlyList<string> stepNames)
    {
        public TaskInfo Task { get; } = task;
        public int Step { get; } = step;
        public Action<TaskInfo, int>? OnProgress { get; } = onProgress;
        public IReadOnlyList<string> StepNames { get; } = stepNames;

        /// <summary>Advance only every n-th tick (the sample firmware upload is slow).</summary>
        public int TicksPerStep { get; init; } = 1;

        public int Ticks { get; set; }
    }

    private const string FirmwarePluginId = "oadm.firmware";

    /// <summary>The steps of the Restart task plugin, as the server reports them.</summary>
    private static readonly string[] RestartSteps =
        ["Check device", "Send restart", "Wait for the device to go offline", "Wait for the device to come back", "Verify device"];

    private static readonly string[] IdentifySteps = ["Check device", "Flash LED", "Wait 10 s", "Stop flashing"];

    /// <summary>The steps of the Firmware task plugin on its normal path.</summary>
    private static readonly string[] FirmwareSteps =
    [
        "Check compatibility", "Read device info", "Validate file", "Read firmware status", "Upload firmware",
        "Install firmware", "Wait for device to come back", "Verify version", "Read commit state", "Commit firmware",
    ];

    private static readonly string[] GenericSteps = ["Check compatibility", "Read current settings", "Apply change", "Verify device"];

    /// <summary>The task name the server would give (ITaskPlugin.GetTaskName) for the fake plugins.</summary>
    private static string FakeTaskName(string pluginId, string displayName) => pluginId switch
    {
        RestartPluginId => "Restart device",
        IdentifyPluginId => "Identify device (flash LED)",
        _ => displayName,
    };

    private static string[] StepsOf(string pluginId) => pluginId switch
    {
        RestartPluginId => RestartSteps,
        IdentifyPluginId => IdentifySteps,
        _ => GenericSteps,
    };

    /// <summary>
    /// Simulated steps from the overall progress: equal weights like the server, earlier steps Done, the
    /// current one Running with its share of the progress, later ones Pending. Step times are synthetic.
    /// </summary>
    private static void SetSteps(TaskInfo task, IReadOnlyList<string> names, int progress)
    {
        DateTime start = task.Started?.ToDateTime() ?? DateTime.UtcNow;
        double share = 100.0 / names.Count;
        bool finished = progress >= 100;
        int current = Math.Min(names.Count - 1, (int)(progress / share));
        task.Steps.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            TaskStepState state = finished || i < current ? TaskStepState.Done : i == current ? TaskStepState.Running : TaskStepState.Pending;
            var step = new TaskStep
            {
                Index = i,
                Name = names[i],
                State = state,
                Progress = state == TaskStepState.Done ? 100 : state == TaskStepState.Running ? (int)((progress - (i * share)) / share * 100) : 0,
            };
            if (state != TaskStepState.Pending)
            {
                step.Started = Timestamp.FromDateTime(start.AddSeconds(i * 7));
            }

            if (state == TaskStepState.Done)
            {
                step.Finished = Timestamp.FromDateTime(start.AddSeconds((i * 7) + 6));
            }

            if (state == TaskStepState.Running && names[i] == "Upload firmware")
            {
                step.Detail = string.Create(CultureInfo.InvariantCulture, $"{step.Progress * 82 / 100} of 82 MB");
            }

            task.Steps.Add(step);
        }

        if (finished)
        {
            // Like the server: a successful task ends with the step "Completed".
            Timestamp end = Timestamp.FromDateTime(start.AddSeconds(names.Count * 7));
            task.Steps.Add(new TaskStep { Index = names.Count, Name = "Completed", State = TaskStepState.Done, Progress = 100, Started = end, Finished = end });
        }

        task.CurrentStepIndex = finished ? task.Steps.Count - 1 : current;
        foreach (TaskDeviceResult result in task.Devices.Where(r => r.State == TaskState.Running))
        {
            TaskStep running = task.Steps[task.CurrentStepIndex];
            result.Message = string.IsNullOrEmpty(running.Detail) ? running.Name : $"{running.Name} - {running.Detail}";
        }
    }

    /// <summary>Like the server when a task fails or is cancelled: the running step fails, pending ones are skipped.</summary>
    private static void EndSteps(TaskInfo task, string failure, string skipReason)
    {
        if (task.Steps.Count > 0 && task.Steps[^1].Name == "Completed")
        {
            task.Steps.RemoveAt(task.Steps.Count - 1);
            task.CurrentStepIndex = task.Steps.Count - 1;
        }

        foreach (TaskStep step in task.Steps)
        {
            if (step.State == TaskStepState.Running)
            {
                step.State = TaskStepState.Failed;
                step.Detail = failure;
                step.Finished = Timestamp.FromDateTime(DateTime.UtcNow);
            }
            else if (step.State == TaskStepState.Pending)
            {
                step.State = TaskStepState.Skipped;
                step.Detail = skipReason;
            }
        }
    }

    private enum FakeSessionKind
    {
        Scan,
        Range,
        Manual,
    }

    private sealed class FakeSession(string id, FakeSessionKind kind, IPAddress? from, IPAddress? to, string? entered) : IDisposable
    {
        /// <summary>Cancelled by StopScan, Stop or the zero-conf time limit.</summary>
        public CancellationTokenSource ScanEnd { get; } = new();

        /// <summary>The scan ended; a new watch replays <see cref="Found"/>.</summary>
        public bool Finished { get; set; }

        /// <summary>Login results pushed while the zero-conf watch is open (follow-up logins after a retry).</summary>
        public Channel<DiscoveredDevice> Updates { get; } = Channel.CreateUnbounded<DiscoveredDevice>();

        public void Dispose() => ScanEnd.Dispose();

        public string Id { get; } = id;
        public FakeSessionKind Kind { get; } = kind;
        public IPAddress? From { get; } = from;
        public IPAddress? To { get; } = to;
        public string? Entered { get; } = entered;
        public bool Stopped { get; set; }
        public Dictionary<string, DiscoveredDevice> Found { get; } = [];
    }

    private sealed class Broadcast<T>
    {
        private readonly Lock _gate = new();
        private readonly List<Channel<T>> _subscribers = [];

        public Channel<T> Subscribe()
        {
            var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
            lock (_gate)
            {
                _subscribers.Add(channel);
            }

            return channel;
        }

        public void Unsubscribe(Channel<T> channel)
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }

        public void Publish(T item)
        {
            lock (_gate)
            {
                foreach (Channel<T> channel in _subscribers)
                {
                    channel.Writer.TryWrite(item);
                }
            }
        }

        public void Fail(Exception error)
        {
            lock (_gate)
            {
                foreach (Channel<T> channel in _subscribers)
                {
                    channel.Writer.TryComplete(error);
                }

                _subscribers.Clear();
            }
        }
    }
}
