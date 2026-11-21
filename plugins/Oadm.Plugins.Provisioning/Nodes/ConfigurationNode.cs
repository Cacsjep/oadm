using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Configuration (spec node 7): a configuration file of Device configuration (Export configuration), applied with
/// Device configuration's task "Import configuration" (<c>oadm.device-config.import</c>): only the chosen areas,
/// device-specific values (address, host name, certificates, passwords) never, settings the model lacks a Warning listing them (the task's own checks and texts).
/// </summary>
public sealed class ConfigurationNode : TaskNode<ConfigurationSettings>
{
    public const string ImportTaskId = "oadm.device-config.import";

    public override NodeKind Kind => NodeKind.Configuration;

    public override string Name => "Configuration";

    public override string StepName => "Apply the configuration";

    public override string TaskPluginId => ImportTaskId;

    public override object DefaultSettings => new ConfigurationSettings();

    protected override string? CheckSettings(ConfigurationSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        if (settings.FileId is not { } id)
        {
            return "Choose a configuration file.";
        }

        if (context.Files is { } kept && !kept.ContainsKey(id))
        {
            return "The configuration file is no longer on the server. Choose it again.";
        }

        if (context.Files?.GetValueOrDefault(id) is { } file && !file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "Choose a configuration file of Export configuration (.json).";
        }

        return Sections(settings).Count == 0 ? "Choose what to apply." : null;
    }

    protected override async Task<string> PayloadAsync(ConfigurationSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var id = settings.FileId!.Value;
        var fileName = run.Host.Vault.Files.GetValueOrDefault(id)?.Name ?? "configuration.json";
        var fileId = await run.Host.Vault.UploadIdAsync(id, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["fileId"] = fileId,
            ["fileName"] = fileName,
            ["isZip"] = false,
            ["sections"] = new JsonArray([.. Sections(settings).Select(s => JsonValue.Create(s))]),
        }.ToJsonString();
    }

    protected override string Payload(ConfigurationSettings settings, NodeRun run) =>
        throw new NotSupportedException("The payload needs the file as an upload.");

    protected override string Done(ConfigurationSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var areas = new List<string>();
        if (settings.Parameters)
        {
            areas.Add("parameters");
        }

        if (settings.ActionRules)
        {
            areas.Add("action rules");
        }

        if (settings.Schedules)
        {
            areas.Add("schedules");
        }

        var applied = NodeRules.List(areas);
        return char.ToUpperInvariant(applied[0]) + applied[1..] + " applied.";
    }

    protected override NodeResult Map(TaskOutcome outcome, ConfigurationSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.State == TaskEndState.DoneWithWarnings
            ? NodeResult.Warning(NodeRules.Sentence(outcome.Message, Done(settings, run)))
            : base.Map(outcome, settings, run);
    }

    /// <summary>The configuration areas of the import ("param.v2") in the order the task applies them.</summary>
    public static IReadOnlyList<string> Sections(ConfigurationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sections = new List<string>();
        if (settings.Parameters)
        {
            sections.Add("param.v2");
        }

        if (settings.ActionRules)
        {
            sections.Add("action-rules.v2");
        }

        if (settings.Schedules)
        {
            sections.Add("event-schedules.v2");
        }

        return sections;
    }
}
