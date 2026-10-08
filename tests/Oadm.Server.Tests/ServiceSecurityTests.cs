using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Sdk.Network;
using Oadm.Server.Hosting;

namespace Oadm.Server.Tests;

/// <summary>
/// Installed service hardening: Windows firewall rules through netsh, admin-only data / extraction / plugin folders, no
/// development plugin folder. Everything runs against fakes; no firewall or ACL of this machine is changed.
/// </summary>
public sealed class ServiceSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-service-security", Guid.NewGuid().ToString("N"));

    public ServiceSecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The server's log file may still be closing.
        }
    }

    // ---------------------------------------------------------------- firewall

    [Fact]
    public async Task NetshOpenReplacesTheRuleForTheServerExeOnDomainAndPrivate()
    {
        var runner = new FakeRunner();
        var firewall = new NetshFirewallRules(runner, @"C:\Program Files\OADM\Server\Oadm.Server.exe");

        await firewall.OpenAsync(FirewallRule.ForService("NTP", FirewallProtocol.Udp, 123), CancellationToken.None);

        Assert.Equal(
            [
                "netsh advfirewall|firewall|delete|rule|name=OADM Server (NTP, UDP 123)",
                "netsh advfirewall|firewall|add|rule|name=OADM Server (NTP, UDP 123)|dir=in|action=allow|protocol=UDP|localport=123|" +
                @"program=C:\Program Files\OADM\Server\Oadm.Server.exe|profile=domain,private|enable=yes",
            ],
            runner.Calls);
    }

    [Fact]
    public async Task NetshCloseIgnoresAMissingRuleButOpenReportsFailures()
    {
        var runner = new FakeRunner { Result = new CommandResult(1, "No rules match the specified criteria.") };
        var firewall = new NetshFirewallRules(runner, "Oadm.Server.exe");
        var rule = FirewallRule.ForService("DHCP", FirewallProtocol.Udp, 67);

        await firewall.CloseAsync(rule, CancellationToken.None);
        Assert.Equal(["netsh advfirewall|firewall|delete|rule|name=OADM Server (DHCP, UDP 67)"], runner.Calls);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => firewall.OpenAsync(rule, CancellationToken.None));
        Assert.Contains("exit code 1", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("all", 123)]
    [InlineData("bad \" name", 123)]
    [InlineData("OADM", 0)]
    [InlineData("OADM", 70000)]
    public async Task NetshRefusesRulesThatCouldHitOtherRules(string name, int port)
    {
        var runner = new FakeRunner();
        var firewall = new NetshFirewallRules(runner, "Oadm.Server.exe");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => firewall.CloseAsync(new FirewallRule(name, FirewallProtocol.Udp, port), CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void FirewallIsOnlyManagedByTheWindowsService()
    {
        using (var console = BuildServer(serviceMode: false))
        {
            Assert.Null(console.Services.GetService<IFirewallRules>());
            Assert.Null(console.Services.GetService<FolderGuard>());
            Assert.False(console.Services.GetRequiredService<ServerRunMode>().IsService);
        }

        using var service = BuildServer(serviceMode: true);
        Assert.NotNull(service.Services.GetService<FolderGuard>());
        Assert.Equal(OperatingSystem.IsWindows(), service.Services.GetService<IFirewallRules>() is NetshFirewallRules);
    }

    // ---------------------------------------------------------------- folders

    [Fact]
    public async Task SecureFolderIsNotTouched()
    {
        var permissions = new FakePermissions();
        var check = await Guard(permissions).EnsureAsync(_dir, readableByUsers: false, CancellationToken.None);

        Assert.Equal(new FolderCheck(_dir, FolderVerdict.Secure), check);
        Assert.Empty(permissions.Fixed);
    }

    [Fact]
    public async Task OpenFolderIsFixed()
    {
        var permissions = new FakePermissions();
        permissions.Problems[_dir] = "BUILTIN\\Users can write it";

        var check = await Guard(permissions).EnsureAsync(_dir, readableByUsers: true, CancellationToken.None);

        Assert.Equal(FolderVerdict.Fixed, check.Verdict);
        Assert.Equal("BUILTIN\\Users can write it", check.Problem);
        Assert.Equal([_dir + " readable"], permissions.Fixed);
    }

    [Fact]
    public async Task FolderThatCannotBeFixedIsInsecure()
    {
        var permissions = new FakePermissions { FixError = new UnauthorizedAccessException("Access denied") };
        permissions.Problems[_dir] = "/x is owned by uid 1000";

        var check = await Guard(permissions).EnsureAsync(_dir, readableByUsers: false, CancellationToken.None);

        Assert.Equal(new FolderCheck(_dir, FolderVerdict.Insecure, "/x is owned by uid 1000", "Access denied"), check);
    }

    [Fact]
    public async Task FolderStillOpenAfterTheFixIsInsecure()
    {
        var permissions = new FakePermissions { FixClears = false };
        permissions.Problems[_dir] = "Everyone can write it";

        var check = await Guard(permissions).EnsureAsync(_dir, readableByUsers: false, CancellationToken.None);

        Assert.Equal(FolderVerdict.Insecure, check.Verdict);
    }

    [Fact]
    public async Task MissingFolderIsSecure()
    {
        var check = await Guard(new FakePermissions()).EnsureAsync(Path.Combine(_dir, "nope"), readableByUsers: false, CancellationToken.None);
        Assert.Equal(FolderVerdict.Secure, check.Verdict);
    }

    [Fact]
    public async Task InsecureDataFolderStopsTheServerStart()
    {
        var permissions = new FakePermissions { FixError = new InvalidOperationException("chown failed") };
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        permissions.Problems[data] = data + " is writable by group or others";
        await using var app = BuildServer(serviceMode: true, data, permissions);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => OadmServerHost.StartAsync(app));

        Assert.Contains("does not start", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(data, OadmPaths.DatabaseFileName))); // refused before the database opened
    }

    [Fact]
    public void ExtractionFolderIsCheckedUnlessInsideTheDataFolder()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(_dir, "data", "runtime"));
            Assert.Equal([Path.Combine(_dir, "data")], ServiceHosting.ServerFolders(Path.Combine(_dir, "data")));

            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(_dir, "cache"));
            Assert.Equal([Path.Combine(_dir, "data"), Path.Combine(_dir, "cache")], ServiceHosting.ServerFolders(Path.Combine(_dir, "data")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", previous);
        }
    }

    [Fact]
    public async Task InsecurePluginFolderIsSkippedWithAnError()
    {
        var plugin = Path.Combine(_dir, "plugins", "oadm.evil");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, PluginLoader.ManifestFileName), """{ "id": "oadm.evil", "version": "1.0.0", "minSdkVersion": "0.1.0" }""");
        var permissions = new FakePermissions { FixError = new UnauthorizedAccessException("Access denied") };
        permissions.Problems[plugin] = "Everyone can write it";
        var registry = new PluginRegistry();
        var refused = await Guard(permissions).CheckPluginFoldersAsync([Path.Combine(_dir, "plugins"), Path.Combine(_dir, "missing")], CancellationToken.None);
        Assert.Equal([Path.GetFullPath(plugin)], refused.Keys);
        var loader = new PluginLoader(registry) { FolderCheck = folder => refused.GetValueOrDefault(Path.GetFullPath(folder)) };

        loader.LoadFromRoots([Path.Combine(_dir, "plugins")]);

        Assert.Empty(loader.Packages);
        var error = Assert.Single(registry.Errors);
        Assert.StartsWith("Skipped: the plugin folder can be changed by users other than administrators (Everyone can write it)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceNeverUsesTheDevelopmentPluginFolder()
    {
        var paths = new OadmPaths(_dir);
        var development = PluginPaths.Development();
        Assert.NotNull(development); // the tests run inside the repository

        Assert.Contains(development, OadmServerHost.DefaultPluginRoots(paths));
        Assert.DoesNotContain(development, OadmServerHost.DefaultPluginRoots(paths, includeDevelopment: false));
    }

    [Fact]
    public async Task UnixCheckFindsEntriesNotOwnedByRootOrWritableByOthers()
    {
        var runner = new FakeRunner();
        var permissions = new UnixFolderPermissions(runner);

        Assert.Null(await permissions.FindProblemAsync("/var/lib/oadm", CancellationToken.None));
        Assert.Equal("find /var/lib/oadm|!|-type|l|(|!|-user|0|-o|-perm|-0020|-o|-perm|-0002|)|-print", runner.Calls[0]);

        runner.Result = new CommandResult(0, "/var/lib/oadm/plugins/x\n/var/lib/oadm/plugins/x/a.dll\n");
        Assert.Equal(
            "/var/lib/oadm/plugins/x and 1 more are not owned by root or writable by group or others",
            await permissions.FindProblemAsync("/var/lib/oadm", CancellationToken.None));

        runner.Result = new CommandResult(1, "find: permission denied");
        Assert.Equal("cannot check /var/lib/oadm: find: permission denied", await permissions.FindProblemAsync("/var/lib/oadm", CancellationToken.None));
    }

    [Fact]
    public async Task UnixFixMakesRootTheOwnerAndRemovesGroupAndOtherWrite()
    {
        var runner = new FakeRunner();
        await new UnixFolderPermissions(runner).FixAsync("/opt/oadm/server/plugins", readableByUsers: true, CancellationToken.None);
        Assert.Equal(["chown -R|0:0|/opt/oadm/server/plugins", "chmod -R|go-w|/opt/oadm/server/plugins"], runner.Calls);

        runner.Result = new CommandResult(1, "Operation not permitted");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnixFolderPermissions(runner).FixAsync("/x", false, CancellationToken.None));
    }

    [Fact]
    public void WindowsRulesAllowOnlySystemAdministratorsTrustedInstallerAndCreatorOwnerToWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);

        Assert.True(WindowsFolderPermissions.GrantsWriteToOthers(users, FileSystemRights.Modify));
        Assert.True(WindowsFolderPermissions.GrantsWriteToOthers(users, FileSystemRights.AppendData)); // ProgramData's "create folders"
        Assert.True(WindowsFolderPermissions.GrantsWriteToOthers(everyone, (FileSystemRights)0x10000000)); // GENERIC_ALL
        Assert.True(WindowsFolderPermissions.GrantsWriteToOthers(users, FileSystemRights.ChangePermissions));
        Assert.False(WindowsFolderPermissions.GrantsWriteToOthers(users, FileSystemRights.ReadAndExecute));
        Assert.False(WindowsFolderPermissions.GrantsWriteToOthers(admins, FileSystemRights.FullControl));
        Assert.False(WindowsFolderPermissions.GrantsWriteToOthers(system, FileSystemRights.FullControl));
        Assert.False(WindowsFolderPermissions.GrantsWriteToOthers(creatorOwner, (FileSystemRights)0x10000000));
        Assert.True(WindowsFolderPermissions.IsTrusted(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")));
    }

    [Fact]
    public async Task WindowsCheckReportsAUserWritableTempFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A folder in the user's temp folder: owned by this user and writable by them (read only, nothing is changed).
        var problem = await new WindowsFolderPermissions().FindProblemAsync(_dir, CancellationToken.None);

        Assert.NotNull(problem);
    }

    private static FolderGuard Guard(IFolderPermissions permissions) => new(permissions, NullLogger<FolderGuard>.Instance);

    private WebApplication BuildServer(bool serviceMode, string? dataDirectory = null, IFolderPermissions? permissions = null) =>
        OadmServerHost.Build([], new OadmServerHostOptions
        {
            DataDirectory = dataDirectory ?? Path.Combine(_dir, "data-" + Guid.NewGuid().ToString("N")),
            PluginRoots = [],
            LogToConsole = false,
            ServiceMode = serviceMode,
            ConfigureBuilder = b => b.WebHost.UseTestServer(),
            ConfigureServices = services =>
            {
                // No plugin is started here, so the registered netsh firewall is never called.
                if (permissions is not null)
                {
                    services.AddSingleton(permissions);
                }
            },
        });

    private sealed class FakeRunner : ICommandRunner
    {
        public ConcurrentQueue<string> Queue { get; } = new();

        public List<string> Calls => [.. Queue];

        public CommandResult Result { get; set; } = new(0, string.Empty);

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Queue.Enqueue(fileName + " " + string.Join('|', arguments));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePermissions : IFolderPermissions
    {
        public Dictionary<string, string> Problems { get; } = new(StringComparer.Ordinal);

        public List<string> Fixed { get; } = [];

        public Exception? FixError { get; set; }

        public bool FixClears { get; set; } = true;

        public Task<string?> FindProblemAsync(string path, CancellationToken ct) => Task.FromResult(Problems.GetValueOrDefault(path));

        public Task FixAsync(string path, bool readableByUsers, CancellationToken ct)
        {
            if (FixError is not null)
            {
                return Task.FromException(FixError);
            }

            Fixed.Add(path + (readableByUsers ? " readable" : string.Empty));
            if (FixClears)
            {
                Problems.Remove(path);
            }

            return Task.CompletedTask;
        }
    }
}
