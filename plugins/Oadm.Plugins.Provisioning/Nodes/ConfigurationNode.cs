using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Configuration (spec node 7): a configuration file of Device configuration, or a template camera whose configuration is
/// exported when the node runs (read only), applied with Device configuration's task "Import configuration"
/// (<c>oadm.device-config.import</c>): only the chosen areas, device-specific values (address, host name, certificates,
/// passwords) never, settings the model lacks a Warning listing them (the task's own checks and texts).
/// </summary>
public sealed class ConfigurationNode : TaskNode<ConfigurationSettings>
{
    public const string ImportTaskId = "oadm.device-config.import";

    /// <summary>The device configuration API's export (AXIS OS 12.5 and later).</summary>
    public const string ExportPath = "/config/rest/$export";

    /// <summary>Largest export OADM reads from a template camera (Device configuration reads files up to 2 MB).</summary>
    public const int MaxExportBytes = 2 * 1024 * 1024;

    public override NodeKind Kind => NodeKind.Configuration;

    public override string Name => "Configuration";

    public override string TaskPluginId => ImportTaskId;

    public override object DefaultSettings => new ConfigurationSettings();

    protected override string? CheckSettings(ConfigurationSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        switch (settings.Source)
        {
            case ConfigSource.File when settings.FileId is not { } id:
                return "Choose a configuration file.";
            case ConfigSource.File when context.Files is { } kept && !kept.ContainsKey(settings.FileId.Value):
                return "The configuration file is no longer on the server. Choose it again.";
            case ConfigSource.File when context.Files?.GetValueOrDefault(settings.FileId.Value) is { } file
                && !file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase):
                return "Choose a configuration file of Export configuration (.json).";
            case ConfigSource.TemplateCamera when string.IsNullOrWhiteSpace(settings.TemplateSerial):
                return "Choose the template camera.";
            case ConfigSource.File or ConfigSource.TemplateCamera:
                break;
            default:
                return "Choose a configuration file or a template camera.";
        }

        return Sections(settings).Count == 0 ? "Choose what to apply." : null;
    }

    /// <summary>The template camera itself is left as it is.</summary>
    protected override Task<NodeResult?> BeforeAsync(ConfigurationSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        return Task.FromResult(settings.Source == ConfigSource.TemplateCamera
            && string.Equals(settings.TemplateSerial?.Trim(), run.Camera.Serial, StringComparison.OrdinalIgnoreCase)
            ? NodeResult.NotNeeded("this is the template camera.")
            : null);
    }

    protected override async Task<string> PayloadAsync(ConfigurationSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        string fileId;
        string fileName;
        if (settings.Source == ConfigSource.File)
        {
            var id = settings.FileId!.Value;
            fileName = run.Host.Vault.Files.GetValueOrDefault(id)?.Name ?? "configuration.json";
            fileId = await run.Host.Vault.UploadIdAsync(id, ct).ConfigureAwait(false);
        }
        else
        {
            var (serial, text) = await ExportTemplateAsync(settings.TemplateSerial!.Trim(), run, ct).ConfigureAwait(false);
            fileName = $"template-{serial}.json";
            fileId = await run.Host.Vault.UploadAsync(fileName, Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        }

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
        return char.ToUpperInvariant(applied[0]) + applied[1..] + (settings.Source == ConfigSource.TemplateCamera ? " of the template camera applied." : " applied.");
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

    /// <summary>The template camera's configuration (read only). Throws <see cref="InvalidOperationException"/> with the reason.</summary>
    private static async Task<(string Serial, string Text)> ExportTemplateAsync(string serial, NodeRun run, CancellationToken ct)
    {
        IDeviceInfo? template = null;
        foreach (var device in await run.Host.Devices.ListAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(device.Serial, serial, StringComparison.OrdinalIgnoreCase))
            {
                template = device;
                break;
            }
        }

        if (template is null)
        {
            throw new InvalidOperationException($"The template camera {serial} is not in OADM.");
        }

        if (template.Status != DeviceStatus.Ok)
        {
            throw new InvalidOperationException($"The template camera {serial} cannot be read now: {DeviceMessages.StatusText(template).ToLowerInvariant()}.");
        }

        try
        {
            var vapix = await run.Host.Vapix.CreateAsync(template.Id, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, ExportPath);
            using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"The template camera {serial} has no configuration export (AXIS OS 12.5 or later).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The template camera {serial} refused the export (HTTP {(int)response.StatusCode}).");
            }

            if (response.Content.Headers.ContentLength is > MaxExportBytes)
            {
                throw new InvalidOperationException($"The configuration of the template camera {serial} is larger than 2 MB.");
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > MaxExportBytes
                ? throw new InvalidOperationException($"The configuration of the template camera {serial} is larger than 2 MB.")
                : (template.Serial, text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException($"The template camera {serial} could not be read: {ex.Message.TrimEnd('.')}.", ex);
        }
    }
}
