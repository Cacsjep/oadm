using Oadm.Contracts.Vulnerabilities;

// Makes OADM's base list of Axis CVEs (docs/spec/vulnerabilities.md).
//   dotnet run --project tools/Oadm.Vulnerabilities -- [--out <file>] [--nvd <nvd answer> --records <folder of CVE-*.json>]
//       [--if-changed] [--summary <file>]
// Without --nvd it asks NVD and cve.org; with it, it builds the list from saved answers (no network).
// --if-changed keeps the file when only the date would change (the weekly workflow, .github/workflows/vulnerabilities.yml);
// --summary writes the added, removed and changed CVEs as Markdown (the pull request's text).
var arguments = args.ToList();
string? Option(string name)
{
    int i = arguments.IndexOf(name);
    return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
}

var output = Option("--out") ?? Path.Combine("src", "Oadm.Server", "Vulnerabilities", "axis-vulnerabilities.json");
VulnerabilityList list;
IReadOnlyList<string> notes = [];
if (Option("--nvd") is { } nvd)
{
    var folder = Option("--records") ?? throw new ArgumentException("--records <folder> is needed with --nvd");
    var records = Directory.GetFiles(folder, "CVE-*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText, StringComparer.Ordinal);
    (list, notes) = VulnerabilityFeed.Build([File.ReadAllText(nvd)], records, DateTimeOffset.UtcNow, VulnerabilityList.Sources.Release);
}
else
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("OADM-vulnerability-list");
    list = await VulnerabilityFeed.FetchAsync(http, VulnerabilityList.Sources.Release, TimeProvider.System, CancellationToken.None);
}

VulnerabilityList.Parse(list.ToJson()); // what OADM accepts
VulnerabilityList? old = File.Exists(output) ? VulnerabilityList.Parse(File.ReadAllText(output)) : null;
if (arguments.Contains("--if-changed") && old is not null && (old with { Updated = list.Updated, Source = list.Source }).ToJson() == list.ToJson())
{
    Console.WriteLine($"{list.Items.Count} CVEs, no change: {Path.GetFullPath(output)} kept");
    return;
}

if (Option("--summary") is { } summary)
{
    File.WriteAllText(summary, ListChanges.Markdown(old, list));
}

File.WriteAllText(output, list.ToJson() + Environment.NewLine);
foreach (var note in notes)
{
    Console.WriteLine("  " + note);
}

Console.WriteLine($"{list.Items.Count} CVEs of device firmware ({list.Items.Count(i => i.Affected.Any(a => a.Model is null))} AXIS OS, "
    + $"{list.Items.Count(i => i.Affected.All(a => a.Model is not null))} single models) written to {Path.GetFullPath(output)}");

/// <summary>What changed between two lists, as Markdown: the added, removed and changed CVEs by id.</summary>
internal static class ListChanges
{
    public static string Markdown(VulnerabilityList? old, VulnerabilityList list)
    {
        var before = (old?.Items ?? []).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var after = list.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var added = after.Values.Where(v => !before.ContainsKey(v.Id)).ToList();
        var removed = before.Values.Where(v => !after.ContainsKey(v.Id)).ToList();
        var changed = after.Values.Where(v => before.TryGetValue(v.Id, out var b) && !Same(b, v)).ToList();
        var lines = new List<string> { $"{list.Items.Count} CVEs of device firmware (before: {before.Count}).", "" };
        Section(lines, "Added", added);
        Section(lines, "Changed", changed);
        Section(lines, "Removed", removed);
        return string.Join("\n", lines) + "\n";
    }

    private static bool Same(Vulnerability a, Vulnerability b) =>
        new VulnerabilityList(DateTimeOffset.UnixEpoch, "", [a]).ToJson() == new VulnerabilityList(DateTimeOffset.UnixEpoch, "", [b]).ToJson();

    private static void Section(List<string> lines, string title, List<Vulnerability> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        lines.Add($"### {title}");
        lines.Add("");
        lines.AddRange(items.Select(v => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"- {v.Id} {(v.Severity.Length > 0 ? v.Severity : "Unrated")}{(v.Score is { } s ? $" {s:0.0}" : "")}")));
        lines.Add("");
    }
}
