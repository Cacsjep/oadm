using Oadm.Contracts.Vulnerabilities;

// Makes OADM's base list of Axis CVEs (docs/spec/vulnerabilities.md).
//   dotnet run --project tools/Oadm.Vulnerabilities -- [--out <file>] [--nvd <nvd answer> --records <folder of CVE-*.json>]
// Without --nvd it asks NVD and cve.org; with it, it builds the list from saved answers (no network).
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
File.WriteAllText(output, list.ToJson() + Environment.NewLine);
foreach (var note in notes)
{
    Console.WriteLine("  " + note);
}

Console.WriteLine($"{list.Items.Count} CVEs of device firmware ({list.Items.Count(i => i.Affected.Any(a => a.Model is null))} AXIS OS, "
    + $"{list.Items.Count(i => i.Affected.All(a => a.Model is not null))} single models) written to {Path.GetFullPath(output)}");
