using System.Text.Json;

using Oadm.Notices;

// Writes THIRD-PARTY-NOTICES.txt, LICENSE.txt, NOTICE.txt (copyright), LGPL-2.1.txt and TERMS.md (terms of use) into every --out folder (run by "manage publish").
//
//   Oadm.Notices --rid win-x64 --version 1.2.0 --app src/Oadm.Server --app src/Oadm.Client
//                --plugins artifacts/publish/plugins --out <server publish folder> --out <client publish folder>
//
//   --app <project folder>   reads <folder>/obj/Release/<tfm>/<rid>/<Project>.deps.json (written by the publish);
//                            "server" / "client" from the project name
//   --plugins <folder>       every <folder>/<plugin id>/*.deps.json, "plugin <plugin id>"
//   --packages <folder>      NuGet packages folder (default: NUGET_PACKAGES, the project's assets file, ~/.nuget/packages)
//   --repo <folder>          repository root (default: found from the working directory); templates in
//                            packaging/notices/licenses, bundled notes in THIRD-PARTY-NOTICES.md, LICENSE
//   --product site-node      the OADM Site Node (service and app, "manage package site-node"): bundled notes from
//                            packaging/notices/site-node.md, no LGPL part, so no LGPL-2.1.txt
var options = Arguments.Parse(args);
if (options is null)
{
    return 2;
}

var uses = new List<PackageUse>();
var ownLicenses = new List<string>();
string? assetsFile = null;
foreach (var app in options.Apps)
{
    var project = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(app)));
    // <Project>.deps.json, or <AssemblyName>.deps.json when the assembly has its own name (oadm-site-node).
    var deps = Directory.Exists(Path.Combine(app, "obj", "Release"))
        ? Directory.GetDirectories(Path.Combine(app, "obj", "Release"))
            .Select(tfm => Path.Combine(tfm, options.Rid))
            .Where(Directory.Exists)
            .SelectMany(folder => File.Exists(Path.Combine(folder, project + ".deps.json"))
                ? [Path.Combine(folder, project + ".deps.json")]
                : Directory.GetFiles(folder, "*.deps.json"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;
    if (deps is null)
    {
        Console.Error.WriteLine($"error: {project}.deps.json for {options.Rid} not found below {app}/obj/Release (publish it first)");
        return 1;
    }

    var label = project switch
    {
        _ when project.EndsWith(".Server", StringComparison.Ordinal) => "server",
        _ when project.EndsWith(".Client", StringComparison.Ordinal) => "client",
        "Oadm.SiteNode" => "site node service",
        "Oadm.SiteNode.App" => "site node app",
        _ => project,
    };
    uses.AddRange(DepsFile.Read(deps, label));
    assetsFile ??= Path.Combine(app, "obj", "project.assets.json");
}

foreach (var root in options.PluginRoots)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"error: plugin folder {root} not found");
        return 1;
    }

    foreach (var plugin in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
    {
        if (File.Exists(Path.Combine(plugin, "LICENSE")))
        {
            ownLicenses.Add(Path.GetFileName(plugin));
        }

        foreach (var deps in Directory.GetFiles(plugin, "*.deps.json").Order(StringComparer.Ordinal))
        {
            uses.AddRange(DepsFile.Read(deps, "plugin " + Path.GetFileName(plugin)));
        }
    }
}

var packagesRoot = options.Packages ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? PackagesFolderFromAssets(assetsFile)
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var templates = new LicenseTemplates(Path.Combine(options.Repo, "packaging", "notices", "licenses"));
var siteNode = options.Product == "site-node";
var bundled = File.ReadAllText(Path.Combine(options.Repo, siteNode ? Path.Combine("packaging", "notices", "site-node.md") : "THIRD-PARTY-NOTICES.md"));
var noticesOptions = siteNode
    ? new NoticesOptions(options.Version, options.Rid, bundled, ["Apache-2.0", "OFL-1.1"], "OADM Site Node", "the OADM Site Node service and app", "fonts, .NET")
    : new NoticesOptions(options.Version, options.Rid, bundled, ["Apache-2.0", "LGPL-2.1", "OFL-1.1"]) { OwnLicensePlugins = [.. ownLicenses] };
var result = NoticesWriter.Build(
    noticesOptions,
    uses,
    (id, version) => PackageReader.Read(packagesRoot, id, version),
    templates);

foreach (var warning in result.Warnings)
{
    Console.Error.WriteLine("warning: " + warning);
}

var license = PackageReader.Normalize(File.ReadAllText(Path.Combine(options.Repo, "LICENSE")));
var terms = PackageReader.Normalize(File.ReadAllText(Path.Combine(options.Repo, "TERMS.md")));
var notice = PackageReader.Normalize(File.ReadAllText(Path.Combine(options.Repo, "NOTICE")));
var lgpl = templates.Get("LGPL-2.1") ?? throw new InvalidOperationException("packaging/notices/licenses/LGPL-2.1.txt missing");
foreach (var output in options.Outputs)
{
    Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(output, NoticesWriter.FileName), result.Text);
    File.WriteAllText(Path.Combine(output, "LICENSE.txt"), license);
    File.WriteAllText(Path.Combine(output, "NOTICE.txt"), notice);
    if (!siteNode)
    {
        File.WriteAllText(Path.Combine(output, "LGPL-2.1.txt"), lgpl); // FFmpeg (client)
    }

    File.WriteAllText(Path.Combine(output, "TERMS.md"), terms);
    Console.WriteLine($"notices: {result.PackageCount} packages, {result.Text.Length / 1024} KB -> {Path.Combine(output, NoticesWriter.FileName)}");
}

return 0;

static string? PackagesFolderFromAssets(string? assetsFile)
{
    if (assetsFile is null || !File.Exists(assetsFile))
    {
        return null;
    }

    using var doc = JsonDocument.Parse(File.ReadAllText(assetsFile));
    return doc.RootElement.TryGetProperty("packageFolders", out var folders)
        ? folders.EnumerateObject().Select(p => p.Name).FirstOrDefault(Directory.Exists)
        : null;
}

internal sealed record Arguments(string Rid, string Version, string Repo, string? Packages, List<string> Apps, List<string> PluginRoots, List<string> Outputs, string Product)
{
    public static Arguments? Parse(string[] args)
    {
        string? rid = null, version = null, repo = null, packages = null;
        var product = "oadm";
        List<string> apps = [], plugins = [], outputs = [];
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Length)
            {
                return Usage($"option {name} needs a value");
            }

            var value = args[++i];
            switch (name)
            {
                case "--rid": rid = value; break;
                case "--version": version = value; break;
                case "--repo": repo = value; break;
                case "--packages": packages = value; break;
                case "--app": apps.Add(value); break;
                case "--plugins": plugins.Add(value); break;
                case "--out": outputs.Add(value); break;
                case "--product" when value is "oadm" or "site-node": product = value; break;
                default: return Usage($"unknown option {name}");
            }
        }

        repo ??= FindRepo(Directory.GetCurrentDirectory());
        if (rid is null || version is null || repo is null || outputs.Count == 0 || apps.Count == 0)
        {
            return Usage("--rid, --version, --app and --out are required (and --repo outside the repository)");
        }

        return new Arguments(rid, version, repo, packages, apps, plugins, outputs, product);
    }

    private static Arguments? Usage(string message)
    {
        Console.Error.WriteLine("error: " + message);
        Console.Error.WriteLine("usage: Oadm.Notices --rid <rid> --version <v> --app <project folder>... [--plugins <folder>] --out <folder>... [--packages <folder>] [--repo <folder>] [--product oadm|site-node]");
        return null;
    }

    private static string? FindRepo(string start)
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
}
