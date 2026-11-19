using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Plugins.Network;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Provisioning.Engine;

/// <summary>What the engine works with: the host's services and the DHCP server's read-only view.</summary>
public sealed record EngineServices(
    IDeviceAutoAdd AutoAdd,
    ITaskRunner Tasks,
    IDeviceRepository Devices,
    IPluginSettings Settings,
    IPluginRecords Records,
    IPluginEvents? Events,
    Func<IDhcpServerLeases?> Dhcp,
    IAddressProbe Probe,
    IVapixClientFactory? Vapix = null,
    NodeVault? Vault = null);

/// <summary>
/// The provisioning session (docs/spec/provisioning.md): while started, every DHCP acknowledgement of an Axis MAC address
/// becomes a camera row; a factory-new camera runs the session's profile node by node, at most
/// <see cref="ProvisioningOptions.MaxParallel"/> at once (more wait as Queued; a camera waiting for approval holds no
/// place); any other camera is "Not new, left as it is" and never changed. The session (its profile, the reserved Set IP
/// addresses) and the rows are stored, so a server restart keeps them until Stop. Changes reach the page and the database
/// at most every <see cref="ProvisioningOptions.PublishInterval"/>. All time through <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class ProvisioningEngine : INodeHost, IAsyncDisposable
{
    public const string SessionKey = "session";
    public const string CamerasCollection = "cameras";
    public const string CamerasTopic = "cameras";
    public const string OwnerName = "Provisioning";

    public const string NotNewText = "Not new, left as it is.";
    public const string StoppedText = "Provisioning was stopped.";
    public const string RestartedText = "The server restarted while this ran.";
    public const string StartedAlready = "Provisioning is started already.";
    public const string NotStarted = "Provisioning is not started.";
    public const string DhcpFirst = "Turn on the DHCP server first.";

    private readonly EngineServices _services;
    private readonly NodeRegistry _registry;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CameraRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flow> _flows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<string>> _approvals = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _addressLock = new(1, 1);
    private readonly SemaphoreSlim _sessionWrite = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _control = new(1, 1); // Start and Stop one at a time
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource _sessionCts = new();
    private Session? _session;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;
    private int _peak;
    private int _holding;
    private volatile bool _disposing;
    private ITaskRunner? _starterTasks;
    private Task? _pollLoop;
    private Task? _publishLoop;

    public ProvisioningEngine(EngineServices services, NodeRegistry? registry = null, ProvisioningOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registry = registry ?? NodeRegistry.Default;
        Options = options ?? new ProvisioningOptions();
        Time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _slots = new SemaphoreSlim(Math.Max(1, Options.MaxParallel));
        Vault = services.Vault ?? new NodeVault(services.Settings, null, null, null, Time);
    }

    public ProvisioningOptions Options { get; }

    public TimeProvider Time { get; }

    public IDeviceAutoAdd AutoAdd => _services.AutoAdd;

    public ITaskRunner Tasks => _services.Tasks;

    public IDeviceRepository Devices => _services.Devices;

    public IVapixClientFactory Vapix => _services.Vapix ?? NoVapix.Instance;

    public ITaskRunner? StarterTasks => Volatile.Read(ref _starterTasks);

    public NodeVault Vault { get; }

    public string Owner => OwnerName;

    /// <summary>The session while started; null while stopped.</summary>
    public Session? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>Most flows that held a place at the same time (tests).</summary>
    public int PeakParallel => Volatile.Read(ref _peak);

    /// <summary>The row version: rises with every change.</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Reads the stored session and rows (plugin start): a flow that ran ends Failed, queued ones run again.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var session = PluginJson.TryParse<Session>(await _services.Settings.GetAsync(SessionKey, ct).ConfigureAwait(false), ProvisioningJson.Options);
        var stored = await _services.Records.ListAsync(CamerasCollection, ct).ConfigureAwait(false);
        var requeue = new List<CameraRow>();
        lock (_gate)
        {
            _session = session;
            _sessionCts.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            foreach (var (_, json) in stored)
            {
                if (PluginJson.TryParse<CameraRow>(json, ProvisioningJson.Options) is not { } row)
                {
                    continue;
                }

                if (row.State == CameraState.Running || (row.State == CameraState.Queued && session is null))
                {
                    row = Fail(row, row.State == CameraState.Running ? RestartedText : StoppedText);
                    _dirty.Add(row.Mac);
                }
                else if (row.State == CameraState.Queued)
                {
                    requeue.Add(row);
                }

                _rows[row.Mac] = row;
            }
        }

        foreach (var row in requeue)
        {
            // A queued run again keeps to its red nodes; a camera never looked at starts with the look.
            var ran = row.Results.Any(r => r is not null);
            StartFlow(row.Mac, look: !ran, ran ? RedOrNotRun(row) : null);
        }

        if (session is not null)
        {
            TakeActiveLeases();
        }

        _pollLoop ??= Task.Run(() => PollLoopAsync(_life.Token), CancellationToken.None);
        _publishLoop ??= Task.Run(() => PublishLoopAsync(_life.Token), CancellationToken.None);
        Signal();
    }

    /// <summary>
    /// Starts a session with <paramref name="profile"/> (checked by the caller): refused while one runs or without a running
    /// DHCP server. The rows of the last session go; the cameras with an active lease of the DHCP server are taken at once.
    /// </summary>
    public Task StartAsync(Profile profile, string startedBy, CancellationToken ct) => StartAsync(profile, startedBy, null, ct);

    /// <summary>
    /// Starts a session like <see cref="StartAsync(Profile, string, CancellationToken)"/>; <paramref name="starterTasks"/>: the
    /// tasks of the user who started it, kept in memory for the nodes that need that user's MyAxis sign-in.
    /// </summary>
    public async Task StartAsync(Profile profile, string startedBy, ITaskRunner? starterTasks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _control.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartLockedAsync(profile, startedBy, starterTasks, ct).ConfigureAwait(false);
        }
        finally
        {
            _control.Release();
        }
    }

    private async Task StartLockedAsync(Profile profile, string startedBy, ITaskRunner? starterTasks, CancellationToken ct)
    {
        if (Session is not null)
        {
            throw new InvalidOperationException(StartedAlready);
        }

        var dhcp = _services.Dhcp();
        if (dhcp is null || !dhcp.Summary().IsRunning)
        {
            throw new InvalidOperationException(DhcpFirst);
        }

        await _services.Records.ClearAsync(CamerasCollection, ct).ConfigureAwait(false);
        var session = new Session
        {
            StartedAt = Time.GetUtcNow(),
            StartedBy = startedBy,
            Profile = profile,
            AckAfter = dhcp.AcksSince(long.MaxValue, 1).Next,
        };
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException(StartedAlready);
            }

            foreach (var mac in _rows.Keys)
            {
                _removed.Add(mac);
            }

            _rows.Clear();
            _dirty.Clear();
            _session = session;
            _sessionCts.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _version++;
        }

        Volatile.Write(ref _starterTasks, starterTasks);
        await SaveSessionAsync(ct).ConfigureAwait(false);
        LogStarted(profile.Name, startedBy);
        TakeActiveLeases();
        Signal();
    }

    /// <summary>Ends the session: flows stop waiting (a running task of a node runs to its end), the reservations go.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _control.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _control.Release();
        }
    }

    private async Task StopLockedAsync(CancellationToken ct)
    {
        Task[] running;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_session is null)
            {
                throw new InvalidOperationException(NotStarted);
            }

            _session = null;
            _starterTasks = null;
            cts = _sessionCts;
            running = [.. _flows.Values.Select(f => f.Run)];
        }

        await cts.CancelAsync().ConfigureAwait(false); // the flows' tokens: a waiting approval ends with them
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // each flow wrote its own end
        }

        lock (_gate)
        {
            foreach (var row in _rows.Values.Where(r => r.State is CameraState.Queued or CameraState.Running && !_flows.ContainsKey(r.Mac)).ToList())
            {
                SetRow(Fail(row, StoppedText));
            }
        }

        await _services.Settings.SetAsync(SessionKey, null, ct).ConfigureAwait(false);
        LogStopped();
        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the DHCP server's new acknowledgements once (the poll loop calls it; tests call it directly).</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        if (_services.Dhcp() is not { } dhcp || Session is not { } session)
        {
            return;
        }

        var after = session.AckAfter;
        var changed = false;
        while (true)
        {
            var acks = dhcp.AcksSince(after, 1_000);
            if (acks.Next < after)
            {
                after = 0; // the DHCP server started again: its numbers start again
                changed = true;
                continue;
            }

            foreach (var ack in acks.Acks)
            {
                OnAck(ack.Mac, ack.Address);
            }

            changed |= acks.Next != after;
            after = acks.Next;
            if (acks.Acks.Count < 1_000)
            {
                break;
            }
        }

        if (changed)
        {
            lock (_gate)
            {
                if (_session is { } current && current.StartedAt == session.StartedAt)
                {
                    _session = current with { AckAfter = after };
                }
            }

            await SaveSessionAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The DHCP server acknowledged <paramref name="address"/> to <paramref name="mac"/>: a new Axis camera gets a row and its
    /// flow (Queued until a place is free); a known one only follows its address.
    /// </summary>
    public void OnAck(string mac, string address)
    {
        if (!TryMac(mac, out var value, out var text) || !AxisOui.IsAxis(value))
        {
            return;
        }

        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            if (_rows.TryGetValue(text, out var known))
            {
                // A camera on DHCP follows its address (a restart after an upgrade); a fixed IP gets no acknowledgement.
                if (known.Address != address)
                {
                    SetRow(known with { Address = address });
                }

                return;
            }

            if (_rows.Count >= Options.MaxCameras)
            {
                LogTooMany(text, Options.MaxCameras);
                return;
            }

            var now = Time.GetUtcNow();
            SetRow(new CameraRow
            {
                Mac = text,
                Serial = AxisOui.Serial(value),
                Address = address,
                State = CameraState.Queued,
                Results = new NodeResult?[_session.Profile.Nodes.Count],
                Seen = now,
                Updated = now,
            });
        }

        StartFlow(text, look: true, null);
    }

    /// <summary>Runs the red nodes (and those that never ran) of the failed cameras again, in flow order. Returns how many start.</summary>
    public int RunFailedAgain(IReadOnlyCollection<string> macs)
    {
        ArgumentNullException.ThrowIfNull(macs);
        var started = new List<(string Mac, List<int> Nodes)>();
        lock (_gate)
        {
            if (_session is null)
            {
                throw new InvalidOperationException(NotStarted);
            }

            foreach (var mac in macs.Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!_rows.TryGetValue(mac, out var row) || row.State != CameraState.Failed || _flows.ContainsKey(mac))
                {
                    continue;
                }

                SetRow(row with { State = CameraState.Queued, Message = null });
                started.Add((mac, RedOrNotRun(row)));
            }
        }

        foreach (var (mac, nodes) in started)
        {
            StartFlow(mac, look: false, nodes);
        }

        return started.Count;
    }

    /// <summary>Continue on the rows of cameras that wait for approval. Returns how many go on.</summary>
    public int Continue(IReadOnlyCollection<string> macs, string userName)
    {
        ArgumentNullException.ThrowIfNull(macs);
        var count = 0;
        lock (_gate)
        {
            foreach (var mac in macs.Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (_approvals.Remove(mac, out var approval) && approval.TrySetResult(userName))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Removes rows (the cameras stay in OADM); a camera whose flow runs keeps its row. Returns how many went.</summary>
    public int Remove(IReadOnlyCollection<string> macs)
    {
        ArgumentNullException.ThrowIfNull(macs);
        var count = 0;
        lock (_gate)
        {
            foreach (var mac in macs.Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!_flows.ContainsKey(mac) && _rows.Remove(mac))
                {
                    _dirty.Remove(mac);
                    _removed.Add(mac);
                    _version++;
                    count++;
                }
            }
        }

        Signal();
        return count;
    }

    /// <summary>One camera's row; null when there is none.</summary>
    public CameraRow? Find(string mac)
    {
        var key = Normalize(mac);
        lock (_gate)
        {
            return key is not null && _rows.TryGetValue(key, out var row) ? row : null;
        }
    }

    /// <summary>A page of rows by MAC address after <paramref name="after"/>, the total and the counts per state.</summary>
    public CamerasPage Page(string? after, int limit)
    {
        limit = Math.Clamp(limit, 1, 1_000);
        lock (_gate)
        {
            var sorted = _rows.Values.Where(r => after is null || string.CompareOrdinal(r.Mac, after) > 0)
                .OrderBy(r => r.Mac, StringComparer.Ordinal)
                .Take(limit + 1)
                .ToList();
            var more = sorted.Count > limit;
            var rows = more ? sorted[..limit] : sorted;
            return new CamerasPage(rows, _rows.Count, more ? rows[^1].Mac : null, CountsLocked(), _version);
        }
    }

    /// <summary>The counts of the rows per state.</summary>
    public CameraCounts Counts()
    {
        lock (_gate)
        {
            return CountsLocked();
        }
    }

    /// <summary>Completes when no flow runs any more (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = [.. _flows.Values.Select(f => f.Run)];
            }

            if (running.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ended
            }
        }
    }

    /// <summary>Writes the changed rows and publishes them (at most every <see cref="ProvisioningOptions.PublishInterval"/> by the loop).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<CameraRow> changed;
            List<string> removed;
            CameraCounts counts;
            long version;
            lock (_gate)
            {
                changed = [.. _dirty.Select(m => _rows.GetValueOrDefault(m)).OfType<CameraRow>()];
                removed = [.. _removed];
                _dirty.Clear();
                _removed.Clear();
                counts = CountsLocked();
                version = _version;
            }

            if (changed.Count == 0 && removed.Count == 0)
            {
                return;
            }

            var records = changed.Select(r => new KeyValuePair<string, string?>(Key(r.Mac), JsonSerializer.Serialize(r, ProvisioningJson.Options)))
                .Concat(removed.Select(m => new KeyValuePair<string, string?>(Key(m), null)))
                .ToList();
            try
            {
                await _services.Records.SetManyAsync(CamerasCollection, records, ct).ConfigureAwait(false);
            }
            catch
            {
                // Not stored: the rows stay dirty for the next round (a newer change or removal wins).
                lock (_gate)
                {
                    foreach (var row in changed.Where(r => _rows.ContainsKey(r.Mac) && !_removed.Contains(r.Mac)))
                    {
                        _dirty.Add(row.Mac);
                    }

                    foreach (var mac in removed.Where(m => !_rows.ContainsKey(m)))
                    {
                        _removed.Add(mac);
                    }
                }

                throw;
            }

            foreach (var part in changed.Chunk(500).DefaultIfEmpty([]))
            {
                _services.Events?.Publish(CamerasTopic, JsonSerializer.Serialize(new CamerasEvent(part, removed, counts, version), ProvisioningJson.Options));
                removed = [];
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    // ---- INodeHost ----

    /// <summary>
    /// Under a short lock: the camera's reservation of this session when it has one in the pool, else the first address of the
    /// pool that is not reserved in this session, no OADM device has and that answers neither ping nor TCP 80/443; it is
    /// reserved (and stored) before the lock ends, so two cameras never get the same address. The write runs outside.
    /// </summary>
    public async Task<AddressTake> TakeAddressAsync(string mac, SetIpSettings pool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var checkedPool = SetIpNode.Pool.Of(pool, out var error) ?? throw new ArgumentException(error);
        await _addressLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = Session ?? throw new OperationCanceledException(StoppedText);
            if (session.Reserved.TryGetValue(mac, out var mine) && Ipv4.TryParse(mine, out var value) && value >= checkedPool.First && value <= checkedPool.Last)
            {
                return new AddressTake(mine, [], Reused: true);
            }

            var reserved = session.Reserved.Values.ToHashSet(StringComparer.Ordinal);
            var managed = await ManagedAddressesAsync(ct).ConfigureAwait(false);
            var inUse = new List<string>();
            for (var address = checkedPool.First; address <= checkedPool.Last && address >= checkedPool.First; address++)
            {
                var text = Ipv4.Format(address);
                if (reserved.Contains(text))
                {
                    continue;
                }

                if (managed.Contains(text) || (await _services.Probe.ProbeAsync(text, ct).ConfigureAwait(false)).InUse)
                {
                    inUse.Add(text);
                    continue;
                }

                lock (_gate)
                {
                    var current = _session ?? throw new OperationCanceledException(StoppedText);
                    _session = current with { Reserved = new Dictionary<string, string>(current.Reserved, StringComparer.Ordinal) { [mac] = text } };
                }

                await SaveSessionAsync(ct).ConfigureAwait(false);
                LogReserved(text, mac);
                return new AddressTake(text, inUse, Reused: false);
            }

            return new AddressTake(null, inUse, Reused: false);
        }
        finally
        {
            _addressLock.Release();
        }
    }

    /// <summary>The addresses of every OADM device, read once per take (O(n) at 100,000 devices).</summary>
    internal async Task<HashSet<string>> ManagedAddressesAsync(CancellationToken ct)
    {
        var devices = await _services.Devices.ListAsync(ct).ConfigureAwait(false);
        var set = new HashSet<string>(devices.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            set.Add(device.Address);
        }

        return set;
    }

    public async Task<int> NumberAsync(string mac, CancellationToken ct)
    {
        int number;
        lock (_gate)
        {
            var current = _session ?? throw new OperationCanceledException(StoppedText);
            if (current.Numbers.TryGetValue(mac, out var known))
            {
                return known;
            }

            number = current.Numbers.Count;
            _session = current with { Numbers = new Dictionary<string, int>(current.Numbers, StringComparer.Ordinal) { [mac] = number } };
        }

        await SaveSessionAsync(ct).ConfigureAwait(false);
        return number;
    }

    public async Task<string> WaitForApprovalAsync(string mac, CancellationToken ct)
    {
        var approval = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Flow? flow;
        lock (_gate)
        {
            _approvals[mac] = approval;
            _flows.TryGetValue(mac, out flow);
            Update(mac, r => r with { WaitingForApproval = true });
        }

        ReleasePlace(flow);
        try
        {
            using var registration = ct.Register(() => approval.TrySetCanceled(ct));
            var user = await approval.Task.ConfigureAwait(false);
            await TakePlaceAsync(flow, ct).ConfigureAwait(false);
            return user;
        }
        finally
        {
            lock (_gate)
            {
                _approvals.Remove(mac);
                Update(mac, r => r with { WaitingForApproval = false });
            }
        }
    }

    public void DeviceAdded(string mac, Guid deviceId, string? model)
    {
        lock (_gate)
        {
            Update(mac, r => r with { DeviceId = deviceId, Model = model ?? r.Model });
        }
    }

    public void AddressChanged(string mac, string address)
    {
        lock (_gate)
        {
            Update(mac, r => r with { Address = address });
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Stopping: the rows are stored again on the next change.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFlushFailed(ex);
        }

        _disposing = true;
        Task[] running;
        lock (_gate)
        {
            running = [.. _flows.Values.Select(f => f.Run)];
        }

        await _life.CancelAsync().ConfigureAwait(false);
        foreach (var task in running.Concat([_pollLoop, _publishLoop]).OfType<Task>())
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        _life.Dispose();
        _sessionCts.Dispose();
        _slots.Dispose();
        _addressLock.Dispose();
        _sessionWrite.Dispose();
        _flushLock.Dispose();
        _control.Dispose();
    }

    // ---- flows ----

    private void StartFlow(string mac, bool look, IReadOnlyList<int>? nodes)
    {
        lock (_gate)
        {
            if (_session is not { } session || _flows.ContainsKey(mac))
            {
                return;
            }

            var flow = new Flow(CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token));
            _flows[mac] = flow;
            flow.Run = Task.Run(() => RunFlowAsync(mac, session.Profile, look, nodes, flow), CancellationToken.None);
        }
    }

    private async Task RunFlowAsync(string mac, Profile profile, bool look, IReadOnlyList<int>? nodes, Flow flow)
    {
        var ct = flow.Cts.Token;
        try
        {
            await TakePlaceAsync(flow, ct).ConfigureAwait(false);
            if (look && !await LookAsync(mac, ct).ConfigureAwait(false))
            {
                return;
            }

            nodes ??= [.. Enumerable.Range(0, profile.Nodes.Count)];
            foreach (var index in nodes)
            {
                var node = profile.Nodes[index];
                var kind = _registry.Find(node.Kind);
                CameraRow camera;
                lock (_gate)
                {
                    camera = Update(mac, r => r with { State = CameraState.Running, NodeIndex = index, NodeName = kind?.Name })
                        ?? throw new OperationCanceledException(); // removed meanwhile
                }

                var result = kind is null
                    ? NodeResult.Error("This OADM version does not know this node.")
                    : kind.NeedsDevice && camera.DeviceId is null
                        ? NodeResult.Error(NodeTexts.NotInOadm)
                        : await RunNodeAsync(kind, new NodeRun(this, camera, profile, node), ct).ConfigureAwait(false);
                lock (_gate)
                {
                    Update(mac, r => r with { Results = With(r.Results, index, result) });
                }

                if (result.State == NodeState.Error && (kind?.AlwaysStops != false || !node.ContinueOnError))
                {
                    break;
                }
            }

            lock (_gate)
            {
                Update(mac, r => r with { State = EndOf(r.Results), WaitingForApproval = false });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposing)
        {
            if (!_disposing)
            {
                lock (_gate)
                {
                    Update(mac, r => Fail(r, StoppedText));
                }
            }
        }
#pragma warning disable CA1031 // One camera's failure never stops the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFlowFailed(ex, mac);
            lock (_gate)
            {
                Update(mac, r => Fail(r with { State = CameraState.Running }, "The flow failed: " + ex.Message));
            }
        }
        finally
        {
            ReleasePlace(flow);
            lock (_gate)
            {
                _flows.Remove(mac);
            }

            flow.Cts.Dispose();
            Signal();
        }
    }

    /// <summary>Is the camera factory-new? Waits up to <see cref="ProvisioningOptions.AnswerTimeout"/> for it to answer. False: the flow ends here.</summary>
    private async Task<bool> LookAsync(string mac, CancellationToken ct)
    {
        var end = Time.GetUtcNow() + Options.AnswerTimeout;
        while (true)
        {
            CameraRow row;
            lock (_gate)
            {
                row = Update(mac, r => r with { State = CameraState.Running, NodeName = null }) ?? throw new OperationCanceledException();
            }

            DeviceLook look;
            try
            {
                look = await AutoAdd.LookAsync(row.Address, row.Serial, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                lock (_gate)
                {
                    Update(mac, r => r with { State = CameraState.Failed, Message = DeviceFirstSetupTexts.NotSupported });
                }

                return false;
            }

            switch (look.Result)
            {
                case DeviceLookResult.FactoryNew:
                    lock (_gate)
                    {
                        Update(mac, r => r with { Model = look.Model ?? r.Model, DeviceId = look.DeviceId ?? r.DeviceId });
                    }

                    return true;
                case DeviceLookResult.Unreachable when Time.GetUtcNow() < end:
                    await Task.Delay(Options.AnswerRetry, Time, ct).ConfigureAwait(false);
                    continue;
                case DeviceLookResult.Unreachable:
                    lock (_gate)
                    {
                        Update(mac, r => r with
                        {
                            State = CameraState.Failed,
                            NodeIndex = 0,
                            Results = With(r.Results, 0, NodeResult.Error($"The camera did not answer at {r.Address} within {Options.AnswerTimeout.TotalMinutes:0} minutes.")),
                        });
                    }

                    return false;
                default:
                    lock (_gate)
                    {
                        Update(mac, r => r with { State = CameraState.NotNew, Model = look.Model ?? r.Model, DeviceId = look.DeviceId ?? r.DeviceId, Message = NotNewText });
                    }

                    LogNotNew(mac, look.Message);
                    return false;
            }
        }
    }

    private async Task<NodeResult> RunNodeAsync(IProvisioningNode node, NodeRun run, CancellationToken ct)
    {
        try
        {
            return await node.RunAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A node's failure is this camera's red node; the other cameras go on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNodeFailed(ex, node.Name, run.Camera.Mac);
            return NodeResult.Error($"{node.Name} failed: {ex.Message}");
        }
    }

    private async Task TakePlaceAsync(Flow? flow, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        if (flow is not null)
        {
            flow.HoldsPlace = true;
        }

        var now = Interlocked.Increment(ref _holding);
        int peak;
        do
        {
            peak = Volatile.Read(ref _peak);
        }
        while (now > peak && Interlocked.CompareExchange(ref _peak, now, peak) != peak);
    }

    private void ReleasePlace(Flow? flow)
    {
        if (flow is { HoldsPlace: true })
        {
            flow.HoldsPlace = false;
            Interlocked.Decrement(ref _holding);
            _slots.Release();
        }
    }

    // ---- rows (under _gate) ----

    private CameraRow? Update(string mac, Func<CameraRow, CameraRow> change)
    {
        if (!_rows.TryGetValue(mac, out var row))
        {
            return null;
        }

        return SetRow(change(row));
    }

    private CameraRow SetRow(CameraRow row)
    {
        _version++;
        row = row with { Version = _version, Updated = Time.GetUtcNow() };
        _rows[row.Mac] = row;
        _dirty.Add(row.Mac);
        _removed.Remove(row.Mac);
        Signal();
        return row;
    }

    private CameraCounts CountsLocked()
    {
        int queued = 0, running = 0, waiting = 0, done = 0, warnings = 0, failed = 0, notNew = 0;
        foreach (var row in _rows.Values)
        {
            switch (row.State)
            {
                case CameraState.Queued:
                    queued++;
                    break;
                case CameraState.Running when row.WaitingForApproval:
                    waiting++;
                    break;
                case CameraState.Running:
                    running++;
                    break;
                case CameraState.Done:
                    done++;
                    break;
                case CameraState.DoneWithWarnings:
                    warnings++;
                    break;
                case CameraState.Failed:
                    failed++;
                    break;
                default:
                    notNew++;
                    break;
            }
        }

        return new CameraCounts(_rows.Count, queued, running, waiting, done, warnings, failed, notNew);
    }

    private static CameraRow Fail(CameraRow row, string message)
    {
        var results = row.Results;
        if (row.NodeIndex >= 0 && row.NodeIndex < results.Count && row.State == CameraState.Running)
        {
            results = With(results, row.NodeIndex, NodeResult.Error(message));
        }

        return row with { State = CameraState.Failed, WaitingForApproval = false, Message = message, Results = results };
    }

    private static NodeResult?[] With(IReadOnlyList<NodeResult?> results, int index, NodeResult result)
    {
        var copy = results.ToArray();
        if (index >= 0 && index < copy.Length)
        {
            copy[index] = result;
        }

        return copy;
    }

    /// <summary>The nodes of Run failed again: the red ones and those that never ran, in flow order.</summary>
    private static List<int> RedOrNotRun(CameraRow row)
    {
        var nodes = new List<int>();
        for (var i = 0; i < row.Results.Count; i++)
        {
            if (row.Results[i] is null or { State: NodeState.Error })
            {
                nodes.Add(i);
            }
        }

        return nodes;
    }

    private static CameraState EndOf(IReadOnlyList<NodeResult?> results)
    {
        var warning = false;
        foreach (var result in results)
        {
            if (result is null or { State: NodeState.Error })
            {
                return CameraState.Failed;
            }

            warning |= result.State == NodeState.Warning;
        }

        return warning ? CameraState.DoneWithWarnings : CameraState.Done;
    }

    private void TakeActiveLeases()
    {
        if (_services.Dhcp() is not { } dhcp)
        {
            return;
        }

        foreach (var lease in dhcp.ActiveLeases(AxisOui.Blocks, Options.MaxCameras))
        {
            OnAck(lease.Mac, lease.Address);
        }
    }

    private async Task SaveSessionAsync(CancellationToken ct)
    {
        await _sessionWrite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = Session;
            await _services.Settings.SetAsync(SessionKey, session is null ? null : JsonSerializer.Serialize(session, ProvisioningJson.Options), ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionWrite.Release();
        }
    }

    private void Signal()
    {
        lock (_gate)
        {
            _changed.TrySetResult();
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Options.LeasePoll, Time, ct).ConfigureAwait(false);
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop keeps going; the next round tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollFailed(ex);
            }
        }
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Task changed;
                lock (_gate)
                {
                    if (_changed.Task.IsCompleted)
                    {
                        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    changed = _dirty.Count > 0 || _removed.Count > 0 ? Task.CompletedTask : _changed.Task;
                }

                await changed.WaitAsync(ct).ConfigureAwait(false);
                await Task.Delay(Options.PublishInterval, Time, ct).ConfigureAwait(false);
                await FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop keeps going; the rows stay dirty for the next round.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFlushFailed(ex);
            }
        }
    }

    private static string Key(string mac) => mac.Replace(":", string.Empty, StringComparison.Ordinal);

    private static string? Normalize(string? mac) => TryMac(mac, out _, out var text) ? text : null;

    private static bool TryMac(string? mac, out ulong value, out string text)
    {
        value = 0;
        text = string.Empty;
        var hex = (mac ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        if (hex.Length != 12 || !ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        var upper = hex.ToUpperInvariant();
        text = string.Join(':', Enumerable.Range(0, 6).Select(i => upper.Substring(i * 2, 2)));
        return true;
    }

    private sealed class Flow(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public Task Run { get; set; } = Task.CompletedTask;

        public bool HoldsPlace { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning started with profile {Profile} by {User}")]
    private partial void LogStarted(string profile, string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Mac} is not new, left as it is ({Reason})")]
    private partial void LogNotNew(string mac, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Address} reserved for {Mac}")]
    private partial void LogReserved(string address, string mac);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: {Mac} left out, the session has {Max} cameras already")]
    private partial void LogTooMany(string mac, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: {Node} failed on {Mac}")]
    private partial void LogNodeFailed(Exception ex, string node, string mac);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: the flow of {Mac} failed")]
    private partial void LogFlowFailed(Exception ex, string mac);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: reading the DHCP server's leases failed")]
    private partial void LogPollFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: storing the camera rows failed")]
    private partial void LogFlushFailed(Exception ex);
}

/// <summary>The rows per state.</summary>
public sealed record CameraCounts(int Total, int Queued, int Running, int Waiting, int Done, int DoneWithWarnings, int Failed, int NotNew);

/// <summary>A page of the camera table: the rows after the given MAC address, the total, the key of the last row (null at the end).</summary>
public sealed record CamerasPage(IReadOnlyList<CameraRow> Rows, int Total, string? Next, CameraCounts Counts, long Version);

/// <summary>Event <c>cameras</c>: the changed rows, the removed MAC addresses, the counts (at most every 500 ms, 500 rows per event).</summary>
public sealed record CamerasEvent(IReadOnlyList<CameraRow> Changed, IReadOnlyList<string> Removed, CameraCounts Counts, long Version);

/// <summary>A host without VAPIX for plugins: every node that needs it says so.</summary>
internal sealed class NoVapix : IVapixClientFactory
{
    public static readonly NoVapix Instance = new();

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        throw new InvalidOperationException("OADM cannot reach the camera from this plugin.");
}
