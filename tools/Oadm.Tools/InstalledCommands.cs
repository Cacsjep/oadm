using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Oadm.Tools;

/// <summary>
/// <c>oadm deploy</c>, <c>oadm debug --installed</c>, <c>oadm folders</c>, <c>oadm pack</c>: the plugin in an installed OADM,
/// in another OADM's plugin folder, or as the <c>.oadmplugin</c> file for customers.
/// </summary>
internal static class InstalledCommands
{
    /// <summary>
    /// <c>oadm deploy --installed</c>: the client part into this user's client plugin folder, then (with one administrator
    /// prompt) the service stopped, the plugin copied into the server's plugin folder, its download mark removed and the
    /// service started. <c>oadm deploy --to &lt;folder&gt;</c> copies the plugin folders into any plugin folder.
    /// </summary>
    public static int Deploy(Options options, TextWriter output)
    {
        var installed = options.Has("--installed");
        var serverOnly = options.Has("--server-only");
        var to = options.Get("--to");
        var workspace = Workspace.Find(options.Get("--workspace") ?? Directory.GetCurrentDirectory());
        var only = options.All("--plugin");
        options.RejectUnknown();

        var plugins = Select(workspace.RequirePlugins(), only);
        if (to is not null)
        {
            foreach (var plugin in plugins)
            {
                var count = Cli.CopyPlugin(plugin.Path, to, plugin.Id);
                output.WriteLine($"{plugin.Id} {plugin.Version} -> {Path.Combine(Path.GetFullPath(to), plugin.Id)} ({count} files)");
            }

            return 0;
        }

        if (!installed)
        {
            throw new UsageException("say where to: --installed (the OADM installed on this computer) or --to <plugin folder>. The development server loads every build by itself.");
        }

        var service = InstalledOadm.RequireService();
        var serverFolders = InstalledOadm.ServerFolders(service);
        InstalledOadm.RequireCurrent(serverFolders, output);
        if (!serverOnly && InstalledOadm.ClientFolders() is { } client)
        {
            InstalledOadm.RequireCurrent(client, output);
            foreach (var plugin in plugins)
            {
                var target = Path.Combine(client.DeployFolder, plugin.Id);
                Cli.CopyPlugin(plugin.Path, client.DeployFolder, plugin.Id);
                InstalledOadm.Unblock(target);
                output.WriteLine($"Client: {plugin.Id} {plugin.Version} -> {target} (start the client again to load it)");
            }
        }

        if (!Environment.IsPrivilegedProcess)
        {
            output.WriteLine("The server's plugin folder needs administrator rights.");
            return Elevate(["deploy", "--installed", "--server-only", "--workspace", workspace.Root, .. only.SelectMany(p => new[] { "--plugin", p })], output);
        }

        // Every id is checked before the service stops (CopyPlugin checks again).
        if (plugins.FirstOrDefault(p => !Cli.IsSafeId(p.Id)) is { } bad)
        {
            throw new ToolException($"The plugin id \"{bad.Id}\" is not a plain name. Nothing was changed.");
        }

        var wasRunning = InstalledOadm.StopService(service, output);
        try
        {
            foreach (var plugin in plugins)
            {
                var target = Path.Combine(serverFolders.DeployFolder, plugin.Id);
                Cli.CopyPlugin(plugin.Path, serverFolders.DeployFolder, plugin.Id);
                InstalledOadm.Unblock(target);
                InstalledOadm.SecureForService(target);
                output.WriteLine($"Server: {plugin.Id} {plugin.Version} -> {target}");
            }
        }
        finally
        {
            InstalledOadm.StartAgain(service, wasRunning, output);
        }

        return 0;
    }

    /// <summary>
    /// <c>oadm debug --installed</c>: stops the installed service and runs the installed server in this console with the
    /// service's data folder and the plugin from the build output, so a debugger can attach from the first line; the
    /// service starts again when the server ends (Ctrl+C).
    /// </summary>
    public static int Debug(Options options, TextWriter output, CancellationToken ct)
    {
        if (!options.Has("--installed"))
        {
            throw new UsageException("oadm debug works with the installed OADM: oadm debug --installed (for the development server: oadm dev start and attach to it)");
        }

        var workspace = Workspace.Find(options.Get("--workspace") ?? Directory.GetCurrentDirectory());
        options.RejectUnknown();
        var plugins = workspace.RequirePlugins();
        var service = InstalledOadm.RequireService();
        InstalledOadm.RequireCurrent(InstalledOadm.ServerFolders(service), output);
        if (!Environment.IsPrivilegedProcess)
        {
            output.WriteLine("Stopping the installed service needs administrator rights.");
            return Elevate(["debug", "--installed", "--workspace", workspace.Root], output);
        }

        var wasRunning = InstalledOadm.StopService(service, output);
        var startedAgain = 0;
        void StartAgainOnce()
        {
            if (Interlocked.Exchange(ref startedAgain, 1) == 0)
            {
                InstalledOadm.StartAgain(service, wasRunning, output);
            }
        }

        // Closing the window (Windows) or the terminal ends this process without the finally below.
        using var closed = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, _ => StartAgainOnce());
        try
        {
            using var server = Cli.Start(service.Exe, [.. service.Args, $"--Oadm:PluginDir={string.Join(';', plugins.Select(p => p.Path))}"], new Dictionary<string, string>(service.Environment));
            output.WriteLine($"The installed server runs in this console with {string.Join(", ", plugins.Select(p => p.Id))} from the build output.");
            output.WriteLine($"Attach the debugger to process {server.Id} ({Path.GetFileName(service.Exe)}). Ctrl+C ends it and starts the service again.");
            try
            {
                server.WaitForExitAsync(ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                if (!server.WaitForExit(TimeSpan.FromSeconds(20)))
                {
                    server.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            StartAgainOnce();
        }

        return 0;
    }

    /// <summary><c>oadm folders</c>: what the installed server and client and this workspace use.</summary>
    public static int ShowFolders(Options options, TextWriter output)
    {
        options.RejectUnknown();
        var workspace = Workspace.Find(Directory.GetCurrentDirectory());
        output.WriteLine($"Workspace {workspace.Root}");
        output.WriteLine($"  plugin folders: {workspace.PluginsFolder}");
        output.WriteLine($"  development server data: {workspace.ServerData}");
        if (InstalledOadm.FindService() is { } service)
        {
            output.WriteLine($"Installed server (service {service.Name}):");
            output.WriteLine(InstalledOadm.ServerFolders(service).Describe());
        }
        else
        {
            output.WriteLine("Installed server: none");
        }

        output.WriteLine(InstalledOadm.ClientFolders() is { } client ? "Installed client (this user):\n" + client.Describe() : "Installed client: none");
        return 0;
    }

    /// <summary>
    /// <c>oadm pack</c>: a Release build into a temporary plugin folder, then one <c>&lt;id&gt;-&lt;version&gt;.oadmplugin</c>
    /// (a ZIP of the plugin folder) per plugin.
    /// </summary>
    public static int Pack(Options options, TextWriter output)
    {
        var workspace = Workspace.Find(Directory.GetCurrentDirectory());
        var outFolder = Path.GetFullPath(options.Get("--out") ?? workspace.Root);
        var configuration = options.Get("--configuration") ?? "Release";
        options.RejectUnknown();

        var staging = Path.Combine(Path.GetTempPath(), "oadm-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.WriteLine($"Building {workspace.Root} ({configuration})...");
            var code = Cli.Run("dotnet", ["build", "-c", configuration, "-nologo", "-v", "q", $"-p:OadmDeployRoot={staging}{Path.DirectorySeparatorChar}"], workingDirectory: workspace.Root);
            if (code != 0)
            {
                throw new ToolException("The build failed; nothing was packed.");
            }

            var plugins = Workspace.Read(staging);
            if (plugins.Count == 0)
            {
                throw new ToolException("The build wrote no plugin folder. Do the plugin projects use Oadm.Plugin.Sdk?");
            }

            Directory.CreateDirectory(outFolder);
            foreach (var plugin in plugins)
            {
                var file = Path.Combine(outFolder, $"{plugin.Id}-{plugin.Version}.oadmplugin");
                File.Delete(file);
                ZipFile.CreateFromDirectory(plugin.Path, file, CompressionLevel.Optimal, includeBaseDirectory: false);
                output.WriteLine($"{file} ({new FileInfo(file).Length / 1024} KB)");
            }

            return 0;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static IReadOnlyList<PluginFolder> Select(IReadOnlyList<PluginFolder> plugins, IReadOnlyList<string> only)
    {
        if (only.Count == 0)
        {
            return plugins;
        }

        var missing = only.Where(id => !plugins.Any(p => p.Id == id)).ToList();
        return missing.Count == 0
            ? [.. plugins.Where(p => only.Contains(p.Id))]
            : throw new ToolException($"Not built here: {string.Join(", ", missing)}. Built: {string.Join(", ", plugins.Select(p => p.Id))}.");
    }

    /// <summary>Runs this tool again with administrator rights (UAC prompt, or sudo in this terminal) and returns its exit code.</summary>
    private static int Elevate(IReadOnlyList<string> args, TextWriter output)
    {
        var (file, prefix) = Cli.Self();
        if (!OperatingSystem.IsWindows())
        {
            return Cli.Run("sudo", [file, .. prefix, .. args]);
        }

        // The elevated tool runs in a window of its own; its output comes back through a file.
        var log = Path.Combine(Path.GetTempPath(), "oadm-elevated-" + Guid.NewGuid().ToString("N") + ".log");
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', prefix.Concat(args).Concat(["--log", log]).Select(Quote)),
        };
        try
        {
            using var process = Process.Start(info) ?? throw new ToolException("The tool could not be started with administrator rights.");
            process.WaitForExit();
            if (File.Exists(log))
            {
                output.Write(File.ReadAllText(log));
                File.Delete(log);
            }

            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            throw new ToolException("Administrator rights were not given. Nothing was changed on the server.");
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Writes to the console and to the log file of an elevated run.</summary>
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}
