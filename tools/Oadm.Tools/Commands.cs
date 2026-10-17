namespace Oadm.Tools;

/// <summary>The commands of <c>oadm</c> and their help.</summary>
internal static class Commands
{
    public const string Help = """
        oadm - tools for OADM plugin developers

        Development server (a portable OADM on this computer, own data, the plugins of this folder):
          oadm dev start [--port 5090] [--from <folder or ZIP>]   runs it here; a new build restarts it; Ctrl+C stops it
          oadm dev client [--port <port>]                          the OADM client with this folder's plugins
          oadm dev stop | status | logs [--lines 50]

        Installed OADM (the server service and client on this computer):
          oadm deploy --installed [--plugin <id>]   copies the plugin in (one administrator prompt), restarts the service
          oadm debug --installed                    runs the installed server in this console with the build output
          oadm folders                              where the installed server and client keep data and plugins
          oadm deploy --to <plugin folder>          copies the plugin folders into any OADM plugin folder

        Shipping:
          oadm pack [--out <folder>]                one <id>-<version>.oadmplugin per plugin (Release build)
          oadm license keys [--out <folder>]        your key pair for licensed plugins
          oadm license sign --key <private-key.pem> --plugin <id> --server <server ID> --customer <name>
                            [--expires yyyy-MM-dd] [--features a,b] [--limit name=n]... [--out <file>]

          oadm doctor                               checks .NET, ports, the installed OADM and this folder
          oadm version

        Run the commands in the plugin's folder (the one with the .slnx). Guide: https://oadm.app/dev/plugin-sdk/
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
            {
                output.WriteLine(Help);
                return 0;
            }

            var rest = args.Skip(1).ToList();
            switch (args[0])
            {
                case "version" or "--version":
                    output.WriteLine(Cli.Version);
                    return 0;
                case "license":
                    return LicenseCommands.Run([.. rest], output, error, Directory.GetCurrentDirectory(), DateOnly.FromDateTime(DateTime.Today));
                case "dev":
                    if (rest.Count == 0)
                    {
                        throw new UsageException("dev needs a command: start, client, stop, status or logs");
                    }

                    return await DevCommands.RunAsync(rest[0], Options.Parse(rest.Skip(1)), output, ct).ConfigureAwait(false);
                case "deploy":
                    return InstalledCommands.Deploy(Options.Parse(rest, "--installed", "--server-only"), output);
                case "debug":
                    return InstalledCommands.Debug(Options.Parse(rest, "--installed"), output, ct);
                case "folders":
                    return InstalledCommands.ShowFolders(Options.Parse(rest), output);
                case "pack":
                    return InstalledCommands.Pack(Options.Parse(rest), output);
                case "doctor":
                    return Doctor.Run(Options.Parse(rest), output);
                default:
                    throw new UsageException($"unknown command '{args[0]}'");
            }
        }
        catch (UsageException ex)
        {
            error.WriteLine("oadm: " + ex.Message);
            error.WriteLine("Run \"oadm help\" for the commands.");
            return 2;
        }
        catch (ToolException ex)
        {
            error.WriteLine("oadm: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            error.WriteLine("oadm: " + ex.Message);
            return 1;
        }
    }
}

/// <summary><c>oadm doctor</c>: what a plugin developer's computer has and what is missing.</summary>
internal static class Doctor
{
    public static int Run(Options options, TextWriter output)
    {
        options.RejectUnknown();
        var problems = 0;
        output.WriteLine($"oadm {Cli.Version} ({Cli.Rid})");

        var sdks = Cli.Capture("dotnet", ["--list-sdks"]);
        var has10 = sdks.Code == 0 && sdks.Output.Split('\n').Any(l => l.StartsWith("10.", StringComparison.Ordinal));
        problems += Line(output, has10, ".NET 10 SDK", has10 ? sdks.Output.Split('\n').Last(l => l.StartsWith("10.", StringComparison.Ordinal)).Split(' ')[0] : "missing: install the .NET 10 SDK");

        var workspace = Workspace.Find(Directory.GetCurrentDirectory());
        var plugins = workspace.Plugins();
        problems += Line(output, plugins.Count > 0, "Plugins built here", plugins.Count > 0 ? string.Join(", ", plugins.Select(p => $"{p.Id} {p.Version}")) : $"none in {workspace.PluginsFolder}: run dotnet build");

        var state = DevCommands.ReadState(workspace);
        output.WriteLine($"  Development server   {(state is null ? "not running" : $"https://localhost:{state.Port}")}");
        problems += Line(output, state is not null || DevCommands.IsPortFree(DevCommands.DefaultPort), $"Port {DevCommands.DefaultPort}", state is not null || DevCommands.IsPortFree(DevCommands.DefaultPort) ? "free" : "in use: oadm dev start --port <other>");

        var cache = Path.Combine(PortableOadm.DefaultCache, Cli.Version);
        output.WriteLine($"  Portable OADM        {(Directory.Exists(cache) ? cache : "not downloaded yet (oadm dev start does it once)")}");

        try
        {
            if (InstalledOadm.FindService() is { } service)
            {
                var folders = InstalledOadm.ServerFolders(service);
                output.WriteLine($"  Installed server     {(folders.Version.Length > 0 ? folders.Version + ", " : "")}plugins in {folders.DeployFolder}");
            }
            else
            {
                output.WriteLine("  Installed server     none");
            }

            output.WriteLine(InstalledOadm.ClientFolders() is { } client
                ? $"  Installed client     {(client.Version.Length > 0 ? client.Version + ", " : "")}plugins in {client.DeployFolder}"
                : "  Installed client     none");
        }
        catch (ToolException ex)
        {
            problems += Line(output, false, "Installed OADM", ex.Message);
        }

        output.WriteLine(problems == 0 ? "Everything is ready." : $"{problems} problem{(problems == 1 ? "" : "s")} found.");
        return problems == 0 ? 0 : 1;
    }

    private static int Line(TextWriter output, bool ok, string what, string detail)
    {
        output.WriteLine($"{(ok ? "  " : "! ")}{what,-20} {detail}");
        return ok ? 0 : 1;
    }
}
