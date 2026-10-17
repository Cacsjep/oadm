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

    /// <summary>The plugin package (plugin.json id next to the dll) a part was loaded from; null for built-in parts.</summary>
    string? PackageOf(object part) => null;
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
    private readonly Dictionary<object, string> _packageOf = new(ReferenceEqualityComparer.Instance);

    // Kept alive on purpose: a collectible load context whose object is collected starts unloading, and a plugin
    // that loads one of its own assemblies later (e.g. its shared server models on first use) then fails.
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly ILogger<ClientPluginLoader> _logger;

    public ClientPluginLoader(AppOptions options, ILogger<ClientPluginLoader> logger)
        : this(DefaultRoots(options), logger)
    {
    }

    /// <summary>Bundled (next to the exe), installed (data folder) and, in a checkout, the repo's artifacts/plugins.</summary>
    public static IReadOnlyList<string> DefaultRoots(AppOptions? options, string? appDirectory = null)
    {
        string baseDirectory = appDirectory ?? AppContext.BaseDirectory;
        var roots = new List<string>(options?.PluginDirs ?? []) // --plugin-dir first: a build output wins over installed copies
        {
            Path.Combine(baseDirectory, "plugins"),
            Path.Combine(options?.DataFolder ?? AppOptions.DefaultDataFolder, "plugins"),
        };
        if (AppBundleResources(baseDirectory) is { } resources)
        {
            roots.Add(Path.Combine(resources, "plugins"));
        }

        if (DevelopmentRoot(baseDirectory) is { } development)
        {
            roots.Add(development);
        }

        return roots;
    }

    /// <summary>
    /// <c>OADM.app/Contents/Resources</c> when the client runs from <c>OADM.app/Contents/MacOS</c>: the macOS package
    /// keeps the plugins there, because codesign treats a folder with a dot in <c>Contents/MacOS</c> ("oadm.network")
    /// as a nested bundle and refuses to sign the app. Null elsewhere.
    /// </summary>
    public static string? AppBundleResources(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var macOs = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        return macOs.Name == "MacOS" && macOs.Parent is { Name: "Contents" } contents
            ? Path.Combine(contents.FullName, "Resources")
            : null;
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
            _loadedPackages.UnionWith(_rootPackages);
            _rootPackages.Clear();
        }
    }

    public IReadOnlyList<ITaskPluginDialog> Dialogs => _dialogs;
    public IReadOnlyList<ICorePluginPage> Pages => _pages;
    public IReadOnlyList<IToolbarPlugin> ToolbarPlugins => _toolbar;

    public ITaskPluginDialog? FindDialog(string pluginId) =>
        _dialogs.Find(d => string.Equals(d.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

    public string? PackageOf(object part) => _packageOf.TryGetValue(part, out string? id) ? id : null;

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
                string? packageId = ReadPackageId(Path.GetDirectoryName(Path.GetFullPath(dll)));
                if (packageId is not null && _loadedPackages.Contains(packageId))
                {
                    // An earlier root (a --plugin-dir build output) has this package already: that copy wins.
                    LogSkipped(_logger, packageId, dll);
                    continue;
                }

                if (packageId is not null)
                {
                    _rootPackages.Add(packageId);
                }

                var context = new PluginLoadContext(dll);
                _contexts.Add(context);
                // From memory: the file stays replaceable, so a plugin build can overwrite it while the client runs.
                Assembly assembly = context.LoadWithoutLock(Path.GetFullPath(dll));

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
                        if (packageId is not null)
                        {
                            _packageOf[toolbar] = packageId;
                        }
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

    /// <summary>The id of plugin.json in the folder, or null.</summary>
    private static string? ReadPackageId(string? directory)
    {
        if (directory is null)
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, "plugin.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Packages of the roots loaded so far; a later root never loads the same package again.</summary>
    private readonly HashSet<string> _loadedPackages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Packages of the root being loaded (one package may have several client dlls).</summary>
    private readonly HashSet<string> _rootPackages = new(StringComparer.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client plugin {PackageId} in {Path} skipped: an earlier folder (--plugin-dir) has it")]
    private static partial void LogSkipped(ILogger logger, string packageId, string path);

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
        if (assemblyName.Name is null)
        {
            return null;
        }

        if (IsShared(assemblyName.Name))
        {
            return FromHost(assemblyName);
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>Loads the plugin's main assembly (and its .pdb) from memory, so the file is not locked.</summary>
    public Assembly LoadWithoutLock(string assemblyPath)
    {
        using var assembly = new MemoryStream(File.ReadAllBytes(assemblyPath));
        string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
        {
            return LoadFromStream(assembly);
        }

        using var symbols = new MemoryStream(File.ReadAllBytes(pdbPath));
        return LoadFromStream(assembly, symbols);
    }

    /// <summary>
    /// The host's copy of a shared assembly whatever version the plugin was built against (a plugin built with the SDK
    /// packages of a newer release still loads; plugin.json minSdkVersion decides), like the server's PluginLoadContext.
    /// </summary>
    internal static Assembly? FromHost(AssemblyName assemblyName)
    {
        var loaded = Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            return loaded;
        }

        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name!));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
