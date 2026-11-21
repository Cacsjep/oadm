using Oadm.Plugins.DeviceConfig;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Configuration (spec node 7): a configuration file (.cfg) of Create configuration file or ADM, applied with Device
/// configuration's task "Configure devices" (<see cref="ConfigureIds.Configure"/>) on any AXIS OS with the parameter API:
/// the payload the Configure devices dialog builds for every setting of the chosen areas (<see cref="AdmConfigApply.ForEveryDevice"/>:
/// never an address, a per-device name, an identity or connection parameter); the task checks every value against the
/// camera's own parameters and skips the ones it does not have (Done with warnings).
/// </summary>
public sealed class ConfigurationNode : TaskNode<ConfigurationSettings>
{
    public const string WrongFile = "Choose a configuration file (.cfg).";

    public const string TooLarge = "The configuration file is larger than 1 MB.";

    public override NodeKind Kind => NodeKind.Configuration;

    public override string Name => "Configuration";

    public override string StepName => "Apply the configuration";

    public override string TaskPluginId => ConfigureIds.Configure;

    public override object DefaultSettings => new ConfigurationSettings();

    /// <summary>A kept file's name that a Configuration node takes.</summary>
    public static bool IsConfigFile(string name) =>
        name?.EndsWith("." + AdmConfigFile.Extension, StringComparison.OrdinalIgnoreCase) == true;

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

        if (context.Files?.GetValueOrDefault(id) is { } file)
        {
            if (!IsConfigFile(file.Name))
            {
                return WrongFile;
            }

            if (file.Size > AdmConfigFile.MaxFileBytes)
            {
                return TooLarge;
            }
        }

        return Areas(settings) is { Parameters: false, ActionRules: false, StreamProfiles: false } ? "Choose what to apply." : null;
    }

    protected override async Task<string> PayloadAsync(ConfigurationSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var id = settings.FileId!.Value;
        var name = run.Host.Vault.Files.GetValueOrDefault(id)?.Name;
        if (name is not null && !IsConfigFile(name))
        {
            throw new InvalidOperationException(WrongFile);
        }

        string text;
        try
        {
            text = await run.Host.Vault.ReadTextAsync(id, AdmConfigFile.MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(TooLarge, ex);
        }

        AdmConfig file;
        try
        {
            file = AdmConfigFile.Read(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var plan = AdmConfigApply.ForEveryDevice(file, Areas(settings), name);
        return plan.IsValid ? plan.Payload.ToJson() : throw new InvalidOperationException(plan.Errors[0].Message);
    }

    protected override string Payload(ConfigurationSettings settings, NodeRun run) =>
        throw new NotSupportedException("The payload needs the kept file.");

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

        if (settings.StreamProfiles)
        {
            areas.Add("stream profiles");
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

    /// <summary>The areas of the file the node applies.</summary>
    public static AdmConfigAreas Areas(ConfigurationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AdmConfigAreas(settings.Parameters, settings.ActionRules, settings.StreamProfiles);
    }
}
