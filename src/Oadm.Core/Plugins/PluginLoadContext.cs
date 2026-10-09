using System.Reflection;
using System.Runtime.Loader;

namespace Oadm.Core.Plugins;

/// <summary>
/// Collectible load context for one plugin folder. Shared contract assemblies (the Oadm SDKs
/// and Microsoft.Extensions.*) always resolve from the host so interface type identity holds;
/// everything else is resolved from the plugin folder via its .deps.json, then by file name in the plugin folder (a
/// published .deps.json can lack a package's runtime list: the installed 1.2.0 Snapshot report could not find MigraDoc),
/// falling back to the host.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;

    public PluginLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _directory = Path.GetDirectoryName(Path.GetFullPath(mainAssemblyPath))!;
    }

    /// <summary>True for assemblies that must come from the host, never from a plugin folder.</summary>
    public static bool IsSharedAssembly(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        var name = assemblyName.Name;
        if (name is null)
        {
            return false;
        }

        return name.Equals("Oadm.Sdk", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Oadm.Sdk.Client", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads an assembly from memory (with its .pdb when present) so the file stays replaceable
    /// while the host runs, e.g. when a developer rebuilds a plugin.
    /// </summary>
    public Assembly LoadFromFileWithoutLock(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        using var assembly = new MemoryStream(File.ReadAllBytes(assemblyPath));
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
        {
            return LoadFromStream(assembly);
        }

        using var symbols = new MemoryStream(File.ReadAllBytes(pdbPath));
        return LoadFromStream(assembly, symbols);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedAssembly(assemblyName))
        {
            return null; // Default context.
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName) ?? InPluginFolder(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>"&lt;name&gt;.dll" next to the plugin when it exists, else null (then the host resolves it).</summary>
    private string? InPluginFolder(AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name) || assemblyName.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
