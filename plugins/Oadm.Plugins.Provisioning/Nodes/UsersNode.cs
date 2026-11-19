using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Users (spec node 8): more accounts on the camera, one Users task (<c>oadm.users</c>, mode Add) per account with its
/// checks: OADM's own account is never removed, demoted or given another password (the task refuses it). The password is
/// typed once and kept in the vault (encrypted with the master key); it reaches only the task's payload in memory, never a
/// profile, a log or the page. Several Users nodes are allowed.
/// </summary>
public sealed partial class UsersNode : DeviceNode<UsersSettings>
{
    public const string UsersTaskId = "oadm.users";

    /// <summary>Most accounts of one node.</summary>
    public const int MaxAccounts = 10;

    public override NodeKind Kind => NodeKind.Users;

    public override string Name => "Users";

    public override string StepName => "Add the users";

    public override object DefaultSettings => new UsersSettings([]);

    /// <summary>The camera's user names: 1 to 14 letters and digits.</summary>
    [GeneratedRegex("^[A-Za-z0-9]{1,14}$")]
    private static partial Regex UserName();

    protected override string? CheckSettings(UsersSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        var accounts = settings.Accounts ?? [];
        if (accounts.Count == 0)
        {
            return "Add an account.";
        }

        if (accounts.Count > MaxAccounts)
        {
            return $"Add at most {MaxAccounts} accounts.";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            var name = account?.Name?.Trim() ?? string.Empty;
            if (!UserName().IsMatch(name))
            {
                return name.Length == 0 ? "Enter a name for every account." : $"{name}: use 1 to 14 letters and digits.";
            }

            if (string.Equals(name, "root", StringComparison.OrdinalIgnoreCase))
            {
                return "root is the account of Set password.";
            }

            if (!names.Add(name))
            {
                return $"The account {name} is there twice.";
            }

            if (!Enum.IsDefined(account!.Role))
            {
                return $"Choose the role of {name}.";
            }

            if (account.SecretId is not { } secret)
            {
                return $"Type the password of {name}.";
            }

            if (context.Secrets is { } kept && !kept.Contains(secret))
            {
                return $"The password of {name} is no longer on the server. Type it again.";
            }
        }

        return null;
    }

    protected override async Task<NodeResult> RunOnAsync(UsersSettings settings, NodeRun run, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var added = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var account in settings.Accounts ?? [])
        {
            var name = account.Name!.Trim();
            if (run.Host.Vault.ReadSecret(account.SecretId!.Value) is not { } password)
            {
                errors.Add($"{name}: {NodeVault.SecretGone}");
                continue;
            }

            var outcome = await TaskRuns.RunTaskAsync(run.Host, UsersTaskId, deviceId, Payload(name, account, password), ct).ConfigureAwait(false);
            switch (outcome?.State)
            {
                case TaskEndState.Done:
                    added.Add(name);
                    break;
                case TaskEndState.DoneWithWarnings:
                    warnings.Add($"{name}: {NodeRules.Sentence(outcome.Message, "done with warnings")}");
                    break;
                case null:
                    errors.Add($"{name}: {NodeTexts.TaskLost(Name)}");
                    break;
                default:
                    errors.Add($"{name}: {NodeRules.Sentence(outcome.Message, "the task failed")}");
                    break;
            }
        }

        var done = added.Count == 0 ? string.Empty : $"Added {NodeRules.List(added)}.";
        var text = string.Join(" ", new[] { done }.Concat(warnings).Concat(errors).Where(t => t.Length > 0));
        return errors.Count > 0 ? NodeResult.Error(text) : warnings.Count > 0 ? NodeResult.Warning(text) : NodeResult.Success(text);
    }

    /// <summary>The Users task's payload for one account: mode Add, the role, PTZ and the password (in memory only).</summary>
    internal static string Payload(string name, UserAccount account, string password) => new JsonObject
    {
        ["mode"] = "add",
        ["userName"] = name,
        ["password"] = password,
        ["role"] = account.Role switch
        {
            CameraRole.Administrator => "administrator",
            CameraRole.Operator => "operator",
            _ => "viewer",
        },
        ["ptz"] = account.Ptz,
    }.ToJsonString();
}
