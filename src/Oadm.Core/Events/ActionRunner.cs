using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Events;

/// <summary>
/// One edge of a rule for the action runner. <paramref name="Aggregate"/>: actions on chosen / tag / all devices run too
/// (the first subject turned on, the last one off, or a one-shot fire); otherwise only actions on the event's device
/// and actions without devices.
/// </summary>
public sealed record RuleEdgeJob(RuleDefinition Rule, bool Stateful, ActionEdge Edge, Guid? Subject, bool Aggregate, ActionTrigger Trigger);

/// <summary>Receives the edges of the rule engine.</summary>
public interface IActionDispatcher
{
    void Dispatch(RuleEdgeJob job);
}

public sealed class ActionRunnerOptions
{
    /// <summary>Action runs (targets) at the same time, all rules together.</summary>
    public int MaxParallel { get; init; } = 16;

    /// <summary>
    /// Runs at the same time whose action asked for more than <see cref="ActionTimeout"/> (<c>IActionPlugin.MaxRunTime</c>,
    /// a pulse): their own lane, so they never hold the slots of the others.
    /// </summary>
    public int MaxParallelLong { get; init; } = 16;

    /// <summary>
    /// A run that waited this long for a free slot is skipped and logged. A run of a "turns off" edge always waits: skipping
    /// it could leave a relay on.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The device list read for actions on tags, all devices or many devices is dropped after this long without a use.</summary>
    public TimeSpan DeviceListIdle { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>One action run on one target.</summary>
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The wait for running actions at a stop: longer than the 10 s a pulse has for its off write.</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(12);
}

/// <summary>
/// Runs the actions of rule edges. The edges of one rule and subject run one after another in the order they happened (so
/// a subject's "turns off" never overtakes its "turns on"); edges that run actions on chosen / tag / all devices are also
/// ordered among themselves. Edges of other subjects do not wait for each other (one device that does not answer never
/// delays another). Within an edge the actions run in the listed order, each on its targets in parallel (at most
/// <see cref="ActionRunnerOptions.MaxParallel"/> runs at once, all rules together; runs that asked for a longer time have
/// <see cref="ActionRunnerOptions.MaxParallelLong"/> slots of their own). A run that waited
/// <see cref="ActionRunnerOptions.MaxWait"/> for its slot is skipped, except on a "turns off" edge. A failed action is
/// logged and the next one still runs (no retry). Every run is a line in the event log. The device list of actions on
/// tags, all devices or many devices is read once per version of the device table and shared. Nothing runs before
/// <see cref="PluginsStarted"/>:
/// edges of the start-up evaluation wait for the core plugins whose actions they use. The fields of a chosen recipient are
/// merged into the settings of a run and of a test; an action without its recipient fails with
/// <see cref="ActionSettings.ChooseRecipient"/>.
/// </summary>
public sealed partial class ActionRunner : IActionDispatcher, IAsyncDisposable
{
    private readonly EventCatalog _catalog;
    private readonly RuleStore _store;
    private readonly RecipientStore _recipients;
    private readonly EventLog _log;
    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly IPluginSettingsProvider _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ActionRunnerOptions _options;
    private readonly TimeProvider _time;
    private readonly Devices.DeviceChangeFeed? _deviceFeed;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _longSlots;
    private readonly SemaphoreSlim _deviceListGate = new(1, 1);
    private readonly ITimer _deviceListRelease;
    private volatile DeviceList? _deviceList;
    private int _deviceListReads;
    private int _waiting;
    private readonly Dictionary<TailKey, Task> _tails = [];
    private readonly TaskCompletionSource _pluginsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private readonly Lock _sync = new();

    public ActionRunner(
        EventCatalog catalog,
        RuleStore store,
        RecipientStore recipients,
        EventLog log,
        IDeviceRepository devices,
        IVapixClientFactory vapix,
        IPluginSettingsProvider settings,
        ILoggerFactory? loggerFactory = null,
        ActionRunnerOptions? options = null,
        TimeProvider? time = null,
        Devices.IDeviceChangeFeed? deviceFeed = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ActionRunner>();
        _options = options ?? new ActionRunnerOptions();
        _time = time ?? TimeProvider.System;
        _deviceFeed = deviceFeed as Devices.DeviceChangeFeed; // without its version every edge reads the devices itself
        _slots = new SemaphoreSlim(_options.MaxParallel, _options.MaxParallel);
        _longSlots = new SemaphoreSlim(_options.MaxParallelLong, _options.MaxParallelLong);
        _deviceListRelease = _time.CreateTimer(_ => _deviceList = null, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs waiting for a free slot now (tests).</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>How often the whole device table was read for the targets of an action (tests).</summary>
    public int DeviceListReads => Volatile.Read(ref _deviceListReads);

    /// <summary>Chains still running or waiting (tests).</summary>
    public int PendingChains
    {
        get
        {
            lock (_sync)
            {
                return _tails.Count;
            }
        }
    }

    /// <summary>The open connections of persistent recipients (<see cref="IActionContext.SessionAsync{T}"/>); null without them.</summary>
    public RecipientSessionPool? Sessions { get; set; }

    /// <summary>The certificates a recipient's Test may name; null or a null result: none.</summary>
    public Func<Sdk.Certificates.IRecipientCertificates?>? Certificates { get; set; }

    /// <summary>The licenses of commercial plugins, handed to their actions (<see cref="IActionContext.License"/>).</summary>
    public Plugins.IPluginLicenseSource? Licenses { get; set; }

    /// <summary>The core plugins have started (server start, after step 4b): queued edges run now, later ones at once.</summary>
    public void PluginsStarted() => _pluginsStarted.TrySetResult();

    public void Dispatch(RuleEdgeJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var subjectKey = new TailKey(job.Rule.Id, job.Subject ?? Guid.Empty, Aggregate: false);
        var aggregateKey = new TailKey(job.Rule.Id, Guid.Empty, Aggregate: true);
        var ordered = job.Aggregate && HasAggregateActions(job.Rule);
        lock (_sync)
        {
            var before = new List<Task>(3) { _pluginsStarted.Task };
            if (_tails.TryGetValue(subjectKey, out var subjectTail))
            {
                before.Add(subjectTail);
            }

            if (ordered && _tails.TryGetValue(aggregateKey, out var aggregateTail))
            {
                before.Add(aggregateTail);
            }

            var next = Task.WhenAll(before)
                .ContinueWith(_ => RunJobAsync(job), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
            _tails[subjectKey] = next;
            if (ordered)
            {
                _tails[aggregateKey] = next;
            }

            // A finished chain whose last edge this was is removed: the dictionary only holds running chains.
            _ = next.ContinueWith(
                finished => RemoveTail(finished, subjectKey, ordered ? aggregateKey : null),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Waits until every dispatched edge finished (tests, shutdown).</summary>
    public async Task WaitIdleAsync(CancellationToken ct)
    {
        Task[] tails;
        lock (_sync)
        {
            tails = [.. _tails.Values.Distinct()];
        }

        await Task.WhenAll(tails).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The choices of a dynamic action setting (rule dialog).</summary>
    public async Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(string actionId, string fieldKey, CancellationToken ct)
    {
        var action = _catalog.FindAction(actionId) ?? throw new KeyNotFoundException($"Unknown action '{actionId}'.");
        if (!action.Available)
        {
            throw new InvalidOperationException(action.UnavailableText + ".");
        }

        var context = new QueryContext(
            _loggerFactory.CreateLogger("Oadm.Actions." + action.Id),
            action.Registered.Owner,
            _settings.GetSettings(SettingsId(action.Registered)),
            _devices);
        return await action.Plugin.ListChoicesAsync(context, fieldKey, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _pluginsStarted.TrySetResult(); // edges still waiting see the stop and end
        try
        {
            await WaitIdleAsync(CancellationToken.None).WaitAsync(_options.StopWait).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Shutdown: runs that do not stop in time are abandoned.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        await _deviceListRelease.DisposeAsync().ConfigureAwait(false);
        _deviceList = null;
        _stop.Dispose();
        _slots.Dispose();
        _longSlots.Dispose();
        _deviceListGate.Dispose();
    }

    private void RemoveTail(Task finished, TailKey subjectKey, TailKey? aggregateKey)
    {
        lock (_sync)
        {
            if (_tails.TryGetValue(subjectKey, out var current) && current == finished)
            {
                _tails.Remove(subjectKey);
            }

            if (aggregateKey is { } key && _tails.TryGetValue(key, out current) && current == finished)
            {
                _tails.Remove(key);
            }
        }
    }

    /// <summary>Whether the rule has actions on chosen / tag / all devices (they run only on aggregate edges).</summary>
    private static bool HasAggregateActions(RuleDefinition rule) =>
        rule.Actions.Exists(a => a.Targets.Mode != DeviceSelectorMode.EventDevice);

    /// <summary>While true no event rule action runs (the server is updating, docs/spec/self-update.md rule 6).</summary>
    public bool IsHeld
    {
        get => Volatile.Read(ref _held);
        set => Volatile.Write(ref _held, value);
    }

    private bool _held;

    private async Task RunJobAsync(RuleEdgeJob job)
    {
        var ct = _stop.Token;
        if (IsHeld)
        {
            LogHeld(job.Rule.Name);
            return;
        }
        foreach (var action in job.Rule.Actions)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RunActionAsync(job, action, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A broken action must not stop the next one or the runner.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogRunFailed(ex, job.Rule.Name, action.ActionId);
            }
        }
    }

    private async Task RunActionAsync(RuleEdgeJob job, RuleAction action, CancellationToken ct)
    {
        var catalogAction = _catalog.FindAction(action.ActionId);
        var subjectDevice = job.Subject;
        if (catalogAction is null || !catalogAction.Available)
        {
            Write(job, null, catalogAction?.Plugin.DisplayName ?? action.ActionId, EventLogResult.Skipped,
                catalogAction is null ? "Not available: the action is not installed" : catalogAction.UnavailableText);
            return;
        }

        var plugin = catalogAction.Plugin;
        var settingsObject = _store.DecryptSettings(job.Rule.Id, action);
        bool runs;
        try
        {
            // Before the merge: an edge that does not run reads no recipient and decrypts nothing.
            using var own = JsonDocument.Parse(settingsObject.ToJsonString());
            runs = plugin.RunsOn(job.Edge, own.RootElement);
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            runs = job.Edge != ActionEdge.TurnedOff;
        }

        if (!runs)
        {
            return;
        }

        var recipientIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        string? recipientProblem;
        try
        {
            recipientProblem = await MergeRecipientsAsync(plugin, settingsObject, recipientIds, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Every run is a line in the event log, also when the database fails.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogActionFailed(ex, job.Rule.Name, plugin.DisplayName, string.Empty, RecipientUnreadable);
            recipientProblem = RecipientUnreadable;
        }

        if (recipientProblem is not null)
        {
            Write(job, null, plugin.DisplayName, EventLogResult.Failed, recipientProblem);
            return;
        }

        using var settingsDocument = JsonDocument.Parse(settingsObject.ToJsonString());
        var settings = settingsDocument.RootElement.Clone();

        if (!plugin.NeedsDevice)
        {
            // Actions without devices run with every edge (an email per camera), like actions on the event's device.
            await RunOneAsync(job, catalogAction, settings, recipientIds, null, null, null, ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<(Guid Id, IDeviceInfo? Device)> targets;
        if (action.Targets.Mode == DeviceSelectorMode.EventDevice)
        {
            if (subjectDevice is not { } subject)
            {
                return;
            }

            targets = [(subject, null)];
        }
        else
        {
            if (!job.Aggregate)
            {
                return;
            }

            targets = await ResolveTargetsAsync(action.Targets, ct).ConfigureAwait(false);
        }

        if (targets.Count == 0)
        {
            Write(job, null, plugin.DisplayName, EventLogResult.Skipped, "No device matches the targets");
            return;
        }

        // Runs of many targets that got no slot in time are one line with their count, never one line per device.
        var late = targets.Count > 1 ? new LateRuns() : null;
        await Task.WhenAll(targets.Select(target => RunOneAsync(job, catalogAction, settings, recipientIds, target.Id, target.Device, late, ct))).ConfigureAwait(false);
        if (late is { Count: > 0 })
        {
            Write(job, null, plugin.DisplayName, EventLogResult.Skipped,
                string.Create(CultureInfo.InvariantCulture, $"{WaitedTooLong} ({late.Count:N0} of {targets.Count:N0} devices)"));
        }
    }

    /// <summary>The runs of one action on many targets that waited too long for a slot.</summary>
    private sealed class LateRuns
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// Merges the chosen recipient of every recipient field into <paramref name="settings"/> under the recipient's own keys:
    /// its values, passwords decrypted, win over old values of the same keys. Returns
    /// <see cref="ActionSettings.ChooseRecipient"/> when a field has no recipient of its types (none chosen, or it is gone).
    /// <paramref name="ids"/> gets the recipient of each field for <see cref="ActionInvocation.RecipientIds"/>.
    /// </summary>
    private async Task<string?> MergeRecipientsAsync(IActionPlugin plugin, JsonObject settings, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        IReadOnlyList<ActionField> fields;
        try
        {
            fields = plugin.Fields;
        }
#pragma warning disable CA1031 // Plugin code is untrusted: its run reports the problem.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        foreach (var field in fields.Where(f => f.Type == ActionFieldType.Recipient))
        {
            var recipient = Guid.TryParse(ActionSettings.Text(settings, field.Key), out var id) ? await _recipients.GetAsync(id, ct).ConfigureAwait(false) : null;
            if (recipient is null || !field.RecipientTypes.Contains(recipient.TypeId, StringComparer.OrdinalIgnoreCase))
            {
                return ActionSettings.ChooseRecipient;
            }

            var type = _catalog.FindRecipientType(recipient.TypeId)?.Type;
            var values = _recipients.DecryptSettings(recipient);
            if (type?.Sessions is not null)
            {
                // The session holds the login; the action needs only the session.
                foreach (var password in type.Fields.Where(f => f.Type == ActionFieldType.Password))
                {
                    values.Remove(password.Key);
                }
            }

            Merge(settings, values, type?.Fields ?? []);
            ids[field.Key] = recipient.Id;
        }

        return null;
    }

    /// <summary>The recipient's fields replace what the settings hold under the same keys, also a value the recipient leaves empty.</summary>
    private static void Merge(JsonObject settings, JsonObject values, IReadOnlyList<ActionField> fields)
    {
        foreach (var field in fields)
        {
            settings.Remove(field.Key);
        }

        foreach (var (key, value) in values)
        {
            settings[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// The target devices; when the whole device list was read, each with its device (no second read per target). Chosen
    /// devices that were removed stay in the list without a device (logged as removed).
    /// </summary>
    private async Task<IReadOnlyList<(Guid Id, IDeviceInfo? Device)>> ResolveTargetsAsync(DeviceSelector selector, CancellationToken ct)
    {
        if (selector.IsChoice && selector.Tags.Count == 0 && selector.DeviceIds.Count <= FewDevices)
        {
            return [.. selector.DeviceIds.Distinct().Select(id => (id, (IDeviceInfo?)null))];
        }

        var all = await AllDevicesAsync(ct).ConfigureAwait(false);
        if (selector.Mode == DeviceSelectorMode.All)
        {
            return [.. all.Select(d => (d.Id, (IDeviceInfo?)d))];
        }

        if (!selector.IsChoice)
        {
            return [];
        }

        var byId = new Dictionary<Guid, IDeviceInfo>(all.Count);
        foreach (var device in all)
        {
            byId.TryAdd(device.Id, device);
        }

        var tags = new HashSet<string>(selector.Tags, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Guid>();
        var targets = new List<(Guid, IDeviceInfo?)>();
        foreach (var id in selector.DeviceIds)
        {
            if (seen.Add(id))
            {
                targets.Add((id, byId.GetValueOrDefault(id)));
            }
        }

        if (tags.Count > 0)
        {
            foreach (var device in all)
            {
                if (device.Tags.Any(tags.Contains) && seen.Add(device.Id))
                {
                    targets.Add((device.Id, device));
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// Every device, read once per version of the device table (<see cref="Devices.DeviceChangeFeed.Version"/>): edges that
    /// ask at the same time share one read, and at most one read runs at a time. An edge never gets a list read before the
    /// last change published when it asked.
    /// </summary>
    private async Task<IReadOnlyList<IDeviceInfo>> AllDevicesAsync(CancellationToken ct)
    {
        if (_deviceFeed is not { } feed)
        {
            Interlocked.Increment(ref _deviceListReads);
            return await _devices.ListAsync(ct).ConfigureAwait(false);
        }

        var asked = feed.Version;
        if (_deviceList is { } known && known.Version >= asked)
        {
            _deviceListRelease.Change(_options.DeviceListIdle, Timeout.InfiniteTimeSpan);
            return known.Devices;
        }

        await _deviceListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_deviceList is { } read && read.Version >= asked)
            {
                return read.Devices; // read while this edge waited, after it asked
            }

            // The version before the devices: a change in between makes the next edge read again.
            var version = feed.Version;
            Interlocked.Increment(ref _deviceListReads);
            var devices = await _devices.ListAsync(ct).ConfigureAwait(false);
            _deviceList = new DeviceList(version, devices);
            _deviceListRelease.Change(_options.DeviceListIdle, Timeout.InfiniteTimeSpan);
            return devices;
        }
        finally
        {
            _deviceListGate.Release();
        }
    }

    private sealed record DeviceList(long Version, IReadOnlyList<IDeviceInfo> Devices);

    /// <summary>
    /// Waits for a slot of the lane. False: the run waited <see cref="ActionRunnerOptions.MaxWait"/> and is skipped. A
    /// "turns off" run waits as long as it takes (never skipped: a relay would stay on).
    /// </summary>
    private async Task<bool> WaitForSlotAsync(SemaphoreSlim slots, ActionEdge edge, CancellationToken ct)
    {
        if (await slots.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            return true; // a free slot: nothing to wait for
        }

        using var late = edge == ActionEdge.TurnedOff ? null : new CancellationTokenSource(_options.MaxWait, _time);
        using var either = late is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, late.Token);
        Interlocked.Increment(ref _waiting);
        try
        {
            await slots.WaitAsync(either?.Token ?? ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private async Task RunOneAsync(
        RuleEdgeJob job, CatalogAction action, JsonElement settings, IReadOnlyDictionary<string, Guid> recipientIds, Guid? deviceId, IDeviceInfo? known, LateRuns? late, CancellationToken ct)
    {
        var plugin = action.Plugin;
        IDeviceInfo? device = null;
        if (deviceId is { } id)
        {
            device = known ?? await _devices.FindAsync(id, ct).ConfigureAwait(false);
            if (device is null)
            {
                Write(job, deviceId, plugin.DisplayName, EventLogResult.Skipped, DeviceMessages.Removed, deviceText: DeviceMessages.Removed);
                return;
            }

            // Nothing is sent to a device with a changed certificate, rejected credentials, no password or an offline site.
            // An action of a package that does not work at sites never reaches a device there.
            var refused = DeviceMessages.Refusal(device)
                ?? (device.SiteId is not null && action.Registered.Origin.SiteSupport == Sdk.Sites.SiteSupport.None ? Sdk.Sites.SiteTexts.NotAvailableAtSite : null);
            if (refused is not null)
            {
                Write(job, deviceId, plugin.DisplayName, EventLogResult.Skipped, refused, device);
                return;
            }
        }

        // Actions without devices (email, HTTP) still name the device of the event in their texts.
        var textDevice = device;
        if (textDevice is null && job.Trigger.DeviceId is { } eventDevice)
        {
            textDevice = await _devices.FindAsync(eventDevice, ct).ConfigureAwait(false);
        }

        // A run that asked for a longer time (a pulse) takes a slot of the long lane: it never holds a slot of the others.
        var limit = _options.ActionTimeout;
        var slots = _slots;
        try
        {
            if (plugin.MaxRunTime(settings) is { } longer && longer > limit)
            {
                limit = longer < TimeSpan.FromHours(2) ? longer : TimeSpan.FromHours(2);
                slots = _longSlots;
            }
        }
#pragma warning disable CA1031 // Plugin code is untrusted: the default applies.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (!await WaitForSlotAsync(slots, job.Edge, ct).ConfigureAwait(false))
        {
            if (late is null)
            {
                Write(job, deviceId, plugin.DisplayName, EventLogResult.Skipped, WaitedTooLong, device);
            }
            else
            {
                late.Add();
            }

            return;
        }

        var lines = new List<string>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            IVapixClient? vapix = null;
            if (device is not null)
            {
                vapix = await _vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false); // cached by the factory, never disposed here
            }

            var context = new RunContext(
                vapix,
                _loggerFactory.CreateLogger("Oadm.Actions." + action.Id),
                action.Registered.Owner,
                _settings.GetSettings(SettingsId(action.Registered)),
                lines,
                job,
                textDevice,
                Licenses?.LicenseOf(action.Registered.Origin.PackageId),
                Sessions,
                recipientIds);
            var invocation = new ActionInvocation(job.Rule.Id, job.Rule.Name, job.Edge, settings, device, job.Trigger) { RecipientIds = recipientIds };
            await plugin.ExecuteAsync(context, invocation, timeout.Token).ConfigureAwait(false);
            Write(job, deviceId, plugin.DisplayName, EventLogResult.Ok, Joined(lines, EdgeText(job.Edge)), device);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Write(job, deviceId, plugin.DisplayName, EventLogResult.Failed,
                Joined(lines, "Timeout - the action did not finish in time"), device);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // An action failure is a log line, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Write(job, deviceId, plugin.DisplayName, EventLogResult.Failed, Joined(lines, ex.Message), device, error: ex);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// "Send test" of the rule dialog (administrators): runs an action without devices once now, outside every rule state
    /// and limit, with sample values for the event ("sample" for every value of <paramref name="eventTypeId"/>) and the
    /// given device as the device of the event. Password fields left empty take the stored value of the rule's action.
    /// Nothing is written to the event log. Returns the result line. An action on a persistent recipient is refused: it would
    /// really write to a machine, and the recipient's Test only connects.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(
        string actionId, JsonObject settings, string ruleName, Guid? ruleId, Guid? actionKey, string? eventTypeId, Guid? deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var action = _catalog.FindAction(actionId);
        if (action is null || !action.Available)
        {
            return (false, action is null ? "Not available: the action is not installed" : action.UnavailableText);
        }

        if (action.Plugin.NeedsDevice)
        {
            return (false, "Only actions without devices can be tested here.");
        }

        if (action.Plugin.Fields.Any(f => f.Type == ActionFieldType.Recipient && f.RecipientTypes.Any(t => _catalog.FindRecipientType(t)?.Type.Sessions is not null)))
        {
            return (false, RecipientSessionTexts.TestTheRecipient);
        }

        if (ruleId is { } rule && actionKey is { } key && await _store.GetAsync(rule, ct).ConfigureAwait(false) is { } stored
            && stored.Definition.Actions.FirstOrDefault(a => a.Key == key && a.ActionId == actionId) is { } storedAction)
        {
            var decrypted = _store.DecryptSettings(rule, storedAction);
            foreach (var field in action.Plugin.Fields.Where(f => f.Type == ActionFieldType.Password))
            {
                if (ActionSettings.Text(settings, field.Key) is not { Length: > 0 } && decrypted[field.Key] is { } kept)
                {
                    settings[field.Key] = kept.DeepClone();
                }
            }
        }

        var recipientIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (await MergeRecipientsAsync(action.Plugin, settings, recipientIds, ct).ConfigureAwait(false) is { } recipientProblem)
        {
            return (false, recipientProblem);
        }

        var type = _catalog.FindEventType(eventTypeId);
        var device = deviceId is { } id ? await _devices.FindAsync(id, ct).ConfigureAwait(false) : null;
        var data = (type?.Info.DataFields ?? []).ToDictionary(f => f.Name, f => f.Values is { Count: > 0 } values ? values[0] : "sample", StringComparer.Ordinal);
        var trigger = new ActionTrigger(
            type?.Id ?? string.Empty, type?.Info.DisplayName ?? "Test", device?.Id, device?.Address, device?.Model, true, DateTimeOffset.UtcNow, data);
        var definition = new RuleDefinition { Id = ruleId ?? Guid.Empty, Name = string.IsNullOrWhiteSpace(ruleName) ? "Test" : ruleName };
        return await RunTestAsync(action, settings, definition, trigger, device, recipientIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Test" of the recipient dialog: runs the type's test action once now with that action's default values, the values
    /// of the form and sample event values. A password left empty takes the stored one of <paramref name="id"/>, only while the
    /// target is the same. Throws <see cref="RecipientValidationException"/> when the checks refuse: a result means they passed.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestRecipientAsync(string typeId, Guid? id, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_catalog.FindRecipientType(typeId) is not { Available: true, TestAction: { } action } type)
        {
            throw new RecipientValidationException("Not available: this type of recipient is turned off");
        }

        var recipient = id is { } known ? await _recipients.GetAsync(known, ct).ConfigureAwait(false) : null;
        if (recipient is not null && !string.Equals(recipient.TypeId, type.Type.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new RecipientValidationException("This recipient has another type."); // a stored password never goes to a target of another type
        }

        var fields = type.Type.Fields;
        var changed = recipient is not null && RecipientChecks.TargetChanged(fields, values, recipient.Settings);
        if (recipient is not null && !changed)
        {
            var stored = _recipients.DecryptSettings(recipient);
            foreach (var field in fields.Where(f => f.Type == ActionFieldType.Password))
            {
                if (ActionSettings.Text(values, field.Key) is not { Length: > 0 } && stored[field.Key] is { } kept)
                {
                    values[field.Key] = kept.DeepClone();
                }
            }
        }

        var secrets = changed ? RecipientStore.ForClient(recipient!).StoredSecretFields : [];
        if (RecipientChecks.Errors(type, values, secrets).FirstOrDefault() is { Value: not null } error)
        {
            throw new RecipientValidationException(error.Key, error.Value);
        }

        await RecipientChecks.CheckAgainstStoredAsync(fields, values, recipient, Certificates?.Invoke(), ct).ConfigureAwait(false);

        if (type.Type.Sessions is not null)
        {
            // A persistent type only connects: Test never publishes or writes to a real machine.
            return Sessions is null ? (false, "Not available: this type of recipient is turned off") : await Sessions.TestAsync(type.Type, values, ct).ConfigureAwait(false);
        }

        var settings = RecipientChecks.WithDefaults(action.Plugin, values);
        var sample = ActionPlaceholders.Fixed.ToDictionary(p => p.Name, _ => "sample", StringComparer.Ordinal);
        var trigger = new ActionTrigger(string.Empty, "Test event", null, null, null, true, DateTimeOffset.UtcNow, sample);
        return await RunTestAsync(action, settings, new RuleDefinition { Name = "OADM test" }, trigger, null, new Dictionary<string, Guid>(), ct).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> RunTestAsync(
        CatalogAction action, JsonObject settings, RuleDefinition definition, ActionTrigger trigger, IDeviceInfo? device, IReadOnlyDictionary<string, Guid> recipientIds, CancellationToken ct)
    {
        var job = new RuleEdgeJob(definition, true, ActionEdge.TurnedOn, device?.Id, false, trigger);
        using var document = JsonDocument.Parse(settings.ToJsonString());
        var lines = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = _options.ActionTimeout;
        try
        {
            if (action.Plugin.MaxRunTime(document.RootElement) is { } longer && longer > limit)
            {
                limit = longer < TimeSpan.FromMinutes(5) ? longer : TimeSpan.FromMinutes(5);
            }
        }
#pragma warning disable CA1031 // Plugin code is untrusted: the default applies.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        timeout.CancelAfter(limit);
        try
        {
            var context = new RunContext(
                null,
                _loggerFactory.CreateLogger("Oadm.Actions." + action.Id),
                action.Registered.Owner,
                _settings.GetSettings(SettingsId(action.Registered)),
                lines,
                job,
                device,
                Licenses?.LicenseOf(action.Registered.Origin.PackageId),
                Sessions,
                recipientIds);
            var invocation = new ActionInvocation(definition.Id, definition.Name, ActionEdge.TurnedOn, document.RootElement.Clone(), null, trigger) { RecipientIds = recipientIds };
            await action.Plugin.ExecuteAsync(context, invocation, timeout.Token).ConfigureAwait(false);
            return (true, Joined(lines, "Sent"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var message = Joined(lines, "Timeout - the action did not finish in time");
            LogActionFailed(null, definition.Name, action.Plugin.DisplayName, TestRun, message);
            return (false, message);
        }
#pragma warning disable CA1031 // A failed test is the answer, never a crash.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // Never an empty answer: a dependency that cannot load can end here with no message of its own.
            var message = Joined(lines, string.IsNullOrWhiteSpace(ex.Message) ? $"The action failed ({ex.GetType().Name})" : ex.Message);
            LogActionFailed(ex, definition.Name, action.Plugin.DisplayName, TestRun, message);
            return (false, message);
        }
    }

    /// <summary>The server log's mark of a Send test.</summary>
    private const string TestRun = " (test)";

    private static string EdgeText(ActionEdge edge) => edge switch
    {
        ActionEdge.TurnedOn => "Rule turned on",
        ActionEdge.TurnedOff => "Rule turned off",
        _ => "Rule fired",
    };

    private static string Joined(List<string> lines, string result) =>
        lines.Count == 0 ? result : result + ": " + string.Join(" · ", lines);

    /// <summary>
    /// One action run in the event log; failures also reach the server log as warnings (with the exception when the action
    /// threw something else than its own refusal) and skipped runs as information, so the Logs page shows every problem.
    /// </summary>
    private void Write(RuleEdgeJob job, Guid? deviceId, string what, EventLogResult result, string message, IDeviceInfo? device = null, string? deviceText = null, Exception? error = null)
    {
        var target = deviceText ?? (device is null ? string.Empty : DeviceRef.Text(device.Address, device.Model));
        _log.Write(EventLogKind.Action, result, what, deviceId, target, job.Rule.Id, job.Rule.Name, message);
        var on = target.Length == 0 ? string.Empty : " on " + target;
        if (result == EventLogResult.Failed)
        {
            LogActionFailed(error is null or InvalidOperationException ? null : error, job.Rule.Name, what, on, message);
        }
        else if (result == EventLogResult.Skipped)
        {
            LogActionSkipped(job.Rule.Name, what, on, message);
        }
    }

    private const string RecipientUnreadable = "The recipient cannot be read";

    /// <summary>The event log's text of a run that got no slot within <see cref="ActionRunnerOptions.MaxWait"/>.</summary>
    public const string WaitedTooLong = "Waited too long: too many actions are running";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event rule {Rule}: action {Action}{On} failed: {Reason}")]
    private partial void LogActionFailed(Exception? ex, string rule, string action, string on, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event rule {Rule}: action {Action}{On} skipped: {Reason}")]
    private partial void LogActionSkipped(string rule, string action, string on, string reason);

    /// <summary>Contributed actions share their core plugin's settings; standalone ones use their package's.</summary>
    private static string SettingsId(RegisteredActionPlugin action) => action.Owner?.Id ?? action.Origin.PackageId;

    /// <summary>Up to this many chosen devices (no tags) are read one by one; more read the device list once.</summary>
    private const int FewDevices = 8;

    private readonly record struct TailKey(Guid Rule, Guid Subject, bool Aggregate);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event rule {Rule}: action {Action} failed unexpectedly")]
    private partial void LogRunFailed(Exception ex, string rule, string action);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event rule {Rule}: actions skipped, the server is updating")]
    private partial void LogHeld(string rule);

    private sealed class RunContext(
        IVapixClient? vapix,
        ILogger logger,
        ICorePlugin? owner,
        IPluginSettings settings,
        List<string> lines,
        RuleEdgeJob job,
        IDeviceInfo? device,
        Sdk.Licensing.IPluginLicense? license,
        RecipientSessionPool? sessions,
        IReadOnlyDictionary<string, Guid> recipientIds) : IActionContext
    {
        public Sdk.Licensing.IPluginLicense? License { get; } = license;

        public IVapixClient? Vapix { get; } = vapix;

        public ILogger Logger { get; } = logger;

        public ICorePlugin? Owner { get; } = owner;

        public IPluginSettings Settings { get; } = settings;

        public void Log(string message)
        {
            if (!string.IsNullOrWhiteSpace(message) && lines.Count < 20)
            {
                lines.Add(message.Trim());
            }
        }

        public string Text(string? text) => Placeholders.Fill(text, job, device);

        public string Text(string? text, PlaceholderEscape escape) => Placeholders.Fill(text, job, device, escape);

        public async Task<T> SessionAsync<T>(string fieldKey, CancellationToken ct)
            where T : class
        {
            if (sessions is null || !recipientIds.TryGetValue(fieldKey, out var id))
            {
                throw new InvalidOperationException(ActionSettings.ChooseRecipient);
            }

            return await sessions.GetAsync(id, ct).ConfigureAwait(false) as T
                ?? throw new InvalidOperationException("The recipient has another type.");
        }
    }

    private sealed class QueryContext(ILogger logger, ICorePlugin? owner, IPluginSettings settings, IDeviceRepository devices) : IActionQueryContext
    {
        public ILogger Logger { get; } = logger;

        public ICorePlugin? Owner { get; } = owner;

        public IPluginSettings Settings { get; } = settings;

        public IDeviceRepository Devices { get; } = devices;
    }
}

/// <summary>
/// The values of <see cref="ActionPlaceholders"/> for one action run: the rule, the event, the device (the action's, else
/// the event's), then the values of the event by their name. Names are case-insensitive.
/// </summary>
public static class Placeholders
{
    public static string Fill(string? text, RuleEdgeJob job, IDeviceInfo? device, PlaceholderEscape escape = PlaceholderEscape.None)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var values = Values(job.Rule.Name, job.Trigger, device);
        return ActionPlaceholders.Fill(text, name => values.GetValueOrDefault(name), escape);
    }

    /// <summary>Every placeholder value of a run: the fixed ones first, the event's values where no fixed name is taken.</summary>
    public static Dictionary<string, string> Values(string ruleName, ActionTrigger trigger, IDeviceInfo? device)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var address = device?.Address ?? trigger.DeviceAddress ?? string.Empty;
        var model = device?.Model ?? trigger.DeviceModel ?? string.Empty;
        var local = trigger.TimeUtc.ToLocalTime();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rule"] = ruleName,
            ["event"] = trigger.EventName,
            ["state"] = trigger.State switch { true => "on", false => "off", _ => string.Empty },
            ["time"] = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["date"] = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["timeUtc"] = trigger.TimeUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["device"] = address.Length == 0 ? string.Empty : DeviceRef.Text(address, model),
            ["address"] = address,
            ["hostName"] = device?.HostName ?? string.Empty,
            ["model"] = model,
            ["serial"] = device?.Serial ?? string.Empty,
            ["mac"] = Mac(device?.Serial),
            ["firmware"] = device?.FirmwareVersion ?? string.Empty,
            ["category"] = device is null ? string.Empty : CategoryText(device.Category),
            ["tags"] = device is null ? string.Empty : string.Join("; ", device.Tags),
            ["status"] = device is null ? string.Empty : DeviceMessages.StatusText(device),
            ["site"] = device?.SiteName ?? string.Empty,
            ["webUrl"] = address.Length == 0 ? string.Empty : (device?.HttpsEnabled == false ? "http://" : "https://") + UrlHost(address) + "/",
        };

        foreach (var (name, value) in trigger.Data)
        {
            // A fixed name that is empty here (no device: {address} of a site event) takes the event's value.
            if (name.Length > 0 && (!values.TryGetValue(name, out var fixedValue) || fixedValue.Length == 0))
            {
                values[name] = value;
            }
        }

        return values;
    }

    /// <summary>"B8A44F631339" -> "B8:A4:4F:63:13:39"; anything else as it is.</summary>
    private static string Mac(string? serial) =>
        serial is { Length: 12 } s && s.All(char.IsAsciiHexDigit)
            ? string.Join(':', Enumerable.Range(0, 6).Select(i => s.Substring(i * 2, 2)))
            : serial ?? string.Empty;

    private static string UrlHost(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address;

    private static string CategoryText(DeviceCategory category) => category switch
    {
        DeviceCategory.IoModule => "I/O module",
        DeviceCategory.DoorController => "Door controller",
        _ => category.ToString(),
    };
}
