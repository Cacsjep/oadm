using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// The rollout task contributed by the VAPIX Commander: runs the selected commands on one device, one named step
/// per command ("Check compatibility" first). Started only from the Commander page (<see cref="ShowInMenus"/> false).
/// Compatibility is checked against the cached API list for read-only rollouts and a fresh one before the first write.
/// A failed command fails its step with the device's error text; the task fails at the end with the first error.
/// With "Stop on first error" the first failure stops the whole rollout (see <see cref="RolloutRegistry"/>).
/// </summary>
public sealed class RolloutTaskPlugin : ITaskPlugin
{
    public const string PluginId = "oadm.vapix-commander.run";
    public const string CompatibilityStep = "Check compatibility";
    public const string StoppedHereReason = "Not run: stopped on the first error.";
    public const string StoppedElsewhereReason = "Not run: the rollout stopped after an error on another device.";

    /// <summary>Commands per rollout; with "Check compatibility" this stays below the 200 steps of a task.</summary>
    public const int MaxCommands = 100;

    private readonly VapixCommanderPlugin _owner;

    /// <summary>Created by its core plugin only (no public constructor, so the loader never registers it alone).</summary>
    internal RolloutTaskPlugin(VapixCommanderPlugin owner)
    {
        _owner = owner;
    }

    public string Id => PluginId;

    public string DisplayName => "VAPIX commands";

    public string? IconKey => "command";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status != DeviceStatus.CertificateChanged;
    }

    /// <summary>Unique step names: the command name, "(2)", "(3)" ... for repeats.</summary>
    public static IReadOnlyList<string> StepNames(IEnumerable<CommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var command in commands)
        {
            var name = command.Name.Length > 180 ? command.Name[..180] : command.Name;
            var count = seen[name] = seen.GetValueOrDefault(name) + 1;
            names.Add(count == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({count})"));
        }

        return names;
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = JsonSerializer.Deserialize<RolloutPayload>(payloadJson ?? "{}", CommandJson.Api)
            ?? throw new InvalidOperationException("The rollout has no commands.");
        if (payload.Commands.Count == 0)
        {
            throw new InvalidOperationException("The rollout has no commands.");
        }

        var owner = _owner;
        var rollout = owner.Rollouts.Get(payload.RolloutId, payload.StopOnFirstError);
        var commands = payload.Commands.Select(c => c.Command).ToList();
        var names = StepNames(commands);
        ctx.PlanSteps([CompatibilityStep, .. names]);
        try
        {
            if (!rollout.Join(ctx.TaskId))
            {
                StopBecauseOfOtherDevice(ctx, owner, [CompatibilityStep, .. names], ct);
            }

            var compatibility = await CheckCompatibilityAsync(ctx, device, commands, ct).ConfigureAwait(false);
            var failures = new List<string>();
            for (var i = 0; i < commands.Count; i++)
            {
                if (rollout.IsAborted && failures.Count == 0)
                {
                    StopBecauseOfOtherDevice(ctx, owner, names.Skip(i), ct);
                }

                var error = await RunCommandAsync(ctx, payload.Commands[i], names[i], compatibility[i], ct).ConfigureAwait(false);
                if (error is null)
                {
                    continue;
                }

                failures.Add($"{names[i]}: {error}");
                if (payload.StopOnFirstError)
                {
                    foreach (var name in names.Skip(i + 1))
                    {
                        ctx.SkipStep(name, StoppedHereReason);
                    }

                    var cancel = rollout.Abort($"{device.Address}: {failures[0]}");
                    foreach (var taskId in cancel)
                    {
                        owner.CancelTask(taskId);
                    }

                    break;
                }
            }

            if (failures.Count > 0)
            {
                throw new VapixCommandException(failures.Count == 1
                    ? failures[0]
                    : string.Create(CultureInfo.InvariantCulture, $"{failures[0]} (+{failures.Count - 1} more failed)"));
            }
        }
        finally
        {
            rollout.Finish(ctx.TaskId);
        }
    }

    /// <summary>
    /// One step: reads the API list (fresh when a command writes or nothing is cached) and checks every command.
    /// Returns null per compatible command, else the reason. A failed read fails the task: nothing was sent.
    /// </summary>
    private static async Task<string?[]> CheckCompatibilityAsync(ITaskExecutionContext ctx, IDeviceInfo device, List<CommandDefinition> commands, CancellationToken ct)
    {
        using var step = ctx.BeginStep(CompatibilityStep);
        IReadOnlyList<DeviceApi> apis = device.Apis;
        var fresh = commands.Any(c => c.Writes) || apis.Count == 0;
        if (fresh)
        {
            try
            {
                apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var text = "Could not read the API list: " + TransportErrors.Describe(ex, TimeSpan.FromSeconds(CommandLimits.DefaultTimeoutSeconds)) + ". Nothing was changed.";
                step.Fail(text);
                throw new VapixCommandException(text, ex);
            }
        }

        var result = new string?[commands.Count];
        for (var i = 0; i < commands.Count; i++)
        {
            var check = Compatibility.Check(commands[i], apis, device.HasVideo);
            result[i] = check.IsCompatible ? null : check.Text + ". Nothing was sent.";
        }

        var unsupported = result.Count(r => r is not null);
        var source = fresh ? "fresh API list" : "cached API list";
        step.Complete(unsupported == 0
            ? string.Create(CultureInfo.InvariantCulture, $"All {commands.Count} commands supported ({source})")
            : string.Create(CultureInfo.InvariantCulture, $"{unsupported} of {commands.Count} commands not supported ({source})"));
        return result;
    }

    /// <summary>Runs one command step. Returns null on success, else the error text (the step is already Failed).</summary>
    private static async Task<string?> RunCommandAsync(ITaskExecutionContext ctx, RolloutPayloadCommand item, string name, string? incompatible, CancellationToken ct)
    {
        using var step = ctx.BeginStep(name);
        if (incompatible is not null)
        {
            step.Fail(incompatible);
            ctx.Log(TaskLogLevel.Error, $"{name}: {incompatible}");
            return incompatible;
        }

        RenderedRequest request;
        try
        {
            request = CommandRenderer.Render(item.Command, item.Values);
        }
        catch (CommandValidationException ex)
        {
            var text = ex.Message + " Nothing was sent.";
            step.Fail(text);
            ctx.Log(TaskLogLevel.Error, $"{name}: {text}");
            return text;
        }

        CommandOutcome outcome;
        try
        {
            outcome = await CommandExecutor.ExecuteAsync(ctx.Vapix, item.Command, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            step.Fail("Cancelled.");
            throw;
        }

        var status = outcome.StatusCode is { } code ? string.Create(CultureInfo.InvariantCulture, $"HTTP {code}") : "no answer";
        if (outcome.Success)
        {
            step.Complete(outcome.Summary);
            ctx.Log(TaskLogLevel.Info, $"{name}: {request.Describe()} -> {status}, {outcome.Summary}");
            return null;
        }

        step.Fail(outcome.Summary);
        ctx.Log(TaskLogLevel.Error, $"{name}: {request.Describe()} -> {status}, {outcome.Summary}");
        return outcome.Summary;
    }

    /// <summary>Skips the remaining steps and ends the task as Cancelled (another device failed with "Stop on first error").</summary>
    private static void StopBecauseOfOtherDevice(ITaskExecutionContext ctx, VapixCommanderPlugin owner, IEnumerable<string> remaining, CancellationToken ct)
    {
        foreach (var name in remaining)
        {
            ctx.SkipStep(name, StoppedElsewhereReason);
        }

        ctx.Log(TaskLogLevel.Warning, "The rollout stopped after an error on another device.");
        owner.CancelTask(ctx.TaskId);
        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException("The rollout stopped after an error on another device.");
    }
}

/// <summary>A command failed on the device; the message is the user-facing error ("Set shift level: Bad Request - HTTP 400").</summary>
public sealed class VapixCommandException : Exception
{
    public VapixCommandException()
    {
    }

    public VapixCommandException(string message)
        : base(message)
    {
    }

    public VapixCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
