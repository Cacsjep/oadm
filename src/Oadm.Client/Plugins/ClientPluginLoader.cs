using System.Reflection;
using System.Runtime.Loader;

using Microsoft.Extensions.Logging;

using Oadm.Client.Infrastructure;
using Oadm.Sdk.Client;

namespace Oadm.Client.Plugins;

/// <summary>Client-side plugin parts found in <c>*.Client.dll</c> files.</summary>
public interface IClientPluginRegistry
{
    ITaskPluginDialog? FindDialog(string pluginId);
    ICorePluginPage? FindPage(string pluginId);
    IReadOnlyList<ITaskPluginDialog> Dialogs { get; }
    IReadOnlyList<ICorePluginPage> Pages { get; }

    /// <summary>Toolbar parts of the Devices page from plugins (the built-in ones are registered in the container).</summary>
    IReadOnlyList<IToolbarPlugin> ToolbarPlugins { get; }
}

/// <summary>
/// Scans <c>&lt;app&gt;/plugins/*/</c>, <c>&lt;datafolder&gt;/plugins/*/</c> and, when running from a repository checkout,
/// <c>&lt;repo&gt;/artifacts/plugins/*/</c> (same discovery as the server's <c>PluginPaths.Development</c>) for
/// <c>*.Client.dll</c>, loads each in its own
/// <see cref="AssemblyLoadContext"/> that shares the SDK and Avalonia with the host, and instantiates every
/// <see cref="ITaskPluginDialog"/>, <see cref="ICorePluginPage"/> and <see cref="IToolbarPlugin"/>. Failures are logged and skipped.
/// </summary>
public sealed partial class ClientPluginLoader : IClientPluginRegistry
{
    private readonly List<ITaskPluginDialog> _dialogs = [];
    private readonly List<ICorePluginPage> _pages = [];
    private readonly List<IToolbarPlugin> _toolbar = [];
    private readonly ILogger<ClientPluginLoader> _logger;

    public ClientPluginLoader(AppOptions options, ILogger<ClientPluginLoader> logger)
        : this(DefaultRoots(options), logger)
    {
    }

    /// <summary>Bundled (next to the exe), installed (data folder) and, in a checkout, the repo's artifacts/plugins.</summary>
    public static IReadOnlyList<string> DefaultRoots(AppOptions? options, string? appDirectory = null)
    {
        string baseDirectory = appDirectory ?? AppContext.BaseDirectory;
        var roots = new List<string>
        {
            Path.Combine(baseDirectory, "plugins"),
            Path.Combine(options?.DataFolder ?? AppOptions.DefaultDataFolder, "plugins"),
        };
        if (DevelopmentRoot(baseDirectory) is { } development)
        {
            roots.Add(development);
        }

        return roots;
    }

    /// <summary>
    /// <c>&lt;repo&gt;/artifacts/plugins</c>, where plugin projects copy their build output; found by walking up
    /// from <paramref name="startDirectory"/> to the folder containing Oadm.sln. Null outside a checkout.
    /// </summary>
    public static string? DevelopmentRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Oadm.sln")))
            {
                return Path.Combine(dir.FullName, "artifacts", "plugins");
            }
        }

        return null;
    }

    public ClientPluginLoader(IEnumerable<string> pluginRoots, ILogger<ClientPluginLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(pluginRoots);
        _logger = logger;
        foreach (string root in pluginRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            LoadFrom(root);
        }
    }

    public IReadOnlyList<ITaskPluginDialog> Dialogs => _dialogs;
    public IReadOnlyList<ICorePluginPage> Pages => _pages;
    public IReadOnlyList<IToolbarPlugin> ToolbarPlugins => _toolbar;

    public ITaskPluginDialog? FindDialog(string pluginId) =>
        _dialogs.Find(d => string.Equals(d.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

    public ICorePluginPage? FindPage(string pluginId) =>
        _pages.Find(p => string.Equals(p.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

    private void LoadFrom(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string dll in Directory.EnumerateFiles(root, "*.Client.dll", SearchOption.AllDirectories))
        {
            try
            {
                var context = new PluginLoadContext(dll);
                Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
                foreach (Type type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
                {
                    if (typeof(ITaskPluginDialog).IsAssignableFrom(type) && Activator.CreateInstance(type) is ITaskPluginDialog dialog)
                    {
                        _dialogs.Add(dialog);
                        LogLoaded(_logger, "dialog", dialog.PluginId, dll);
                    }

                    if (typeof(ICorePluginPage).IsAssignableFrom(type) && Activator.CreateInstance(type) is ICorePluginPage page)
                    {
                        _pages.Add(page);
                        LogLoaded(_logger, "page", page.PluginId, dll);
                    }

                    if (typeof(IToolbarPlugin).IsAssignableFrom(type) && Activator.CreateInstance(type) is IToolbarPlugin toolbar)
                    {
                        _toolbar.Add(toolbar);
                        LogLoaded(_logger, "toolbar", toolbar.Id, dll);
                    }
                }
            }
            catch (Exception ex)
            {
                LogLoadFailed(_logger, ex, dll);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded client plugin {Kind} for {PluginId} from {Path}")]
    private static partial void LogLoaded(ILogger logger, string kind, string pluginId, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not load client plugin {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception ex, string path);
}

/// <summary>Isolated load context; host-shared assemblies (SDK, Avalonia, logging) resolve from the default context.</summary>
internal sealed class PluginLoadContext(string pluginPath)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    internal static bool IsShared(string name) =>
        name is "Oadm.Sdk" or "Oadm.Sdk.Client"
        || name.StartsWith("Avalonia", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)
        || name.StartsWith("CommunityToolkit.Mvvm", StringComparison.Ordinal)
        || name.StartsWith("SkiaSharp", StringComparison.Ordinal)
        || name.StartsWith("HarfBuzzSharp", StringComparison.Ordinal);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || IsShared(assemblyName.Name))
        {
            return null;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
