namespace Oadm.Client.Infrastructure;

/// <summary>Command line options of the client.</summary>
public sealed record AppOptions
{
    /// <summary>Use the in-process <see cref="Api.FakeOadmApi"/> instead of a real server.</summary>
    public bool UseFake { get; init; }

    /// <summary>Overrides the server address from the client settings for this run.</summary>
    public string? ServerAddress { get; init; }

    /// <summary>Folder for client settings, logs and client plugins. Defaults to LocalApplicationData/Oadm.</summary>
    public string DataFolder { get; init; } = DefaultDataFolder;

    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oadm");

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new AppOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--fake", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { UseFake = true };
            }
            else if (string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { ServerAddress = args[++i] };
            }
            else if (string.Equals(arg, "--data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                options = options with { DataFolder = args[++i] };
            }
        }

        return options;
    }
}
