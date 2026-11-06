using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Sdk.Network;
using Oadm.Server.AddOns;
using Oadm.Server.Hosting;
using Oadm.Server.Tests.AddOns;
using Oadm.Tests.Shared;

namespace Oadm.Server.Tests;

/// <summary>
/// Installed service hardening: Windows firewall rules through netsh, admin-only data / extraction / plugin folders, no
/// development plugin folder. Everything runs against fakes; no firewall or ACL of this machine is changed.
/// </summary>
[Collection(Support.ProcessEnvironment.Name)] // sets DOTNET_BUNDLE_EXTRACT_BASE_DIR
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
    public async Task NetshOpensAPortRangeAsOneRule()
    {
        var runner = new FakeRunner();
        var firewall = new NetshFirewallRules(runner, "Oadm.Server.exe");

        await firewall.OpenAsync(FirewallRule.ForServiceRange("Load test", FirewallProtocol.Udp, 50000, 50799), CancellationToken.None);

        Assert.Equal(
            [
                "netsh advfirewall|firewall|delete|rule|name=OADM Server (Load test, UDP 50000-50799)",
                "netsh advfirewall|firewall|add|rule|name=OADM Server (Load test, UDP 50000-50799)|dir=in|action=allow|protocol=UDP|localport=50000-50799|" +
                "program=Oadm.Server.exe|profile=domain,private|enable=yes",
            ],
            runner.Calls);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => firewall.OpenAsync(
            new FirewallRule("OADM", FirewallProtocol.Udp, 50000) { LastPort = 70000 }, CancellationToken.None));
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

    private static string PlantPlugin(string data, string id = "oadm.planted")
    {
        var plugin = Path.Combine(PluginPaths.Installed(data), id);
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, PluginLoader.ManifestFileName), $$"""{ "id": "{{id}}", "version": "1.0.0", "minSdkVersion": "0.1.0" }""");
        return plugin;
    }

    private static bool Knows(WebApplication app, string packageId) =>
        app.Services.GetRequiredService<PluginLoader>().Packages.Any(p => p.Manifest.Id == packageId)
        || app.Services.GetRequiredService<PluginRegistry>().Errors.Any(e => e.Source.Contains(packageId, StringComparison.Ordinal));

    [Fact]
    public async Task InstalledPluginsOfADataFolderThatWasLooseItselfAreMovedAsideAndNotLoaded()
    {
        var data = Path.Combine(_dir, "data");
        PlantPlugin(data); // put there while everybody could write the data folder
        var permissions = new FakePermissions();
        permissions.Problems[data] = "BUILTIN\\Users can write it";
        await using var app = BuildServer(serviceMode: true, data, permissions, pluginRoots: [PluginPaths.Installed(data)]);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.Equal([data], permissions.Fixed);
            Assert.False(Knows(app, "oadm.planted"));
            Assert.False(Directory.Exists(PluginPaths.Installed(data)));
            var aside = Assert.Single(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));
            Assert.True(File.Exists(Path.Combine(aside, "oadm.planted", PluginLoader.ManifestFileName))); // kept for the administrator
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task InstalledPluginsOfASecureDataFolderStayAndAreLoaded()
    {
        var data = Path.Combine(_dir, "data");
        var plugin = PlantPlugin(data);
        await using var app = BuildServer(serviceMode: true, data, new FakePermissions(), pluginRoots: [PluginPaths.Installed(data)]);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.True(Knows(app, "oadm.planted")); // the loader took the folder (it holds no code: a package or its load error)
            Assert.True(Directory.Exists(plugin));
            Assert.Empty(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task ALooseFileElsewhereInTheDataFolderIsFixedAndChangesNothingForThePlugins()
    {
        var data = Path.Combine(_dir, "data");
        var plugin = PlantPlugin(data);
        var permissions = new FakePermissions();
        // A backup an administrator copied in with their own account, a log file: nothing a plugin loads from.
        permissions.Problems[Path.Combine(data, "backups", "monday.oadmbackup")] = "monday.oadmbackup is owned by PC\\anna";
        permissions.Problems[Path.Combine(data, "logs", "server-20261106.log")] = "server-20261106.log is owned by PC\\anna";
        await using var app = BuildServer(serviceMode: true, data, permissions, pluginRoots: [PluginPaths.Installed(data)]);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.Equal([data], permissions.Fixed); // fixed as before
            Assert.Empty(permissions.Problems);
            Assert.True(Knows(app, "oadm.planted"));
            Assert.True(Directory.Exists(plugin));
            Assert.Empty(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));
            Assert.False(File.Exists(Path.Combine(data, InstalledPluginsTrust.MarkerFileName)));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task OnlyTheLoosePluginFolderIsMovedAsideTheOthersAreLoaded()
    {
        var data = Path.Combine(_dir, "data");
        var loose = PlantPlugin(data, "acme.moved-in"); // installed with "sudo mv": still owned by the user
        var good = PlantPlugin(data, "acme.copied-in");
        var permissions = new FakePermissions();
        permissions.Problems[Path.Combine(loose, "Acme.MovedIn.Server.dll")] = "Acme.MovedIn.Server.dll is owned by uid 1000";
        await using var app = BuildServer(serviceMode: true, data, permissions, pluginRoots: [PluginPaths.Installed(data)]);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.Equal([data], permissions.Fixed);
            Assert.False(Knows(app, "acme.moved-in"));
            Assert.False(Directory.Exists(loose));
            var aside = Assert.Single(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));
            Assert.True(File.Exists(Path.Combine(aside, "acme.moved-in", PluginLoader.ManifestFileName))); // kept for the administrator
            Assert.True(Knows(app, "acme.copied-in"));
            Assert.True(Directory.Exists(good));
            Assert.False(File.Exists(Path.Combine(data, InstalledPluginsTrust.MarkerFileName)));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task APluginsFolderThatWasLooseItselfIsMovedAsideAsAWhole()
    {
        var data = Path.Combine(_dir, "data");
        PlantPlugin(data);
        var permissions = new FakePermissions();
        permissions.Problems[PluginPaths.Installed(data)] = "Everyone can write it"; // anyone could put a plugin folder there
        await using var app = BuildServer(serviceMode: true, data, permissions, pluginRoots: [PluginPaths.Installed(data)]);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.False(Knows(app, "oadm.planted"));
            Assert.False(Directory.Exists(PluginPaths.Installed(data)));
            var aside = Assert.Single(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));
            Assert.True(File.Exists(Path.Combine(aside, "oadm.planted", PluginLoader.ManifestFileName)));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task TheFolderCheckSaysWhatWasLooseAboutThePlugins()
    {
        var data = Path.Combine(_dir, "data");
        var plugins = PluginPaths.Installed(data);
        var first = PlantPlugin(data, "acme.first");
        PlantPlugin(data, "acme.second");
        var note = Path.Combine(plugins, "readme.txt");
        File.WriteAllText(note, "a file beside the plugin folders");
        var extract = Path.Combine(_dir, "runtime");
        Directory.CreateDirectory(extract);

        // Nothing loose: nothing fixed, nothing said.
        var permissions = new FakePermissions();
        Assert.Equal(ServerFoldersCheck.Untouched, await Guard(permissions).EnsureServerFoldersAsync([data, extract], plugins, CancellationToken.None));
        Assert.Empty(permissions.Fixed);

        // Loose elsewhere, in the data folder and in the extraction folder: fixed, and no plugin is named.
        permissions.Problems[Path.Combine(data, OadmPaths.DatabaseFileName)] = "owned by uid 1000";
        permissions.Problems[Path.Combine(extract, "lib.so")] = "owned by uid 1000";
        var elsewhere = await Guard(permissions).EnsureServerFoldersAsync([data, extract], plugins, CancellationToken.None);
        Assert.True(elsewhere.Fixed);
        Assert.False(elsewhere.Plugins.All);
        Assert.Empty(elsewhere.Plugins.Entries);
        Assert.Equal([data, extract], permissions.Fixed);

        // A plugin folder that holds something loose and a loose file beside the folders: exactly those two.
        permissions.Problems[Path.Combine(first, "sub", "a.dll")] = "owned by uid 1000";
        permissions.Problems[note] = "owned by uid 1000";
        var some = await Guard(permissions).EnsureServerFoldersAsync([data], plugins, CancellationToken.None);
        Assert.False(some.Plugins.All);
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(note)], some.Plugins.Entries);

        // The data folder itself, or the plugins folder itself: everything in it.
        permissions.Problems[data] = "Everyone can write it";
        Assert.True((await Guard(permissions).EnsureServerFoldersAsync([data], plugins, CancellationToken.None)).Plugins.All);
        permissions.Problems[plugins] = "Everyone can write it";
        Assert.True((await Guard(permissions).EnsureServerFoldersAsync([data], plugins, CancellationToken.None)).Plugins.All);

        // A plugins folder that is one plugin itself is loose as a whole.
        File.WriteAllText(Path.Combine(plugins, PluginLoader.ManifestFileName), "{}");
        permissions.Problems[Path.Combine(first, "a.dll")] = "owned by uid 1000";
        Assert.True((await Guard(permissions).EnsureServerFoldersAsync([data], plugins, CancellationToken.None)).Plugins.All);

        // Facts that cannot be read count as loose.
        File.Delete(Path.Combine(plugins, PluginLoader.ManifestFileName));
        permissions.Problems[Path.Combine(first, "a.dll")] = "owned by uid 1000";
        permissions.OwnError = new UnauthorizedAccessException("Access denied");
        Assert.True((await Guard(permissions).EnsureServerFoldersAsync([data], plugins, CancellationToken.None)).Plugins.All);
    }

    // ---------------------------------------------------------------- optional plugins (<datafolder>/addons)

    /// <summary>Text recognition put into its folder by hand, of the version <see cref="BuildServer"/> gives the server.</summary>
    private static string PlantAddOn(string data)
    {
        var folder = Path.Combine(AddOnFolders.Root(data), AddOnCatalog.TextRecognitionId);
        Directory.CreateDirectory(folder);
        using var zip = new ZipArchive(new MemoryStream(AddOnRig.Zip()), ZipArchiveMode.Read);
        zip.ExtractToDirectory(folder, overwriteFiles: true);
        return folder;
    }

    private static async Task<AddOnStatus> TextRecognitionAsync(WebApplication app) =>
        Assert.Single(await app.Services.GetRequiredService<AddOnInstaller>().ListAsync(CancellationToken.None));

    [Fact]
    public async Task ALooseFileElsewhereInTheDataFolderIsFixedAndChangesNothingForTheOptionalPlugins()
    {
        var data = Path.Combine(_dir, "data");
        var addOn = PlantAddOn(data);
        var permissions = new FakePermissions();
        // A log file and a backup an administrator copied in with their own account: nothing an optional plugin loads from.
        permissions.Problems[Path.Combine(data, "logs", "server-20261106.log")] = "server-20261106.log is owned by PC\\anna";
        permissions.Problems[Path.Combine(data, "backups", "monday.oadmbackup")] = "monday.oadmbackup is owned by PC\\anna";
        await using var app = BuildServer(serviceMode: true, data, permissions, addOnVersion: AddOnRig.Version);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.Equal([data], permissions.Fixed); // fixed as before
            Assert.Empty(permissions.Problems);
            Assert.Equal(AddOnState.Installed, (await TextRecognitionAsync(app)).State);
            Assert.True(Knows(app, AddOnCatalog.TextRecognitionId));
            Assert.True(File.Exists(Path.Combine(addOn, PluginLoader.ManifestFileName)));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Theory]
    [InlineData("data")] // the data folder itself: anyone could put a folder into addons
    [InlineData("addons")] // the addons folder itself
    [InlineData("unreadable")] // something loose elsewhere, and the facts about addons cannot be read
    public async Task OptionalPluginsOfAFolderThatWasLooseItselfOrCouldNotBeReadAreDeletedAndNotLoaded(string what)
    {
        var data = Path.Combine(_dir, "data");
        var addOn = PlantAddOn(data);
        var permissions = new FakePermissions();
        switch (what)
        {
            case "data":
                permissions.Problems[data] = "BUILTIN\\Users can write it";
                break;
            case "addons":
                permissions.Problems[AddOnFolders.Root(data)] = "Everyone can write it";
                break;
            default:
                permissions.Problems[Path.Combine(data, "logs", "server-20261106.log")] = "server-20261106.log is owned by PC\\anna";
                permissions.OwnError = new UnauthorizedAccessException("Access denied");
                break;
        }

        await using var app = BuildServer(serviceMode: true, data, permissions, addOnVersion: AddOnRig.Version);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.Equal([data], permissions.Fixed);
            Assert.Equal(AddOnState.NotInstalled, (await TextRecognitionAsync(app)).State);
            Assert.False(Knows(app, AddOnCatalog.TextRecognitionId));
            Assert.False(Directory.Exists(addOn));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task OnlyTheOptionalPluginWhoseFolderHeldSomethingLooseIsDeletedTheInstalledPluginsLoad()
    {
        var data = Path.Combine(_dir, "data");
        var addOn = PlantAddOn(data);
        var plugin = PlantPlugin(data);
        var permissions = new FakePermissions();
        permissions.Problems[Path.Combine(addOn, "models", "reader.onnx")] = "reader.onnx is owned by uid 1000";
        await using var app = BuildServer(serviceMode: true, data, permissions, pluginRoots: [PluginPaths.Installed(data)], addOnVersion: AddOnRig.Version);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.Equal([data], permissions.Fixed);
            Assert.Equal(AddOnState.NotInstalled, (await TextRecognitionAsync(app)).State);
            Assert.False(Knows(app, AddOnCatalog.TextRecognitionId));
            Assert.False(Directory.Exists(addOn));
            Assert.True(Knows(app, "oadm.planted")); // a loose optional plugin says nothing about the installed plugins
            Assert.True(Directory.Exists(plugin));
            Assert.Empty(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task ALooseInstalledPluginChangesNothingForTheOptionalPlugins()
    {
        var data = Path.Combine(_dir, "data");
        var addOn = PlantAddOn(data);
        var plugin = PlantPlugin(data);
        var permissions = new FakePermissions();
        permissions.Problems[Path.Combine(plugin, "Acme.Server.dll")] = "Acme.Server.dll is owned by uid 1000";
        await using var app = BuildServer(serviceMode: true, data, permissions, pluginRoots: [PluginPaths.Installed(data)], addOnVersion: AddOnRig.Version);

        await OadmServerHost.StartAsync(app);
        try
        {
            Assert.False(Knows(app, "oadm.planted")); // moved aside, as before
            Assert.Equal(AddOnState.Installed, (await TextRecognitionAsync(app)).State);
            Assert.True(Directory.Exists(addOn));
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
        }
    }

    [Fact]
    public async Task TheFolderCheckSaysWhatWasLooseAboutTheOptionalPlugins()
    {
        var data = Path.Combine(_dir, "data");
        var plugins = PluginPaths.Installed(data);
        var addOns = AddOnFolders.Root(data);
        var plugin = PlantPlugin(data, "acme.first");
        var addOn = PlantAddOn(data);
        var waiting = Path.Combine(addOns, ".update-" + AddOnCatalog.TextRecognitionId);
        Directory.CreateDirectory(waiting);
        var log = Path.Combine(data, "logs", "server-20261106.log");

        // Nothing loose: nothing fixed, nothing said.
        var permissions = new FakePermissions();
        Assert.Equal(ServerFoldersCheck.Untouched, await Guard(permissions).EnsureServerFoldersAsync([data], plugins, addOns, CancellationToken.None));
        Assert.Empty(permissions.Fixed);

        // Loose elsewhere and in an installed plugin: fixed, the plugin is named, no optional plugin is.
        permissions.Problems[log] = "owned by uid 1000";
        permissions.Problems[Path.Combine(plugin, "a.dll")] = "owned by uid 1000";
        var elsewhere = await Guard(permissions).EnsureServerFoldersAsync([data], plugins, addOns, CancellationToken.None);
        Assert.True(elsewhere.Fixed);
        Assert.Equal([Path.GetFullPath(plugin)], elsewhere.Plugins.Entries);
        Assert.False(elsewhere.AddOns.All);
        Assert.Empty(elsewhere.AddOns.Entries);

        // Something loose in an optional plugin's folder and a loose waiting update: exactly those two, no installed plugin.
        permissions.Problems[Path.Combine(addOn, "models", "reader.onnx")] = "owned by uid 1000";
        permissions.Problems[waiting] = "owned by uid 1000";
        var some = await Guard(permissions).EnsureServerFoldersAsync([data], plugins, addOns, CancellationToken.None);
        Assert.False(some.AddOns.All);
        Assert.Equal([Path.GetFullPath(waiting), Path.GetFullPath(addOn)], some.AddOns.Entries);
        Assert.False(some.Plugins.All);
        Assert.Empty(some.Plugins.Entries);

        // The addons folder itself: every optional plugin, no installed plugin.
        permissions.Problems[addOns] = "Everyone can write it";
        var folder = await Guard(permissions).EnsureServerFoldersAsync([data], plugins, addOns, CancellationToken.None);
        Assert.True(folder.AddOns.All);
        Assert.False(folder.Plugins.All);

        // The data folder itself: both.
        permissions.Problems[data] = "Everyone can write it";
        var both = await Guard(permissions).EnsureServerFoldersAsync([data], plugins, addOns, CancellationToken.None);
        Assert.True(both.AddOns.All);
        Assert.True(both.Plugins.All);

        // Facts that cannot be read count as loose.
        permissions.Problems[log] = "owned by uid 1000";
        permissions.OwnError = new UnauthorizedAccessException("Access denied");
        Assert.True((await Guard(permissions).EnsureServerFoldersAsync([data], plugins, addOns, CancellationToken.None)).AddOns.All);

        // Asked without the optional plugins' folder: nothing is said about them.
        permissions.OwnError = null;
        permissions.Problems[data] = "Everyone can write it";
        Assert.Same(LoosePlugins.None, (await Guard(permissions).EnsureServerFoldersAsync([data], plugins, CancellationToken.None)).AddOns);
    }

    [Fact]
    public void ALoosePluginFolderThatCannotBeMovedKeepsThePluginsUnloadedAndOnlyItIsMovedLater()
    {
        var data = Path.Combine(_dir, "data");
        var loose = PlantPlugin(data, "acme.moved-in");
        var good = PlantPlugin(data, "acme.copied-in");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var logger = NullLogger.Instance;
        var found = new LoosePlugins(false, [loose]);
        static void Locked(string from, string to) => throw new IOException("The folder is in use.");

        // The start that found it: somebody holds a file. No installed plugin loads, nothing was moved.
        Assert.False(InstalledPluginsTrust.Check(data, found, time, logger, Locked));
        Assert.True(Directory.Exists(loose));
        Assert.Empty(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));

        // The next start finds every folder in order (the fix worked) and still does not load them.
        Assert.False(InstalledPluginsTrust.Check(data, LoosePlugins.None, time, logger, Locked));

        // Once the move works only the folder that was loose goes aside.
        Assert.True(InstalledPluginsTrust.Check(data, LoosePlugins.None, time, logger));
        Assert.True(File.Exists(Path.Combine(data, InstalledPluginsTrust.AsidePrefix + "20261106-080000", "acme.moved-in", PluginLoader.ManifestFileName)));
        Assert.False(Directory.Exists(loose));
        Assert.True(Directory.Exists(good));
        Assert.False(File.Exists(Path.Combine(data, InstalledPluginsTrust.MarkerFileName)));
        Assert.True(InstalledPluginsTrust.Check(data, LoosePlugins.None, time, logger, Locked)); // a normal start: nothing is moved

        // A loose file beside the plugin folders goes aside too; a folder that is gone meanwhile is no problem.
        var note = Path.Combine(PluginPaths.Installed(data), "readme.txt");
        File.WriteAllText(note, "a file");
        Assert.True(InstalledPluginsTrust.Check(data, new LoosePlugins(false, [note, Path.Combine(PluginPaths.Installed(data), "acme.gone")]), time, logger));
        Assert.True(File.Exists(Path.Combine(data, InstalledPluginsTrust.AsidePrefix + "20261106-080000-2", "readme.txt")));
        Assert.True(Directory.Exists(good));
    }

    [Fact]
    public void InstalledPluginsThatCannotBeMovedStayUnloadedUntilTheMoveWorks()
    {
        var data = Path.Combine(_dir, "data");
        var plugin = PlantPlugin(data);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var logger = NullLogger.Instance;
        static void Locked(string from, string to) => throw new IOException("The folder is in use.");

        // The start that fixed the data folder: somebody holds a file, the folder cannot be moved.
        Assert.False(InstalledPluginsTrust.Check(data, LoosePlugins.Everything, time, logger, Locked));
        Assert.True(Directory.Exists(plugin));

        // The next start finds the data folder in order, and still does not load them.
        Assert.False(InstalledPluginsTrust.Check(data, LoosePlugins.None, time, logger, Locked));

        // Once the move works they are set aside and the folder is free for plugins again.
        Assert.True(InstalledPluginsTrust.Check(data, LoosePlugins.None, time, logger));
        Assert.True(File.Exists(Path.Combine(data, InstalledPluginsTrust.AsidePrefix + "20261106-080000", "oadm.planted", PluginLoader.ManifestFileName)));
        Assert.False(Directory.Exists(PluginPaths.Installed(data)));
        Assert.True(InstalledPluginsTrust.Check(data, LoosePlugins.None, time, logger, Locked)); // a normal start: nothing is moved
        Assert.Single(Directory.GetDirectories(data, InstalledPluginsTrust.AsidePrefix + "*"));

        // A second incident in the same second gets its own folder; an empty plugins folder is left alone.
        PlantPlugin(data);
        Assert.True(InstalledPluginsTrust.Check(data, LoosePlugins.Everything, time, logger));
        Assert.True(Directory.Exists(Path.Combine(data, InstalledPluginsTrust.AsidePrefix + "20261106-080000-2")));
        Directory.CreateDirectory(PluginPaths.Installed(data));
        Assert.True(InstalledPluginsTrust.Check(data, LoosePlugins.Everything, time, logger, Locked));
        Assert.True(Directory.Exists(PluginPaths.Installed(data)));
    }

    [Fact]
    public void ServiceNeverUsesTheDevelopmentPluginFolder()
    {
        var paths = new OadmPaths(_dir);
        // A server started from its build output inside the repository (src/Oadm.Server/bin/...).
        var app = TestRepository.Path("src", "Oadm.Server", "bin", "Debug", "net10.0");
        var development = TestRepository.DevelopmentPlugins;

        Assert.Contains(development, OadmServerHost.DefaultPluginRoots(paths, appDirectory: app));
        Assert.DoesNotContain(development, OadmServerHost.DefaultPluginRoots(paths, includeDevelopment: false, appDirectory: app));
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
    public async Task UnixCheckOfAnEntryItselfDoesNotLookBelowIt()
    {
        var runner = new FakeRunner();
        var permissions = new UnixFolderPermissions(runner);

        Assert.Null(await permissions.FindOwnProblemAsync("/var/lib/oadm", CancellationToken.None));
        Assert.Equal("find /var/lib/oadm|-maxdepth|0|!|-type|l|(|!|-user|0|-o|-perm|-0020|-o|-perm|-0002|)|-print", runner.Calls[0]);

        runner.Result = new CommandResult(0, "/var/lib/oadm\n");
        Assert.Equal(
            "/var/lib/oadm is not owned by root or writable by group or others",
            await permissions.FindOwnProblemAsync("/var/lib/oadm", CancellationToken.None));
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

        // The folder itself and a file in it, each asked on its own.
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");
        Assert.NotNull(await new WindowsFolderPermissions().FindOwnProblemAsync(_dir, CancellationToken.None));
        Assert.NotNull(await new WindowsFolderPermissions().FindOwnProblemAsync(file, CancellationToken.None));
    }

    private static FolderGuard Guard(IFolderPermissions permissions) => new(permissions, NullLogger<FolderGuard>.Instance);

    /// <param name="addOnVersion">The server's version for its optional plugins (a folder of that version loads); null: the build's.</param>
    private WebApplication BuildServer(
        bool serviceMode, string? dataDirectory = null, IFolderPermissions? permissions = null, IReadOnlyList<string>? pluginRoots = null, string? addOnVersion = null)
    {
        dataDirectory ??= Path.Combine(_dir, "data-" + Guid.NewGuid().ToString("N"));
        return OadmServerHost.Build([], new OadmServerHostOptions
        {
            DataDirectory = dataDirectory,
            PluginRoots = pluginRoots ?? [],
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

                if (addOnVersion is not null)
                {
                    services.RemoveAll<AddOnInstallerOptions>();
                    services.AddSingleton(new AddOnInstallerOptions(dataDirectory, addOnVersion, AddOnInstallerOptions.CurrentRuntimeId()));
                }
            },
        });
    }

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

    /// <summary>The loose entries by their path: a folder has a problem when it or something below it is loose.</summary>
    private sealed class FakePermissions : IFolderPermissions
    {
        public Dictionary<string, string> Problems { get; } = new(StringComparer.Ordinal);

        public List<string> Fixed { get; } = [];

        public Exception? FixError { get; set; }

        /// <summary>Asking about an entry itself fails (the facts cannot be read).</summary>
        public Exception? OwnError { get; set; }

        public bool FixClears { get; set; } = true;

        private static bool IsAtOrBelow(string entry, string path) =>
            entry == path || entry.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        public Task<string?> FindProblemAsync(string path, CancellationToken ct) =>
            Task.FromResult(Problems.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => IsAtOrBelow(p.Key, path)).Select(p => p.Value).FirstOrDefault());

        public Task<string?> FindOwnProblemAsync(string path, CancellationToken ct) =>
            OwnError is not null ? Task.FromException<string?>(OwnError) : Task.FromResult(Problems.GetValueOrDefault(path));

        public Task FixAsync(string path, bool readableByUsers, CancellationToken ct)
        {
            if (FixError is not null)
            {
                return Task.FromException(FixError);
            }

            Fixed.Add(path + (readableByUsers ? " readable" : string.Empty));
            if (FixClears)
            {
                foreach (var entry in Problems.Keys.Where(k => IsAtOrBelow(k, path)).ToList())
                {
                    Problems.Remove(entry);
                }
            }

            return Task.CompletedTask;
        }
    }
}
