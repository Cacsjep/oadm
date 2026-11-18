using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.VapixCommander;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// VAPIX commands (spec node 13): commands of the VAPIX Commander library with their values, run by the Commander's task
/// (<c>oadm.vapix-commander.run</c>) with its checks (the request's safety, the API requirements before the first write).
/// The values are checked like the Commander page does (<see cref="FieldValues.Resolve"/>); commands with a password or a
/// file and dangerous commands are not possible in a profile. A command the camera lacks (its fresh API list) is left out
/// and named in a Warning.
/// </summary>
public sealed class VapixCommandsNode(CommandLibrary library) : DeviceNode<VapixCommandsSettings>
{
    public const string RolloutTaskId = "oadm.vapix-commander.run";

    /// <summary>Most commands of one node (the Commander task takes 100).</summary>
    public const int MaxCommands = 50;

    /// <summary>The library the commands come from (loaded from the plugin's folder).</summary>
    public CommandLibrary Library { get; } = library ?? throw new ArgumentNullException(nameof(library));

    public override NodeKind Kind => NodeKind.VapixCommands;

    public override string Name => "VAPIX commands";

    public override object DefaultSettings => new VapixCommandsSettings([]);

    protected override string? CheckSettings(VapixCommandsSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var steps = settings.Commands ?? [];
        if (steps.Count == 0)
        {
            return "Add a command.";
        }

        if (steps.Count > MaxCommands)
        {
            return $"Add at most {MaxCommands} commands.";
        }

        foreach (var step in steps)
        {
            if (Library.Find(step?.CommandId ?? string.Empty) is not { } command)
            {
                return $"{step?.CommandId ?? "A command"} is not in the VAPIX Commander library.";
            }

            if (command.Dangerous)
            {
                return $"{command.Name} cannot run in a profile.";
            }

            if (command.Fields.Any(f => f.Type is FieldTypes.Password or FieldTypes.File))
            {
                return $"{command.Name} needs a password or a file; it cannot run in a profile.";
            }

            try
            {
                FieldValues.Resolve(command, step!.Values);
            }
            catch (CommandValidationException ex)
            {
                return $"{command.Name}: {(ex.Problems.Count > 0 ? ex.Problems[0] : ex.Message)}";
            }
        }

        return null;
    }

    protected override async Task<NodeResult> RunOnAsync(VapixCommandsSettings settings, NodeRun run, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        IReadOnlyList<DeviceApi> apis;
        bool hasVideo;
        try
        {
            var vapix = await run.Host.Vapix.CreateAsync(deviceId, ct).ConfigureAwait(false);
            apis = await vapix.GetApiListAsync(ct).ConfigureAwait(false);
            hasVideo = (await run.Host.Devices.FindAsync(deviceId, ct).ConfigureAwait(false))?.HasVideo ?? true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            return NodeResult.Error("The camera's API list could not be read: " + ex.Message.TrimEnd('.') + ".");
        }

        var run1 = new List<(CommandDefinition Command, IReadOnlyDictionary<string, JsonElement> Values)>();
        var lacking = new List<string>();
        foreach (var step in settings.Commands ?? [])
        {
            var command = Library.Find(step.CommandId!)!;
            if (Compatibility.Check(command, apis, hasVideo).IsCompatible)
            {
                run1.Add((command, step.Values ?? new Dictionary<string, JsonElement>()));
            }
            else
            {
                lacking.Add(command.Name);
            }
        }

        var notHere = lacking.Count == 0 ? null : $"Not on this camera: {NodeRules.List(lacking)}.";
        if (run1.Count == 0)
        {
            return NodeResult.Warning(notHere!);
        }

        var outcome = await TaskRuns.RunTaskAsync(run.Host, RolloutTaskId, deviceId, Payload(run1), ct).ConfigureAwait(false);
        var ran = $"Ran {NodeRules.List([.. run1.Select(c => c.Command.Name)])}.";
        return outcome?.State switch
        {
            TaskEndState.Done => notHere is null ? NodeResult.Success(ran) : NodeResult.Warning(ran + " " + notHere),
            TaskEndState.DoneWithWarnings => NodeResult.Warning((NodeRules.Sentence(outcome.Message, ran) + " " + notHere).Trim()),
            null => NodeResult.Error(NodeTexts.TaskLost(Name)),
            _ => NodeResult.Error(NodeRules.Sentence(outcome.Message, "The commands failed")),
        };
    }

    /// <summary>
    /// The Commander task's payload: a rollout of its own, the commands confirmed (the profile is the person's confirmation;
    /// dangerous commands never get here), secrets masked in its log.
    /// </summary>
    internal static string Payload(IReadOnlyList<(CommandDefinition Command, IReadOnlyDictionary<string, JsonElement> Values)> commands) =>
        CommandJson.Write(new RolloutJson(
            Guid.NewGuid(),
            StopOnFirstError: false,
            Confirmed: true,
            ConfirmedDangerous: false,
            MaskSecrets: true,
            [.. commands.Select(c => new RolloutCommandJson(c.Command, c.Values))]));

    private sealed record RolloutJson(Guid RolloutId, bool StopOnFirstError, bool Confirmed, bool ConfirmedDangerous, bool MaskSecrets, IReadOnlyList<RolloutCommandJson> Commands);

    private sealed record RolloutCommandJson(CommandDefinition Command, IReadOnlyDictionary<string, JsonElement> Values);
}
