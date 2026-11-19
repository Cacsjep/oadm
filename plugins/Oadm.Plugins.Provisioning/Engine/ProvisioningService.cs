using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
    Func<IDhcpServerLeases?> Dhcp,
    IAddressProbe Probe,
    IVapixClientFactory? Vapix = null,
    NodeVault? Vault = null);

/// <summary>
/// Provisioning (docs/spec/provisioning.md): while it is on, every factory-new camera OADM finds gets one task "Provision with
/// &lt;profile&gt;" (<see cref="ProvisioningTaskPlugin"/>) that runs the active profile node by node, one named step per node.
/// What starts a task: the server's report of a device stored as Password not set or turned Password not set
/// (<see cref="IDeviceAutoAdd.WatchFactoryNew"/>: the add page, an import, the waiting list, the automatic add, a reset camera),
/// and a lease of OADM's DHCP server to an Axis MAC address (the camera is asked whether it is new and added first). Devices at
/// a site get none. A camera already in a running provisioning task gets no second one. At most
/// <see cref="ProvisioningOptions.MaxParallel"/> tasks run at once (the task plugin's limit; more wait as Queued). The state
/// (on, who, the last lease read) and the reserved pool addresses are stored; the last run of each device too (records
/// <c>runs</c>, the profile by its snapshot), so Run again runs only the failed nodes. All time through <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class ProvisioningService : INodeHost, IAsyncDisposable
{
    public const string StateKey = "state";
    public const string ReservedKey = "reserved";
    public const string RunsCollection = "runs";
    public const string SnapshotsCollection = "snapshots";
    public const string OwnerName = "Provisioning";

    /// <summary>The first step of a task: the camera is asked whether it has a password yet.</summary>
    public const string CheckStep = "Check that the camera is new";

    public const string AlreadyOn = "Provisioning is on already.";
    public const string AlreadyOff = "Provisioning is off already.";
    public const string HasPasswordText = "The camera has a password already. Nothing was changed.";
    public const string NewText = "The camera has no password yet.";

    private readonly EngineServices _services;
    private readonly Func<CancellationToken, Task<Profile?>> _runnableProfile;
    private readonly NodeRegistry _registry;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Guid> _active = [];
    private readonly HashSet<Guid> _starting = [];
    private readonly HashSet<string> _looking = new(StringComparer.Ordinal);
    private readonly Channel<(Guid Id, bool Reset)> _found = Channel.CreateUnbounded<(Guid, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<(string Serial, string Address)> _leases = Channel.CreateUnbounded<(string, string)>();
    private readonly SemaphoreSlim _addressLock = new(1, 1);
    private readonly SemaphoreSlim _stateWrite = new(1, 1);
    private readonly SemaphoreSlim _runWrite = new(1, 1);
    private readonly SemaphoreSlim _snapshots = new(1, 1);
    private readonly HashSet<string> _runningSerials = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource _onCts = new();
    private ProvisioningState _state = new();
    private Dictionary<string, string> _reserved = new(StringComparer.Ordinal);
    private ITaskRunner? _starterTasks;
    private IDisposable? _watch;
    private readonly List<Task> _loops = [];

    /// <param name="services">The host's services.</param>
    /// <param name="runnableProfile">The active profile when it has no errors; null: no task starts.</param>
    /// <param name="registry">The node kinds.</param>
    /// <param name="options">Timings and limits.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The server log.</param>
    public ProvisioningService(EngineServices services, Func<CancellationToken, Task<Profile?>> runnableProfile, NodeRegistry? registry = null,
        ProvisioningOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _runnableProfile = runnableProfile ?? throw new ArgumentNullException(nameof(runnableProfile));
        _registry = registry ?? NodeRegistry.Default;
        Options = options ?? new ProvisioningOptions();
        Time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
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

    /// <summary>Whether provisioning is on, who turned it on and when.</summary>
    public ProvisioningState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsOn => State.On;

    /// <summary>Provisioning tasks started and not ended yet (as far as this server knows).</summary>
    public int ActiveTasks
    {
        get
        {
            lock (_gate)
            {
                return _active.Count + _starting.Count;
            }
        }
    }

    /// <summary>No reported device waits to get its task and none is being started from a report (tests).</summary>
    public bool ReportsDone => Volatile.Read(ref _queuedReports) == 0 && Volatile.Read(ref _startingReports) == 0;

    private int _queuedReports;
    private int _startingReports;

    /// <summary>Cameras of DHCP leases being checked now or waiting for it (tests).</summary>
    public int PendingLooks
    {
        get
        {
            lock (_gate)
            {
                return _looking.Count;
            }
        }
    }

    /// <summary>Reads the stored state and reservations, watches the server's factory-new devices and starts the loops.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var state = PluginJson.TryParse<ProvisioningState>(await _services.Settings.GetAsync(StateKey, ct).ConfigureAwait(false), ProvisioningJson.Options) ?? new ProvisioningState();
        var reserved = PluginJson.TryParse<Dictionary<string, string>>(await _services.Settings.GetAsync(ReservedKey, ct).ConfigureAwait(false), ProvisioningJson.Options);
        lock (_gate)
        {
            _state = state;
            _reserved = new Dictionary<string, string>(reserved ?? [], StringComparer.Ordinal);
        }

        try
        {
            _watch ??= AutoAdd.WatchFactoryNew(OnFactoryNew);
        }
        catch (NotSupportedException)
        {
            LogNoWatch(); // an older host: only the DHCP server's leases start tasks
        }

        if (_loops.Count == 0)
        {
            var life = _life.Token;
            _loops.Add(Task.Run(() => FoundLoopAsync(life), CancellationToken.None));
            _loops.Add(Task.Run(() => PollLoopAsync(life), CancellationToken.None));
            for (var i = 0; i < Math.Max(1, Options.MaxLooks); i++)
            {
                _loops.Add(Task.Run(() => LookLoopAsync(life), CancellationToken.None));
            }
        }
    }

    /// <summary>
    /// Turns provisioning on (the caller checked the profile and the rights): from now on every factory-new camera gets its task.
    /// The cameras that hold a lease of OADM's DHCP server now are checked at once. <paramref name="starterTasks"/>: the tasks
    /// of the administrator, kept in memory for the nodes that need their MyAxis sign-in.
    /// </summary>
    public async Task TurnOnAsync(string user, ITaskRunner? starterTasks, CancellationToken ct)
    {
        var dhcp = _services.Dhcp();
        lock (_gate)
        {
            if (_state.On)
            {
                throw new InvalidOperationException(AlreadyOn);
            }

            _state = new ProvisioningState { On = true, OnBy = user, OnAt = Time.GetUtcNow(), AckAfter = dhcp?.AcksSince(long.MaxValue, 1).Next ?? 0 };
            _onCts.Dispose();
            _onCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        }

        Volatile.Write(ref _starterTasks, starterTasks);
        await SaveStateAsync(ct).ConfigureAwait(false);
        LogTurnedOn(user);
        if (dhcp is not null)
        {
            foreach (var lease in dhcp.ActiveLeases(AxisOui.Blocks, Options.MaxWaitingLooks))
            {
                OnAck(lease.Mac, lease.Address);
            }
        }
    }

    /// <summary>
    /// Turns provisioning off: no new task starts; running and queued tasks run to their end (a person cancels them in the tasks
    /// pane) and Run again still works, so the starter's tasks stay for their nodes until the next On replaces them.
    /// </summary>
    public async Task TurnOffAsync(CancellationToken ct)
    {
        CancellationTokenSource on;
        lock (_gate)
        {
            if (!_state.On)
            {
                throw new InvalidOperationException(AlreadyOff);
            }

            _state = _state with { On = false };
            on = _onCts;
        }

        await on.CancelAsync().ConfigureAwait(false); // the checks of leases stop
        await SaveStateAsync(ct).ConfigureAwait(false);
        LogTurnedOff();
    }

    // ---- what starts a task ----

    /// <summary>
    /// The server stored a device as Password not set, or a managed one turned Password not set: queued, never blocks. A device
    /// whose last run failed keeps that run for Run again unless it was just added or had a password in between (reset).
    /// </summary>
    public void OnFactoryNew(FactoryNewDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.AtSite && IsOn)
        {
            // Sites: no provisioning in the first version.
            Interlocked.Increment(ref _queuedReports);
            if (!_found.Writer.TryWrite((device.DeviceId, device.Added || device.HadPassword)))
            {
                Interlocked.Decrement(ref _queuedReports); // stopping
            }
        }
    }

    /// <summary>
    /// The DHCP server acknowledged <paramref name="address"/> to <paramref name="mac"/>: an Axis camera is checked (queued, at
    /// most <see cref="ProvisioningOptions.MaxLooks"/> at once) and, when it is new, added and given its task.
    /// </summary>
    public void OnAck(string mac, string address)
    {
        var hex = (mac ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || !AxisOui.IsAxis(value)
            || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var serial = AxisOui.Serial(value);
        lock (_gate)
        {
            if (!_state.On || _looking.Contains(serial))
            {
                return;
            }

            if (_looking.Count >= Options.MaxWaitingLooks)
            {
                LogTooMany(serial, Options.MaxWaitingLooks);
                return;
            }

            _looking.Add(serial);
        }

        _leases.Writer.TryWrite((serial, address.Trim()));
    }

    /// <summary>Reads the DHCP server's new acknowledgements once (the poll loop calls it; tests call it directly).</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        if (_services.Dhcp() is not { } dhcp || State is not { On: true } state)
        {
            return;
        }

        var after = state.AckAfter;
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
                if (_state.On)
                {
                    _state = _state with { AckAfter = after };
                }
            }

            await SaveStateAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the task of each device (one batch, one task per device) while provisioning is on and the active profile has no
    /// errors; a device whose provisioning task runs or waits gets none. Returns how many started.
    /// </summary>
    public async Task<int> StartForAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (!IsOn || deviceIds.Count == 0)
        {
            return 0;
        }

        var profile = await _runnableProfile(ct).ConfigureAwait(false);
        if (profile is null)
        {
            LogNoProfile(deviceIds.Count);
            return 0;
        }

        var payload = JsonSerializer.Serialize(new RunPayload { Profile = profile }, ProvisioningJson.Options);
        return await StartAsync([(Payload: payload, Profile: profile, Ids: Take(deviceIds), Looked: false)], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An automatic start (a report, a lease): a device whose last run failed or did not end keeps that run for Run again, unless
    /// its camera was reset since (<c>Reset</c>: just added, or it had a password in between). The others start as
    /// <see cref="StartForAsync"/> starts them.
    /// </summary>
    private async Task<int> StartFoundAsync(List<(Guid Id, bool Reset)> found, CancellationToken ct)
    {
        var ids = new List<Guid>(found.Count);
        foreach (var (id, reset) in found)
        {
            if (reset || await ReadRunAsync(id, ct).ConfigureAwait(false) is not { } run || !run.CanRunAgain())
            {
                ids.Add(id);
            }
            else
            {
                LogKeptFailedRun(id);
            }
        }

        return await StartForAsync(ids, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run again: for each device whose last provisioning run failed and whose task does not run, a new task with the same
    /// profile that runs only the red nodes and those that never ran, in order (a camera that failed the first check is
    /// checked and run again whole). Works while provisioning is off too: a person asks for it.
    /// </summary>
    public async Task<RunAgainReply> RunAgainAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var groups = new Dictionary<string, (Profile Profile, List<Guid> Ids, bool Looked)>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var id in deviceIds.Distinct())
        {
            var plan = await PlanAgainAsync(id, ct).ConfigureAwait(false);
            if (plan is not { } again)
            {
                skipped++;
                continue;
            }

            var payload = JsonSerializer.Serialize(new RunPayload { Profile = again.Profile, Nodes = again.Nodes, Look = !again.Looked }, ProvisioningJson.Options);
            if (!groups.TryGetValue(payload, out var group))
            {
                groups[payload] = group = (again.Profile, [], again.Looked);
            }

            group.Ids.Add(id);
        }

        var batches = groups.Select(g => (Payload: g.Key, g.Value.Profile, Ids: Take(g.Value.Ids), g.Value.Looked)).ToList();
        var requested = groups.Values.Sum(g => g.Ids.Count);
        var started = await StartAsync(batches, ct).ConfigureAwait(false);
        return new RunAgainReply(started, skipped + requested - started);
    }

    /// <summary>What Run again would run for one device, or null when it cannot (no failed run, its task runs, the profile is gone).</summary>
    public async Task<(Profile Profile, IReadOnlyList<int>? Nodes, bool Looked)?> PlanAgainAsync(Guid deviceId, CancellationToken ct)
    {
        if (IsBusy(deviceId) || await ReadRunAsync(deviceId, ct).ConfigureAwait(false) is not { } run
            || PluginJson.TryParse<Profile>(await _services.Records.GetAsync(SnapshotsCollection, run.Snapshot, ct).ConfigureAwait(false), ProvisioningJson.Options) is not { } profile
            || run.Results.Count != profile.Nodes.Count)
        {
            return null;
        }

        if (!run.Looked)
        {
            return (profile, null, false);
        }

        var failed = run.Failed();
        return failed.Count == 0 ? null : (profile, failed, true);
    }

    /// <summary>The device's last provisioning run; null when it has none.</summary>
    public async Task<RunRecord?> ReadRunAsync(Guid deviceId, CancellationToken ct) =>
        PluginJson.TryParse<RunRecord>(await _services.Records.GetAsync(RunsCollection, Key(deviceId), ct).ConfigureAwait(false), ProvisioningJson.Options);

    /// <summary>The devices of <paramref name="deviceIds"/> that get a task now: marked as starting (none of them twice).</summary>
    private List<Guid> Take(IEnumerable<Guid> deviceIds)
    {
        var taken = new List<Guid>();
        lock (_gate)
        {
            foreach (var id in deviceIds.Distinct())
            {
                if (!IsBusyLocked(id))
                {
                    _starting.Add(id);
                    taken.Add(id);
                }
            }
        }

        return taken;
    }

    /// <summary>Stores the run of each device, then starts one task per device for each batch.</summary>
    private async Task<int> StartAsync(IReadOnlyList<(string Payload, Profile Profile, List<Guid> Ids, bool Looked)> batches, CancellationToken ct)
    {
        var started = 0;
        foreach (var (payload, profile, ids, looked) in batches)
        {
            if (ids.Count == 0)
            {
                continue;
            }

            try
            {
                // The snapshot and the runs that name it under one lock: the prune never sees the one without the other.
                await _snapshots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var snapshot = await SnapshotAsync(profile, ct).ConfigureAwait(false);
                    if (!looked)
                    {
                        var now = Time.GetUtcNow();
                        var fresh = new RunRecord { Snapshot = snapshot, ProfileName = profile.Name, Results = new NodeResult?[profile.Nodes.Count], Started = now };
                        var json = JsonSerializer.Serialize(fresh, ProvisioningJson.Options);
                        await WriteRunsAsync([.. ids.Select(id => new KeyValuePair<string, string?>(Key(id), json))], ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _snapshots.Release();
                }

                var taskIds = await Tasks.RunAsync(ProvisioningInfo.TaskPluginId, ids, payload, Owner, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    for (var i = 0; i < Math.Min(ids.Count, taskIds.Count); i++)
                    {
                        // The task may have started (and even ended) already: it removes its own entry when it ends.
                        if (!_ended.Remove(taskIds[i]))
                        {
                            _active[ids[i]] = taskIds[i];
                        }
                    }
                }

                started += taskIds.Count;
                LogStarted(taskIds.Count, profile.Name);
            }
#pragma warning disable CA1031 // A batch that cannot start is logged; the next camera found tries again.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogStartFailed(ex, ids.Count);
            }
            finally
            {
                lock (_gate)
                {
                    foreach (var id in ids)
                    {
                        _starting.Remove(id);
                    }
                }
            }
        }

        return started;
    }

    private readonly HashSet<Guid> _ended = [];

    private bool IsBusy(Guid deviceId)
    {
        lock (_gate)
        {
            return IsBusyLocked(deviceId);
        }
    }

    private bool IsBusyLocked(Guid deviceId)
    {
        if (_starting.Contains(deviceId))
        {
            return true;
        }

        if (!_active.TryGetValue(deviceId, out var taskId))
        {
            return false;
        }

        if (Tasks.IsActive(taskId) == false)
        {
            _active.Remove(deviceId); // ended without running (cancelled while queued)
            return false;
        }

        return true;
    }

    // ---- the task ----

    /// <summary>
    /// One provisioning task on one camera (<see cref="ProvisioningTaskPlugin"/>): the check that it is new (its first step,
    /// skipped by Run again after it passed), then one step per node in order. A node's Error stops the task when the node
    /// always stops (Set password) or its Continue on error is off; a Warning always continues. The task fails when a node
    /// failed, ends with warnings for a yellow one. Each result is stored for Run again.
    /// </summary>
    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var run = PluginJson.TryParse<RunPayload>(payloadJson, ProvisioningJson.Options) ?? throw new InvalidOperationException("The task has no profile.");
        var profile = run.Profile;
        lock (_gate)
        {
            _active[device.Id] = ctx.TaskId;
            _runningSerials.Add(device.Serial);
        }

        try
        {
            await ExecuteCoreAsync(ctx, device, run, profile, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_active.TryGetValue(device.Id, out var current) && current == ctx.TaskId)
                {
                    _active.Remove(device.Id);
                }

                _runningSerials.Remove(device.Serial);
                if (_starting.Contains(device.Id))
                {
                    _ended.Add(ctx.TaskId); // ended before its start was noted: the start must not note it as running
                }
            }
        }
    }

    private async Task ExecuteCoreAsync(ITaskExecutionContext ctx, IDeviceInfo device, RunPayload run, Profile profile, CancellationToken ct)
    {
        var camera = new CameraInfo(device.Serial, device.Address, device.Model, device.Id);
        var indexes = (run.Nodes ?? [.. Enumerable.Range(0, profile.Nodes.Count)]).Where(i => i >= 0 && i < profile.Nodes.Count).Distinct().Order().ToList();
        var steps = indexes.Select(i => StepNameOf(profile.Nodes[i])).ToList();
        if (run.Look)
        {
            steps.Insert(0, CheckStep);
        }

        ctx.PlanSteps([.. steps]);
        var record = await ReadRunAsync(device.Id, ct).ConfigureAwait(false) is { } stored && stored.Results.Count == profile.Nodes.Count
            ? stored with { TaskId = ctx.TaskId }
            : await NewRunAsync(device.Id, ctx.TaskId, profile, ct).ConfigureAwait(false);

        if (run.Look)
        {
            using var step = ctx.BeginStep(CheckStep);
            var (ok, message, model) = await CheckNewAsync(camera, ct).ConfigureAwait(false);
            record = record with { Looked = ok };
            await SaveRunAsync(device.Id, record, ct).ConfigureAwait(false);
            if (!ok)
            {
                step.Fail(message);
                ctx.Log(TaskLogLevel.Error, message);
                throw new InvalidOperationException(message);
            }

            step.Complete(message);
            camera = camera with { Model = model ?? camera.Model };
        }
        else
        {
            await SaveRunAsync(device.Id, record, ct).ConfigureAwait(false);
        }

        var failures = new List<string>();
        foreach (var index in indexes)
        {
            var node = profile.Nodes[index];
            var kind = _registry.Find(node.Kind);
            using var step = ctx.BeginStep(StepNameOf(node));
            var result = kind is null
                ? NodeResult.Error(UnknownNode)
                : await RunNodeAsync(kind, new NodeRun(this, camera, profile, node), ct).ConfigureAwait(false);
            record = record with { Results = With(record.Results, index, result) };
            await SaveRunAsync(device.Id, record, ct).ConfigureAwait(false);
            switch (result.State)
            {
                case NodeState.Success:
                    step.Complete(result.Message);
                    ctx.Log(TaskLogLevel.Info, $"{kind?.Name}: {result.Message}");
                    break;
                case NodeState.NotNeeded:
                    step.Skip(result.Message);
                    ctx.Log(TaskLogLevel.Info, $"{kind?.Name}: {result.Message}");
                    break;
                case NodeState.Warning:
                    step.Warn(result.Message);
                    break;
                default:
                    step.Fail(result.Message);
                    ctx.Log(TaskLogLevel.Error, $"{kind?.Name ?? node.Kind.ToString()}: {result.Message}");
                    failures.Add(result.Message);
                    break;
            }

            if (result.State == NodeState.Error && (kind?.AlwaysStops != false || !node.ContinueOnError))
            {
                break;
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(failures.Count == 1 ? failures[0] : $"{failures.Count} steps failed. {failures[0]}");
        }
    }

    public const string UnknownNode = "This OADM version does not know this node.";

    /// <summary>The run of a task that found none of its own: its snapshot and the run stored at once, under the snapshot lock.</summary>
    private async Task<RunRecord> NewRunAsync(Guid deviceId, Guid taskId, Profile profile, CancellationToken ct)
    {
        await _snapshots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = new RunRecord
            {
                TaskId = taskId,
                Snapshot = await SnapshotAsync(profile, ct).ConfigureAwait(false),
                ProfileName = profile.Name,
                Results = new NodeResult?[profile.Nodes.Count],
                Started = Time.GetUtcNow(),
            };
            await SaveRunAsync(deviceId, record, ct).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _snapshots.Release();
        }
    }

    /// <summary>The step name of a node ("Set the network settings").</summary>
    public string StepNameOf(FlowNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _registry.Find(node.Kind)?.StepName ?? "Run " + node.Kind;
    }

    /// <summary>
    /// Is the camera factory-new? Asked of the camera itself (the server waits while it is still starting); no answer is asked
    /// again for up to <see cref="ProvisioningOptions.AnswerTimeout"/>. Never a write.
    /// </summary>
    private async Task<(bool Ok, string Message, string? Model)> CheckNewAsync(CameraInfo camera, CancellationToken ct)
    {
        var end = Time.GetUtcNow() + Options.AnswerTimeout;
        while (true)
        {
            DeviceLook look;
            try
            {
                look = await AutoAdd.LookAsync(camera.Address, camera.Serial, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return (false, DeviceFirstSetupTexts.NotSupported, null);
            }
            catch (ArgumentException)
            {
                return (false, NodeTexts.NeedsIpAddress, null);
            }

            switch (look.Result)
            {
                case DeviceLookResult.FactoryNew:
                    return (true, NewText, look.Model);
                case DeviceLookResult.Unreachable when Time.GetUtcNow() < end:
                    await Task.Delay(Options.AnswerRetry, Time, ct).ConfigureAwait(false);
                    continue;
                case DeviceLookResult.Unreachable:
                    return (false, $"The camera did not answer at {camera.Address} within {Options.AnswerTimeout.TotalMinutes:0} minutes.", null);
                case DeviceLookResult.HasPassword:
                    return (false, HasPasswordText, look.Model);
                default:
                    return (false, NodeRules.Sentence(look.Message, "The camera did not say whether it is new.") + " Nothing was changed.", look.Model);
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
#pragma warning disable CA1031 // A node's failure is this camera's red step; the next nodes decide by Continue on error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNodeFailed(ex, node.Name, run.Camera.Serial);
            return NodeResult.Error($"{node.Name} failed: {ex.Message}");
        }
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

    // ---- INodeHost: the pool ----

    /// <summary>
    /// Under a short lock: the camera's own reservation when it is in the pool, else its current address when that is in the
    /// pool, else the first address of the pool that is not reserved for another camera, no other OADM device has and that
    /// answers neither ping nor TCP 80/443. It is reserved (and stored) before the lock ends, so two cameras never get the same
    /// address; the write runs outside. A reservation stays while a provisioning task runs for its camera or while its camera
    /// is an OADM device that does not have the address yet; the others are released here (the camera has it now, was removed
    /// from OADM, or was never added).
    /// </summary>
    public async Task<AddressTake> TakeAddressAsync(CameraInfo camera, NetworkNode.Pool pool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(pool);
        await _addressLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The devices read once per take (O(n) at 100,000 devices).
            var devices = await _services.Devices.ListAsync(ct).ConfigureAwait(false);
            var managed = new HashSet<string>(devices.Count, StringComparer.OrdinalIgnoreCase);
            var addressBySerial = new Dictionary<string, string>(devices.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                addressBySerial[device.Serial] = device.Address;
                if (device.Id != camera.DeviceId)
                {
                    managed.Add(device.Address);
                }
            }

            await ReleaseDoneAsync(addressBySerial, ct).ConfigureAwait(false);
            var reserved = Reserved;
            var others = reserved.Where(r => r.Key != camera.Serial).Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
            if (reserved.TryGetValue(camera.Serial, out var mine) && pool.Contains(mine))
            {
                return new AddressTake(mine, [], Reused: true);
            }

            if (pool.Contains(camera.Address) && !managed.Contains(camera.Address) && !others.Contains(camera.Address))
            {
                await ReserveAsync(camera.Serial, camera.Address, ct).ConfigureAwait(false);
                return new AddressTake(camera.Address, [], Reused: true);
            }

            var inUse = new List<string>();
            for (var address = pool.First; address <= pool.Last && address >= pool.First; address++)
            {
                var text = Ipv4.Format(address);
                if (others.Contains(text))
                {
                    continue;
                }

                if (managed.Contains(text) || (await _services.Probe.ProbeAsync(text, ct).ConfigureAwait(false)).InUse)
                {
                    inUse.Add(text);
                    continue;
                }

                await ReserveAsync(camera.Serial, text, ct).ConfigureAwait(false);
                LogReserved(text, camera.Serial);
                return new AddressTake(text, inUse, Reused: false);
            }

            return new AddressTake(null, inUse, Reused: false);
        }
        finally
        {
            _addressLock.Release();
        }
    }

    /// <summary>The reserved pool addresses by serial number (tests, the status).</summary>
    public IReadOnlyDictionary<string, string> Reserved
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_reserved, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// The Network node did not write <paramref name="address"/> (the camera refused before any change): its reservation is
    /// released, so the address is free for the next camera.
    /// </summary>
    public async Task ReleaseAddressAsync(CameraInfo camera, string address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(camera);
        await _addressLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? json = null;
            lock (_gate)
            {
                if (_reserved.TryGetValue(camera.Serial, out var mine) && string.Equals(mine, address, StringComparison.Ordinal))
                {
                    _reserved.Remove(camera.Serial);
                    json = JsonSerializer.Serialize(_reserved, ProvisioningJson.Options);
                }
            }

            if (json is not null)
            {
                await _services.Settings.SetAsync(ReservedKey, json, ct).ConfigureAwait(false);
                LogReleased(address, camera.Serial);
            }
        }
        finally
        {
            _addressLock.Release();
        }
    }

    /// <summary>
    /// Drops (and stores) the reservations that are done: no provisioning task runs for the camera and it is no OADM device
    /// (removed, never added) or has the address by now. Called under the address lock.
    /// </summary>
    private async Task ReleaseDoneAsync(Dictionary<string, string> addressBySerial, CancellationToken ct)
    {
        string? json = null;
        lock (_gate)
        {
            var done = _reserved
                .Where(r => !_runningSerials.Contains(r.Key)
                    && (!addressBySerial.TryGetValue(r.Key, out var now) || string.Equals(now, r.Value, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Key)
                .ToList();
            foreach (var serial in done)
            {
                _reserved.Remove(serial);
            }

            if (done.Count > 0)
            {
                json = JsonSerializer.Serialize(_reserved, ProvisioningJson.Options);
            }
        }

        if (json is not null)
        {
            await _services.Settings.SetAsync(ReservedKey, json, ct).ConfigureAwait(false);
        }
    }

    private async Task ReserveAsync(string serial, string address, CancellationToken ct)
    {
        string json;
        lock (_gate)
        {
            _reserved[serial] = address;
            json = JsonSerializer.Serialize(_reserved, ProvisioningJson.Options);
        }

        await _services.Settings.SetAsync(ReservedKey, json, ct).ConfigureAwait(false);
    }

    // ---- loops ----

    private async Task FoundLoopAsync(CancellationToken ct)
    {
        var batch = new List<(Guid Id, bool Reset)>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _found.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return;
                }

                batch.Clear();
                Interlocked.Increment(ref _startingReports);
                try
                {
                    while (batch.Count < 5_000 && _found.Reader.TryRead(out var found))
                    {
                        batch.Add(found);
                        Interlocked.Decrement(ref _queuedReports);
                    }

                    await StartFoundAsync(batch, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _startingReports);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop keeps going; the next device found tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStartFailed(ex, batch.Count);
            }
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

    private async Task LookLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            (string Serial, string Address) lease;
            try
            {
                lease = await _leases.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                CancellationToken on;
                lock (_gate)
                {
                    on = _onCts.Token;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, on);
                await LeaseAsync(lease.Serial, lease.Address, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // turned off or stopping
            }
#pragma warning disable CA1031 // One camera's failure never stops the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLeaseFailed(ex, lease.Serial);
            }
            finally
            {
                lock (_gate)
                {
                    _looking.Remove(lease.Serial);
                }
            }
        }
    }

    /// <summary>
    /// A camera of a lease: asked whether it is new (no answer is asked again for up to <see cref="ProvisioningOptions.AnswerTimeout"/>);
    /// a managed new one gets its task, an unmanaged new one is added (Password not set: the server's report starts its task,
    /// and so does this). A camera with a password is never changed nor added.
    /// </summary>
    private async Task LeaseAsync(string serial, string address, CancellationToken ct)
    {
        var end = Time.GetUtcNow() + Options.AnswerTimeout;
        while (IsOn)
        {
            var look = await AutoAdd.LookAsync(address, serial, ct).ConfigureAwait(false);
            switch (look.Result)
            {
                case DeviceLookResult.Unreachable when Time.GetUtcNow() < end:
                    await Task.Delay(Options.AnswerRetry, Time, ct).ConfigureAwait(false);
                    continue;
                case DeviceLookResult.FactoryNew when look.DeviceId is { } managed:
                    await StartFoundAsync([(managed, false)], ct).ConfigureAwait(false);
                    return;
                case DeviceLookResult.FactoryNew:
                    var added = await AutoAdd.AddAsync(address, serial, Owner, ct).ConfigureAwait(false);
                    if (added.DeviceId is { } id && added.Result == DeviceAutoAddResult.AddedPasswordNotSet)
                    {
                        await StartForAsync([id], ct).ConfigureAwait(false);
                    }
                    else
                    {
                        LogNotAdded(serial, added.Message);
                    }

                    return;
                default:
                    LogNotNew(serial, look.Message);
                    return;
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        await _stateWrite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _services.Settings.SetAsync(StateKey, JsonSerializer.Serialize(State, ProvisioningJson.Options), ct).ConfigureAwait(false);
        }
        finally
        {
            _stateWrite.Release();
        }
    }

    /// <summary>Stores the profile as a task ran it, once per content: the key is its SHA-256.</summary>
    private async Task<string> SnapshotAsync(Profile profile, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(profile, ProvisioningJson.Options);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (await _services.Records.GetAsync(SnapshotsCollection, key, ct).ConfigureAwait(false) is null)
        {
            await _services.Records.SetManyAsync(SnapshotsCollection, [new KeyValuePair<string, string?>(key, json)], ct).ConfigureAwait(false);
        }

        return key;
    }

    private Task SaveRunAsync(Guid deviceId, RunRecord record, CancellationToken ct) =>
        WriteRunsAsync([new KeyValuePair<string, string?>(Key(deviceId), JsonSerializer.Serialize(record, ProvisioningJson.Options))], ct);

    private async Task WriteRunsAsync(IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
    {
        await _runWrite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _services.Records.SetManyAsync(RunsCollection, records, ct).ConfigureAwait(false);
        }
        finally
        {
            _runWrite.Release();
        }
    }

    /// <summary>
    /// Drops the runs of devices OADM no longer manages and the profile snapshots no run names (plugin start, in the background).
    /// Never throws: a failure is logged and the old runs stay until the next start.
    /// </summary>
    public async Task PruneRunsAsync(CancellationToken ct)
    {
        try
        {
            await PruneRunsCoreAsync(ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Old runs stay until the next start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPruneFailed(ex);
        }
    }

    /// <summary>
    /// Runs <paramref name="use"/> (the vault's prune) with the profiles tasks may still need: the snapshot of every run that did
    /// not end well (a queued or running task, a run Run again starts). Under the snapshot lock, so no task starts meanwhile with
    /// a snapshot <paramref name="use"/> does not see.
    /// </summary>
    public async Task WithProfilesInUseAsync(Func<IReadOnlyList<Profile>, CancellationToken, Task> use, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(use);
        await _snapshots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var runs = await _services.Records.ListAsync(RunsCollection, ct).ConfigureAwait(false);
            var keys = runs.Values.Select(v => PluginJson.TryParse<RunRecord>(v, ProvisioningJson.Options)).OfType<RunRecord>()
                .Where(r => r.CanRunAgain()).Select(r => r.Snapshot).ToHashSet(StringComparer.Ordinal);
            var snapshots = await _services.Records.ListAsync(SnapshotsCollection, ct).ConfigureAwait(false);
            var profiles = snapshots.Where(s => keys.Contains(s.Key)).Select(s => PluginJson.TryParse<Profile>(s.Value, ProvisioningJson.Options))
                .OfType<Profile>().ToList();
            await use(profiles, ct).ConfigureAwait(false);
        }
        finally
        {
            _snapshots.Release();
        }
    }

    private async Task PruneRunsCoreAsync(CancellationToken ct)
    {
        // Under the snapshot lock: a task that starts meanwhile writes its snapshot after the prune, never between its two lists.
        await _snapshots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PruneRunsLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _snapshots.Release();
        }
    }

    private async Task PruneRunsLockedAsync(CancellationToken ct)
    {
        var devices = (await _services.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => Key(d.Id)).ToHashSet(StringComparer.Ordinal);
        var runs = await _services.Records.ListAsync(RunsCollection, ct).ConfigureAwait(false);
        var gone = runs.Keys.Where(k => !devices.Contains(k)).Select(k => new KeyValuePair<string, string?>(k, null)).ToList();
        if (gone.Count > 0)
        {
            await WriteRunsAsync(gone, ct).ConfigureAwait(false);
        }

        var named = runs.Where(r => devices.Contains(r.Key)).Select(r => PluginJson.TryParse<RunRecord>(r.Value, ProvisioningJson.Options)?.Snapshot)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var snapshots = await _services.Records.ListAsync(SnapshotsCollection, ct).ConfigureAwait(false);
        var unused = snapshots.Keys.Where(k => !named.Contains(k)).Select(k => new KeyValuePair<string, string?>(k, null)).ToList();
        if (unused.Count > 0)
        {
            await _services.Records.SetManyAsync(SnapshotsCollection, unused, ct).ConfigureAwait(false);
        }
    }

    private static string Key(Guid deviceId) => deviceId.ToString("N");

    public async ValueTask DisposeAsync()
    {
        _watch?.Dispose();
        _watch = null;
        await _life.CancelAsync().ConfigureAwait(false);
        _found.Writer.TryComplete();
        _leases.Writer.TryComplete();
        foreach (var loop in _loops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        _life.Dispose();
        _onCts.Dispose();
        _addressLock.Dispose();
        _stateWrite.Dispose();
        _runWrite.Dispose();
        _snapshots.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning turned on by {User}")]
    private partial void LogTurnedOn(string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning turned off")]
    private partial void LogTurnedOff();

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Count} tasks started with profile {Profile}")]
    private partial void LogStarted(int count, string profile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: {Count} cameras got no task, the active profile has errors or is gone")]
    private partial void LogNoProfile(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: starting the tasks of {Count} cameras failed")]
    private partial void LogStartFailed(Exception ex, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: this server does not report factory-new devices; only DHCP leases start tasks")]
    private partial void LogNoWatch();

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Serial} is not new, left as it is ({Reason})")]
    private partial void LogNotNew(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Serial} was not added ({Reason})")]
    private partial void LogNotAdded(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Address} reserved for {Serial}")]
    private partial void LogReserved(string address, string serial);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: {Address} released for {Serial}, the camera did not take it")]
    private partial void LogReleased(string address, string serial);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning: device {DeviceId} gets no new task, its last run failed (Run again)")]
    private partial void LogKeptFailedRun(Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: {Serial} left out, {Max} leases wait to be checked already")]
    private partial void LogTooMany(string serial, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: {Node} failed on {Serial}")]
    private partial void LogNodeFailed(Exception ex, string node, string serial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: checking the camera {Serial} of a lease failed")]
    private partial void LogLeaseFailed(Exception ex, string serial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: dropping the runs of removed devices failed")]
    private partial void LogPruneFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provisioning: reading the DHCP server's leases failed")]
    private partial void LogPollFailed(Exception ex);
}

/// <summary>The answer of Run again: tasks started, cameras left out (no failed run, a task runs, the profile is gone).</summary>
public sealed record RunAgainReply(int Started, int Skipped);

/// <summary>A host without VAPIX for plugins: every node that needs it says so.</summary>
internal sealed class NoVapix : IVapixClientFactory
{
    public static readonly NoVapix Instance = new();

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        throw new InvalidOperationException("OADM cannot reach the camera from this plugin.");
}
