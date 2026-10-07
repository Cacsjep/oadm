using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Oadm.Core.Tests.Hardware;

/// <summary>One entry of dev-cameras.yaml. <see cref="ToString"/> never includes the password.</summary>
public sealed class DevCamera
{
    public string Address { get; set; } = string.Empty;

    public string User { get; set; } = "root";

    public string Password { get; set; } = string.Empty;

    /// <summary>"https" (default) or "http".</summary>
    public string? Scheme { get; set; }

    public string? Note { get; set; }

    public string EffectiveScheme => string.IsNullOrWhiteSpace(Scheme) ? Uri.UriSchemeHttps : Scheme.Trim().ToLowerInvariant();

    public override string ToString()
    {
        return Note is null ? Address : $"{Address} ({Note})";
    }
}

/// <summary>
/// Loads the git-ignored dev-cameras.yaml used by tests in category "Hardware".
/// Lookup order: env var OADM_DEV_CAMERAS (explicit file path), then dev-cameras.yaml in the
/// test assembly directory and every parent directory. Because git worktrees live below the
/// main checkout (.claude/worktrees/...), the upward search also finds the main repo's file.
/// </summary>
public static class DevCameras
{
    public const string EnvironmentVariable = "OADM_DEV_CAMERAS";
    public const string FileName = "dev-cameras.yaml";

    private static readonly Lazy<(string? Path, IReadOnlyList<DevCamera> Cameras)> Loaded = new(LoadDefault);

    /// <summary>All configured cameras; empty when no file was found.</summary>
    public static IReadOnlyList<DevCamera> All => Loaded.Value.Cameras;

    /// <summary>Path of the file that was loaded, or null.</summary>
    public static string? ConfigPath => Loaded.Value.Path;

    public static bool Any => All.Count > 0;

    /// <summary>Finds the config file, or null.</summary>
    public static string? FindConfigFile(string? startDirectory = null)
    {
        var explicitPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        for (var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Parses dev-cameras.yaml content. Entries without an address are dropped.</summary>
    public static IReadOnlyList<DevCamera> Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var file = deserializer.Deserialize<DevCamerasFile?>(yaml);
        return file?.Cameras?.Where(c => !string.IsNullOrWhiteSpace(c.Address)).ToList() ?? [];
    }

    private static (string? Path, IReadOnlyList<DevCamera> Cameras) LoadDefault()
    {
        var path = FindConfigFile();
        return path is null ? (null, []) : (path, Parse(File.ReadAllText(path)));
    }

    private sealed class DevCamerasFile
    {
        public List<DevCamera>? Cameras { get; set; }
    }
}
