using System.Text.RegularExpressions;

namespace Oadm.Notices;

/// <summary>SPDX license expressions of NuGet packages ("MIT", "(MIT OR Apache-2.0)", "LGPL-2.1-or-later").</summary>
public static partial class LicenseExpressions
{
    /// <summary>The license ids of an expression, in order, without operators, brackets and exceptions.</summary>
    public static IReadOnlyList<string> Identifiers(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [];
        }

        var ids = new List<string>();
        var skipNext = false;
        foreach (Match m in TokenRegex().Matches(expression))
        {
            var token = m.Value;
            if (skipNext)
            {
                skipNext = false; // the exception id after WITH
                continue;
            }

            switch (token.ToUpperInvariant())
            {
                case "AND":
                case "OR":
                    continue;
                case "WITH":
                    skipNext = true;
                    continue;
            }

            var id = token.TrimEnd('+');
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>The bundled template for an SPDX id: LGPL-2.1-or-later -> LGPL-2.1, OFL-1.1-RFN -> OFL-1.1. Null when none.</summary>
    public static string? TemplateId(string spdxId)
    {
        ArgumentNullException.ThrowIfNull(spdxId);
        return spdxId.ToUpperInvariant() switch
        {
            "MIT" => "MIT",
            "APACHE-2.0" => "Apache-2.0",
            "BSD-2-CLAUSE" => "BSD-2-Clause",
            "BSD-3-CLAUSE" => "BSD-3-Clause",
            "OFL-1.1" or "OFL-1.1-RFN" or "OFL-1.1-NO-RFN" => "OFL-1.1",
            "LGPL-2.1" or "LGPL-2.1-ONLY" or "LGPL-2.1-OR-LATER" => "LGPL-2.1",
            _ => null,
        };
    }

    [GeneratedRegex(@"[A-Za-z0-9.\-+:]+")]
    private static partial Regex TokenRegex();
}

/// <summary>Full standard license texts, one <c>&lt;id&gt;.txt</c> per license (packaging/notices/licenses).</summary>
public sealed class LicenseTemplates
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    public LicenseTemplates(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        foreach (var file in Directory.GetFiles(directory, "*.txt"))
        {
            _texts[Path.GetFileNameWithoutExtension(file)] = PackageReader.Normalize(File.ReadAllText(file));
        }
    }

    public LicenseTemplates(IReadOnlyDictionary<string, string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var (id, text) in texts)
        {
            _texts[id] = PackageReader.Normalize(text);
        }
    }

    public IReadOnlyCollection<string> Ids => _texts.Keys;

    public string? Get(string templateId) => _texts.GetValueOrDefault(templateId);
}
