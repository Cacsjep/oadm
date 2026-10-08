namespace Oadm.Core.Persistence;

/// <summary>
/// Locations of the server data folder and the files inside it.
/// Resolution order: explicit constructor argument, env var <c>OADM_DATA_DIR</c>,
/// then <c>LocalApplicationData/Oadm</c>.
/// </summary>
public sealed class OadmPaths
{
    public const string DataDirEnvironmentVariable = "OADM_DATA_DIR";
    public const string DatabaseFileName = "oadm.db";
    public const string MasterKeyFileName = "master.key";

    public OadmPaths(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(ResolveDataDirectory(dataDirectory));
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    public string MasterKeyPath => Path.Combine(DataDirectory, MasterKeyFileName);

    /// <summary>Working folders of the core plugins (<c>ICorePluginContext.DataDirectory</c>), one per plugin id.</summary>
    public string PluginDataDirectory => Path.Combine(DataDirectory, "plugin-data");

    public string PluginsDirectory => Path.Combine(DataDirectory, "plugins");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>SQLite connection string for <see cref="DatabasePath"/>.</summary>
    public string ConnectionString => $"Data Source={DatabasePath}";

    /// <summary>Creates the data, plugins and logs folders if missing.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveDataDirectory(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return explicitDirectory;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData))
        {
            // Headless Linux without HOME/XDG: fall back to the working directory.
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "Oadm");
    }

    public override string ToString() => DataDirectory;
}
