using Oadm.ApiDocs;

// Generates the SDK reference of the website from Oadm.Sdk and Oadm.Sdk.Client (next to this tool, with their doc
// comment files). Usage: Oadm.ApiDocs [--repo <repository root>] [--base /oadm/]
// Writes website/src/content/docs/api/ (replaced as a whole) and website/src/generated/api-sidebar.json.
string? repo = null;
var basePath = "/oadm/";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo" when i + 1 < args.Length:
            repo = args[++i];
            break;
        case "--base" when i + 1 < args.Length:
            basePath = args[++i];
            break;
        default:
            await Console.Error.WriteLineAsync($"Unknown argument {args[i]}. Usage: Oadm.ApiDocs [--repo <folder>] [--base /oadm/]");
            return 2;
    }
}

repo ??= FindRepo(Directory.GetCurrentDirectory());
if (repo is null)
{
    await Console.Error.WriteLineAsync("Oadm.sln not found: run inside the repository or pass --repo.");
    return 2;
}

var docs = Pages.Generate(ApiModel.Load(AppContext.BaseDirectory, repo), basePath);
var website = Path.Combine(repo, "website", "src");
var output = Path.Combine(website, "content", "docs", Layout.Root);

// Files are replaced in place and only pages of types that are gone are deleted, so a running docs server keeps working.
var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var (path, text) in docs.Pages)
{
    var file = Path.GetFullPath(Path.Combine(website, "content", "docs", path.Replace('/', Path.DirectorySeparatorChar)));
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    if (!File.Exists(file) || await File.ReadAllTextAsync(file) != text)
    {
        await File.WriteAllTextAsync(file, text);
    }

    written.Add(file);
}

foreach (var stale in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Where(f => !written.Contains(Path.GetFullPath(f))).ToList())
{
    File.Delete(stale);
}

foreach (var dir in Directory.EnumerateDirectories(output, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
{
    if (!Directory.EnumerateFileSystemEntries(dir).Any())
    {
        Directory.Delete(dir);
    }
}

var sidebar = Path.Combine(website, "generated", "api-sidebar.json");
Directory.CreateDirectory(Path.GetDirectoryName(sidebar)!);
await File.WriteAllTextAsync(sidebar, docs.SidebarJson);
Console.WriteLine($"SDK reference: {docs.Pages.Count} pages in {output}");
return 0;

static string? FindRepo(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Oadm.sln")))
        {
            return dir.FullName;
        }
    }

    return null;
}
