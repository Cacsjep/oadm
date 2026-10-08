using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oadm.Notices;

/// <summary>What goes into the notices file besides the packages.</summary>
/// <param name="ProductVersion">OADM version ("1.2.0").</param>
/// <param name="Rid">Runtime id of the publish ("win-x64"), null when unknown.</param>
/// <param name="BundledNotices">Hand-written notes on bundled components (THIRD-PARTY-NOTICES.md: FFmpeg, fonts).</param>
/// <param name="AlwaysIncluded">License texts included even when no package names them (OADM itself, FFmpeg, Inter).</param>
public sealed record NoticesOptions(string ProductVersion, string? Rid, string BundledNotices, IReadOnlyList<string> AlwaysIncluded);

/// <param name="Text">The notices file.</param>
/// <param name="PackageCount">Distinct package versions listed.</param>
/// <param name="Warnings">Packages whose license could not be resolved to a text (shown by the tool, never fatal).</param>
public sealed record NoticesResult(string Text, int PackageCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds THIRD-PARTY-NOTICES.txt: the bundled components, every package with license, copyright and where it is used,
/// the full text of every license named by an expression (from the templates) and every license or notice file the
/// packages ship (deduplicated by content; files equal to a template are not repeated). Deterministic output.
/// </summary>
public static partial class NoticesWriter
{
    public const string FileName = "THIRD-PARTY-NOTICES.txt";

    public static NoticesResult Build(NoticesOptions options, IEnumerable<PackageUse> uses, Func<string, string, PackageInfo> readPackage, LicenseTemplates templates)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(uses);
        ArgumentNullException.ThrowIfNull(readPackage);
        ArgumentNullException.ThrowIfNull(templates);

        var warnings = new List<string>();
        var packages = uses
            .GroupBy(u => (Id: u.Id.ToUpperInvariant(), Version: u.Version.ToUpperInvariant()))
            .Select(g => (Use: g.First(), UsedBy: g.Select(u => u.UsedBy).Distinct(StringComparer.Ordinal).OrderBy(UsedByOrder).ThenBy(u => u, StringComparer.Ordinal).ToList()))
            .OrderBy(p => p.Use.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Use.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var templateUsers = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var always in options.AlwaysIncluded)
        {
            templateUsers.TryAdd(always, []);
        }

        var files = new List<(string Key, string Text, List<string> Origins)>();
        var fileIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var templateKeys = templates.Ids.Select(id => templates.Get(id)!).Select(Key).ToHashSet(StringComparer.Ordinal);

        var list = new StringBuilder();
        foreach (var (use, usedBy) in packages)
        {
            var info = readPackage(use.Id, use.Version);
            var name = $"{use.Id} {use.Version}";
            list.Append(name).Append('\n');
            var licenseText = info.LicenseExpression;
            var ids = LicenseExpressions.Identifiers(info.LicenseExpression);
            foreach (var id in ids)
            {
                if (LicenseExpressions.TemplateId(id) is { } template && templates.Get(template) is not null)
                {
                    if (!templateUsers.TryGetValue(template, out var users))
                    {
                        templateUsers[template] = users = [];
                    }

                    users.Add(name);
                }
                else if (info.Files.Count == 0)
                {
                    warnings.Add($"{name}: no license text for '{id}' (add packaging/notices/licenses/{id}.txt)");
                }
            }

            if (licenseText is null)
            {
                licenseText = info.LicenseFile is not null ? $"see the file {info.LicenseFile} of the package (section 4)"
                    : info.LicenseUrl is not null ? info.LicenseUrl + (info.Files.Count > 0 ? " (license files of the package in section 4)" : string.Empty)
                    : info.Files.Count > 0 ? "see the license files of the package (section 4)"
                    : "unknown";
            }

            if (!info.Found)
            {
                warnings.Add($"{name}: package not found in the NuGet packages folder");
            }
            else if (ids.Count == 0 && info.Files.Count == 0)
            {
                warnings.Add($"{name}: no license expression and no license file ({licenseText})");
            }

            list.Append("  License: ").Append(licenseText).Append('\n');
            if (info.Copyright is { } copyright)
            {
                list.Append("  Copyright: ").Append(OneLine(copyright)).Append('\n');
            }
            else if (info.Authors is { } authors)
            {
                list.Append("  Authors: ").Append(OneLine(authors)).Append('\n');
            }

            if (info.ProjectUrl is { } url)
            {
                list.Append("  Project: ").Append(url).Append('\n');
            }

            list.Append("  Used by: ").Append(string.Join(", ", usedBy)).Append('\n').Append('\n');

            foreach (var file in info.Files)
            {
                var key = Key(file.Text);
                if (key.Length == 0 || templateKeys.Contains(key))
                {
                    continue;
                }

                var origin = $"{name}: {file.Path}";
                if (fileIndex.TryGetValue(key, out var index))
                {
                    files[index].Origins.Add(origin);
                }
                else
                {
                    fileIndex[key] = files.Count;
                    files.Add((key, file.Text, [origin]));
                }
            }
        }

        var sb = new StringBuilder();
        var title = "OADM third-party notices";
        sb.Append(title).Append('\n').Append(new string('=', title.Length)).Append("\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"OADM {options.ProductVersion}{(options.Rid is null ? string.Empty : $" ({options.Rid})")}. OADM itself is licensed under the Apache License 2.0 (LICENSE.txt).\n");
        sb.Append("This file lists the third-party software in the OADM server, the OADM client and the bundled plugins:\n");
        sb.Append("  1. Bundled components (FFmpeg, fonts, .NET)\n");
        sb.Append(CultureInfo.InvariantCulture, $"  2. NuGet packages ({packages.Count})\n");
        sb.Append("  3. License texts\n");
        sb.Append("  4. License and notice files shipped in the packages\n");
        sb.Append("Generated by tools/Oadm.Notices from the published dependency lists; do not edit by hand.\n\n");

        Section(sb, "1. Bundled components");
        sb.Append(PackageReader.Normalize(options.BundledNotices)).Append('\n');

        Section(sb, "2. NuGet packages");
        sb.Append(list);

        Section(sb, "3. License texts");
        foreach (var (template, users) in templateUsers)
        {
            if (templates.Get(template) is not { } text)
            {
                warnings.Add($"license text {template} missing in the templates");
                continue;
            }

            sb.Append("=== ").Append(template).Append(" ===\n");
            if (users.Count > 0)
            {
                sb.Append("Used by: ").Append(string.Join(", ", users)).Append('\n');
            }

            sb.Append('\n').Append(text).Append('\n');
        }

        Section(sb, "4. License and notice files shipped in the packages");
        foreach (var (_, text, origins) in files)
        {
            sb.Append("=== ").Append(origins[0]).Append(" ===\n");
            if (origins.Count > 1)
            {
                sb.Append("Same text in: ").Append(string.Join(", ", origins.Skip(1))).Append('\n');
            }

            sb.Append('\n').Append(text).Append('\n');
        }

        return new NoticesResult(sb.ToString(), packages.Count, warnings);
    }

    /// <summary>Server first, then client, then plugins.</summary>
    private static int UsedByOrder(string usedBy) => usedBy switch
    {
        "server" => 0,
        "client" => 1,
        _ => 2,
    };

    private static void Section(StringBuilder sb, string title) =>
        sb.Append(title).Append('\n').Append(new string('-', title.Length)).Append("\n\n");

    private static string OneLine(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    /// <summary>Content key for deduplication: whitespace collapsed.</summary>
    private static string Key(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
