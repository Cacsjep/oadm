using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// A node that works on the camera's OADM device with more than one task or with VAPIX itself (Users, SD card, Autofocus,
/// VAPIX commands): reads and checks its settings, then
/// <see cref="RunOnAsync"/>. Like <see cref="TaskNode{TSettings}"/> it never throws for a device problem.
/// </summary>
/// <typeparam name="TSettings">The node's settings record.</typeparam>
public abstract class DeviceNode<TSettings> : IProvisioningNode
    where TSettings : class
{
    public abstract NodeKind Kind { get; }

    public abstract string Name { get; }

    public abstract string StepName { get; }

    public abstract object DefaultSettings { get; }

    /// <summary>The one error of readable settings, or null.</summary>
    protected abstract string? CheckSettings(TSettings settings, ProfileCheckContext context);

    /// <summary>Runs on the camera's device (settings checked, device known).</summary>
    protected abstract Task<NodeResult> RunOnAsync(TSettings settings, NodeRun run, Guid deviceId, CancellationToken ct);

    public string? Check(JsonElement? settings, ProfileCheckContext context) =>
        NodeSettings.Read<TSettings>(settings) is { } read ? CheckSettings(read, context) : TaskNode<TSettings>.Unreadable;

    public virtual string? Warning(JsonElement? settings, ProfileCheckContext context) => null;

    public async Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SettingsAs<TSettings>() is not { } settings)
        {
            return NodeResult.Error(TaskNode<TSettings>.Unreadable);
        }

        if (CheckSettings(settings, ProfileCheckContext.Empty) is { } error)
        {
            return NodeResult.Error(error);
        }

        return run.Camera.DeviceId is { } deviceId
            ? await RunOnAsync(settings, run, deviceId, ct).ConfigureAwait(false)
            : NodeResult.Error(NodeTexts.NotInOadm);
    }
}

/// <summary>Small rules several nodes share.</summary>
public static partial class NodeRules
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex DomainName();

    [GeneratedRegex(@"[^A-Za-z0-9\-]+")]
    private static partial Regex NotHostCharacters();

    /// <summary>A domain name ("site.example.com").</summary>
    public static bool IsDomainName(string? name) => name is { Length: <= 253 } && DomainName().IsMatch(name);

    /// <summary>The model as a host name part: "AXIS P3265-LVE" becomes "P3265-LVE".</summary>
    public static string ModelPart(string? model)
    {
        var text = (model ?? string.Empty).Trim();
        if (text.StartsWith("AXIS ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..];
        }

        return NotHostCharacters().Replace(text, "-").Trim('-');
    }

    /// <summary>"12.6.95" compared with "12.11.1" by number; null when either is not a version.</summary>
    public static int? CompareVersions(string? a, string? b)
    {
        if (Parts(a) is not { } x || Parts(b) is not { } y)
        {
            return null;
        }

        for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            var left = i < x.Count ? x[i] : 0;
            var right = i < y.Count ? y[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    /// <summary>"12.6.95": two to four numbers.</summary>
    public static bool IsVersion(string? text) => Parts(text) is { Count: >= 2 };

    private static List<int>? Parts(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }

        var list = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            list.Add(n);
        }

        return list;
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string List(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
        };
    }

    /// <summary>The task's message as the node's text: trimmed, ending with a full stop.</summary>
    public static string Sentence(string? message, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>The message of an outcome, or the fallback.</summary>
    public static string MessageOf(TaskOutcome? outcome, string fallback) =>
        string.IsNullOrWhiteSpace(outcome?.Message) ? fallback : outcome.Message.Trim();
}
