using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// SD card (spec node 11): with Edge storage's task (<c>oadm.edge-storage.task</c>, its checks) format the card, or turn on
/// encryption with the typed passphrase (which formats it), then how many days recordings stay (the cleanup's maximum
/// age). The format uses the camera's own file system. No card: Not needed. The passphrase is typed once and kept in the
/// vault; it reaches only the task's payload in memory.
/// </summary>
public sealed class SdCardNode : DeviceNode<SdCardSettings>
{
    public const string StorageTaskId = "oadm.edge-storage.task";

    /// <summary>The task's text when the camera has no card ("No SD card. Nothing was changed.").</summary>
    public const string NoCard = "No SD card";

    /// <summary>Most days the cleanup keeps recordings (the camera's limit).</summary>
    public const int MaxDays = 7000;

    /// <summary>Longest passphrase the camera takes, in UTF-8 bytes.</summary>
    public const int MaxPassphraseBytes = 512;

    public override NodeKind Kind => NodeKind.SdCard;

    public override string Name => "SD card";

    public override object DefaultSettings => new SdCardSettings();

    protected override string? CheckSettings(SdCardSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        if (!settings.Format && !settings.Encrypt && settings.MaxAgeDays is null)
        {
            return "Choose what to do with the SD card.";
        }

        if (settings.Encrypt)
        {
            if (settings.SecretId is not { } secret)
            {
                return "Type the passphrase.";
            }

            if (context.Secrets is { } kept && !kept.Contains(secret))
            {
                return "The passphrase is no longer on the server. Type it again.";
            }
        }

        return settings.MaxAgeDays is < 1 or > MaxDays ? $"Keep recordings 1 to {MaxDays} days." : null;
    }

    protected override async Task<NodeResult> RunOnAsync(SdCardSettings settings, NodeRun run, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var steps = new List<(string Payload, string Done)>();
        if (settings.Encrypt)
        {
            var passphrase = run.Host.Vault.ReadSecret(settings.SecretId!.Value);
            if (passphrase is null)
            {
                return NodeResult.Error(NodeVault.SecretGone);
            }

            if (Encoding.UTF8.GetByteCount(passphrase) > MaxPassphraseBytes)
            {
                return NodeResult.Error("The passphrase is too long.");
            }

            steps.Add((new JsonObject { ["action"] = "encryptionOn", ["passphrase"] = passphrase }.ToJsonString(), "encrypted and formatted"));
        }
        else if (settings.Format)
        {
            steps.Add((new JsonObject { ["action"] = "format" }.ToJsonString(), "formatted"));
        }

        if (settings.MaxAgeDays is { } days)
        {
            steps.Add((new JsonObject { ["action"] = "retention", ["retentionDays"] = days }.ToJsonString(),
                string.Create(CultureInfo.InvariantCulture, $"recordings kept {days} {(days == 1 ? "day" : "days")}")));
        }

        var done = new List<string>();
        foreach (var (payload, text) in steps)
        {
            var outcome = await TaskRuns.RunTaskAsync(run.Host, StorageTaskId, deviceId, payload, ct).ConfigureAwait(false);
            var message = NodeRules.MessageOf(outcome, NodeTexts.TaskLost(Name));
            switch (outcome?.State)
            {
                case TaskEndState.Done:
                    done.Add(text);
                    break;
                case TaskEndState.DoneWithWarnings:
                    return NodeResult.Warning(Summary(done, NodeRules.Sentence(message, text)));
                case not null when message.StartsWith(NoCard, StringComparison.Ordinal) && done.Count == 0:
                    return NodeResult.NotNeeded("no SD card.");
                default:
                    return NodeResult.Error(Summary(done, NodeRules.Sentence(message, "The task failed")));
            }
        }

        return NodeResult.Success(Summary(done, null));
    }

    /// <summary>"SD card formatted, recordings kept 30 days." plus the problem.</summary>
    private static string Summary(List<string> done, string? problem)
    {
        var text = done.Count == 0 ? string.Empty : "SD card " + string.Join(", ", done) + ".";
        return problem is null ? text : (text + " " + problem).Trim();
    }
}
