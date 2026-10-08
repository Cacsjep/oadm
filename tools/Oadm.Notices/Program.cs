using System.Text.Json;

using Oadm.Notices;

// Writes THIRD-PARTY-NOTICES.txt, LICENSE.txt and LGPL-2.1.txt into every --out folder (run by "manage publish").
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
var options = Arguments.Parse(args);
if (options is null)
{
    return 2;
}

var uses = new List<PackageUse>();
string? assetsFile = null;
foreach (var app in options.Apps)
{
    var project = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(app)));
    var deps = Directory.Exists(Path.Combine(app, "obj", "Release"))
        ? Directory.GetDirectories(Path.Combine(app, "obj", "Release"))
            .Select(tfm => Path.Combine(tfm, options.Rid, project + ".deps.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;
    if (deps is null)
    {
        Console.Error.WriteLine($"error: {project}.deps.json for {options.Rid} not found below {app}/obj/Release (publish it first)");
        return 1;
    }

    var label = project.EndsWith(".Server", StringComparison.Ordinal) ? "server" : project.EndsWith(".Client", StringComparison.Ordinal) ? "client" : project;
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
        foreach (var deps in Directory.GetFiles(plugin, "*.deps.json").Order(StringComparer.Ordinal))
        {
            uses.AddRange(DepsFile.Read(deps, "plugin " + Path.GetFileName(plugin)));
        }
    }
}

var packagesRoot = options.Packages ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? PackagesFolderFromAssets(assetsFile)
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var templates = new LicenseTemplates(Path.Combine(options.Repo, "packaging", "notices", "licenses"));
var bundled = File.ReadAllText(Path.Combine(options.Repo, "THIRD-PARTY-NOTICES.md"));
var result = NoticesWriter.Build(
    new NoticesOptions(options.Version, options.Rid, bundled, ["Apache-2.0", "LGPL-2.1", "OFL-1.1"]),
    uses,
    (id, version) => PackageReader.Read(packagesRoot, id, version),
    templates);

foreach (var warning in result.Warnings)
{
    Console.Error.WriteLine("warning: " + warning);
}

var license = PackageReader.Normalize(File.ReadAllText(Path.Combine(options.Repo, "LICENSE")));
var lgpl = templates.Get("LGPL-2.1") ?? throw new InvalidOperationException("packaging/notices/licenses/LGPL-2.1.txt missing");
foreach (var output in options.Outputs)
{
    Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(output, NoticesWriter.FileName), result.Text);
    File.WriteAllText(Path.Combine(output, "LICENSE.txt"), license);
    File.WriteAllText(Path.Combine(output, "LGPL-2.1.txt"), lgpl);
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

internal sealed record Arguments(string Rid, string Version, string Repo, string? Packages, List<string> Apps, List<string> PluginRoots, List<string> Outputs)
{
    public static Arguments? Parse(string[] args)
    {
        string? rid = null, version = null, repo = null, packages = null;
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
                default: return Usage($"unknown option {name}");
            }
        }

        repo ??= FindRepo(Directory.GetCurrentDirectory());
        if (rid is null || version is null || repo is null || outputs.Count == 0 || apps.Count == 0)
        {
            return Usage("--rid, --version, --app and --out are required (and --repo outside the repository)");
        }

        return new Arguments(rid, version, repo, packages, apps, plugins, outputs);
    }

    private static Arguments? Usage(string message)
    {
        Console.Error.WriteLine("error: " + message);
        Console.Error.WriteLine("usage: Oadm.Notices --rid <rid> --version <v> --app <project folder>... [--plugins <folder>] --out <folder>... [--packages <folder>] [--repo <folder>]");
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
