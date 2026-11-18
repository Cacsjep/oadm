using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>The Upgrade AXIS OS task (<c>oadm.firmware</c>) as the two upgrade nodes use it.</summary>
public static partial class FirmwareTask
{
    public const string PluginId = "oadm.firmware";

    public const string NoFileForModel = "No file for this model.";

    [GeneratedRegex(@"^Already up to date: the device runs (?<version>[0-9.]+)\.")]
    private static partial Regex AlreadyUpToDate();

    /// <summary>
    /// The node's result of an ended upgrade: the version the camera runs already is Success "Already 12.6.95.", no file for
    /// the model a Warning, any other warning a Warning and a failure an Error with the task's text.
    /// </summary>
    public static NodeResult Map(TaskOutcome outcome, string done)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var message = NodeRules.MessageOf(outcome, "The upgrade failed.");
        return outcome.State switch
        {
            TaskEndState.Done => NodeResult.Success(done),
            TaskEndState.DoneWithWarnings when AlreadyUpToDate().Match(message) is { Success: true } match =>
                NodeResult.Success($"Already {match.Groups["version"].Value}."),
            TaskEndState.DoneWithWarnings when message.StartsWith(NoFileForModel, StringComparison.Ordinal) => NodeResult.Warning(NoFileForModel),
            TaskEndState.DoneWithWarnings => NodeResult.Warning(message),
            _ => NodeResult.Error(message),
        };
    }
}

/// <summary>
/// Upgrade AXIS OS online (spec node 3): the newest AXIS OS of the camera's own track, the latest LTS or the latest active
/// track from axis.com, chosen by the Upgrade AXIS OS task when it runs (<c>"latest"</c>), downloaded with the MyAxis sign-in
/// of the administrator who started provisioning (the task runs as that user). Optional: only for cameras older than a
/// version. The task's checks apply (APIs, the decision table, the installed applications).
/// </summary>
public sealed class UpgradeOnlineNode : TaskNode<UpgradeOnlineSettings>
{
    public const string SignIn = "Sign in to MyAxis.";

    public override NodeKind Kind => NodeKind.UpgradeOnline;

    public override string Name => "Upgrade AXIS OS online";

    public override string TaskPluginId => FirmwareTask.PluginId;

    public override object DefaultSettings => new UpgradeOnlineSettings();

    protected override string? CheckSettings(UpgradeOnlineSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Target))
        {
            return "Choose which AXIS OS to install.";
        }

        return string.IsNullOrWhiteSpace(settings.OnlyIfOlderThan) || NodeRules.IsVersion(settings.OnlyIfOlderThan)
            ? null
            : "Enter a version like 12.6.95.";
    }

    /// <summary>The camera runs the given version or a newer one already: Success without a task.</summary>
    protected override async Task<NodeResult?> BeforeAsync(UpgradeOnlineSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(settings.OnlyIfOlderThan) || run.Camera.DeviceId is not { } id)
        {
            return null;
        }

        var current = (await run.Host.Devices.FindAsync(id, ct).ConfigureAwait(false))?.FirmwareVersion;
        return NodeRules.CompareVersions(current, settings.OnlyIfOlderThan) is >= 0 ? NodeResult.Success($"Already {current}.") : null;
    }

    /// <summary>The user who started provisioning: their MyAxis sign-in downloads the file.</summary>
    protected override ITaskRunner? RunnerFor(NodeRun run) => run?.Host.StarterTasks;

    protected override string Payload(UpgradeOnlineSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = settings.Target switch
        {
            OsTarget.LatestLts => "latestLts",
            OsTarget.LatestActive => "latestActive",
            _ => "ownTrack",
        };
        return new JsonObject { ["latest"] = new JsonObject { ["target"] = target } }.ToJsonString();
    }

    protected override string Done(UpgradeOnlineSettings settings, NodeRun run) => "AXIS OS from axis.com installed.";

    protected override NodeResult Map(TaskOutcome outcome, UpgradeOnlineSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.State == TaskEndState.Failed && outcome.Message is { } message
            && (message.StartsWith("Sign in to MyAxis", StringComparison.Ordinal) || message.Contains("MyAxis sign-in has expired", StringComparison.Ordinal)))
        {
            return NodeResult.Error(SignIn);
        }

        return FirmwareTask.Map(outcome, Done(settings, run));
    }
}

/// <summary>
/// Upgrade AXIS OS from files (spec node 4): the AXIS OS files of the vault, each camera the file for its model (the Upgrade
/// AXIS OS task matches the product in the file name); no file for the model is a Warning. An older version only with Allow
/// downgrade (and AXIS OS refuses a downgrade that keeps the settings: then the task's Error).
/// </summary>
public sealed partial class UpgradeFilesNode : TaskNode<UpgradeFilesSettings>
{
    /// <summary>Most files of one node (one per model).</summary>
    public const int MaxFiles = 32;

    public override NodeKind Kind => NodeKind.UpgradeFiles;

    public override string Name => "Upgrade AXIS OS from files";

    public override string TaskPluginId => FirmwareTask.PluginId;

    public override object DefaultSettings => new UpgradeFilesSettings([]);

    /// <summary>"P3265-LVE_12_6_95.bin": product and version as Axis names its downloads.</summary>
    [GeneratedRegex(@"^(?<product>[A-Za-z0-9][A-Za-z0-9\-]*)_(?<version>[0-9]+(_[0-9]+){1,3})(_[A-Za-z0-9]+)?\.bin$", RegexOptions.IgnoreCase)]
    private static partial Regex OsFileName();

    protected override string? CheckSettings(UpgradeFilesSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        var files = settings.Files ?? [];
        if (files.Count == 0)
        {
            return "Choose the AXIS OS files.";
        }

        if (files.Count > MaxFiles)
        {
            return $"Choose at most {MaxFiles} files.";
        }

        var products = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file?.FileId is not { } id)
            {
                return "Choose the AXIS OS files.";
            }

            var name = context.Files?.GetValueOrDefault(id)?.Name ?? file.Name;
            if (context.Files is { } kept && !kept.ContainsKey(id))
            {
                return $"{file.Name ?? "A file"} is no longer on the server. Choose it again.";
            }

            if (Product(name) is not { } product)
            {
                return $"{name} is not an AXIS OS file (e.g. P3265-LVE_12_6_95.bin).";
            }

            if (!products.Add(product))
            {
                return $"Two files are for {product}.";
            }
        }

        return null;
    }

    /// <summary>The files as uploads the task reads (each added again when the server deleted the last one).</summary>
    protected override async Task<string> PayloadAsync(UpgradeFilesSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var files = new JsonArray();
        foreach (var file in settings.Files ?? [])
        {
            var id = file.FileId!.Value;
            var name = run.Host.Vault.Files.GetValueOrDefault(id)?.Name ?? file.Name ?? string.Empty;
            files.Add(new JsonObject { ["fileId"] = await run.Host.Vault.UploadIdAsync(id, ct).ConfigureAwait(false), ["fileName"] = name });
        }

        return new JsonObject { ["files"] = files, ["allowDowngrade"] = settings.AllowDowngrade }.ToJsonString();
    }

    protected override string Payload(UpgradeFilesSettings settings, NodeRun run) =>
        throw new NotSupportedException("The payload needs the files as uploads.");

    protected override string Done(UpgradeFilesSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var model = NodeRules.ModelPart(run.Camera.Model);
        var version = (settings.Files ?? [])
            .Select(f => run.Host.Vault.Files.GetValueOrDefault(f.FileId ?? Guid.Empty)?.Name ?? f.Name)
            .Select(n => OsFileName().Match(n ?? string.Empty))
            .FirstOrDefault(m => m.Success && string.Equals(m.Groups["product"].Value, model, StringComparison.OrdinalIgnoreCase))?
            .Groups["version"].Value.Replace('_', '.');
        return version is { Length: > 0 } ? $"AXIS OS {version} installed." : "AXIS OS installed.";
    }

    protected override NodeResult Map(TaskOutcome outcome, UpgradeFilesSettings settings, NodeRun run) =>
        FirmwareTask.Map(outcome, Done(settings, run));

    /// <summary>The product of an AXIS OS file name, or null.</summary>
    public static string? Product(string? fileName) =>
        OsFileName().Match(fileName ?? string.Empty) is { Success: true } match ? match.Groups["product"].Value : null;
}
