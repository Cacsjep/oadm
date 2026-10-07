using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Oadm.Plugins.VapixCommander.Library.Tests.Hardware;

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

    public override string ToString() => Note is null ? Address : $"{Address} ({Note})";
}

/// <summary>
/// Loads the git-ignored dev-cameras.yaml (same lookup as Oadm.Core.Tests): env var OADM_DEV_CAMERAS, then
/// dev-cameras.yaml in the test assembly directory and every parent (finds the main checkout from a worktree).
/// </summary>
public static class DevCameras
{
    public const string EnvironmentVariable = "OADM_DEV_CAMERAS";
    public const string FileName = "dev-cameras.yaml";

    private static readonly Lazy<IReadOnlyList<DevCamera>> Loaded = new(Load);

    public static IReadOnlyList<DevCamera> All => Loaded.Value;

    private static List<DevCamera> Load()
    {
        string? path = null;
        var explicitPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            path = File.Exists(explicitPath) ? explicitPath : null;
        }
        else
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && path is null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, FileName);
                path = File.Exists(candidate) ? candidate : null;
            }
        }

        if (path is null)
        {
            return [];
        }

        var file = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<DevCamerasFile?>(File.ReadAllText(path));
        return file?.Cameras?.Where(c => !string.IsNullOrWhiteSpace(c.Address)).ToList() ?? [];
    }

    private sealed class DevCamerasFile
    {
        public List<DevCamera>? Cameras { get; set; }
    }
}

/// <summary>Skipped when no dev cameras are configured (CI). Hardware tests here are strictly read-only.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (DevCameras.All.Count == 0)
        {
            Skip = $"No dev cameras configured ({DevCameras.FileName} not found and {DevCameras.EnvironmentVariable} not set).";
        }
    }
}
