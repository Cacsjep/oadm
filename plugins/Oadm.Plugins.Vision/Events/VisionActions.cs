using System.Text.Json;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Vision.Events;

/// <summary>
/// Rule action "Run pipeline" (docs/spec/vision-center.md "Events and the rule actions"): runs one picture of the chosen
/// pipeline now. An On action pipeline publishes its events (Objects found or No objects found, ...); a watching one answers
/// with its fresh result. While a run of that On action pipeline runs, the action ends with "Already running" in the event
/// log and nothing is queued. Event rules are for users with all devices, so every pipeline is a choice.
/// </summary>
public sealed class RunPipelineAction(VisionPlugin owner) : IActionPlugin
{
    public string Id => VisionInfo.PluginId + ".run-pipeline";

    public string DisplayName => VisionInfo.RunPipelineActionName;

    public string? IconKey => VisionInfo.IconKey;

    public string Group => VisionInfo.DisplayName;

    public string? Description => "Runs one picture through a vision pipeline now.";

    public bool NeedsDevice => false;

    public IReadOnlyList<ActionField> Fields { get; } = [ActionField.DynamicChoice(VisionInfo.PipelineActionField, "Pipeline")];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var id = VisionActionSettings.Get(settings, VisionInfo.PipelineActionField);
        if (string.IsNullOrEmpty(id))
        {
            return new Dictionary<string, string> { [VisionInfo.PipelineActionField] = VisionInfo.ChoosePipeline };
        }

        return owner.FindPipeline(id) is not null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [VisionInfo.PipelineActionField] = PipelineRules.Gone };
    }

    public string Describe(JsonElement settings) =>
        owner.FindPipeline(VisionActionSettings.Get(settings, VisionInfo.PipelineActionField)) is { } pipeline ? $"Run the pipeline {pipeline.Name}" : DisplayName;

    public Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IActionQueryContext ctx, string fieldKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActionChoice>>(fieldKey == VisionInfo.PipelineActionField
            ? [.. owner.AllPipelines.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new ActionChoice(p.Id, p.Name))]
            : []);

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var pipeline = owner.FindPipeline(invocation.Get(VisionInfo.PipelineActionField)) ?? throw new InvalidOperationException(PipelineRules.Gone);
        try
        {
            var result = await owner.RunOnceAsync(pipeline, ct).ConfigureAwait(false);
            ctx.Log(Summary(pipeline, result));
        }
        catch (InvalidOperationException ex) when (ex.Message == PipelineRules.AlreadyRunning)
        {
            ctx.Log(PipelineRules.AlreadyRunning);
        }
    }

    /// <summary>The event log's line of a run: the live lines of its Count and Event nodes ("Gate count: 14; Gate: On").</summary>
    internal static string Summary(Pipeline pipeline, FrameResult result)
    {
        var parts = pipeline.Nodes
            .Where(n => n.Kind is NodeKinds.Count or NodeKinds.Event)
            .Select(n => result.Nodes.TryGetValue(n.Id, out var output) && output.Live.Length > 0 ? $"{n.Name}: {output.Live}" : null)
            .OfType<string>()
            .ToList();
        return parts.Count > 0 ? string.Join("; ", parts) : "Done";
    }
}

/// <summary>
/// Rule action "Reset counter": a Count node of a pipeline starts again at 0. The counter's value names its pipeline too
/// (<c>&lt;pipeline id&gt;/&lt;node id&gt;</c>, labels "Pipeline: Counter"), since a choice list cannot follow another field.
/// </summary>
public sealed class ResetCounterAction(VisionPlugin owner) : IActionPlugin
{
    public string Id => VisionInfo.PluginId + ".reset-counter";

    public string DisplayName => VisionInfo.ResetCounterActionName;

    public string? IconKey => VisionInfo.IconKey;

    public string Group => VisionInfo.DisplayName;

    public string? Description => "Sets a counter of a vision pipeline back to 0.";

    public bool NeedsDevice => false;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.DynamicChoice(VisionInfo.PipelineActionField, "Pipeline"),
        ActionField.DynamicChoice(VisionInfo.CounterActionField, "Counter"),
    ];

    /// <summary>The value of a counter choice.</summary>
    public static string CounterValue(string pipelineId, string nodeId) => pipelineId + "/" + nodeId;

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var errors = new Dictionary<string, string>();
        var pipeline = owner.FindPipeline(VisionActionSettings.Get(settings, VisionInfo.PipelineActionField));
        if (pipeline is null)
        {
            errors[VisionInfo.PipelineActionField] = string.IsNullOrEmpty(VisionActionSettings.Get(settings, VisionInfo.PipelineActionField))
                ? VisionInfo.ChoosePipeline
                : PipelineRules.Gone;
        }

        if (CounterOf(pipeline, VisionActionSettings.Get(settings, VisionInfo.CounterActionField)) is null)
        {
            errors[VisionInfo.CounterActionField] = VisionInfo.ChooseCounter;
        }

        return errors;
    }

    public string Describe(JsonElement settings)
    {
        var pipeline = owner.FindPipeline(VisionActionSettings.Get(settings, VisionInfo.PipelineActionField));
        return CounterOf(pipeline, VisionActionSettings.Get(settings, VisionInfo.CounterActionField)) is { } node
            ? $"Reset {node.Name} of {pipeline!.Name}"
            : DisplayName;
    }

    public Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IActionQueryContext ctx, string fieldKey, CancellationToken ct)
    {
        var pipelines = owner.AllPipelines.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        IReadOnlyList<ActionChoice> choices = fieldKey switch
        {
            VisionInfo.PipelineActionField => [.. pipelines.Where(p => p.Nodes.Any(n => n.Kind == NodeKinds.Count)).Select(p => new ActionChoice(p.Id, p.Name))],
            VisionInfo.CounterActionField => [.. pipelines.SelectMany(p => p.Nodes.Where(n => n.Kind == NodeKinds.Count)
                .Select(n => new ActionChoice(CounterValue(p.Id, n.Id), $"{p.Name}: {n.Name}")))],
            _ => [],
        };
        return Task.FromResult(choices);
    }

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var pipeline = owner.FindPipeline(invocation.Get(VisionInfo.PipelineActionField)) ?? throw new InvalidOperationException(PipelineRules.Gone);
        var node = CounterOf(pipeline, invocation.Get(VisionInfo.CounterActionField)) ?? throw new InvalidOperationException(VisionInfo.ChooseCounter);
        await owner.ResetCounterAsync(pipeline.Id, node.Id).ConfigureAwait(false);
        ctx.Log($"{node.Name} of {pipeline.Name} is 0 again.");
    }

    /// <summary>The Count node a counter value names, only of <paramref name="pipeline"/>; null otherwise.</summary>
    private static PipelineNode? CounterOf(Pipeline? pipeline, string? value)
    {
        if (pipeline is null || string.IsNullOrEmpty(value))
        {
            return null;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || value[..slash] != pipeline.Id)
        {
            return null;
        }

        var nodeId = value[(slash + 1)..];
        return pipeline.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Kind == NodeKinds.Count);
    }
}

internal static class VisionActionSettings
{
    public static string? Get(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
