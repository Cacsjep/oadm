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

    /// <summary>One action run on one target.</summary>
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs the actions of rule edges. The edges of one rule and subject run one after another in the order they happened (so
/// a subject's "turns off" never overtakes its "turns on"); edges that run actions on chosen / tag / all devices are also
/// ordered among themselves. Edges of other subjects do not wait for each other (one device that does not answer never
/// delays another). Within an edge the actions run in the listed order, each on its targets in parallel (at most
/// <see cref="ActionRunnerOptions.MaxParallel"/> runs at once, all rules together). A failed action is logged and the next
/// one still runs (no retry). Every run is a line in the event log. Nothing runs before <see cref="PluginsStarted"/>:
/// edges of the start-up evaluation wait for the core plugins whose actions they use.
/// </summary>
public sealed partial class ActionRunner : IActionDispatcher, IAsyncDisposable
{
    private readonly EventCatalog _catalog;
    private readonly RuleStore _store;
    private readonly EventLog _log;
    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly IPluginSettingsProvider _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ActionRunnerOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly Dictionary<TailKey, Task> _tails = [];
    private readonly TaskCompletionSource _pluginsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private readonly Lock _sync = new();

    public ActionRunner(
        EventCatalog catalog,
        RuleStore store,
        EventLog log,
        IDeviceRepository devices,
        IVapixClientFactory vapix,
        IPluginSettingsProvider settings,
        ILoggerFactory? loggerFactory = null,
        ActionRunnerOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ActionRunner>();
        _options = options ?? new ActionRunnerOptions();
        _slots = new SemaphoreSlim(_options.MaxParallel, _options.MaxParallel);
    }

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
            await WaitIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Shutdown: runs that do not stop in time are abandoned.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stop.Dispose();
        _slots.Dispose();
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

    private async Task RunJobAsync(RuleEdgeJob job)
    {
        var ct = _stop.Token;
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
        using var settingsDocument = JsonDocument.Parse(settingsObject.ToJsonString());
        var settings = settingsDocument.RootElement.Clone();
        bool runs;
        try
        {
            runs = plugin.RunsOn(job.Edge, settings);
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

        if (!plugin.NeedsDevice)
        {
            // Actions without devices run with every edge (an email per camera), like actions on the event's device.
            await RunOneAsync(job, catalogAction, settings, null, null, ct).ConfigureAwait(false);
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

        await Task.WhenAll(targets.Select(target => RunOneAsync(job, catalogAction, settings, target.Id, target.Device, ct))).ConfigureAwait(false);
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

        var all = await _devices.ListAsync(ct).ConfigureAwait(false);
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

    private async Task RunOneAsync(RuleEdgeJob job, CatalogAction action, JsonElement settings, Guid? deviceId, IDeviceInfo? known, CancellationToken ct)
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
            var refused = DeviceMessages.Refusal(device);
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

        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var lines = new List<string>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var limit = _options.ActionTimeout;
            try
            {
                if (plugin.MaxRunTime(settings) is { } longer && longer > limit)
                {
                    limit = longer < TimeSpan.FromHours(2) ? longer : TimeSpan.FromHours(2);
                }
            }
#pragma warning disable CA1031 // Plugin code is untrusted: the default applies.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

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
                Licenses?.LicenseOf(action.Registered.Origin.PackageId));
            var invocation = new ActionInvocation(job.Rule.Id, job.Rule.Name, job.Edge, settings, device, job.Trigger);
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
            _slots.Release();
        }
    }

    /// <summary>
    /// "Send test" of the rule dialog (administrators): runs an action without devices once now, outside every rule state
    /// and limit, with sample values for the event ("sample" for every value of <paramref name="eventTypeId"/>) and the
    /// given device as the device of the event. Password fields left empty take the stored value of the rule's action.
    /// Nothing is written to the event log. Returns the result line.
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

        var type = _catalog.FindEventType(eventTypeId);
        var device = deviceId is { } id ? await _devices.FindAsync(id, ct).ConfigureAwait(false) : null;
        var data = (type?.Info.DataFields ?? []).ToDictionary(f => f.Name, f => f.Values is { Count: > 0 } values ? values[0] : "sample", StringComparer.Ordinal);
        var trigger = new ActionTrigger(
            type?.Id ?? string.Empty, type?.Info.DisplayName ?? "Test", device?.Id, device?.Address, device?.Model, true, DateTimeOffset.UtcNow, data);
        var definition = new RuleDefinition { Id = ruleId ?? Guid.Empty, Name = string.IsNullOrWhiteSpace(ruleName) ? "Test" : ruleName };
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
                Licenses?.LicenseOf(action.Registered.Origin.PackageId));
            var invocation = new ActionInvocation(definition.Id, definition.Name, ActionEdge.TurnedOn, document.RootElement.Clone(), null, trigger);
            await action.Plugin.ExecuteAsync(context, invocation, timeout.Token).ConfigureAwait(false);
            return (true, Joined(lines, "Sent"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, Joined(lines, "Timeout - the action did not finish in time"));
        }
#pragma warning disable CA1031 // A failed test is the answer, never a crash.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return (false, Joined(lines, ex.Message));
        }
    }

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

    private sealed class RunContext(
        IVapixClient? vapix,
        ILogger logger,
        ICorePlugin? owner,
        IPluginSettings settings,
        List<string> lines,
        RuleEdgeJob job,
        IDeviceInfo? device,
        Sdk.Licensing.IPluginLicense? license) : IActionContext
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
            ["site"] = device?.SiteName ?? (trigger.Data.TryGetValue("site", out var site) ? site : string.Empty),
            ["webUrl"] = address.Length == 0 ? string.Empty : (device?.HttpsEnabled == false ? "http://" : "https://") + UrlHost(address) + "/",
        };

        foreach (var (name, value) in trigger.Data)
        {
            if (name.Length > 0)
            {
                values.TryAdd(name, value);
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
