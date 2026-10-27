namespace Oadm.Client.Infrastructure;

/// <summary>Command line options of the client.</summary>
public sealed record AppOptions
{
    /// <summary>Use the in-process <see cref="Api.FakeOadmApi"/> instead of a real server.</summary>
    public bool UseFake { get; init; }

    /// <summary>
    /// The sample user fake mode runs as (<c>--fake-user tech2</c>, or the environment variable <c>OADM_FAKE_USER</c>); null =
    /// the administrator "admin". "tech2" has the device group Building B, "guard" the group Lobby (no site: cannot add).
    /// </summary>
    public string? FakeUser { get; init; }

    /// <summary>The environment variable that sets <see cref="FakeUser"/> (the arguments win).</summary>
    public const string FakeUserVariable = "OADM_FAKE_USER";

    /// <summary>
    /// The version this client takes itself for when it asks for a newer release (<c>--as-version 4.4.0</c>, or the environment
    /// variable <c>OADM_CLIENT_VERSION</c>): shows the new version dialog from a development build. Null = the real version.
    /// </summary>
    public string? AsVersion { get; init; }

    /// <summary>The environment variable that sets <see cref="AsVersion"/> (the arguments win).</summary>
    public const string AsVersionVariable = "OADM_CLIENT_VERSION";

    /// <summary>Overrides the server address from the client settings for this run.</summary>
    public string? ServerAddress { get; init; }

    /// <summary>Folder for client settings, logs and client plugins. Defaults to LocalApplicationData/Oadm.</summary>
    public string DataFolder { get; init; } = DefaultDataFolder;

    /// <summary>
    /// Plugin folders loaded straight from a build output (<c>--plugin-dir &lt;folder&gt;</c>, repeatable; plugin development):
    /// loaded first, so they win over installed copies of the same package.
    /// </summary>
    public IReadOnlyList<string> PluginDirs { get; init; } = [];

    /// <summary>The appearance for this run instead of the one in the client settings (headless tests).</summary>
    public Oadm.Theme.Appearance? Appearance { get; init; }

    /// <summary>Clock of the fake server's task simulation (<see cref="UseFake"/>); null = the system clock. Headless tests freeze it.</summary>
    public TimeProvider? FakeSimulationTime { get; init; }

    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oadm");

    /// <param name="args">The command line.</param>
    /// <param name="environment">Reads an environment variable (tests); null = the process environment.</param>
    public static AppOptions Parse(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        Func<string, string?> env = environment ?? Environment.GetEnvironmentVariable;
        string? fakeUser = env(FakeUserVariable);
        string? asVersion = env(AsVersionVariable);
        var options = new AppOptions
        {
            FakeUser = string.IsNullOrWhiteSpace(fakeUser) ? null : fakeUser.Trim(),
            AsVersion = string.IsNullOrWhiteSpace(asVersion) ? null : asVersion.Trim(),
        };
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--fake", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { UseFake = true };
            }
            else if (string.Equals(arg, "--fake-user", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { FakeUser = args[++i] };
            }
            else if (string.Equals(arg, "--as-version", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { AsVersion = args[++i] };
            }
            else if (string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { ServerAddress = args[++i] };
            }
            else if (string.Equals(arg, "--data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { DataFolder = args[++i] };
            }
            else if (string.Equals(arg, "--plugin-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { PluginDirs = [.. options.PluginDirs, Path.GetFullPath(args[++i])] };
            }
        }

        return options;
    }
}
