using System.Text;
using System.Text.Json;

namespace Oadm.Tools;

/// <summary>How the installed service starts the server: the program, its arguments and environment.</summary>
internal sealed record ServiceDefinition(string Name, string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment);

/// <summary>Where an installed OADM program keeps its data and plugins (the fields of <c>--print-folders</c>).</summary>
internal sealed record Folders(string App, string Version, string AppFolder, string DataFolder, string DeployFolder, IReadOnlyList<string> PluginFolders)
{
    /// <summary>One line per folder, for <c>oadm folders</c>.</summary>
    public string Describe() =>
        $"  program:        {AppFolder}{(Version.Length > 0 ? $" ({Version})" : "")}\n" +
        $"  data:           {DataFolder}\n" +
        $"  plugins (deploy): {DeployFolder}\n" +
        $"  plugin folders: {string.Join(", ", PluginFolders)}";
}

/// <summary>
/// The OADM installed on this computer: the service definition is read from the service manager (Windows service
/// "OadmServer", systemd unit oadm-server, LaunchDaemon com.oadm.server) and the folders follow from the service's own
/// arguments and environment by the server's rules, so nothing is guessed and no installed program is run to ask.
/// </summary>
internal static class InstalledOadm
{
    public const string WindowsService = "OadmServer";
    public const string SystemdUnit = "oadm-server";
    public const string LaunchdLabel = "com.oadm.server";
    public const string LaunchdPlist = "/Library/LaunchDaemons/com.oadm.server.plist";

    /// <summary>The installed service, or null when OADM's server is not installed here.</summary>
    public static ServiceDefinition? FindService()
    {
        if (OperatingSystem.IsWindows())
        {
            var (code, text) = Cli.Capture("sc.exe", ["qc", WindowsService, "4096"]);
            return code == 0 && ParseScQc(text) is { } binary && SplitWindowsCommandLine(binary) is [var exe, .. var args]
                ? new ServiceDefinition(WindowsService, exe, args, new Dictionary<string, string>())
                : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (!File.Exists(LaunchdPlist))
            {
                return null;
            }

            var arguments = JsonDocument.Parse(Cli.Capture("plutil", ["-extract", "ProgramArguments", "json", "-o", "-", LaunchdPlist]).Output);
            var environment = Cli.Capture("plutil", ["-extract", "EnvironmentVariables", "json", "-o", "-", LaunchdPlist]);
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (environment.Code == 0)
            {
                foreach (var p in JsonDocument.Parse(environment.Output).RootElement.EnumerateObject())
                {
                    env[p.Name] = p.Value.GetString() ?? "";
                }
            }

            var list = arguments.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            return list.Count == 0 ? null : new ServiceDefinition(LaunchdLabel, list[0], list[1..], env);
        }

        var load = Cli.Capture("systemctl", ["show", SystemdUnit, "--property=LoadState", "--value"]);
        if (load.Code != 0 || load.Output.Trim() != "loaded")
        {
            return null;
        }

        var exec = Cli.Capture("systemctl", ["show", SystemdUnit, "--property=ExecStart", "--value"]).Output;
        var envText = Cli.Capture("systemctl", ["show", SystemdUnit, "--property=Environment", "--value"]).Output;
        var argv = ParseSystemdExecStart(exec);
        if (argv.Count == 0)
        {
            return null;
        }

        var envs = ParseSystemdEnvironment(envText);
        if (File.Exists("/etc/default/oadm-server"))
        {
            foreach (var line in File.ReadAllLines("/etc/default/oadm-server").Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#' && l.Contains('=', StringComparison.Ordinal)))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                envs[line[..eq]] = line[(eq + 1)..].Trim('"');
            }
        }

        return new ServiceDefinition(SystemdUnit, argv[0], argv[1..], envs);
    }

    public static ServiceDefinition RequireService() =>
        FindService() ?? throw new ToolException("The OADM server is not installed as a service on this computer.");

    /// <summary>
    /// The installed server's folders, read from the service's own arguments and environment by the server's rules
    /// (<c>--Oadm:DataDir</c>, env <c>Oadm__DataDir</c>, appsettings.json, env <c>OADM_DATA_DIR</c>; plugins in
    /// &lt;data folder&gt;/plugins). The installed program is never run for this: an older one would not know an option and
    /// start a second server on the live data.
    /// </summary>
    public static Folders ServerFolders(ServiceDefinition service)
    {
        var app = Path.GetDirectoryName(service.Exe) ?? throw new ToolException($"The service names no program folder ({service.Exe}).");
        var data = DataFolder(service.Args, service.Environment, Path.Combine(app, "appsettings.json"))
            ?? throw new ToolException($"The service {service.Name} names no data folder (--Oadm:DataDir or OADM_DATA_DIR). Reinstall OADM.");
        return new Folders("server", VersionOf(service.Exe), app, data, Path.Combine(data, "plugins"), [Path.Combine(app, "plugins"), Path.Combine(data, "plugins")]);
    }

    /// <summary>The data folder of a server started with these arguments and environment, by the server's own rules; null when none is named.</summary>
    public static string? DataFolder(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment, string? appSettings)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            foreach (var prefix in new[] { "--Oadm:DataDir=", "--Oadm__DataDir=", "/Oadm:DataDir=" })
            {
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i][prefix.Length..];
                }
            }

            if (i + 1 < args.Count && args[i] is var name && (name.Equals("--Oadm:DataDir", StringComparison.OrdinalIgnoreCase) || name.Equals("--Oadm__DataDir", StringComparison.OrdinalIgnoreCase)))
            {
                return args[i + 1];
            }
        }

        if (environment.TryGetValue("Oadm__DataDir", out var fromConfig) && fromConfig.Length > 0)
        {
            return fromConfig;
        }

        if (appSettings is not null && File.Exists(appSettings))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(appSettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (json.RootElement.TryGetProperty("Oadm", out var oadm) && oadm.ValueKind == JsonValueKind.Object
                    && oadm.TryGetProperty("DataDir", out var dir) && dir.GetString() is { Length: > 0 } fromFile)
                {
                    return fromFile;
                }
            }
            catch (JsonException)
            {
                // the server would not start with it either
            }
        }

        return environment.TryGetValue("OADM_DATA_DIR", out var fromEnv) && fromEnv.Length > 0 ? fromEnv : null;
    }

    /// <summary>The installed client's program of this OS (where the installers put it), or null.</summary>
    public static string? ClientExe()
    {
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OADM", "Client", "Oadm.Client.exe")
            : OperatingSystem.IsMacOS() ? "/Applications/OADM.app/Contents/MacOS/Oadm.Client" : "/opt/oadm/client/Oadm.Client";
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The installed client's folders for this user, or null without an installed client: the client keeps its settings
    /// and the user's plugins in &lt;LocalApplicationData&gt;/Oadm (the installers start it without --data). Never run.
    /// </summary>
    public static Folders? ClientFolders()
    {
        if (ClientExe() is not { } exe)
        {
            return null;
        }

        var app = Path.GetDirectoryName(exe)!;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Oadm");
        var bundled = OperatingSystem.IsMacOS() ? Path.Combine(Path.GetDirectoryName(app)!, "Resources", "plugins") : Path.Combine(app, "plugins");
        return new Folders("client", VersionOf(exe), app, data, Path.Combine(data, "plugins"), [bundled, Path.Combine(data, "plugins")]);
    }

    /// <summary>The program's version where the OS keeps one in the file (Windows), else empty.</summary>
    private static string VersionOf(string exe) =>
        OperatingSystem.IsWindows() && File.Exists(exe) ? (System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "").Split('+')[0] : "";

    /// <summary>
    /// Refuses an installed OADM older than this tool's release: its loader and SDK do not know what a plugin built with
    /// this SDK needs (newer SDK assemblies, --Oadm:PluginDir). Without a version in the file (Linux, macOS) it only warns.
    /// </summary>
    public static void RequireCurrent(Folders folders, TextWriter output)
    {
        if (!System.Version.TryParse(Cli.Version.Split('-')[0], out var tool))
        {
            return;
        }

        if (!System.Version.TryParse(folders.Version.Split('-')[0], out var installed))
        {
            output.WriteLine($"The installed OADM's version is not known here; plugins built with this SDK need OADM {tool.ToString(3)} or later.");
            return;
        }

        if (installed < tool)
        {
            throw new ToolException($"The installed OADM is {folders.Version}; plugins built with this SDK need OADM {tool.ToString(3)} or later. Update OADM first. Nothing was changed.");
        }
    }

    /// <summary>Stops the installed service and waits until it stopped.</summary>
    public static void StopService(ServiceDefinition service, TextWriter output)
    {
        output.WriteLine($"Stopping {service.Name}...");
        if (OperatingSystem.IsWindows())
        {
            Cli.Capture("sc.exe", ["stop", service.Name]);
            for (var i = 0; i < 120 && !Cli.Capture("sc.exe", ["query", service.Name]).Output.Contains("STOPPED", StringComparison.Ordinal); i++)
            {
                Thread.Sleep(500);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            Cli.Capture("launchctl", ["bootout", "system/" + LaunchdLabel]);
        }
        else
        {
            Expect(Cli.Capture("systemctl", ["stop", service.Name]), "systemctl stop");
        }
    }

    public static void StartService(ServiceDefinition service, TextWriter output)
    {
        output.WriteLine($"Starting {service.Name}...");
        if (OperatingSystem.IsWindows())
        {
            Expect(Cli.Capture("sc.exe", ["start", service.Name]), "sc start");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Expect(Cli.Capture("launchctl", ["bootstrap", "system", LaunchdPlist]), "launchctl bootstrap");
        }
        else
        {
            Expect(Cli.Capture("systemctl", ["start", service.Name]), "systemctl start");
        }
    }

    /// <summary>Removes the "downloaded from the internet" mark from every file (Windows: Zone.Identifier, macOS: quarantine).</summary>
    public static void Unblock(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file + ":Zone.Identifier");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // no mark
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            Cli.Capture("xattr", ["-dr", "com.apple.quarantine", folder]);
        }
    }

    /// <summary>Unix: owner root and no group or world write, as the service's folder check wants it.</summary>
    public static void SecureForService(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            Cli.Capture("chown", ["-R", "0:0", folder]);
            Cli.Capture("chmod", ["-R", "go-w", folder]);
        }
    }

    /// <summary>The <c>BINARY_PATH_NAME</c> of <c>sc qc</c>.</summary>
    public static string? ParseScQc(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BINARY_PATH_NAME", StringComparison.Ordinal)) is { } line
            && line.IndexOf(':', StringComparison.Ordinal) is var colon and > 0
            ? line[(colon + 1)..].Trim()
            : null;

    /// <summary>A Windows command line split like the C runtime does for quotes (no escapes in OADM's service line).</summary>
    public static List<string> SplitWindowsCommandLine(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>The argv of <c>systemctl show -p ExecStart --value</c> (<c>{ path=... ; argv[]=/opt/x --a ; ... }</c>).</summary>
    public static List<string> ParseSystemdExecStart(string text)
    {
        var start = text.IndexOf("argv[]=", StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var rest = text[(start + 7)..];
        var end = rest.IndexOf(" ;", StringComparison.Ordinal);
        return SplitWindowsCommandLine(end >= 0 ? rest[..end] : rest);
    }

    /// <summary><c>systemctl show -p Environment --value</c>: "A=1 B=2".</summary>
    public static Dictionary<string, string> ParseSystemdEnvironment(string text)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in SplitWindowsCommandLine(text.Trim()))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                env[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        return env;
    }

    private static void Expect((int Code, string Output) result, string what)
    {
        if (result.Code != 0)
        {
            throw new ToolException($"{what} failed: {result.Output.Trim()}");
        }
    }
}
