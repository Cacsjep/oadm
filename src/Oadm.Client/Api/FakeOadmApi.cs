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
/// wizard commit and the Restart task. Start the client with <c>--fake</c> to use it.
/// </summary>
public sealed class FakeOadmApi : IOadmApi, IDisposable
{
    public const string RestartPluginId = "oadm.restart";
    public const string IdentifyPluginId = "oadm.identify";

    private readonly Lock _gate = new();
    private readonly List<Device> _devices = [];
    private readonly List<TaskInfo> _tasks = [];
    private readonly Dictionary<string, FakeJob> _jobs = [];
    private readonly Dictionary<string, FakeSession> _sessions = [];
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
        ServerName = "acs",
        ListenUrl = "http://0.0.0.0:5080",
    };
    private bool _online = true;

    /// <param name="tick">Simulation step. Tests pass a few milliseconds.</param>
    /// <param name="seedSampleData">False starts with an empty device and task table.</param>
    public FakeOadmApi(TimeSpan? tick = null, bool seedSampleData = true)
    {
        _tick = tick ?? TimeSpan.FromMilliseconds(250);
        _pluginTemplates =
        [
            new TaskPluginInfo { Id = RestartPluginId, DisplayName = "Restart", IconKey = "restart", ShowInToolbar = true },
            new TaskPluginInfo { Id = IdentifyPluginId, DisplayName = "Identify (flash LED)", IconKey = "identify" },
        ];
        if (seedSampleData)
        {
            Seed();
        }

        _ = Task.Run(() => SimulationLoopAsync(_cts.Token));
    }

    public string ServerAddress => "fake";

    /// <summary>Task plugins as returned by ListTaskPlugins. Tests may add entries.</summary>
    public IList<TaskPluginInfo> PluginTemplates => _pluginTemplates;

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

    public Task<string> StartZeroConfAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var session = new FakeSession(Guid.NewGuid().ToString("N"), null, null);
            _sessions[session.Id] = session;
            return Task.FromResult(session.Id);
        }
    }

    public Task<string> StartRangeScanAsync(string firstAddress, string lastAddress, CancellationToken ct)
    {
        if (!IPAddress.TryParse(firstAddress, out IPAddress? first) || !IPAddress.TryParse(lastAddress, out IPAddress? last))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid address range"));
        }

        lock (_gate)
        {
            ThrowIfOffline();
            var session = new FakeSession(Guid.NewGuid().ToString("N"), first, last);
            _sessions[session.Id] = session;
            return Task.FromResult(session.Id);
        }
    }

    public async IAsyncEnumerable<DiscoveredDevice> WatchDiscoveredAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        FakeSession session;
        lock (_gate)
        {
            ThrowIfOffline();
            session = _sessions.GetValueOrDefault(sessionId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "unknown discovery session"));
        }

        List<DiscoveredDevice> candidates = BuildDiscoveryCandidates(session);
        if (session.IsRangeScan)
        {
            const int steps = 20;
            for (int step = 1; step <= steps; step++)
            {
                await Task.Delay(_tick, ct);
                int percent = step * 100 / steps;
                foreach (DiscoveredDevice found in candidates.Where((_, i) => (i * steps / Math.Max(1, candidates.Count)) + 1 == step))
                {
                    found.ProgressPercent = percent;
                    session.Found[found.DiscoveredId] = found;
                    yield return found.Clone();
                }

                yield return new DiscoveredDevice { ProgressPercent = percent };
            }

            yield return new DiscoveredDevice { ScanFinished = true, ProgressPercent = 100 };
        }
        else
        {
            foreach (DiscoveredDevice found in candidates)
            {
                await Task.Delay(_tick, ct);
                session.Found[found.DiscoveredId] = found;
                yield return found.Clone();
            }

            // mDNS keeps browsing while the wizard is open
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    public Task StopDiscoveryAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- add devices

    public Task<AddPlan> PrepareAddAsync(string sessionId, IReadOnlyCollection<string> discoveredIds, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            FakeSession session = _sessions.GetValueOrDefault(sessionId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "unknown discovery session"));
            var plan = new AddPlan();
            foreach (string id in discoveredIds)
            {
                if (!session.Found.TryGetValue(id, out DiscoveredDevice? found))
                {
                    continue;
                }

                plan.Items.Add(new AddPlanItem
                {
                    DiscoveredId = id,
                    Serial = found.Serial,
                    Address = found.Address,
                    HostName = found.HostName,
                    Model = found.Model,
                    NeedsInitialPassword = found.Status == DeviceStatus.PasswordNotSet,
                    NeedsCredentials = found.Status == DeviceStatus.CredentialsRequired,
                });
            }

            return Task.FromResult(plan);
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
            var reply = new CommitReply();
            DeviceCredentials? forAll = request.Credentials.FirstOrDefault(c => c.DiscoveredId.Length == 0);
            foreach (string id in request.DiscoveredIds)
            {
                if (!session.Found.TryGetValue(id, out DiscoveredDevice? found) || found.AlreadyManaged
                    || _devices.Exists(d => d.Serial == found.Serial))
                {
                    continue;
                }

                DeviceCredentials? credentials = request.Credentials.FirstOrDefault(c => c.DiscoveredId == id) ?? forAll;
                DeviceStatus status = found.Status switch
                {
                    DeviceStatus.PasswordNotSet => request.InitialRootPassword.Length > 0 ? DeviceStatus.Ok : DeviceStatus.PasswordNotSet,
                    DeviceStatus.CredentialsRequired => credentials is not null && IsAcceptedPassword(credentials.Password)
                        ? DeviceStatus.Ok
                        : DeviceStatus.CredentialsRequired,
                    _ => found.Status,
                };
                Device device = CreateDevice(found.Serial, found.Address, found.Model, "12.11.77", status);
                device.HostName = found.HostName;
                device.UseHostName = request.UseHostName && found.HostName.Length > 0;
                device.HasCredentials = credentials is not null || request.InitialRootPassword.Length > 0;
                _devices.Add(device);
                reply.DeviceIds.Add(device.Id);
                _deviceEvents.Publish(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Added, Device = device.Clone() });
                found.AlreadyManaged = true;
            }

            // Adding devices is not a task: devices just appear and fill in.
            return Task.FromResult(reply);
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

    public Task<string> RunTaskAsync(string pluginId, IReadOnlyCollection<string> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            TaskPluginInfo plugin = _pluginTemplates.Find(p => p.Id == pluginId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"task plugin {pluginId} not found"));
            TaskInfo task = AddTask(pluginId, plugin.DisplayName, owner, TaskState.Queued, 0, deviceIds);
            Action<TaskInfo, int>? onProgress = pluginId == RestartPluginId ? SimulateRestart : null;
            _jobs[task.Id] = new FakeJob(task, pluginId == RestartPluginId ? 4 : 25, onProgress);
            return Task.FromResult(task.Id);
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
                _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Removed, Task = new TaskInfo { Id = task.Id } });
            }

            return Task.FromResult(all.Count);
        }
    }

    // ---------------------------------------------------------------- settings and core plugins

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

            _settings = settings.Clone();
            return Task.FromResult(_settings.Clone());
        }
    }

    public Task<IReadOnlyList<CorePluginInfo>> ListCorePluginsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<CorePluginInfo>>([]);
        }
    }

    public Task<string?> InvokeCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "no core plugins in fake mode"));

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

            task.Progress = Math.Min(100, task.Progress + job.Step);
            foreach (TaskDeviceResult r in task.Devices.Where(r => r.State == TaskState.Running))
            {
                r.Progress = task.Progress;
            }

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

            if (progress is >= 12 and < 16)
            {
                result.Message = "Restarting";
                device.Status = DeviceStatus.Unreachable;
                PublishUpdated(device);
            }
            else if (progress is >= 80 and < 84)
            {
                result.Message = "Device is back online";
                device.Status = DeviceStatus.Ok;
                device.LastSeen = Timestamp.FromDateTime(DateTime.UtcNow);
                PublishUpdated(device);
            }
        }
    }

    private void Finish(TaskInfo task, TaskState state, string? message)
    {
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
        }

        _taskEvents.Publish(new TaskChanged { Kind = TaskChanged.Types.Kind.Updated, Task = task.Clone() });
    }

    private TaskInfo AddTask(string pluginId, string name, string owner, TaskState state, int progress, IEnumerable<string> deviceIds)
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
        };
        if (state != TaskState.Queued)
        {
            task.Started = Timestamp.FromDateTime(now);
        }

        foreach (string id in deviceIds)
        {
            task.Devices.Add(new TaskDeviceResult { DeviceId = id, State = state, Progress = progress });
        }

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

    private List<DiscoveredDevice> BuildDiscoveryCandidates(FakeSession session)
    {
        var list = new List<DiscoveredDevice>();
        lock (_gate)
        {
            // some managed devices answer too and show up greyed out
            foreach (Device managed in _devices.Take(3))
            {
                list.Add(Discovered(managed.Serial, managed.Address, managed.Model, DeviceStatus.Ok, alreadyManaged: true));
            }
        }

        list.Add(Discovered("B8A44F7788AA", "10.0.0.90", "AXIS M3215-LVE", DeviceStatus.PasswordNotSet));
        list.Add(Discovered("B8A44F99CC01", "10.0.0.91", "AXIS P3268-LV", DeviceStatus.PasswordNotSet));
        list.Add(Discovered("ACCC8E5F6071", "10.0.0.92", "AXIS Q6075-E", DeviceStatus.CredentialsRequired));
        list.Add(Discovered("ACCC8E8192A3", "10.0.0.93", "AXIS C1310-E Mk II", DeviceStatus.CredentialsRequired));
        list.Add(Discovered("B8A44FB4C5D6", "10.0.0.94", "AXIS P1455-LE", DeviceStatus.CredentialsRequired));

        if (session.IsRangeScan)
        {
            // place the finds inside the requested range
            uint from = ToUInt(session.From!);
            uint to = Math.Max(from, ToUInt(session.To!));
            uint span = to - from + 1;
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Source = DiscoverySource.RangeScan;
                list[i].Address = FromUInt(from + (uint)((i * 7 + 3) % span));
            }

            // the managed ones keep their real address, so only new devices make sense in a range scan
            list.RemoveAll(d => d.AlreadyManaged);
        }

        foreach (DiscoveredDevice d in list)
        {
            d.DiscoveredId = "d-" + d.Serial;
        }

        return list;

        static DiscoveredDevice Discovered(string serial, string address, string model, DeviceStatus status, bool alreadyManaged = false) => new()
        {
            Serial = serial,
            Address = address,
            HostName = "axis-" + serial.ToLowerInvariant() + ".local",
            Model = model,
            Status = status,
            AlreadyManaged = alreadyManaged,
            Source = DiscoverySource.Mdns,
            Scheme = "https",
            ProductType = SampleProductType(model),
            Category = SampleCategory(model),
        };
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

        TaskInfo failed = AddTask(RestartPluginId, "Restart", "admin@SECURITY-PC", TaskState.Failed, 100, [_devices[4].Id]);
        failed.Started = Timestamp.FromDateTime(now.AddMinutes(-40));
        failed.Devices[0].Message = "Device did not come back within 3 minutes";

        TaskInfo cancelled = AddTask(RestartPluginId, "Restart", OwnerName, TaskState.Cancelled, 30, [_devices[7].Id]);
        cancelled.Started = Timestamp.FromDateTime(now.AddMinutes(-25));

        TaskInfo running = AddTask(RestartPluginId, "Restart", OwnerName, TaskState.Running, 36, [_devices[1].Id, _devices[5].Id]);
        running.Started = Timestamp.FromDateTime(now.AddSeconds(-50));
        _jobs[running.Id] = new FakeJob(running, 1, null);

        TaskInfo identify = AddTask(IdentifyPluginId, "Identify (flash LED)", "admin@SECURITY-PC", TaskState.Running, 64, [p3265.Id]);
        identify.Started = Timestamp.FromDateTime(now.AddSeconds(-20));
        _jobs[identify.Id] = new FakeJob(identify, 1, null);
    }

    private Device CreateDevice(string serial, string address, string model, string firmware, DeviceStatus status) => new()
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
    };

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

    private sealed class FakeJob(TaskInfo task, int step, Action<TaskInfo, int>? onProgress)
    {
        public TaskInfo Task { get; } = task;
        public int Step { get; } = step;
        public Action<TaskInfo, int>? OnProgress { get; } = onProgress;
    }

    private sealed class FakeSession(string id, IPAddress? from, IPAddress? to)
    {
        public string Id { get; } = id;
        public IPAddress? From { get; } = from;
        public IPAddress? To { get; } = to;
        public bool IsRangeScan => From is not null;
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
