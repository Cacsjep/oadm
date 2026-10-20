using System.Globalization;

using Oadm.Contracts.SiteNode;

namespace Oadm.Sites.Binding;

/// <summary>
/// What a node's Hello may carry (docs/sites.md, Security review): the server stores, logs, audits and shows it before the
/// node is bound, so every text is checked first and a Hello that breaks a rule is refused whole.
/// </summary>
public static class HelloRules
{
    public const int MaxHostName = 253;
    public const int MaxVersion = 32;
    public const int MaxName = 64;
    public const int MaxFeatures = 32;
    public const int MaxFeature = 32;

    /// <summary>Null when <paramref name="hello"/> is acceptable, else what is wrong (for the server log).</summary>
    public static string? Problem(HelloReply? hello)
    {
        if (hello is null)
            return "no answer";
        if (hello.NodeId.Length > 0 && !IsNodeId(hello.NodeId))
            return "invalid node id";
        if (!IsText(hello.HostName, MaxHostName))
            return "invalid host name";
        if (!IsAscii(hello.Version, MaxVersion))
            return "invalid version";
        if (!IsText(hello.BoundServerName, MaxName) || !IsText(hello.SiteName, MaxName))
            return "invalid name";
        if (hello.Features.Count > MaxFeatures || hello.Features.Any(f => !IsAscii(f, MaxFeature)))
            return "invalid features";
        return null;
    }

    /// <summary>A node id: 8 to 64 hex digits (the node makes 16), or a GUID.</summary>
    public static bool IsNodeId(string? id) =>
        id is { Length: >= 8 and <= 64 } && (id.All(char.IsAsciiHexDigit) || Guid.TryParse(id, CultureInfo.InvariantCulture, out _));

    /// <summary>At most <paramref name="max"/> characters, no control or format characters, no lone surrogates.</summary>
    public static bool IsText(string? text, int max)
    {
        if (text is null)
            return true;
        if (text.Length > max)
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>At most <paramref name="max"/> printable ASCII characters.</summary>
    public static bool IsAscii(string? text, int max) =>
        text is null || (text.Length <= max && text.All(c => c is >= ' ' and <= '~'));
}
