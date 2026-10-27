using System.Text.RegularExpressions;

namespace Oadm.Client.Updates;

/// <summary>One group of the release notes ("Added", "Fixed") and its entries.</summary>
public sealed record ReleaseNoteSection(string Title, IReadOnlyList<string> Items)
{
    /// <summary>The heading is green (Added).</summary>
    public bool IsOk => Title.Equals("Added", StringComparison.OrdinalIgnoreCase);

    /// <summary>The heading is amber (Fixed, Security); other groups use the accent.</summary>
    public bool IsWarning => Title.Equals("Fixed", StringComparison.OrdinalIgnoreCase) || Title.Equals("Security", StringComparison.OrdinalIgnoreCase);

    public string Heading => Title.ToUpperInvariant();
}

/// <summary>
/// The notes of a release page (the CHANGELOG section the release workflow copies there): <c>### Added</c> headings with
/// <c>- </c> entries (nested ones too, as entries of their own), an entry continuing on indented lines. Markdown emphasis, code
/// and links become plain text.
/// </summary>
public static partial class ReleaseNotes
{
    public static IReadOnlyList<ReleaseNoteSection> Parse(string? markdown)
    {
        var sections = new List<ReleaseNoteSection>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return sections;
        }

        string? title = null;
        var items = new List<string>();
        void Flush()
        {
            if (title is not null && items.Count > 0)
            {
                sections.Add(new ReleaseNoteSection(title, [.. items]));
            }

            items = [];
        }

        foreach (string raw in markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                title = Plain(line[4..]);
            }
            else if (line.StartsWith('#'))
            {
                Flush();
                title = null; // another level: not a group of entries
            }
            else if (title is not null && (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)))
            {
                items.Add(Plain(line.TrimStart()[2..])); // a nested entry (older notes) is an entry of its own
            }
            else if (title is not null && items.Count > 0 && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                items[^1] += " " + Plain(line.Trim());
            }
        }

        Flush();
        return sections;
    }

    private static string Plain(string text)
    {
        text = Link().Replace(text, "$1");
        return text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
