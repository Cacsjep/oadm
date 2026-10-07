using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>A plugin folder that was loaded into its own load context.</summary>
public sealed record LoadedPluginPackage(
    PluginManifest Manifest,
    string Directory,
    IReadOnlyList<Assembly> Assemblies,
    AssemblyLoadContext LoadContext);

/// <summary>
/// Discovers plugins and registers them into a <see cref="PluginRegistry"/>.
/// <list type="bullet">
/// <item>Folder plugins: <c>&lt;root&gt;/&lt;plugin&gt;/plugin.json</c> plus <c>*.Server.dll</c>,
/// each folder in its own collectible <see cref="PluginLoadContext"/>.</item>
/// <item>Built-in plugins: types in an assembly already loaded by the host (tests, bundled plugins).</item>
/// </list>
/// Every failure is logged and recorded in <see cref="PluginRegistry.Errors"/>; nothing throws.
/// </summary>
public sealed partial class PluginLoader
{
    public const string ManifestFileName = "plugin.json";
    public const string ServerAssemblyPattern = "*.Server.dll";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly PluginRegistry _registry;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private readonly List<LoadedPluginPackage> _packages = [];

    public PluginLoader(PluginRegistry registry, ILogger<PluginLoader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<LoadedPluginPackage> Packages
    {
        get
        {
            lock (_sync)
            {
                return [.. _packages];
            }
        }
    }

    /// <summary>
    /// Loads every plugin folder below the given roots. A root that itself contains plugin.json is
    /// loaded as one plugin. Missing roots are skipped. A package id loaded from an earlier root wins.
    /// </summary>
    public void LoadFromRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                LogRootMissing(root);
                continue;
            }

            if (File.Exists(Path.Combine(root, ManifestFileName)))
            {
                LoadPackage(root);
                continue;
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _registry.AddError(new PluginLoadError(root, $"Cannot enumerate plugin root: {ex.Message}", ex));
                continue;
            }

            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (File.Exists(Path.Combine(folder, ManifestFileName)))
                {
                    LoadPackage(folder);
                }
            }
        }
    }

    /// <summary>Loads one plugin folder. Returns null when it was rejected (see registry errors).</summary>
    public LoadedPluginPackage? LoadPackage(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        var directory = Path.GetFullPath(pluginDirectory);
        PluginLoadContext? context = null;
        try
        {
            var manifest = ReadManifest(Path.Combine(directory, ManifestFileName));
            if (!IsSdkCompatible(manifest.MinSdkVersion))
            {
                throw new PluginLoadException(
                    $"Plugin {manifest.Id} requires SDK {manifest.MinSdkVersion}, host has {SdkInfo.Version}.");
            }

            lock (_sync)
            {
                if (_packages.Exists(p => string.Equals(p.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new PluginLoadException($"Plugin package {manifest.Id} is already loaded from another folder.");
                }
            }

            var dlls = Directory.GetFiles(directory, ServerAssemblyPattern, SearchOption.TopDirectoryOnly);
            if (dlls.Length == 0)
            {
                throw new PluginLoadException($"No {ServerAssemblyPattern} found.");
            }

            Array.Sort(dlls, StringComparer.OrdinalIgnoreCase);
            context = new PluginLoadContext(dlls[0], "plugin:" + manifest.Id);
            var assemblies = dlls.Select(context.LoadFromFileWithoutLock).ToArray();

            var origin = new PluginOrigin(manifest.Id, manifest.Version, directory);
            var registered = assemblies.Sum(a => RegisterTypes(a, origin));
            if (registered == 0)
            {
                _registry.AddError(new PluginLoadError(manifest.Id, "Package contains no task or core plugin."));
            }

            var package = new LoadedPluginPackage(manifest, directory, assemblies, context);
            lock (_sync)
            {
                _packages.Add(package);
            }

            LogPackageLoaded(manifest.Id, manifest.Version, directory, registered);
            return package;
        }
#pragma warning disable CA1031 // A broken plugin must never stop the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _registry.AddError(new PluginLoadError(directory, ex.Message, ex is PluginLoadException ? null : ex));
            try
            {
                context?.Unload();
            }
            catch (InvalidOperationException)
            {
                // Already unloading.
            }

            return null;
        }
    }

    /// <summary>Registers plugins compiled into an assembly the host already loaded.</summary>
    /// <returns>Number of plugins registered (core plugins plus their contributed tasks).</returns>
    public int LoadFromAssembly(Assembly assembly, PluginOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return RegisterTypes(assembly, origin ?? PluginOrigin.FromAssembly(assembly));
    }

    /// <summary>Reads and validates plugin.json. Throws <see cref="PluginLoadException"/> on any problem.</summary>
    public static PluginManifest ReadManifest(string path)
    {
        PluginManifest? manifest;
        try
        {
            using var stream = File.OpenRead(path);
            manifest = JsonSerializer.Deserialize<PluginManifest>(stream, ManifestJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new PluginLoadException($"Cannot read {ManifestFileName}: {ex.Message}", ex);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new PluginLoadException($"{ManifestFileName} has no id.");
        }

        if (TryParseVersion(manifest.Version) is null)
        {
            throw new PluginLoadException($"{ManifestFileName} of {manifest.Id} has an invalid version '{manifest.Version}'.");
        }

        if (TryParseVersion(manifest.MinSdkVersion) is null)
        {
            throw new PluginLoadException($"{ManifestFileName} of {manifest.Id} has an invalid minSdkVersion '{manifest.MinSdkVersion}'.");
        }

        return manifest;
    }

    /// <summary>True when the host SDK (<see cref="SdkInfo.Version"/>) is at least <paramref name="minSdkVersion"/>.</summary>
    public static bool IsSdkCompatible(string? minSdkVersion)
    {
        var required = TryParseVersion(minSdkVersion);
        var host = TryParseVersion(SdkInfo.Version);
        return required is not null && host is not null && host >= required;
    }

    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Ignore SemVer pre-release and build metadata: "1.2.3-beta+abc" -> "1.2.3".
        var core = value.Split('-', '+')[0].Trim();
        if (!core.Contains('.', StringComparison.Ordinal))
        {
            core += ".0";
        }

        return Version.TryParse(core, out var version) ? version : null;
    }

    private int RegisterTypes(Assembly assembly, PluginOrigin origin)
    {
        var types = GetLoadableTypes(assembly, origin)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false, IsPublic: true })
            .ToArray();

        var count = 0;
        var contributedTypes = new HashSet<Type>();

        foreach (var type in types.Where(t => typeof(ICorePlugin).IsAssignableFrom(t)))
        {
            if (TryCreate(type, origin) is not ICorePlugin core)
            {
                continue;
            }

            var before = _registry.TaskPlugins.Count;
            if (_registry.RegisterCorePlugin(core, origin))
            {
                count += 1 + (_registry.TaskPlugins.Count - before);
                foreach (var task in SafeContributed(core))
                {
                    contributedTypes.Add(task.GetType());
                }
            }
        }

        foreach (var type in types.Where(t => typeof(ITaskPlugin).IsAssignableFrom(t) && !typeof(ICorePlugin).IsAssignableFrom(t)))
        {
            // Contributed task types are owned by their core plugin, and types without a
            // parameterless constructor can only be created by one.
            if (contributedTypes.Contains(type) || type.GetConstructor(Type.EmptyTypes) is null)
            {
                LogSkippedType(type.FullName ?? type.Name, origin.PackageId);
                continue;
            }

            if (TryCreate(type, origin) is ITaskPlugin task && _registry.RegisterTaskPlugin(task, origin))
            {
                count++;
            }
        }

        return count;
    }

    private static ITaskPlugin[] SafeContributed(ICorePlugin core)
    {
        try
        {
            return core.TaskPlugins?.Where(t => t is not null).ToArray() ?? [];
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private Type[] GetLoadableTypes(Assembly assembly, PluginOrigin origin)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var first = ex.LoaderExceptions.FirstOrDefault(e => e is not null);
            _registry.AddError(new PluginLoadError(
                origin.PackageId,
                $"Some types of {assembly.GetName().Name} could not be loaded: {first?.Message}",
                first));
            return [.. ex.Types.Where(t => t is not null).Cast<Type>()];
        }
    }

    private object? TryCreate(Type type, PluginOrigin origin)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
#pragma warning disable CA1031 // Plugin constructors are untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            _registry.AddError(new PluginLoadError(
                origin.PackageId,
                string.Create(CultureInfo.InvariantCulture, $"Cannot create {type.FullName}: {inner.Message}"),
                inner));
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plugin root {Root} does not exist, skipped")]
    private partial void LogRootMissing(string? root);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded plugin package {PackageId} {Version} from {Directory} ({Count} plugin(s))")]
    private partial void LogPackageLoaded(string packageId, string version, string directory, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped type {Type} in {PackageId}: contributed by a core plugin or no parameterless constructor")]
    private partial void LogSkippedType(string type, string packageId);
}

/// <summary>A plugin package is invalid (manifest, SDK version, missing assembly).</summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException()
    {
    }

    public PluginLoadException(string message)
        : base(message)
    {
    }

    public PluginLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
