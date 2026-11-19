using System.Text.RegularExpressions;

using Oadm.Plugins.Provisioning.Nodes;

namespace Oadm.Plugins.Provisioning.Model;

/// <summary>The result of <see cref="ProfileCheck.Check"/>: errors of the whole profile, one error and one warning per node (by node id).</summary>
public sealed record ProfileCheckResult
{
    public IReadOnlyList<string> ProfileErrors { get; init; } = [];

    public IReadOnlyDictionary<string, string> NodeErrors { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> NodeWarnings { get; init; } = new Dictionary<string, string>();

    /// <summary>The profile can be active and run.</summary>
    public bool IsValid => ProfileErrors.Count == 0 && NodeErrors.Count == 0;
}

/// <summary>
/// The check of a profile (like Vision Center's <c>VisionGraph.Check</c>): the same in the editor, on save and before a
/// profile is active. One error per node: first its place (Set password first, once), then its settings. Deterministic and
/// cheap.
/// </summary>
public static partial class ProfileCheck
{
    public const int MaxNameLength = 64;

    public const int MaxNodes = 40;

    public const int MaxProfiles = 50;

    public const string FixFirst = "Fix the profile first.";

    public const string StartsWithSetPassword = "A profile starts with Set password.";

    [GeneratedRegex("^[A-Za-z0-9-]{1,32}$")]
    private static partial Regex NodeId();

    /// <summary>Checks <paramref name="profile"/>; <paramref name="others"/> are the other stored profiles (for the name).</summary>
    public static ProfileCheckResult Check(Profile profile, NodeRegistry registry, ProfileCheckContext context, IEnumerable<Profile>? others = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        var profileErrors = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new Dictionary<string, string>(StringComparer.Ordinal);

        var name = profile.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            profileErrors.Add("Enter a name.");
        }
        else if (name.Length > MaxNameLength)
        {
            profileErrors.Add($"The name has at most {MaxNameLength} characters.");
        }
        else if (others?.Any(o => o.Id != profile.Id && string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            profileErrors.Add($"A profile named {name} exists already.");
        }

        var nodes = profile.Nodes ?? [];
        if (nodes.Count > MaxNodes)
        {
            profileErrors.Add($"A profile has at most {MaxNodes} nodes.");
            return new ProfileCheckResult { ProfileErrors = profileErrors };
        }

        if (nodes.Count < 1 || nodes[0].Kind != NodeKind.SetPassword)
        {
            profileErrors.Add(StartsWithSetPassword);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var id = node.Id ?? string.Empty;
            if (!NodeId().IsMatch(id) || !ids.Add(id))
            {
                profileErrors.Add($"Node {i + 1} needs an id of its own.");
                continue;
            }

            if (Place(node, i, registry) is { } placeError)
            {
                errors[id] = placeError;
                continue;
            }

            var kind = registry.Find(node.Kind)!;
            if (kind.Check(node.Settings, context) is { } error)
            {
                errors[id] = error;
            }
            else if (kind.Warning(node.Settings, context) is { } warning)
            {
                warnings[id] = warning;
            }
        }

        return new ProfileCheckResult { ProfileErrors = profileErrors, NodeErrors = errors, NodeWarnings = warnings };
    }

    /// <summary>The first error of the profile, for a refusal ("Fix the profile first: ..."); null when it is valid.</summary>
    public static string? FirstError(ProfileCheckResult result, Profile profile)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(profile);
        if (result.ProfileErrors.Count > 0)
        {
            return result.ProfileErrors[0];
        }

        foreach (var node in profile.Nodes)
        {
            if (result.NodeErrors.TryGetValue(node.Id, out var error))
            {
                return $"{NodeRegistry.Default.Find(node.Kind)?.Name ?? "A node"}: {error}";
            }
        }

        return null;
    }

    private static string? Place(FlowNode node, int index, NodeRegistry registry)
    {
        if (registry.Find(node.Kind) is not { } kind)
        {
            return "This OADM version does not know this node.";
        }

        return node.Kind switch
        {
            NodeKind.SetPassword when index != 0 => "Set password is always the first node.",
            _ when !kind.IsFixed && index < 1 => "Set password comes first.",
            _ => null,
        };
    }
}
