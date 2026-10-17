using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Oadm.Tools.Tests;

/// <summary>The oadm tool without an installed OADM: arguments, service definitions, folders, the portable download check.</summary>
public sealed class ToolTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-tool-" + Guid.NewGuid().ToString("N"));

    public ToolTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_service_line_of_sc_qc_gives_the_program_and_its_data_folder()
    {
        const string qc = """
            [SC] QueryServiceConfig SUCCESS

            SERVICE_NAME: OadmServer
                    TYPE               : 10  WIN32_OWN_PROCESS
                    START_TYPE         : 2   AUTO_START
                    BINARY_PATH_NAME   : "C:\Program Files\OADM\Server\Oadm.Server.exe" --Oadm:DataDir="C:\ProgramData\OADM"
                    DISPLAY_NAME       : OADM Server
            """;

        var line = InstalledOadm.ParseScQc(qc);
        var argv = InstalledOadm.SplitWindowsCommandLine(line!);

        Assert.Equal([@"C:\Program Files\OADM\Server\Oadm.Server.exe", @"--Oadm:DataDir=C:\ProgramData\OADM"], argv);
        Assert.Equal(@"C:\ProgramData\OADM", InstalledOadm.DataFolder(argv[1..], new Dictionary<string, string>(), null));
    }

    [Fact]
    public void Systemd_gives_the_program_and_the_environment()
    {
        var argv = InstalledOadm.ParseSystemdExecStart("{ path=/opt/oadm/server/Oadm.Server ; argv[]=/opt/oadm/server/Oadm.Server ; ignore_errors=no ; start_time=[n/a] ; pid=0 ; code=(null) }");
        var env = InstalledOadm.ParseSystemdEnvironment("OADM_DATA_DIR=/var/lib/oadm DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/oadm\n");

        Assert.Equal(["/opt/oadm/server/Oadm.Server"], argv);
        Assert.Equal("/var/cache/oadm", env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]);
        Assert.Equal("/var/lib/oadm", InstalledOadm.DataFolder(argv[1..], env, null));
    }

    [Fact]
    public void The_data_folder_follows_the_servers_order_and_none_is_no_guess()
    {
        var settings = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(settings, """{ "Oadm": { "DataDir": "/from/settings" } }""");
        var env = new Dictionary<string, string> { ["OADM_DATA_DIR"] = "/from/env" };

        Assert.Equal("/from/args", InstalledOadm.DataFolder(["--Oadm:DataDir", "/from/args"], env, settings));
        Assert.Equal("/from/config", InstalledOadm.DataFolder([], new Dictionary<string, string>(env) { ["Oadm__DataDir"] = "/from/config" }, settings));
        Assert.Equal("/from/settings", InstalledOadm.DataFolder([], env, settings));
        Assert.Equal("/from/env", InstalledOadm.DataFolder([], env, null));
        Assert.Null(InstalledOadm.DataFolder([], new Dictionary<string, string>(), null));
    }

    [Fact]
    public void An_installed_OADM_older_than_the_tool_is_refused()
    {
        var old = new Folders("server", "1.0.0", "app", "data", "data/plugins", []);
        var unknown = old with { Version = "" };
        using var output = new StringWriter();

        if (Version.TryParse(Cli.Version.Split('-')[0], out var tool) && tool > new Version(1, 0, 0))
        {
            Assert.Throws<ToolException>(() => InstalledOadm.RequireCurrent(old, output));
        }

        InstalledOadm.RequireCurrent(old with { Version = "999.0.0" }, output);
        InstalledOadm.RequireCurrent(unknown, output); // Linux, macOS: a warning only
    }

    [Fact]
    public void The_workspace_is_the_folder_with_oadm_and_lists_its_built_plugins()
    {
        var plugin = Path.Combine(_folder, ".oadm", "plugins", "acme.door");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), """{ "id": "acme.door", "version": "1.2.0" }""");
        Directory.CreateDirectory(Path.Combine(_folder, ".oadm", "plugins", "not-a-plugin"));
        var sub = Directory.CreateDirectory(Path.Combine(_folder, "Door", "bin")).FullName;

        var workspace = Workspace.Find(sub);

        Assert.Equal(Path.GetFullPath(_folder), workspace.Root);
        Assert.Equal([new PluginFolder("acme.door", "1.2.0", plugin)], workspace.Plugins());
        Assert.Throws<ToolException>(() => WorkspaceWithoutPlugins().RequirePlugins());
    }

    [Fact]
    public void The_development_server_gets_its_own_data_this_computer_only_and_every_plugin()
    {
        var workspace = WorkspaceWithoutPlugins();
        var args = DevCommands.ServerArguments(workspace, 5090, [new PluginFolder("a", "1", "/p/a"), new PluginFolder("b", "1", "/p/b")]);

        Assert.Equal([$"--Oadm:DataDir={workspace.ServerData}", "--Oadm:ListenUrl=https://localhost:5090", "--Oadm:PluginDir=/p/a;/p/b"], args);
    }

    [Fact]
    public void Only_the_files_the_server_loads_count_as_a_new_build()
    {
        var plugin = Directory.CreateDirectory(Path.Combine(_folder, "plugins", "acme.door")).FullName;
        File.WriteAllText(Path.Combine(plugin, "Door.Server.dll"), "1");
        var before = DevCommands.PluginChanges.Fingerprint(Path.Combine(_folder, "plugins"));

        File.WriteAllText(Path.Combine(plugin, "Door.Client.dll"), "client only");
        Assert.Equal(before, DevCommands.PluginChanges.Fingerprint(Path.Combine(_folder, "plugins")));

        File.WriteAllText(Path.Combine(plugin, "Door.Server.dll"), "22");
        Assert.NotEqual(before, DevCommands.PluginChanges.Fingerprint(Path.Combine(_folder, "plugins")));
    }

    [Fact]
    public void A_download_must_match_the_release_checksum()
    {
        var zip = Path.Combine(_folder, "OADM-Server-3.5.0-win-x64.zip");
        File.WriteAllText(zip, "zip bytes");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("zip bytes")));

        PortableOadm.Verify(zip, "OADM-Server-3.5.0-win-x64.zip", $"{hash}  OADM-Server-3.5.0-win-x64.zip\nabc  other.zip\n");
        Assert.Throws<ToolException>(() => PortableOadm.Verify(zip, "OADM-Server-3.5.0-win-x64.zip", $"{new string('0', 64)}  OADM-Server-3.5.0-win-x64.zip\n"));
        Assert.Throws<ToolException>(() => PortableOadm.Verify(zip, "OADM-Server-3.5.0-win-x64.zip", "abc  other.zip\n"));
    }

    [Fact]
    public async Task A_portable_OADM_comes_from_a_folder_or_a_ZIP_with_its_top_folder()
    {
        var publish = Directory.CreateDirectory(Path.Combine(_folder, "pub", "OADM-Server-3.5.0-win-x64")).FullName;
        File.WriteAllText(Path.Combine(publish, Cli.Exe("Oadm.Server")), "exe");
        var zip = Path.Combine(_folder, "server.zip");
        ZipFile.CreateFromDirectory(Path.Combine(_folder, "pub"), zip);
        using var http = new HttpClient();
        var portable = new PortableOadm(Path.Combine(_folder, "cache"), http, TextWriter.Null);

        Assert.Equal(publish, await portable.ResolveAsync("Server", "3.5.0", Path.Combine(_folder, "pub"), CancellationToken.None));
        var extracted = await portable.ResolveAsync("Server", "3.5.0", zip, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(extracted, Cli.Exe("Oadm.Server"))));
        await Assert.ThrowsAsync<ToolException>(() => portable.ResolveAsync("Server", "1.0.0-dev", null, CancellationToken.None));
    }

    [Fact]
    public void Options_refuse_what_a_command_does_not_know()
    {
        var options = Options.Parse(["--port", "5091", "--fake", "--plugin", "a", "--plugin=b", "--typo", "x"], "--fake");

        Assert.Equal(5091, options.Int("--port", 1, 1, 65535));
        Assert.True(options.Has("--fake"));
        Assert.Equal(["a", "b"], options.All("--plugin"));
        Assert.Throws<UsageException>(options.RejectUnknown);
        Assert.Throws<UsageException>(() => Options.Parse(["--port"]));
        Assert.Throws<UsageException>(() => Options.Parse(["--port", "x"]).Int("--port", 1, 1, 10));
    }

    [Fact]
    public async Task Commands_answer_usage_errors_with_exit_code_2()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await Commands.RunAsync(["help"], output, error, CancellationToken.None));
        Assert.Contains("oadm dev start", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Commands.RunAsync(["bogus"], output, error, CancellationToken.None));
        Assert.Equal(2, await Commands.RunAsync(["dev"], output, error, CancellationToken.None));
        Assert.Contains("unknown command 'bogus'", error.ToString(), StringComparison.Ordinal);
    }

    private Workspace WorkspaceWithoutPlugins()
    {
        var root = Directory.CreateDirectory(Path.Combine(_folder, "ws-" + Guid.NewGuid().ToString("N")[..6], ".oadm")).Parent!.FullName;
        return Workspace.Find(root);
    }
}
