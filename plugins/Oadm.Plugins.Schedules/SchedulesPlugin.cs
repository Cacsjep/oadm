using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Plugins.Schedules.Tools;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules;

/// <summary>
/// Core plugin "Schedules" (docs/spec/schedules.md, the plugin README): maintenance at a set time. A schedule has devices, a time
/// and a list of actions (Provisioning's nodes); each run gives every device one task "Run &lt;schedule&gt;"
/// (<see cref="ScheduleTaskPlugin"/>) with a named step per action, as the schedule's owner. Rail page Schedules (group
/// Automation), free, on by default. Rights "show" (reads; Operator on) and "manage" (changes and Run now; Operator off); saving
/// also needs the right of each action's task. Users with a device group see only the schedules of users with the same view.
/// </summary>
public sealed class SchedulesPlugin : ICorePlugin, IEventSource, IAsyncDisposable
{
    public const string ScheduleGone = "The schedule no longer exists.";

    private readonly SchedulesOptions _options;
    private readonly TimeProvider _time;
    private ICorePluginContext? _ctx;
    private volatile ScheduleEngine? _engine;
    private volatile ScheduleStore? _store;
    private volatile NodeVault? _vault;
    private ActionCatalog _catalog = ActionCatalog.Default;
    private bool _startedBefore;

    // Kept across off and on: devices whose task of a schedule still runs get no second one from the next engine.
    private readonly LeftTasks _left = new();

    public SchedulesPlugin()
        : this(new SchedulesOptions(), TimeProvider.System)
    {
    }

    /// <param name="options">Timings and limits.</param>
    /// <param name="time">The clock (tests pass a fake one).</param>
    public SchedulesPlugin(SchedulesOptions options, TimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        TaskPlugins = [new ScheduleTaskPlugin(() => _engine)];
    }

    public string Id => SchedulesInfo.PluginId;

    public string DisplayName => SchedulesInfo.DisplayName;

    public string? IconKey => SchedulesInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Automation;

    public bool HasPage => true;

    /// <summary>The task "Run &lt;schedule&gt;", one per device and run.</summary>
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The tools of this plugin are named <c>oadm_schedules_*</c>.</summary>
    public string? ToolArea => "schedules";

    /// <summary>The tools (docs/spec/mcp.md "Plugins"): the page's reads and changes with its checks and rights.</summary>
    public IReadOnlyList<PluginTool> Tools => SchedulesTools.Set.Tools;

    /// <summary>The event "Schedule run ended".</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes { get; } = [RunEndedEvent.Type];

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show schedules", "See the schedules and their runs.") { OperatorDefault = true, ViewerDefault = false },
        new(PermissionKeys.Manage, ActionCatalog.Manage.Name, "Create, change, turn on and off, delete and run schedules; each run starts tasks on the devices as the user who saved it.")
        {
            OperatorDefault = false,
            FullDeviceControl = true,
        },
    ];

    internal ScheduleEngine? Engine => _engine;

    internal ScheduleEngine RunningEngine => _engine ?? throw new InvalidOperationException("The Schedules plugin is not running.");

    internal ScheduleStore RunningStore => _store ?? throw new InvalidOperationException("The Schedules plugin is not running.");

    internal NodeVault RunningVault => _vault ?? throw new InvalidOperationException("The Schedules plugin is not running.");

    internal ActionCatalog Catalog => _catalog;

    /// <summary>Reads: "show". Everything else: "manage".</summary>
    public string? RequiredPermission(string method) =>
        SchedulesMethods.IsRead(SchedulesTools.Set.RulesMethodOf(method)) ? PermissionKeys.Show : PermissionKeys.Manage;

    public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method) && !SchedulesMethods.IsRead(SchedulesTools.Set.RulesMethodOf(method));

    /// <summary>The audit text: what was done to which schedule.</summary>
    public string? AuditDetail(string method, string? payloadJson)
    {
        var rules = SchedulesTools.Set.RulesMethodOf(method);
        if (SchedulesTools.Set.Handles(method))
        {
            return rules switch
            {
                SchedulesMethods.Save => "Saved a schedule",
                SchedulesMethods.Delete => "Deleted a schedule",
                SchedulesMethods.TurnOn => "Turned a schedule on or off",
                SchedulesMethods.RunNow => "Ran a schedule now",
                SchedulesMethods.StoreFile => "Stored a file for a schedule",
                _ => null,
            };
        }

        var json = SchedulesJson.Options;
        string NameOf(Guid id) => _store?.Find(id)?.Schedule.Name ?? "a schedule";
        return rules switch
        {
            SchedulesMethods.Save when PluginJson.TryParse<ScheduleRequest>(payloadJson, json)?.Schedule is { } s =>
                (s.Id == Guid.Empty ? "Created schedule " : "Changed schedule ") + s.Name?.Trim(),
            SchedulesMethods.Delete => "Deleted a schedule",
            SchedulesMethods.TurnOn when PluginJson.TryParse<TurnOnRequest>(payloadJson, json) is { } t =>
                (t.On ? "Turned on schedule " : "Turned off schedule ") + NameOf(t.Id),
            SchedulesMethods.RunNow when PluginJson.TryParse<IdRequest>(payloadJson, json) is { } r => "Ran schedule " + NameOf(r.Id) + " now",
            SchedulesMethods.StoreFile => "Stored a file for a schedule",
            _ => null,
        };
    }

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        if (ctx.PluginDirectory is { } folder)
        {
            // The VAPIX Commander library copied into this plugin's folder (the same commands as the Commander page).
            _catalog = new ActionCatalog(VapixCommander.CommandLibrary.Load(Path.Combine(folder, "Library")));
        }

        var store = new ScheduleStore(ctx.Settings, ctx.Records ?? new MemoryPluginRecords());
        await store.LoadAsync(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone), ct).ConfigureAwait(false);
        var vault = new NodeVault(ctx.Settings, ctx.Secrets, ctx.DataDirectory, ctx.Uploads, _time);
        await vault.LoadAsync(ct).ConfigureAwait(false);
        var engine = new ScheduleEngine(
            new EngineDeps(ctx.System.Tasks, ctx.System.Devices, ctx.System.Vapix, ctx.UserCalls, store, vault, ctx.EventPublisher, _left),
            _catalog,
            _options,
            _time,
            ctx.Logger);
        engine.Changed += id => Publish(id, _store?.Find(id)?.OwnerScopeKey);
        _store = store;
        _vault = vault;
        _engine = engine;
        await PruneAsync(ct).ConfigureAwait(false);

        // The host loads one instance per process and turns it off and on with StopAsync and StartAsync (CorePluginHost): only the
        // first start in the process (the server started) makes up missed starts (decision 2026-11-24).
        engine.Start(catchUp: !_startedBefore);
        _startedBefore = true;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_engine is { } engine)
        {
            _engine = null;
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        if (_store is { } store)
        {
            _store = null;
            store.Dispose();
        }

        if (_vault is { } vault)
        {
            _vault = null;
            vault.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The plugin needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (SchedulesTools.Set.Handles(method))
        {
            return await SchedulesTools.Set.InvokeAsync(this, caller, method, payloadJson, ct).ConfigureAwait(false);
        }

        var json = SchedulesJson.Options;
        return method switch
        {
            SchedulesMethods.List => PluginJson.Serialize(await ListAsync(caller, ct).ConfigureAwait(false), json),
            SchedulesMethods.Get => PluginJson.Serialize(await GetAsync(caller, PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            SchedulesMethods.Check => PluginJson.Serialize(await CheckAsync(caller, PluginJson.Parse<ScheduleRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            SchedulesMethods.ActionKinds => PluginJson.Serialize(ActionKindInfos(), json),
            SchedulesMethods.Save => PluginJson.Serialize(await SaveAsync(caller, RequestOf(payloadJson), ct).ConfigureAwait(false), json),
            SchedulesMethods.Delete => PluginJson.Serialize(await DeleteAsync(caller, PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            SchedulesMethods.TurnOn => PluginJson.Serialize(await TurnOnAsync(caller, PluginJson.Parse<TurnOnRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            SchedulesMethods.RunNow => PluginJson.Serialize(await RunNowAsync(caller, PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            SchedulesMethods.Runs => PluginJson.Serialize(new RunsReply(await RunsOfAsync(caller, PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false)), json),
            SchedulesMethods.RunFailures => PluginJson.Serialize(await FailuresAsync(caller, PluginJson.Parse<RunRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            SchedulesMethods.Policies => PluginJson.Serialize(await PoliciesAsync(ct).ConfigureAwait(false) ?? [], json),
            SchedulesMethods.VapixCommands => PluginJson.Serialize(VapixCommands(), json),
            SchedulesMethods.StoreFile => PluginJson.Serialize(await StoreFileAsync(caller, PluginJson.Parse<FileRequest>(payloadJson, json).UploadId, ct).ConfigureAwait(false), json),
            SchedulesMethods.ReadConfigFile => PluginJson.Serialize(await ReadConfigFileAsync(caller, PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    // ---- the page's operations (also the tools') ----

    /// <summary>An access key cannot own a schedule (it acts as "MCP &lt;name&gt;", no user account to run as).</summary>
    public const string KeyCannotOwn = "An access key cannot own a schedule. Create it in the client or with the Assistant.";

    /// <summary>
    /// Refuses an access key's call (its user name starts with "MCP ", which no user account may: fail closed) for what makes the
    /// caller the owner or starts a run as the owner: save, turn on, Run now.
    /// </summary>
    internal static void RequireUser(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.UserName.StartsWith("MCP ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(KeyCannotOwn);
        }
    }

    /// <summary>
    /// The schedule when the caller may see it (all devices; or the owner's current view is the caller's); else
    /// <see cref="ScheduleGone"/>, like an unknown id.
    /// </summary>
    internal async Task<StoredSchedule> VisibleAsync(ICallContext caller, Guid id, CancellationToken ct) =>
        RunningStore.Find(id) is { } stored && await RunningEngine.CanSeeAsync(caller, stored, ct).ConfigureAwait(false)
            ? stored
            : throw new KeyNotFoundException(ScheduleGone);

    internal async Task<SchedulesReply> ListAsync(ICallContext caller, CancellationToken ct)
    {
        var engine = RunningEngine;
        var list = new List<ScheduleSummary>();
        foreach (var stored in RunningStore.All.OrderBy(s => s.Schedule.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (await engine.CanSeeAsync(caller, stored, ct).ConfigureAwait(false))
            {
                list.Add(Summarize(stored, await WarningAsync(stored, ct).ConfigureAwait(false)));
            }
        }

        return new SchedulesReply(list);
    }

    /// <summary>Why the schedule will not run as it should (the owner lost a right, no MyAxis sign-in); null: fine. The owner's call is cached.</summary>
    private async Task<string?> WarningAsync(StoredSchedule stored, CancellationToken ct)
    {
        if (!stored.Schedule.On)
        {
            return null;
        }

        var problem = (await RunningEngine.OwnerAsync(stored, ct).ConfigureAwait(false)).Problem;
        return problem ?? (stored.SignInMissing ? UpgradeOnlineNode.SignIn : null);
    }

    internal ScheduleSummary Summarize(StoredSchedule stored, string? warning)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var s = stored.Schedule;
        var engine = RunningEngine;
        var now = _time.GetUtcNow();
        var after = stored.Handled is { } handled && handled > now ? handled : now;
        return new ScheduleSummary
        {
            Id = s.Id,
            Name = s.Name,
            On = s.On,
            Stages = s.Stages,
            DevicesKind = s.Devices.Kind,
            DeviceCount = s.Devices.DeviceIds?.Count ?? 0,
            Tags = s.Devices.Tags ?? [],
            When = s.When,
            Actions = [.. (s.Actions ?? []).Select(a => a.Kind)],
            Owner = s.Owner,
            NextRun = s.On ? ScheduleTimes.NextAfter(s.When, after, engine.Zone) : null,
            LastRun = engine.LiveRun(s.Id) ?? (RunningStore.RunsOf(s.Id) is { Count: > 0 } runs ? runs[0].Run : null),
            Warning = warning,
        };
    }

    internal async Task<ScheduleReply> GetAsync(ICallContext caller, Guid id, CancellationToken ct)
    {
        var stored = await VisibleAsync(caller, id, ct).ConfigureAwait(false);
        return new ScheduleReply(stored.Schedule, await CheckCoreAsync(caller, stored.Schedule, strict: false, null, ct).ConfigureAwait(false));
    }

    /// <summary>The wizard's check (the same as on save); a chosen device outside the caller's view answers like an unknown id.</summary>
    internal async Task<ScheduleCheck> CheckAsync(ICallContext caller, ScheduleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var schedule = request.Schedule ?? throw new ArgumentException("The request has no schedule.");
        if (schedule.Id != Guid.Empty && RunningStore.Find(schedule.Id) is not null)
        {
            var stored = await VisibleAsync(caller, schedule.Id, ct).ConfigureAwait(false);
            if (!request.DevicesUnchanged)
            {
                schedule = ScheduleChecks.DropGone(schedule, stored.Schedule, await caller.Devices.ListAsync(ct).ConfigureAwait(false));
            }
        }

        return await CheckCoreAsync(caller, schedule, strict: true, request.DevicesUnchanged ? request.KnownDeviceCount ?? 0 : null, ct).ConfigureAwait(false);
    }

    private async Task<ScheduleCheck> CheckCoreAsync(ICallContext caller, Schedule schedule, bool strict, int? knownDeviceCount, CancellationToken ct)
    {
        var engine = RunningEngine;
        var inputs = new ScheduleChecks.Inputs(
            RunningStore.All,
            _catalog,
            await ContextAsync(ct).ConfigureAwait(false),
            _time.GetUtcNow(),
            engine.Zone,
            _options.EstimateParallel,
            schedule.Id == Guid.Empty ? null : RunningStore.LastEnded(schedule.Id)?.DeviceTime,
            CheckPast: strict,
            StrictDevices: strict,
            KnownDeviceCount: knownDeviceCount);
        return await ScheduleChecks.CheckAsync(schedule, caller, inputs, ct).ConfigureAwait(false);
    }

    /// <summary>The check of a save (the tool's preview too): refused with the first error; never writes.</summary>
    internal async Task<(Schedule Schedule, ScheduleCheck Check)> CheckSaveAsync(ICallContext caller, Schedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(schedule);
        RequireUser(caller);
        var store = RunningStore;
        if (schedule.Id != Guid.Empty)
        {
            var stored = await VisibleAsync(caller, schedule.Id, ct).ConfigureAwait(false);
            schedule = ScheduleChecks.DropGone(schedule, stored.Schedule, await caller.Devices.ListAsync(ct).ConfigureAwait(false));
        }
        else if (store.All.Count >= SchedulesInfo.MaxSchedules)
        {
            throw new InvalidOperationException($"OADM keeps at most {SchedulesInfo.MaxSchedules} schedules.");
        }

        var normalized = schedule with
        {
            Name = schedule.Name?.Trim() ?? string.Empty,
            Devices = schedule.Devices ?? new ScheduleDevices(),
            When = schedule.When ?? new ScheduleWhen(),
            Actions = [.. (schedule.Actions ?? []).Where(a => a is not null)],
        };
        var check = await CheckCoreAsync(caller, normalized, strict: true, null, ct).ConfigureAwait(false);
        if (!check.IsValid)
        {
            throw new ArgumentException(ScheduleChecks.FirstError(check, normalized, _catalog));
        }

        return (normalized, check);
    }

    internal async Task<ScheduleReply> SaveAsync(ICallContext caller, Schedule schedule, CancellationToken ct)
    {
        var (normalized, check) = await CheckSaveAsync(caller, schedule, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var saved = normalized with
        {
            Id = normalized.Id == Guid.Empty ? Guid.NewGuid() : normalized.Id,
            Owner = caller.UserName,
            Saved = now,
        };
        var previousScope = RunningStore.Find(saved.Id)?.OwnerScopeKey;
        await RunningStore.SaveAsync(new StoredSchedule { Schedule = saved, OwnerScopeKey = caller.DeviceScopeKey, Handled = now }, ct).ConfigureAwait(false);
        await PruneAsync(ct).ConfigureAwait(false);
        if (previousScope is not null && previousScope != caller.DeviceScopeKey)
        {
            Publish(saved.Id, previousScope);
        }

        Publish(saved.Id, caller.DeviceScopeKey);
        return new ScheduleReply(saved, check with { NextRun = saved.On ? ScheduleTimes.NextAfter(saved.When, now, RunningEngine.Zone) : null });
    }

    /// <summary>The reason of a run whose schedule was deleted while it ran.</summary>
    public const string Deleted = "The schedule was deleted.";

    internal async Task<SchedulesReply> DeleteAsync(ICallContext caller, Guid id, CancellationToken ct)
    {
        var stored = await VisibleAsync(caller, id, ct).ConfigureAwait(false);
        if (!await RunningStore.DeleteAsync(id, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException(ScheduleGone);
        }

        // Its run closes: queued tasks are cancelled, no further stage starts; running tasks run to their end.
        RunningEngine.StopRun(id, Deleted);
        await PruneAsync(ct).ConfigureAwait(false);
        Publish(id, stored.OwnerScopeKey);
        return await ListAsync(caller, ct).ConfigureAwait(false);
    }

    internal async Task<ScheduleSummary> TurnOnAsync(ICallContext caller, TurnOnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.On)
        {
            RequireUser(caller);
        }

        var stored = await VisibleAsync(caller, request.Id, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        // Turned on, it runs at its next time: the times that passed while it was off never run (no catch-up).
        await RunningStore.UpdateAsync(request.Id, s => s with { Schedule = s.Schedule with { On = request.On }, Handled = request.On && !s.Schedule.On ? now : s.Handled }, ct).ConfigureAwait(false);
        Publish(request.Id, stored.OwnerScopeKey);
        var current = RunningStore.Find(request.Id) ?? throw new KeyNotFoundException(ScheduleGone);
        return Summarize(current, null);
    }

    internal async Task<ScheduleRun> RunNowAsync(ICallContext caller, Guid id, CancellationToken ct)
    {
        RequireUser(caller);
        await VisibleAsync(caller, id, ct).ConfigureAwait(false);
        return await RunningEngine.RunNowAsync(id, caller.UserName, ct).ConfigureAwait(false);
    }

    /// <summary>The runs of a schedule, newest first, the running one with its live counts.</summary>
    internal async Task<IReadOnlyList<ScheduleRun>> RunsOfAsync(ICallContext caller, Guid scheduleId, CancellationToken ct)
    {
        await VisibleAsync(caller, scheduleId, ct).ConfigureAwait(false);
        var live = RunningEngine.LiveRun(scheduleId);
        return [.. RunningStore.RunsOf(scheduleId).Select(r => live is not null && r.Run.Id == live.Id ? live : r.Run)];
    }

    internal async Task<RunFailuresReply> FailuresAsync(ICallContext caller, RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await VisibleAsync(caller, request.ScheduleId, ct).ConfigureAwait(false);
        if (!RunningStore.RunsOf(request.ScheduleId).Any(r => r.Run.Id == request.RunId))
        {
            throw new KeyNotFoundException("The run no longer exists.");
        }

        return new RunFailuresReply(RunningEngine.LiveFailures(request.RunId) ?? await RunningStore.FailuresAsync(request.RunId, ct).ConfigureAwait(false));
    }

    internal IReadOnlyList<ActionKindInfo> ActionKindInfos() =>
        [.. _catalog.All.Select(n => new ActionKindInfo(n.Kind, n.Name, n.StepName, NodeSettings.Write(n.DefaultSettings), ActionCatalog.StartingEstimate(n.Kind)))];

    /// <summary>PKI's HTTPS and IEEE 802.1X certificate policies; null when PKI does not run.</summary>
    internal async Task<IReadOnlyList<IssuingPolicyInfo>?> PoliciesAsync(CancellationToken ct)
    {
        if (_ctx?.Services?.Find<IIssuingPolicies>() is not { } pki)
        {
            return null;
        }

        return [.. await pki.ListAsync(IssuingPolicyUse.Https, ct).ConfigureAwait(false), .. await pki.ListAsync(IssuingPolicyUse.Dot1x, ct).ConfigureAwait(false)];
    }

    /// <summary>The commands the action VAPIX commands can run, by category and name.</summary>
    internal IReadOnlyList<ActionCommand> VapixCommands() =>
        [.. _catalog.CommandsNode.Library.Commands
            .Where(c => !c.Dangerous && !c.Fields.Any(f => f.IsSecret || f.IsFile))
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ActionCommand(c.Id, c.Name, c.Category, c.Description, c.Fields))];

    /// <summary>Keeps a file the caller uploaded (their own uploads only) for an action and answers it.</summary>
    internal async Task<StoredFile> StoreFileAsync(ICallContext caller, string uploadId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var upload = await caller.Uploads.FindAsync(uploadId ?? string.Empty, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The uploaded file is no longer on the server. Upload it again.");
        await using var stream = await caller.Uploads.OpenReadAsync(upload.Id, ct).ConfigureAwait(false);
        var file = await RunningVault.AddFileAsync(upload.Name, stream, ct).ConfigureAwait(false);
        lock (_storedBy)
        {
            if (!_storedBy.TryGetValue(StorerOf(caller), out var files))
            {
                _storedBy[StorerOf(caller)] = files = [];
            }

            files.Add(file.Id);
        }

        return file;
    }

    // The files each login session (or user, without one) kept, for its own preview before the schedule is saved.
    private readonly Dictionary<string, HashSet<Guid>> _storedBy = new(StringComparer.Ordinal);

    private static string StorerOf(ICallContext caller) =>
        string.IsNullOrEmpty(caller.SessionId) ? "user:" + caller.UserName : "session:" + caller.SessionId;

    /// <summary>
    /// Whether the caller may read a kept file: one it kept itself (this session), or one a schedule it may see names. Fail
    /// closed: any other id answers like a file that is gone.
    /// </summary>
    private async Task<bool> MayReadAsync(ICallContext caller, Guid fileId, CancellationToken ct)
    {
        lock (_storedBy)
        {
            if (_storedBy.TryGetValue(StorerOf(caller), out var own) && own.Contains(fileId))
            {
                return true;
            }
        }

        var engine = RunningEngine;
        foreach (var stored in RunningStore.All)
        {
            if (NodeRefs.Of([new Profile { Nodes = stored.Schedule.Actions ?? [] }]).Files.Contains(fileId)
                && await engine.CanSeeAsync(caller, stored, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A kept configuration file (<c>.cfg</c>, at most 1 MB) for the action's file preview; secrets masked.</summary>
    internal async Task<ConfigFileReply> ReadConfigFileAsync(ICallContext caller, Guid fileId, CancellationToken ct)
    {
        var vault = RunningVault;
        if (vault.Files.GetValueOrDefault(fileId) is not { } file || !await MayReadAsync(caller, fileId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException(NodeVault.FileGone);
        }

        if (!ConfigurationNode.IsConfigFile(file.Name))
        {
            throw new ArgumentException(ConfigurationNode.WrongFile);
        }

        if (file.Size > DeviceConfig.AdmConfigFile.MaxFileBytes)
        {
            throw new ArgumentException(ConfigurationNode.TooLarge);
        }

        return new ConfigFileReply(file.Name, DeviceConfig.AdmConfigFile.MaskSecrets(await vault.ReadTextAsync(fileId, DeviceConfig.AdmConfigFile.MaxFileBytes, ct).ConfigureAwait(false)));
    }

    private static Schedule RequestOf(string? payloadJson) =>
        PluginJson.Parse<ScheduleRequest>(payloadJson, SchedulesJson.Options).Schedule ?? throw new ArgumentException("The request has no schedule.");

    private async Task<ProfileCheckContext> ContextAsync(CancellationToken ct) =>
        new(null, null) { Files = _vault?.Files, Policies = await PoliciesAsync(ct).ConfigureAwait(false) };

    /// <summary>The files no schedule and no running run names go after a day (the vault's rule).</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        if (_vault is not { } vault || _store is not { } store || _engine is not { } engine)
        {
            return;
        }

        var named = store.All.Select(s => s.Schedule.Actions ?? [])
            .Concat(engine.RunningActions())
            .Select(actions => new Profile { Nodes = actions })
            .ToList();
        await vault.PruneAsync(named, ct).ConfigureAwait(false);
    }

    /// <summary>The page's <see cref="SchedulesMethods.ChangedTopic"/>: users with all devices and the users of the owner's view.</summary>
    private void Publish(Guid scheduleId, string? scopeKey)
    {
        if (_ctx?.Events is not { } events)
        {
            return;
        }

        var json = PluginJson.Serialize(new ChangedEvent(scheduleId), SchedulesJson.Options);
        events.Publish(SchedulesMethods.ChangedTopic, json, PluginEventAudience.AllDevices);
        if (scopeKey is { Length: > 0 } scope && scope != ICallContext.AllDevicesScopeKey)
        {
            events.Publish(SchedulesMethods.ChangedTopic, json, PluginEventAudience.DeviceScope(scope));
        }
    }
}
