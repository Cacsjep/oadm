using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.OpcUa.Tests;

/// <summary>The plugin in a load context of its own, as on a server: the stack from its folder, the host's Microsoft.Extensions.</summary>
public sealed class OpcUaPluginLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oadm-opcua-load-" + Guid.NewGuid().ToString("N"));

    public OpcUaPluginLoadTests()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "oadm.opcua")).FullName;
        File.Copy(TestRepository.Path("plugins", "Oadm.Plugins.OpcUa", "plugin.json"), Path.Combine(folder, "plugin.json"));
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Where(IsPluginFile))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The stack's assemblies stay loaded; the temp folder is the OS's to clean.
        }
    }

    /// <summary>What a publish of the plugin holds; the stack's server is here for the tests only.</summary>
    private static bool IsPluginFile(string path)
    {
        var name = Path.GetFileName(path);
        return name == "Oadm.Plugins.OpcUa.Server.dll"
            || name.StartsWith("Polly", StringComparison.Ordinal)
            || name.StartsWith("BitFaster", StringComparison.Ordinal)
            || (name.StartsWith("Opc.Ua.", StringComparison.Ordinal) && name != "Opc.Ua.Server.dll");
    }

    [Fact]
    public async Task ItLoadsInItsOwnContextAndConnects()
    {
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);

        loader.LoadFromRoots([_root]);

        Assert.Empty(registry.Errors);
        var package = Assert.Single(loader.Packages);
        Assert.Equal(1, package.Registered);
        Assert.True(package.Manifest.AlwaysOn);
        var type = package.Assemblies[0].GetType(typeof(WriteOpcUaAction).FullName!)!;
        var action = Assert.IsAssignableFrom<IActionPlugin>(Activator.CreateInstance(type));
        var context = AssemblyLoadContext.GetLoadContext(type.Assembly)!;
        Assert.IsType<PluginLoadContext>(context);

        await using var server = await OpcUaTestServer.StartAsync();
        using var settings = JsonDocument.Parse(JsonSerializer.Serialize(new { endpoint = server.Endpoint }));
        var request = new RecipientSessionRequest(settings.RootElement.Clone(), new Dictionary<string, IReadOnlyList<X509Certificate2>>(), NullLogger.Instance);
        await using var session = Assert.Single(action.RecipientTypes).Sessions!.Create(request);

        await session.ConnectAsync(CancellationToken.None);

        var names = context.Assemblies.Select(a => a.GetName().Name).ToList();
        Assert.Contains("Opc.Ua.Client", names);
        Assert.Contains("Opc.Ua.Core", names);
        Assert.DoesNotContain("Oadm.Sdk", names);
        Assert.DoesNotContain(names, n => n!.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal));
    }
}
