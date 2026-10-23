using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Oadm.Core.Collections;
using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Compliance;

/// <summary>Timing and limits of <see cref="ComplianceEngine"/>; tests shorten them.</summary>
public sealed class ComplianceEngineOptions
{
    /// <summary>Devices read at the same time.</summary>
    public int Parallelism { get; init; } = 32;

    /// <summary>Time for one check on one device.</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often due reads are looked for.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Changed results are written at most this often.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>One result as the page shows it. <see cref="Removed"/>: the device left the check (or the check was deleted).</summary>
/// <see cref="Checking"/>: a check of this device was asked for (Check now, a saved check) and its result is not in yet.
public sealed record ComplianceResultModel(
    Guid CheckId, Guid DeviceId, ComplianceOutcome Outcome, string Detail, DateTime CheckedUtc, DateTime SinceUtc, bool Removed = false,
    bool Checking = false);

/// <summary>A check with its counts and why it does not run ("Not available: Edge storage is turned off").</summary>
public sealed record ComplianceCheckState(
    ComplianceCheck Check, string TypeName, string Summary, bool ReadsDevices, int Passed, int Failed, int NotChecked, int NotApplicable, int CouldNotCheck, string? Problem);

/// <summary>
/// Runs the compliance checks: checks of types with cached data on every device change, the others every
/// <see cref="ComplianceCheck.IntervalMinutes"/> per device (all due checks of a device in one run with shared reads,
/// <see cref="ComplianceEngineOptions.Parallelism"/> devices at once) and on <see cref="CheckNow"/>. Keeps every result
/// in memory, writes changes in batches, streams them to the pages and publishes one stateful event per check ("on" while
/// the check fails on a device). Devices the server must not contact (status not Ok) are not read.
/// </summary>
public sealed partial class ComplianceEngine : IHostEventSource, Events.IEventTypeOwners, IAsyncDisposable
{
    /// <summary>The event source of the compliance checks in the event rules.</summary>
    public const string EventSourceId = "oadm.compliance";

    public const string EventSourceName = "Compliance";

    public const string EventGroup = "Compliance";

    private const string EventTypePrefix = "oadm.compliance.check.";

    private readonly ComplianceCatalog _catalog;
    private readonly ComplianceStore _store;
    private readonly DeviceRepository _devices;
    private readonly IDeviceChangeFeed _feed;
    private readonly IVapixClientFactory _vapix;
    private readonly TimeProvider _time;
    private readonly ComplianceEngineOptions _options;
    private readonly ILogger<ComplianceEngine> _logger;
    private readonly Func<IEventPublisher>? _publisher;
    private readonly DeviceGroups.OwnerScopes? _owners;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, CheckRuntime> _checks = [];
    private readonly Dictionary<Guid, Device> _deviceRows = [];
    private readonly HashSet<Guid> _reading = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _forced = [];
    private readonly HashSet<(Guid CheckId, Guid DeviceId)> _checking = [];
    private readonly Dictionary<(Guid CheckId, Guid DeviceId), ComplianceResultEntity?> _dirty = [];
    private readonly List<KeyedCoalescingChannel<(Guid, Guid), ComplianceResultModel>> _watchers = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DeviceChangeSubscription? _subscription;
    private Task _feedLoop = Task.CompletedTask;
    private Task _tickLoop = Task.CompletedTask;
    private Task _flushLoop = Task.CompletedTask;
    private bool _loaded;
    private bool _started;
    private bool _enabled;

    public ComplianceEngine(
        ComplianceCatalog catalog,
        ComplianceStore store,
        DeviceRepository devices,
        IDeviceChangeFeed feed,
        IVapixClientFactory vapix,
        TimeProvider time,
        ComplianceEngineOptions options,
        ILogger<ComplianceEngine> logger,
        Func<IEventPublisher>? publisher = null,
        DeviceGroups.OwnerScopes? owners = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publisher = publisher;
        _owners = owners;
        _slots = new SemaphoreSlim(Math.Max(1, options.Parallelism));
        _catalog.Changed += (_, _) => OnCatalogChanged();
        if (_owners is not null)
        {
            _owners.Changed += OnOwnersChanged;
            _owners.GroupDeleted += OnGroupDeleted;
        }
    }

    public ComplianceCatalog Catalog => _catalog;

    string IHostEventSource.SourceId => EventSourceId;

    string IHostEventSource.SourceName => EventSourceName;

    /// <summary>The checks or their counts changed (raised outside the lock, possibly often: watchers coalesce).</summary>
    public event EventHandler? ChecksChanged;

    /// <summary>The event types changed (a check created, renamed or deleted): the event catalog reads them again.</summary>
    public event EventHandler? EventTypesChanged;

    /// <summary>Compliance is on for the server (off by default): only then are devices checked and events published.</summary>
    public bool Enabled
    {
        get
        {
            lock (_sync)
            {
                return _enabled;
            }
        }
    }

    /// <summary>One stateful event type per check while compliance is on, for the event rules.</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes
    {
        get
        {
            lock (_sync)
            {
                return !_enabled ? [] : [.. _checks.Values.OrderBy(c => c.Check.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => EventTypeOf(c.Check))];
            }
        }
    }

    /// <summary>The event type id of a check.</summary>
    public static string EventTypeId(Guid checkId) => EventTypePrefix + checkId.ToString("N");

    /// <summary>Loads checks, devices and results (before the event rules start, so the check events are known).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_owners is not null)
        {
            await _owners.LoadAsync(ct).ConfigureAwait(false);
        }

        await _store.EnsureDefaultsAsync(ct).ConfigureAwait(false);
        var enabled = await _store.GetEnabledAsync(ct).ConfigureAwait(false);
        var checks = await _store.ListChecksAsync(ct).ConfigureAwait(false);
        var results = await _store.ListResultsAsync(ct).ConfigureAwait(false);
        _subscription = _feed.Subscribe();
        var devices = await _devices.ListDevicesAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var device in devices)
            {
                _deviceRows[device.Id] = device;
            }

            foreach (var check in checks)
            {
                _checks[check.Id] = new CheckRuntime(check);
            }

            foreach (var result in results)
            {
                if (_checks.TryGetValue(result.CheckId, out var runtime) && _deviceRows.ContainsKey(result.DeviceId))
                {
                    runtime.Set(result.DeviceId, new Result(result.Outcome, result.Detail, result.CheckedUtc, result.SinceUtc));
                }
                else
                {
                    _dirty[(result.CheckId, result.DeviceId)] = null; // a removed device or check
                }
            }

            _enabled = enabled;
            _loaded = true;
        }
    }

    /// <summary>Starts checking (after the core plugins started: their types read devices through them).</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("LoadAsync first.");
        }

        List<ComplianceResultModel> changes;
        lock (_sync)
        {
            _started = true;
            changes = [];
            foreach (var runtime in _enabled ? _checks.Values : Enumerable.Empty<CheckRuntime>())
            {
                Reconcile(runtime, changes);
            }
        }

        _ = EvaluateCachedAsync(null, null, _stopping.Token);
        Publish(changes);
        ReportAllEvents();
        var token = _stopping.Token;
        _feedLoop = Task.Run(() => FeedLoopAsync(token), CancellationToken.None);
        _tickLoop = Task.Run(() => TickLoopAsync(token), CancellationToken.None);
        _flushLoop = Task.Run(() => FlushLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns compliance on (every device of every check is checked) or off (nothing runs, no events; the results are
    /// removed, the checks stay). Stored as a server setting.
    /// </summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        await _store.SetEnabledAsync(enabled, ct).ConfigureAwait(false);
        var changes = new List<ComplianceResultModel>();
        lock (_sync)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            foreach (var runtime in _checks.Values)
            {
                if (enabled)
                {
                    Reconcile(runtime, changes);
                }
                else
                {
                    foreach (var deviceId in runtime.Results.Keys.ToList())
                    {
                        RemoveResult(runtime, deviceId, changes);
                    }

                    runtime.ReportedFailing = null;
                }
            }

            if (!enabled)
            {
                _dirty.Clear(); // the store removed every result
                _forced.Clear();
                _checking.Clear();
            }
        }

        Publish(changes);
        Oadm.Core.Auth.CallerContext.Raise(EventTypesChanged, this, EventArgs.Empty);
        if (enabled)
        {
            ReportAllEvents();
            _ = EvaluateCachedAsync(null, null, _stopping.Token);
            Wake();
        }

        Oadm.Core.Auth.CallerContext.Raise(ChecksChanged, this, EventArgs.Empty);
    }

    /// <summary>Every check with its counts, sorted by name (the default checks first).</summary>
    public IReadOnlyList<ComplianceCheckState> ListChecks()
    {
        lock (_sync)
        {
            return [.. _checks.Values
                .OrderByDescending(c => c.Check.BuiltIn)
                .ThenBy(c => c.Check.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(StateOf)];
        }
    }

    /// <summary>The owner of a check, null for an unknown check (cheap: the results watch asks per change).</summary>
    public Auth.DeviceScope? OwnerOf(Guid checkId)
    {
        lock (_sync)
        {
            return _checks.TryGetValue(checkId, out var runtime) ? runtime.Check.Owner : null;
        }
    }

    public ComplianceCheck? GetCheck(Guid id)
    {
        lock (_sync)
        {
            return _checks.TryGetValue(id, out var runtime) ? runtime.Check.Clone() : null;
        }
    }

    /// <summary>
    /// The results now and a reader of every change after them (subscribe, then read the snapshot: nothing is lost).
    /// A slow reader gets the latest result per check and device. Dispose the subscription to stop.
    /// </summary>
    public ComplianceWatch Watch()
    {
        var channel = new KeyedCoalescingChannel<(Guid, Guid), ComplianceResultModel>(r => (r.CheckId, r.DeviceId));
        lock (_sync)
        {
            _watchers.Add(channel);
            var snapshot = new List<ComplianceResultModel>();
            foreach (var runtime in _checks.Values)
            {
                foreach (var (deviceId, result) in runtime.Results)
                {
                    snapshot.Add(Model(runtime.Check.Id, deviceId, result));
                }
            }

            return new ComplianceWatch(snapshot, channel.Reader, () =>
            {
                lock (_sync)
                {
                    _watchers.Remove(channel);
                }

                channel.Complete();
            });
        }
    }

    /// <summary>A check was saved: results of devices that left it are removed; changed settings check every device again.</summary>
    public void ApplySaved(ComplianceCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var changes = new List<ComplianceResultModel>();
        bool typesChanged;
        lock (_sync)
        {
            var isNew = !_checks.TryGetValue(check.Id, out var runtime);
            var resetResults = isNew
                || !string.Equals(runtime!.Check.TypeId, check.TypeId, StringComparison.Ordinal)
                || !JsonNode.DeepEquals(runtime.Check.Settings, check.Settings)
                || runtime.Check.Severity != check.Severity;
            typesChanged = isNew || !string.Equals(runtime!.Check.Name, check.Name, StringComparison.Ordinal);
            if (isNew)
            {
                runtime = new CheckRuntime(check.Clone());
                _checks[check.Id] = runtime;
            }
            else
            {
                runtime!.Update(check.Clone());
            }

            if (resetResults)
            {
                foreach (var deviceId in runtime.Results.Keys.ToList())
                {
                    RemoveResult(runtime, deviceId, changes);
                }
            }

            if (_started && _enabled)
            {
                Reconcile(runtime, changes);
                Force(runtime.Check.Id, null, changes);
            }
        }

        Publish(changes);
        if (typesChanged)
        {
            Oadm.Core.Auth.CallerContext.Raise(EventTypesChanged, this, EventArgs.Empty);
        }

        ReportEvent(check.Id);
        _ = EvaluateCachedAsync(check.Id, null, _stopping.Token);
        Wake();
        Oadm.Core.Auth.CallerContext.Raise(ChecksChanged, this, EventArgs.Empty);
    }

    /// <summary>A check was deleted: its results and its event go.</summary>
    public void ApplyDeleted(Guid checkId)
    {
        var changes = new List<ComplianceResultModel>();
        lock (_sync)
        {
            if (!_checks.Remove(checkId, out var runtime))
            {
                return;
            }

            foreach (var deviceId in runtime.Results.Keys.ToList())
            {
                changes.Add(new ComplianceResultModel(checkId, deviceId, ComplianceOutcome.NotChecked, string.Empty, default, default, Removed: true));
                _dirty.Remove((checkId, deviceId)); // the store already deleted them with the check
            }

            _forced.Remove(checkId);
            _checking.RemoveWhere(k => k.CheckId == checkId);
        }

        Publish(changes);
        Oadm.Core.Auth.CallerContext.Raise(EventTypesChanged, this, EventArgs.Empty);
        Oadm.Core.Auth.CallerContext.Raise(ChecksChanged, this, EventArgs.Empty);
    }

    /// <summary>
    /// Checks again now: the given checks (none = every check that is on) on the given devices (none = every device of
    /// the check). Devices the server must not contact stay Not checked.
    /// </summary>
    public void CheckNow(IReadOnlyCollection<Guid>? checkIds, IReadOnlyCollection<Guid>? deviceIds)
    {
        var deviceSet = deviceIds is { Count: > 0 } ? deviceIds.ToHashSet() : null;
        var cached = new List<Guid>();
        var changes = new List<ComplianceResultModel>();
        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            foreach (var runtime in _checks.Values.Where(c => c.Check.Enabled && (checkIds is not { Count: > 0 } || checkIds.Contains(c.Check.Id))))
            {
                if (IsCached(runtime))
                {
                    cached.Add(runtime.Check.Id);
                }
                else
                {
                    Force(runtime.Check.Id, deviceSet, changes);
                }
            }
        }

        Publish(changes);
        foreach (var checkId in cached)
        {
            _ = EvaluateCachedAsync(checkId, deviceSet, _stopping.Token);
        }

        Wake();
    }

    /// <summary>Waits until every changed result is written (tests, stop).</summary>
    public Task FlushAsync(CancellationToken ct) => WriteDirtyAsync(ct);

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // stopped by the host, then disposed by the container
        }

        if (_owners is not null)
        {
            _owners.Changed -= OnOwnersChanged;
            _owners.GroupDeleted -= OnGroupDeleted;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _subscription?.Dispose();
        try
        {
            await Task.WhenAll(_feedLoop, _tickLoop, _flushLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }

        try
        {
            await WriteDirtyAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed last write is logged; the next start reads the devices again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(ex);
        }

        lock (_sync)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Complete();
            }

            _watchers.Clear();
        }

        _stopping.Dispose();
        _slots.Dispose();
        _wake.Dispose();
    }

    // ---- state (under _sync) ----

    private bool IsCached(CheckRuntime runtime) => _catalog.Find(runtime.Check.TypeId)?.Type is { UsesCachedDataOnly: true };

    /// <summary>
    /// Whether the device is one of the check's devices: O(its tags) through the runtime's sets, and one of its owner's
    /// (docs/spec/device-scope.md, Shared objects: "All devices" and the chosen devices of a group's check are the group's).
    /// </summary>
    private bool InScope(CheckRuntime runtime, Device device) => runtime.InScope(device) && OwnerFilter(runtime).Contains(device);

    /// <summary>The owner's devices now (without the owner scopes: all devices for all devices, nothing for a group).</summary>
    private DeviceGroups.DeviceGroupFilter OwnerFilter(CheckRuntime runtime) =>
        _owners?.FilterOf(runtime.Check.Owner)
        ?? (runtime.Check.Owner.AllDevices ? DeviceGroups.DeviceGroupFilter.Everything : DeviceGroups.DeviceGroupFilter.Nothing);

    /// <summary>The owner of a check's event type ("Compliance: name"), null for a type that is not a check's.</summary>
    public Auth.DeviceScope? OwnerOfEventType(string typeId)
    {
        if (typeId is null || !typeId.StartsWith(EventTypePrefix, StringComparison.Ordinal) || !Guid.TryParseExact(typeId[EventTypePrefix.Length..], "N", out var id))
        {
            return null;
        }

        lock (_sync)
        {
            return _checks.TryGetValue(id, out var runtime) ? runtime.Check.Owner : Auth.DeviceScope.None;
        }
    }

    /// <summary>A group's rules or the tag and site catalog changed: the checks of groups follow their devices again.</summary>
    private void OnOwnersChanged(object? sender, EventArgs e)
    {
        var changes = new List<ComplianceResultModel>();
        List<(Guid Check, List<Guid> Entered)> recheck = [];
        lock (_sync)
        {
            if (!_started || !_enabled)
            {
                return;
            }

            foreach (var runtime in _checks.Values.Where(c => !c.Check.Owner.AllDevices))
            {
                var before = runtime.Results.Keys.ToHashSet();
                Reconcile(runtime, changes);
                var entered = runtime.Results.Keys.Where(id => !before.Contains(id)).ToList();
                if (entered.Count > 0)
                {
                    recheck.Add((runtime.Check.Id, entered)); // only the devices that came in are evaluated
                }
            }
        }

        Publish(changes);
        foreach (var (id, entered) in recheck)
        {
            _ = EvaluateCachedAsync(id, entered, _stopping.Token);
        }

        Wake();
        Oadm.Core.Auth.CallerContext.Raise(ChecksChanged, this, EventArgs.Empty);
    }

    /// <summary>A group was deleted: its checks are off and have no owner in the store; read them again.</summary>
    private async void OnGroupDeleted(object? sender, Guid groupId)
    {
        try
        {
            List<Guid> owned;
            lock (_sync)
            {
                owned = [.. _checks.Values.Where(c => c.Check.Owner.EffectiveGroupId == groupId).Select(c => c.Check.Id)];
            }

            foreach (var id in owned)
            {
                if (await _store.GetCheckAsync(id, _stopping.Token).ConfigureAwait(false) is { } stored)
                {
                    ApplySaved(stored);
                }
            }
        }
#pragma warning disable CA1031 // An event handler must not throw; the next start reads the checks anyway.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(ex);
        }
    }

    /// <summary>Gives every device of the check a result (Not checked yet, or why it is not contacted) and drops the others.</summary>
    private void Reconcile(CheckRuntime runtime, List<ComplianceResultModel> changes)
    {
        var cached = IsCached(runtime);
        var owner = OwnerFilter(runtime);
        foreach (var device in _deviceRows.Values)
        {
            ReconcileDevice(runtime, device, cached, changes, owner);
        }

        foreach (var deviceId in runtime.Results.Keys.Where(id => !_deviceRows.ContainsKey(id)).ToList())
        {
            RemoveResult(runtime, deviceId, changes);
        }
    }

    private void ReconcileDevice(CheckRuntime runtime, Device device, bool cached, List<ComplianceResultModel> changes, DeviceGroups.DeviceGroupFilter? owner = null)
    {
        if (!runtime.InScope(device) || !(owner ?? OwnerFilter(runtime)).Contains(device))
        {
            if (runtime.Results.ContainsKey(device.Id))
            {
                RemoveResult(runtime, device.Id, changes);
            }

            return;
        }

        if (device.Status == DeviceStatus.SiteOffline && device.SiteId is { } startingSite && _devices.Sites.IsStarting(startingSite)
            && runtime.Results.ContainsKey(device.Id))
        {
            // After a server start the site's node is not back yet: the results stay as they were (no event edges).
            return;
        }

        if ((!cached || device.Status == DeviceStatus.SiteOffline) && device.Status != DeviceStatus.Ok)
        {
            // An offline site is Not checked in every check, those with cached data too (Reachable, Login works).
            SetResult(runtime, device.Id, new ComplianceResult(ComplianceOutcome.NotChecked, StatusText(device)), changes);
        }
        else if (!runtime.Results.TryGetValue(device.Id, out var result) || (!cached && result.Outcome == ComplianceOutcome.NotChecked && result.Detail != NotCheckedYet))
        {
            SetResult(runtime, device.Id, new ComplianceResult(ComplianceOutcome.NotChecked, NotCheckedYet), changes);
        }
    }

    private const string NotCheckedYet = "Not checked yet";

    private static string StatusText(Device device) => DeviceMessages.Refusal(device, refuseUnreachable: true) ?? device.Status.ToString();

    private void SetResult(CheckRuntime runtime, Guid deviceId, ComplianceResult result, List<ComplianceResultModel> changes, bool checkedNow = false)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var detail = result.Detail.Length > ComplianceResultEntity.MaxDetailLength ? result.Detail[..ComplianceResultEntity.MaxDetailLength] : result.Detail;
        var had = runtime.Results.TryGetValue(deviceId, out var old);
        var same = had && old!.Outcome == result.Outcome && string.Equals(old.Detail, detail, StringComparison.Ordinal);
        var wasChecking = _checking.Remove((runtime.Check.Id, deviceId));
        if (same && !checkedNow)
        {
            if (wasChecking)
            {
                changes.Add(Model(runtime.Check.Id, deviceId, old!));
            }

            return;
        }

        var stored = new Result(
            result.Outcome,
            detail,
            checkedNow ? now : (had ? old!.CheckedUtc : default),
            had && old!.Outcome == result.Outcome ? old.SinceUtc : now);
        runtime.Set(deviceId, stored);
        _dirty[(runtime.Check.Id, deviceId)] = new ComplianceResultEntity
        {
            CheckId = runtime.Check.Id,
            DeviceId = deviceId,
            Outcome = stored.Outcome,
            Detail = stored.Detail,
            CheckedUtc = stored.CheckedUtc,
            SinceUtc = stored.SinceUtc,
        };
        if (!same || wasChecking)
        {
            changes.Add(Model(runtime.Check.Id, deviceId, stored));
        }
    }

    private void RemoveResult(CheckRuntime runtime, Guid deviceId, List<ComplianceResultModel> changes)
    {
        _checking.Remove((runtime.Check.Id, deviceId));
        if (runtime.Remove(deviceId))
        {
            _dirty[(runtime.Check.Id, deviceId)] = null;
            changes.Add(new ComplianceResultModel(runtime.Check.Id, deviceId, ComplianceOutcome.NotChecked, string.Empty, default, default, Removed: true));
        }
    }

    /// <summary>
    /// Marks a check that reads the devices due now on the given devices (null = every device it has a result for) that may be
    /// contacted, and shows them as checking until their result is in.
    /// </summary>
    private void Force(Guid checkId, HashSet<Guid>? deviceIds, List<ComplianceResultModel> changes)
    {
        if (!_checks.TryGetValue(checkId, out var runtime) || IsCached(runtime) || _catalog.Find(runtime.Check.TypeId)?.Available != true)
        {
            return;
        }

        if (!_forced.TryGetValue(checkId, out var set))
        {
            set = [];
            _forced[checkId] = set;
        }

        foreach (var deviceId in deviceIds is null ? runtime.Results.Keys : deviceIds.Where(runtime.Results.ContainsKey))
        {
            if (!_deviceRows.TryGetValue(deviceId, out var device) || device.Status != DeviceStatus.Ok)
            {
                continue;
            }

            set.Add(deviceId);
            if (_checking.Add((checkId, deviceId)))
            {
                changes.Add(Model(checkId, deviceId, runtime.Results[deviceId]));
            }
        }
    }

    private ComplianceCheckState StateOf(CheckRuntime runtime)
    {
        var type = _catalog.Find(runtime.Check.TypeId);
        string? problem = type is null
            ? "Not available: this kind of check no longer exists"
            : type.Available ? null : type.UnavailableText;
        // The summary once per check version and type (a configuration file check parses its file for it).
        if (runtime.Summary is not { } summary || !ReferenceEquals(runtime.SummaryType, type))
        {
            try
            {
                summary = type is null ? runtime.Check.TypeId : type.Type.Describe(runtime.Invocation.Settings);
            }
#pragma warning disable CA1031 // Plugin code is untrusted.
            catch (Exception)
#pragma warning restore CA1031
            {
                summary = type?.Type.DisplayName ?? runtime.Check.TypeId;
            }

            runtime.Summary = summary;
            runtime.SummaryType = type;
        }

        return new ComplianceCheckState(
            runtime.Check.Clone(copySettings: false), // the settings are never changed; a configuration file is large
            type?.Type.DisplayName ?? runtime.Check.TypeId,
            summary,
            type?.Type.UsesCachedDataOnly == false,
            runtime.Count(ComplianceOutcome.Passed),
            runtime.Count(ComplianceOutcome.Failed),
            runtime.Count(ComplianceOutcome.NotChecked),
            runtime.Count(ComplianceOutcome.NotApplicable),
            runtime.Count(ComplianceOutcome.CouldNotCheck),
            problem);
    }

    private ComplianceResultModel Model(Guid checkId, Guid deviceId, Result result) =>
        new(checkId, deviceId, result.Outcome, result.Detail, result.CheckedUtc, result.SinceUtc, Checking: _checking.Contains((checkId, deviceId)));

    // ---- evaluation ----

    /// <summary>Evaluates the cached-data checks (one check or all) on the given devices (null = all) right away.</summary>
    private async Task EvaluateCachedAsync(Guid? checkId, IReadOnlyCollection<Guid>? deviceIds, CancellationToken ct)
    {
        List<(CheckRuntime Runtime, CatalogComplianceType Type, List<Device> Devices)> work = [];
        lock (_sync)
        {
            if (!_started || !_enabled)
            {
                return;
            }

            foreach (var runtime in _checks.Values.Where(c => c.Check.Enabled && (checkId is null || c.Check.Id == checkId)))
            {
                if (_catalog.Find(runtime.Check.TypeId) is not { Available: true, Type.UsesCachedDataOnly: true } type)
                {
                    continue;
                }

                var devices = (deviceIds is null ? _deviceRows.Values : deviceIds.Where(_deviceRows.ContainsKey).Select(id => _deviceRows[id]))
                    .Where(d => InScope(runtime, d))
                    .ToList();
                work.Add((runtime, type, devices));
            }
        }

        foreach (var (runtime, type, devices) in work)
        {
            var results = new List<(Guid, ComplianceResult)>(devices.Count);
            var evaluated = new Dictionary<Guid, Device>(devices.Count);
            var invocation = runtime.Invocation;
            foreach (var device in devices)
            {
                results.Add((device.Id, await EvaluateAsync(type, invocation, device, null, NoReads.Instance, ct).ConfigureAwait(false)));
                evaluated[device.Id] = device;
            }

            Apply(runtime.Check.Id, runtime.Check.Version, results, cached: true, evaluated);
        }
    }

    /// <summary>The usual text when a device cannot be read (like the table pages).</summary>
    private static string Failure(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: SocketException socket } => DeviceMessages.Unreachable(socket.Message),
        HttpRequestException http => DeviceMessages.Unreachable(http.Message),
        _ => ex.Message,
    };


    private async Task<ComplianceResult> EvaluateAsync(
        CatalogComplianceType type, ComplianceInvocation invocation, Device device, IVapixClient? vapix, IComplianceReads reads, CancellationToken ct)
    {
        if (device.Status == DeviceStatus.SiteOffline)
        {
            return new ComplianceResult(ComplianceOutcome.NotChecked, Sdk.Sites.SiteTexts.SiteOffline(device.SiteName));
        }

        if (device.SiteId is not null && type.SiteSupport == Sdk.Sites.SiteSupport.None)
        {
            // A type of a package that does not work at sites never sees a device there.
            return new ComplianceResult(ComplianceOutcome.NotChecked, Sdk.Sites.SiteTexts.NotAvailableAtSite);
        }

        using var timeout = new CancellationTokenSource(_options.CheckTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var context = new Context(device, vapix, reads, _time.GetUtcNow(), _logger, type.Owner);
            return await type.Type.EvaluateAsync(context, invocation, linked.Token).ConfigureAwait(false)
                ?? ComplianceResult.CouldNotCheck("The check gave no result");
        }
        catch (Exception ex) when (Sites.SiteErrors.Find(ex) is { } site)
        {
            // The site could not carry the read: never a result of the device.
            return new ComplianceResult(ComplianceOutcome.NotChecked, site.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ComplianceResult.CouldNotCheck(string.Create(CultureInfo.InvariantCulture, $"Timeout after {_options.CheckTimeout.TotalSeconds:0} s"));
        }
#pragma warning disable CA1031 // Plugin code is untrusted: a failing check is a result, never a crash.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return ComplianceResult.CouldNotCheck(Failure(ex));
        }
    }

    /// <summary>
    /// Stores results of a run, unless the check changed meanwhile (its results were reset). <paramref name="cached"/>:
    /// a check on cached data, worked out again at every start: only a changed result is kept and written, no checked time.
    /// </summary>
    /// <param name="evaluated">
    /// Cached data: the device rows the results were computed from. A result whose row was replaced meanwhile is dropped:
    /// the change that replaced it evaluates the device again, and an older evaluation that ends later never overwrites it.
    /// </param>
    private void Apply(Guid checkId, int version, IReadOnlyList<(Guid DeviceId, ComplianceResult Result)> results, bool cached = false,
        Dictionary<Guid, Device>? evaluated = null)
    {
        var changes = new List<ComplianceResultModel>();
        lock (_sync)
        {
            if (!_checks.TryGetValue(checkId, out var runtime) || runtime.Check.Version != version)
            {
                return;
            }

            foreach (var (deviceId, result) in results)
            {
                if (_deviceRows.TryGetValue(deviceId, out var device) && InScope(runtime, device)
                    && (evaluated is null || !evaluated.TryGetValue(deviceId, out var from) || ReferenceEquals(from, device)))
                {
                    SetResult(runtime, deviceId, result, changes, checkedNow: !cached);
                }
            }
        }

        Publish(changes);
    }

    // ---- loops ----

    private async Task FeedLoopAsync(CancellationToken ct)
    {
        var reader = _subscription!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var changed = new HashSet<Guid>();
                var changes = new List<ComplianceResultModel>();
                lock (_sync)
                {
                    while (reader.TryRead(out var change))
                    {
                        ApplyDeviceChange(change, changes);
                        if (change.Kind != DeviceChangeKind.Removed)
                        {
                            changed.Add(change.DeviceId);
                        }
                    }
                }

                Publish(changes);
                if (changed.Count > 0)
                {
                    await EvaluateCachedAsync(null, changed, ct).ConfigureAwait(false);
                    Wake();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    private void ApplyDeviceChange(DeviceChange change, List<ComplianceResultModel> changes)
    {
        if (change.Kind == DeviceChangeKind.Removed || change.Device is null)
        {
            _deviceRows.Remove(change.DeviceId);
            foreach (var runtime in _checks.Values)
            {
                RemoveResult(runtime, change.DeviceId, changes);
            }

            return;
        }

        _deviceRows[change.DeviceId] = change.Device;
        foreach (var runtime in _enabled ? _checks.Values : Enumerable.Empty<CheckRuntime>())
        {
            ReconcileDevice(runtime, change.Device, IsCached(runtime), changes);
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.TickInterval, _time);
        var tick = timer.WaitForNextTickAsync(ct).AsTask();
        var wake = _wake.WaitAsync(ct); // one pending wait at a time: an abandoned one would swallow the next wake
        while (!ct.IsCancellationRequested)
        {
            try
            {
                StartDueRuns(ct);
                var done = await Task.WhenAny(tick, wake).ConfigureAwait(false);
                await done.ConfigureAwait(false);
                if (done == tick)
                {
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                }
                else
                {
                    wake = _wake.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop must survive; logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTickFailed(ex);
            }
        }
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // already woken
        }
        catch (ObjectDisposedException)
        {
            // stopped
        }
    }

    /// <summary>
    /// Starts a run for every device with a due read while a slot is free. The scan goes on from where the last one
    /// stopped (one pass over the devices per round, not one per run); the rest waits for a free slot or the next tick.
    /// </summary>
    private void StartDueRuns(CancellationToken ct)
    {
        lock (_sync)
        {
            _readChecks = !_enabled ? [] : [.. _checks.Values
                .Where(c => c.Check.Enabled)
                .Select(c => (Runtime: c, Type: _catalog.Find(c.Check.TypeId)))
                .Where(c => c.Type is { Available: true, Type.UsesCachedDataOnly: false })
                .Select(c => (c.Runtime, c.Type!))];
        }

        while (true)
        {
            Guid deviceId;
            List<(CheckRuntime Runtime, CatalogComplianceType Type)> due;
            lock (_sync)
            {
                if (_slots.CurrentCount == 0 || !TryNextDue(out deviceId, out due))
                {
                    return;
                }

                _reading.Add(deviceId);
            }

            _slots.Wait(ct);
            _ = RunDeviceAsync(deviceId, due, ct);
        }
    }

    private List<(CheckRuntime Runtime, CatalogComplianceType Type)> _readChecks = [];
    private List<Guid> _scanOrder = [];
    private int _scanAt;

    private bool TryNextDue(out Guid deviceId, out List<(CheckRuntime Runtime, CatalogComplianceType Type)> due)
    {
        due = [];
        deviceId = default;
        if (_readChecks.Count == 0)
        {
            return false;
        }

        if (_scanAt >= _scanOrder.Count)
        {
            _scanOrder = [.. _deviceRows.Keys];
            _scanAt = 0;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        while (_scanAt < _scanOrder.Count)
        {
            var id = _scanOrder[_scanAt++];
            if (!_deviceRows.TryGetValue(id, out var device) || device.Status != DeviceStatus.Ok || _reading.Contains(id))
            {
                continue;
            }

            foreach (var (runtime, type) in _readChecks)
            {
                if (!runtime.Results.TryGetValue(id, out var result))
                {
                    continue; // not one of its devices
                }

                var forced = _forced.TryGetValue(runtime.Check.Id, out var set) && set.Contains(id);
                var interval = TimeSpan.FromMinutes(Math.Clamp(runtime.Check.IntervalMinutes, ComplianceStore.MinInterval, ComplianceStore.MaxInterval));
                if (forced || result.CheckedUtc == default || now - result.CheckedUtc >= interval)
                {
                    due.Add((runtime, type));
                    set?.Remove(id);
                }
            }

            if (due.Count > 0)
            {
                deviceId = id;
                return true;
            }
        }

        return false;
    }

    private async Task RunDeviceAsync(Guid deviceId, List<(CheckRuntime Runtime, CatalogComplianceType Type)> due, CancellationToken ct)
    {
        try
        {
            Device? device;
            List<(Guid CheckId, int Version, ComplianceInvocation Invocation, CatalogComplianceType Type)> work;
            lock (_sync)
            {
                device = _deviceRows.GetValueOrDefault(deviceId);
                work = [.. due.Select(d => (d.Runtime.Check.Id, d.Runtime.Check.Version, d.Runtime.Invocation, d.Type))];
            }

            if (device is null || device.Status != DeviceStatus.Ok)
            {
                return;
            }

            IVapixClient? vapix = null;
            string? connectError = null;
            string? siteError = null;
            try
            {
                vapix = await _vapix.CreateAsync(deviceId, ct).ConfigureAwait(false); // cached by the factory, never disposed here
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                connectError = Failure(ex);
                siteError = Sites.SiteErrors.Find(ex)?.Message;
            }

            var reads = new Reads();
            foreach (var (checkId, version, invocation, type) in work)
            {
                var result = siteError is not null
                    ? new ComplianceResult(ComplianceOutcome.NotChecked, siteError)
                    : connectError is not null
                    ? ComplianceResult.CouldNotCheck(connectError)
                    : await EvaluateAsync(type, invocation, device, vapix, reads, ct).ConfigureAwait(false);
                Apply(checkId, version, [(deviceId, result)]);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
#pragma warning disable CA1031 // A failed run is logged; the device is due again at the next tick.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunFailed(ex, deviceId);
        }
        finally
        {
            var cleared = new List<ComplianceResultModel>();
            lock (_sync)
            {
                _reading.Remove(deviceId);

                // A run that ended without a result (stopped, failed) is no longer checking, unless it is due again.
                foreach (var (runtime, _) in due)
                {
                    var key = (runtime.Check.Id, deviceId);
                    if ((!_forced.TryGetValue(runtime.Check.Id, out var set) || !set.Contains(deviceId)) && _checking.Remove(key)
                        && runtime.Results.TryGetValue(deviceId, out var current))
                    {
                        cleared.Add(Model(runtime.Check.Id, deviceId, current));
                    }
                }
            }

            Publish(cleared);
            try
            {
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // stopped
            }

            Wake();
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.FlushInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await WriteDirtyAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogWriteFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <summary>Results written per transaction.</summary>
    internal const int WriteChunk = 5_000;

    private async Task WriteDirtyAsync(CancellationToken ct)
    {
        await _writing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Chunks of WriteChunk rows, one transaction each: a whole round (30,000 devices x 50 checks) never holds the
            // database's write lock for long, so polling and tasks write in between. The keys in the table's order (the
            // upper-case text of the ids), sorted once per flush: neighbouring rows share index pages.
            List<(Guid CheckId, Guid DeviceId)> order;
            lock (_sync)
            {
                if (_dirty.Count == 0)
                {
                    return;
                }

                order = [.. _dirty.Keys
                    .Select(k => (Key: k, Text: k.CheckId.ToString().ToUpperInvariant() + k.DeviceId.ToString().ToUpperInvariant()))
                    .OrderBy(k => k.Text, StringComparer.Ordinal)
                    .Select(k => k.Key)];
            }

            foreach (var keys in order.Chunk(WriteChunk))
            {
                var batch = new List<KeyValuePair<(Guid CheckId, Guid DeviceId), ComplianceResultEntity?>>(keys.Length);
                lock (_sync)
                {
                    foreach (var key in keys)
                    {
                        if (_dirty.Remove(key, out var value))
                        {
                            batch.Add(new(key, value)); // the newest value of the key; a change after this flush stays dirty
                        }
                    }
                }

                try
                {
                    await _store.WriteResultsAsync(
                        [.. batch.Where(b => b.Value is not null).Select(b => b.Value!)],
                        [.. batch.Where(b => b.Value is null).Select(b => b.Key)],
                        ct).ConfigureAwait(false);
                }
                catch
                {
                    lock (_sync)
                    {
                        foreach (var (key, value) in batch)
                        {
                            _dirty.TryAdd(key, value); // a newer change wins
                        }
                    }

                    throw;
                }
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    // ---- watchers and events ----

    private void Publish(List<ComplianceResultModel> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var watcher in _watchers)
            {
                foreach (var change in changes)
                {
                    watcher.TryWrite(change);
                }
            }
        }

        // Events: one change per device whose failing state changed (O(changes)); a check never reported in full gets its
        // full state first.
        var states = new List<(string TypeId, Guid DeviceId, bool On)>();
        var full = new HashSet<Guid>();
        lock (_sync)
        {
            if (_started && _enabled && _publisher is not null)
            {
                foreach (var change in changes)
                {
                    if (!_checks.TryGetValue(change.CheckId, out var runtime))
                    {
                        continue;
                    }

                    if (runtime.ReportedFailing is not { } reported)
                    {
                        full.Add(change.CheckId);
                        continue;
                    }

                    var on = runtime.Failing.Contains(change.DeviceId) && runtime.Check.Enabled && _catalog.Find(runtime.Check.TypeId)?.Available == true;
                    if (on ? reported.Add(change.DeviceId) : reported.Remove(change.DeviceId))
                    {
                        states.Add((EventTypeId(change.CheckId), change.DeviceId, on));
                    }
                }
            }
        }

        foreach (var (typeId, deviceId, on) in states)
        {
            try
            {
                _publisher!().SetState(typeId, deviceId, on);
            }
#pragma warning disable CA1031 // The event rules must never stop the checks.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogEventFailed(ex);
                break;
            }
        }

        foreach (var checkId in full)
        {
            ReportEvent(checkId);
        }

        Oadm.Core.Auth.CallerContext.Raise(ChecksChanged, this, EventArgs.Empty);
    }

    private void OnCatalogChanged()
    {
        List<ComplianceResultModel> changes = [];
        lock (_sync)
        {
            if (!_started || !_enabled)
            {
                return;
            }

            foreach (var runtime in _checks.Values)
            {
                Reconcile(runtime, changes);
            }
        }

        Publish(changes);
        ReportAllEvents();
        _ = EvaluateCachedAsync(null, null, _stopping.Token);
        Wake();
        Oadm.Core.Auth.CallerContext.Raise(ChecksChanged, this, EventArgs.Empty);
    }

    private void ReportAllEvents()
    {
        List<Guid> ids;
        lock (_sync)
        {
            ids = [.. _checks.Keys];
        }

        foreach (var id in ids)
        {
            ReportEvent(id);
        }
    }

    /// <summary>The full state of a check's event: on for every device the check fails on (off while the check is off or not available).</summary>
    private void ReportEvent(Guid checkId)
    {
        if (_publisher is null)
        {
            return;
        }

        List<Guid> failing;
        lock (_sync)
        {
            if (!_started || !_enabled || !_checks.TryGetValue(checkId, out var runtime))
            {
                return;
            }

            var available = runtime.Check.Enabled && _catalog.Find(runtime.Check.TypeId)?.Available == true;
            failing = available ? [.. runtime.Failing] : [];
            if (runtime.ReportedFailing is { } reported && reported.SetEquals(failing))
            {
                return;
            }

            runtime.ReportedFailing = [.. failing];
        }

        try
        {
            _publisher().ReplaceStates(EventTypeId(checkId), failing);
        }
#pragma warning disable CA1031 // The event rules must never stop the checks.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(ex);
        }
    }

    private static EventTypeInfo EventTypeOf(ComplianceCheck check) =>
        new(EventTypeId(check.Id), "Compliance: " + check.Name, EventGroup, EventKind.Stateful)
        {
            Description = "On while the check fails on the device.",
        };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compliance results could not be written")]
    private partial void LogWriteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compliance checks: a round failed")]
    private partial void LogTickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compliance checks of device {DeviceId} failed")]
    private partial void LogRunFailed(Exception ex, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compliance events could not be reported")]
    private partial void LogEventFailed(Exception ex);

    private sealed record Result(ComplianceOutcome Outcome, string Detail, DateTime CheckedUtc, DateTime SinceUtc);

    /// <summary>
    /// A check with its results per device, counters per outcome and the devices it fails on (O(1) per change), and its
    /// devices and tags as sets (O(1) scope test, also for a choice of 30,000 devices).
    /// </summary>
    private sealed class CheckRuntime
    {
        private readonly int[] _counts = new int[5];
        private HashSet<Guid> _deviceIds = [];
        private HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
        private bool _all;

        public CheckRuntime(ComplianceCheck check)
        {
            Check = check;
            Update(check);
        }

        public ComplianceCheck Check { get; private set; }

        /// <summary>
        /// The check as the types get it, built once per version: every device run gets the same instance (a configuration
        /// file in the settings is not serialized again per device; types may cache per instance).
        /// </summary>
        public ComplianceInvocation Invocation { get; private set; } = null!;

        /// <summary>The type's one-line summary of the settings (<see cref="IComplianceCheckType.Describe"/>), once per version.</summary>
        public string? Summary { get; set; }

        /// <summary>The catalog entry the summary was made with (a plugin turned on or off makes it again).</summary>
        public CatalogComplianceType? SummaryType { get; set; }

        public Dictionary<Guid, Result> Results { get; } = [];

        /// <summary>The devices the check fails on now.</summary>
        public HashSet<Guid> Failing { get; } = [];

        /// <summary>The devices the check's event is on for in the event rules; null until the first full report.</summary>
        public HashSet<Guid>? ReportedFailing { get; set; }

        public void Update(ComplianceCheck check)
        {
            Check = check;
            Invocation = new ComplianceInvocation(check.Id, check.Name, JsonSerializer.SerializeToElement(check.Settings));
            Summary = null;
            _all = check.Devices.Mode == DeviceSelectorMode.All;
            _deviceIds = [.. check.Devices.DeviceIds];
            _tags = new HashSet<string>(check.Devices.Tags, StringComparer.OrdinalIgnoreCase);
        }

        public bool InScope(Device device)
        {
            if (_all || _deviceIds.Contains(device.Id))
            {
                return true;
            }

            if (_tags.Count > 0)
            {
                foreach (var tag in device.Tags)
                {
                    if (_tags.Contains(tag))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public int Count(ComplianceOutcome outcome) => _counts[(int)outcome];

        public void Set(Guid deviceId, Result result)
        {
            if (Results.TryGetValue(deviceId, out var old))
            {
                _counts[(int)old.Outcome]--;
            }

            Results[deviceId] = result;
            _counts[(int)result.Outcome]++;
            if (result.Outcome == ComplianceOutcome.Failed)
            {
                Failing.Add(deviceId);
            }
            else
            {
                Failing.Remove(deviceId);
            }
        }

        public bool Remove(Guid deviceId)
        {
            if (!Results.Remove(deviceId, out var old))
            {
                return false;
            }

            _counts[(int)old.Outcome]--;
            Failing.Remove(deviceId);
            return true;
        }
    }

    private sealed class Reads : IComplianceReads
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _values = new(StringComparer.Ordinal);

        public async Task<T> GetOrAddAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(read);
            var value = _values.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await read(ct).ConfigureAwait(false)));
            return (T)(await value.Value.ConfigureAwait(false))!;
        }
    }

    private sealed class NoReads : IComplianceReads
    {
        public static readonly NoReads Instance = new();

        public Task<T> GetOrAddAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken ct) =>
            throw new InvalidOperationException("A check on cached data does not read the device.");
    }

    private sealed record Context(IDeviceInfo Device, IVapixClient? Vapix, IComplianceReads Reads, DateTimeOffset Now, ILogger Logger, ICorePlugin? Owner)
        : IComplianceContext;
}

/// <summary>The results at subscribe time and every change after them; dispose to stop.</summary>
public sealed class ComplianceWatch(IReadOnlyList<ComplianceResultModel> snapshot, ChannelReader<ComplianceResultModel> changes, Action stop) : IDisposable
{
    private int _disposed;

    public IReadOnlyList<ComplianceResultModel> Snapshot { get; } = snapshot;

    public ChannelReader<ComplianceResultModel> Changes { get; } = changes;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            stop();
        }
    }
}
