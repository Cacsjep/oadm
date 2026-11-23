using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>Settings of Restart: none.</summary>
public sealed record RestartSettings;

/// <summary>
/// Restart (Schedules only, docs/spec/schedules.md "Actions"): the Restart task (<c>oadm.restart</c>) restarts the device and
/// waits until it answers again. Not in <see cref="NodeRegistry"/>: a profile never offers it, and a profile that names it has
/// the error of a node this version does not know (it cannot be active). Schedules compiles this source into its own package.
/// </summary>
public sealed class RestartNode : TaskNode<RestartSettings>
{
    public const string RestartTaskId = "oadm.restart";

    public override NodeKind Kind => NodeKind.Restart;

    public override string Name => "Restart";

    public override string StepName => "Restart the device";

    public override string TaskPluginId => RestartTaskId;

    public override object DefaultSettings => new RestartSettings();

    protected override string? CheckSettings(RestartSettings settings, ProfileCheckContext context) => null;

    protected override string Payload(RestartSettings settings, NodeRun run) => "{}";

    protected override string Done(RestartSettings settings, NodeRun run) => "Restarted.";
}
