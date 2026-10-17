using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Oadm.Tools;

/// <summary>
/// <c>oadm dev start | stop | status | logs | client</c>: a portable OADM server on this computer for plugin development,
/// with its own data folder and the workspace's plugin folders (<c>--Oadm:PluginDir</c>), restarted after every build.
/// </summary>
internal static class DevCommands
{
    public const int DefaultPort = 5090;

    /// <summary>The state file of a running development server.</summary>
    internal sealed record State(int SupervisorPid, int ServerPid, int Port, string ServerFolder, DateTime StartedUtc);

    public static async Task<int> RunAsync(string command, Options options, TextWriter output, CancellationToken ct)
    {
        var workspace = Workspace.Find(Directory.GetCurrentDirectory());
        switch (command)
        {
            case "start":
                return await StartAsync(workspace, options, output, ct).ConfigureAwait(false);
            case "stop":
                options.RejectUnknown();
                return Stop(workspace, output);
            case "status":
                options.RejectUnknown();
                return Status(workspace, output);
            case "logs":
                return Logs(workspace, options, output);
            case "client":
                return await ClientAsync(workspace, options, output, ct).ConfigureAwait(false);
            default:
                throw new UsageException($"unknown command 'dev {command}' (start, stop, status, logs or client)");
        }
    }

    /// <summary>The server's arguments: its own data folder, the port on this computer only, every plugin folder.</summary>
    public static List<string> ServerArguments(Workspace workspace, int port, IEnumerable<PluginFolder> plugins) =>
    [
        $"--Oadm:DataDir={workspace.ServerData}",
        $"--Oadm:ListenUrl=https://localhost:{port}",
        $"--Oadm:PluginDir={string.Join(';', plugins.Select(p => p.Path))}",
    ];

    private static async Task<int> StartAsync(Workspace workspace, Options options, TextWriter output, CancellationToken ct)
    {
        var port = options.Int("--port", DefaultPort, 1, 65535);
        var from = options.Get("--from");
        var version = options.Get("--server-version") ?? Cli.Version;
        options.RejectUnknown();

        if (ReadState(workspace) is { } running && IsAlive(running))
        {
            throw new ToolException($"The development server of {workspace.Root} runs already (https://localhost:{running.Port}). Stop it with: oadm dev stop");
        }

        if (!IsPortFree(port))
        {
            throw new ToolException($"Port {port} is in use. Choose another one with --port.");
        }

        var plugins = workspace.RequirePlugins();
        using var http = new HttpClient();
        var folder = await new PortableOadm(PortableOadm.DefaultCache, http, output).ResolveAsync("Server", version, from, ct).ConfigureAwait(false);
        var exe = Path.Combine(folder, Cli.Exe("Oadm.Server"));
        Directory.CreateDirectory(workspace.ServerData);

        output.WriteLine($"OADM development server https://localhost:{port}");
        output.WriteLine($"  plugins: {string.Join(", ", plugins.Select(p => p.Id))}");
        output.WriteLine($"  data:    {workspace.ServerData}");
        output.WriteLine("  The first login creates the administrator. Open the client: oadm dev client");
        output.WriteLine("  A new build restarts the server. Ctrl+C stops it.");
        output.WriteLine();

        using var changes = new PluginChanges(workspace.PluginsFolder);
        while (!ct.IsCancellationRequested)
        {
            changes.Started();
            using var server = Cli.Start(exe, ServerArguments(workspace, port, workspace.Plugins()));
            WriteState(workspace, new State(Environment.ProcessId, server.Id, port, folder, DateTime.UtcNow));
            var restart = false;
            try
            {
                while (!server.HasExited)
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    if (changes.SettledChange())
                    {
                        output.WriteLine("[oadm] The plugin was built again: restarting the server.");
                        restart = true;
                        server.Kill(entireProcessTree: true);
                        await server.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C reaches the server too (same console); give it time to stop by itself.
                if (!server.WaitForExit(TimeSpan.FromSeconds(15)))
                {
                    server.Kill(entireProcessTree: true);
                }
            }

            if (!restart)
            {
                File.Delete(workspace.StateFile);
                return ct.IsCancellationRequested ? 0 : server.ExitCode;
            }
        }

        File.Delete(workspace.StateFile);
        return 0;
    }

    private static int Stop(Workspace workspace, TextWriter output)
    {
        if (ReadState(workspace) is not { } state)
        {
            output.WriteLine("No development server runs for " + workspace.Root + ".");
            return 0;
        }

        foreach (var pid in new[] { state.SupervisorPid, state.ServerPid })
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!IsOurs(process, state))
                {
                    continue; // the state file is old (a crash, a restart): this number belongs to another program now
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            catch (ArgumentException)
            {
                // ended already
            }
            catch (InvalidOperationException)
            {
                // ended already
            }
        }

        File.Delete(workspace.StateFile);
        output.WriteLine("The development server stopped.");
        return 0;
    }

    private static int Status(Workspace workspace, TextWriter output)
    {
        var plugins = workspace.Plugins();
        output.WriteLine($"Workspace {workspace.Root}");
        output.WriteLine(plugins.Count == 0 ? "  plugins: none built yet (dotnet build)" : $"  plugins: {string.Join(", ", plugins.Select(p => $"{p.Id} {p.Version}"))}");
        if (ReadState(workspace) is { } state && IsAlive(state))
        {
            output.WriteLine($"  server:  runs at https://localhost:{state.Port} since {state.StartedUtc.ToLocalTime():HH:mm} (process {state.ServerPid})");
            return 0;
        }

        output.WriteLine("  server:  not running (oadm dev start)");
        return 0;
    }

    private static int Logs(Workspace workspace, Options options, TextWriter output)
    {
        var lines = options.Int("--lines", 50, 1, 100_000);
        options.RejectUnknown();
        var logs = Path.Combine(workspace.ServerData, "logs");
        var newest = Directory.Exists(logs)
            ? new DirectoryInfo(logs).GetFiles("oadm-server-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (newest is null)
        {
            output.WriteLine("The development server has no log yet (oadm dev start).");
            return 0;
        }

        using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            tail.Enqueue(line);
            if (tail.Count > lines)
            {
                tail.Dequeue();
            }
        }

        output.WriteLine(newest.FullName);
        foreach (var line in tail)
        {
            output.WriteLine(line);
        }

        return 0;
    }

    private static async Task<int> ClientAsync(Workspace workspace, Options options, TextWriter output, CancellationToken ct)
    {
        var from = options.Get("--from");
        var version = options.Get("--client-version") ?? Cli.Version;
        var port = options.Get("--port") is null ? ReadState(workspace)?.Port ?? DefaultPort : options.Int("--port", DefaultPort, 1, 65535);
        options.RejectUnknown();

        var plugins = workspace.RequirePlugins();
        using var http = new HttpClient();
        var folder = await new PortableOadm(PortableOadm.DefaultCache, http, output).ResolveAsync("Client", version, from, ct).ConfigureAwait(false);
        var args = new List<string> { "--data", workspace.ClientData, "--server", $"localhost:{port}" };
        foreach (var plugin in plugins)
        {
            args.Add("--plugin-dir");
            args.Add(plugin.Path);
        }

        Directory.CreateDirectory(workspace.ClientData);
        using var client = Cli.Start(Path.Combine(folder, Cli.Exe("Oadm.Client")), args);
        output.WriteLine($"OADM client started for https://localhost:{port} with {string.Join(", ", plugins.Select(x => x.Id))}.");
        return 0;
    }

    internal static State? ReadState(Workspace workspace)
    {
        try
        {
            return File.Exists(workspace.StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(workspace.StateFile)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteState(Workspace workspace, State state)
    {
        Directory.CreateDirectory(workspace.OadmFolder);
        File.WriteAllText(workspace.StateFile, JsonSerializer.Serialize(state));
    }

    /// <summary>The development server or its oadm tool, started no later than the state file says (not a reused process number).</summary>
    internal static bool IsOurs(Process process, State state)
    {
        try
        {
            var name = process.ProcessName;
            var known = name.Equals("Oadm.Server", StringComparison.OrdinalIgnoreCase) || name.Equals("oadm", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.Equals("Oadm.Tools", StringComparison.OrdinalIgnoreCase);
            return known && process.StartTime.ToUniversalTime() <= state.StartedUtc.AddSeconds(5);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsAlive(State state)
    {
        try
        {
            using var process = Process.GetProcessById(state.SupervisorPid);
            return !process.HasExited && IsOurs(process, state);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds of the plugin the server must load: after a change in the plugin folders has rested for 2.5 s (a build writes
    /// several files), the files the server loads (all but the client part's <c>*.Client.*</c>) are compared by name, size
    /// and time with those of the last start.
    /// </summary>
    internal sealed class PluginChanges : IDisposable
    {
        private readonly string _folder;
        private readonly FileSystemWatcher _watcher;
        private long _lastChangeTicks;
        private string _started = "";

        public PluginChanges(string folder)
        {
            _folder = folder;
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += (_, _) => Touch();
            _watcher.Created += (_, _) => Touch();
            _watcher.Deleted += (_, _) => Touch();
            _watcher.Renamed += (_, _) => Touch();
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>The server starts with the files as they are now.</summary>
        public void Started() => _started = Fingerprint(_folder);

        /// <summary>True once after the server's files changed and the change rested.</summary>
        public bool SettledChange()
        {
            var last = Interlocked.Read(ref _lastChangeTicks);
            if (last == 0 || DateTime.UtcNow.Ticks - last < TimeSpan.FromSeconds(2.5).Ticks
                || Interlocked.CompareExchange(ref _lastChangeTicks, 0, last) != last)
            {
                return false;
            }

            return Fingerprint(_folder) != _started;
        }

        public void Dispose() => _watcher.Dispose();

        /// <summary>Name, size and time of every file the server loads (not <c>*.Client.*</c>).</summary>
        internal static string Fingerprint(string folder) =>
            !Directory.Exists(folder)
                ? ""
                : string.Join('\n', new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => !f.Name.Contains(".Client.", StringComparison.OrdinalIgnoreCase))
                    .Select(f => $"{Path.GetRelativePath(folder, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")
                    .Order(StringComparer.Ordinal));

        private void Touch() => Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
    }
}
