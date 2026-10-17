using System.Diagnostics;

using Oadm.NewPlugin;

// Usage: Oadm.NewPlugin --kind task|page|action --name "My cool plugin" [--id acme.my-cool-plugin] [--repo <folder>]
//        [--dotnet <the dotnet that adds the projects to Oadm.sln, as manage found it>]
//        Oadm.NewPlugin --templates <folder> --version <x.y.z> [--repo <folder>]: the content of the package Oadm.Plugin.Templates
string? kind = null, name = null, id = null, repo = null, dotnet = null, templates = null, version = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--kind" when i + 1 < args.Length: kind = args[++i]; break;
        case "--name" when i + 1 < args.Length: name = args[++i]; break;
        case "--id" when i + 1 < args.Length: id = args[++i]; break;
        case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
        case "--dotnet" when i + 1 < args.Length: dotnet = args[++i]; break;
        case "--templates" when i + 1 < args.Length: templates = args[++i]; break;
        case "--version" when i + 1 < args.Length: version = args[++i]; break;
        default:
            return Fail($"unknown argument {args[i]}");
    }
}

if (templates is not null)
{
    if (string.IsNullOrWhiteSpace(version))
    {
        return Fail("--version is missing, e.g. --version 3.5.0");
    }

    foreach (var folder in Templates.Write(repo ?? Directory.GetCurrentDirectory(), templates, version))
    {
        Console.WriteLine("Template " + folder);
    }

    return 0;
}

var sample = Scaffold.Samples.FirstOrDefault(s => s.Kind == kind);
if (sample is null)
{
    return Fail("the kind must be task, page or action");
}

if (string.IsNullOrWhiteSpace(name))
{
    return Fail("--name is missing, e.g. --name \"My cool plugin\"");
}

repo ??= Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(repo, "Oadm.sln")))
{
    return Fail($"Oadm.sln not found in {repo}");
}

try
{
    var names = Scaffold.Names(name, id);
    var projects = Scaffold.Create(repo, sample, names);
    foreach (var project in projects)
    {
        var folder = Path.GetRelativePath(repo, Path.GetDirectoryName(project)!).Split(Path.DirectorySeparatorChar)[0];
        AddToSolution(dotnet ?? "dotnet", repo, project, folder);
    }

    Console.WriteLine(Scaffold.Report(repo, sample, names, projects));
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    return Fail(ex.Message);
}

static void AddToSolution(string dotnet, string repo, string project, string folder)
{
    using var process = Process.Start(new ProcessStartInfo(dotnet)
    {
        ArgumentList = { "sln", Path.Combine(repo, "Oadm.sln"), "add", project, "--solution-folder", folder },
        RedirectStandardOutput = true,
        UseShellExecute = false,
    })!;
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"The plugin was created, but adding {project} to Oadm.sln failed. Add it with: dotnet sln Oadm.sln add {project}");
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine("error: " + message);
    return 2;
}
