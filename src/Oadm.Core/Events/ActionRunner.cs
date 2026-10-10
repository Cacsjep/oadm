using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

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
/// Runs the actions of rule edges. The edges of one rule run one after another in the order they happened (so a
/// "turns off" never overtakes its "turns on"); within an edge the actions run in the listed order, each on its targets
/// in parallel (at most <see cref="ActionRunnerOptions.MaxParallel"/> runs at once, all rules together). A failed action
/// is logged and the next one still runs (no retry). Every run is a line in the event log.
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
    private readonly ConcurrentDictionary<Guid, Task> _tails = new();
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

    public void Dispatch(RuleEdgeJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_sync)
        {
            var previous = _tails.GetValueOrDefault(job.Rule.Id) ?? Task.CompletedTask;
            _tails[job.Rule.Id] = previous.ContinueWith(_ => RunJobAsync(job), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Waits until every dispatched edge finished (tests, shutdown).</summary>
    public async Task WaitIdleAsync(CancellationToken ct)
    {
        Task[] tails;
        lock (_sync)
        {
            tails = [.. _tails.Values];
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
            await RunOneAsync(job, catalogAction, settings, null, ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<Guid> targets;
        if (action.Targets.Mode == DeviceSelectorMode.EventDevice)
        {
            if (subjectDevice is not { } subject)
            {
                return;
            }

            targets = [subject];
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

        await Task.WhenAll(targets.Select(device => RunOneAsync(job, catalogAction, settings, device, ct))).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Guid>> ResolveTargetsAsync(DeviceSelector selector, CancellationToken ct)
    {
        if (selector.Mode == DeviceSelectorMode.Devices)
        {
            return [.. selector.DeviceIds.Distinct()];
        }

        var all = await _devices.ListAsync(ct).ConfigureAwait(false);
        return selector.Mode switch
        {
            DeviceSelectorMode.All => [.. all.Select(d => d.Id)],
            DeviceSelectorMode.Tags => [.. all.Where(d => d.Tags.Any(t => selector.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).Select(d => d.Id)],
            _ => [],
        };
    }

    private async Task RunOneAsync(RuleEdgeJob job, CatalogAction action, JsonElement settings, Guid? deviceId, CancellationToken ct)
    {
        var plugin = action.Plugin;
        IDeviceInfo? device = null;
        if (deviceId is { } id)
        {
            device = await _devices.FindAsync(id, ct).ConfigureAwait(false);
            if (device is null)
            {
                Write(job, deviceId, plugin.DisplayName, EventLogResult.Skipped, DeviceMessages.Removed, deviceText: DeviceMessages.Removed);
                return;
            }

            var refused = device.Status switch
            {
                DeviceStatus.CredentialsRequired => DeviceMessages.CredentialsRequired,
                DeviceStatus.PasswordNotSet => DeviceMessages.PasswordNotSet,
                DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
                _ => null,
            };
            if (refused is not null)
            {
                Write(job, deviceId, plugin.DisplayName, EventLogResult.Skipped, refused, device);
                return;
            }
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
                device);
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
            Write(job, deviceId, plugin.DisplayName, EventLogResult.Failed, Joined(lines, ex.Message), device);
        }
        finally
        {
            _slots.Release();
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

    private void Write(RuleEdgeJob job, Guid? deviceId, string what, EventLogResult result, string message, IDeviceInfo? device = null, string? deviceText = null) =>
        _log.Write(EventLogKind.Action, result, what, deviceId, deviceText ?? (device is null ? string.Empty : DeviceRef.Text(device.Address, device.Model)), job.Rule.Id, job.Rule.Name, message);

    /// <summary>Contributed actions share their core plugin's settings; standalone ones use their package's.</summary>
    private static string SettingsId(RegisteredActionPlugin action) => action.Owner?.Id ?? action.Origin.PackageId;

    [LoggerMessage(Level = LogLevel.Error, Message = "Event rule {Rule}: action {Action} failed unexpectedly")]
    private partial void LogRunFailed(Exception ex, string rule, string action);

    private sealed class RunContext(
        IVapixClient? vapix,
        ILogger logger,
        ICorePlugin? owner,
        IPluginSettings settings,
        List<string> lines,
        RuleEdgeJob job,
        IDeviceInfo? device) : IActionContext
    {
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
    }

    private sealed class QueryContext(ILogger logger, ICorePlugin? owner, IPluginSettings settings, IDeviceRepository devices) : IActionQueryContext
    {
        public ILogger Logger { get; } = logger;

        public ICorePlugin? Owner { get; } = owner;

        public IPluginSettings Settings { get; } = settings;

        public IDeviceRepository Devices { get; } = devices;
    }
}

/// <summary>{rule}, {event}, {device}, {address}, {model}, {serial}, {state}, {time} in action texts.</summary>
public static class Placeholders
{
    public static string Fill(string? text, RuleEdgeJob job, IDeviceInfo? device)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var address = device?.Address ?? job.Trigger.DeviceAddress ?? string.Empty;
        var model = device?.Model ?? job.Trigger.DeviceModel ?? string.Empty;
        var filled = text
            .Replace("{rule}", job.Rule.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{event}", job.Trigger.EventName, StringComparison.OrdinalIgnoreCase)
            .Replace("{device}", address.Length == 0 ? string.Empty : DeviceRef.Text(address, model), StringComparison.OrdinalIgnoreCase)
            .Replace("{address}", address, StringComparison.OrdinalIgnoreCase)
            .Replace("{model}", model, StringComparison.OrdinalIgnoreCase)
            .Replace("{serial}", device?.Serial ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{state}", job.Trigger.State switch { true => "on", false => "off", _ => string.Empty }, StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", job.Trigger.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        // Values of the event (device triggers: {port}, {total}, ...); the fixed names above already went.
        foreach (var (name, value) in job.Trigger.Data)
        {
            if (name.Length > 0 && filled.Contains('{', StringComparison.Ordinal))
            {
                filled = filled.Replace("{" + name + "}", value, StringComparison.OrdinalIgnoreCase);
            }
        }

        return filled;
    }
}
