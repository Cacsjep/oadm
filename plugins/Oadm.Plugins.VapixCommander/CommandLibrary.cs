using System.Text.Json;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// The battery-included commands: every <c>Library/*.json</c> next to the plugin assembly
/// (<c>{ "formatVersion": 1, "category": "...", "commands": [...] }</c>), loaded once at start.
/// Broken files and invalid commands are skipped and reported in <see cref="Problems"/>; duplicate ids keep the first.
/// </summary>
public sealed class CommandLibrary
{
    private readonly Dictionary<string, CommandDefinition> _byId;

    private CommandLibrary(IReadOnlyList<CommandDefinition> commands, IReadOnlyList<string> problems)
    {
        Commands = commands;
        Problems = problems;
        _byId = commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public static CommandLibrary Empty { get; } = new([], []);

    /// <summary>Commands ordered by category (library order), then name.</summary>
    public IReadOnlyList<CommandDefinition> Commands { get; }

    public IReadOnlyList<string> Problems { get; }

    public CommandDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Loads <paramref name="libraryDirectory"/>/*.json (sorted by file name). A missing folder is an empty library.</summary>
    public static CommandLibrary Load(string? libraryDirectory)
    {
        if (string.IsNullOrEmpty(libraryDirectory) || !Directory.Exists(libraryDirectory))
        {
            return Empty;
        }

        var files = Directory.GetFiles(libraryDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path.GetFileName(path), (Func<string>)(() => File.ReadAllText(path))));
        return Load(files);
    }

    /// <summary>Loads library files given as (file name, content reader).</summary>
    public static CommandLibrary Load(IEnumerable<(string Name, Func<string> Read)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var problems = new List<string>();
        var commands = new List<CommandDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, read) in files)
        {
            LibraryFile file;
            try
            {
                file = CommandJson.ParseLibraryFile(read());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add($"{name}: {ex.Message}");
                continue;
            }

            if (file.FormatVersion != CommandLimits.FormatVersion)
            {
                problems.Add($"{name}: formatVersion {file.FormatVersion} is not supported (expected 1).");
                continue;
            }

            if (!CommandCategories.All.Contains(file.Category))
            {
                problems.Add($"{name}: unknown category \"{file.Category}\".");
                continue;
            }

            foreach (var command in file.Commands ?? [])
            {
                var issues = CommandValidator.Validate(command).ToList();
                if (command.Category != file.Category)
                {
                    issues.Add($"Category \"{command.Category}\" differs from the file category \"{file.Category}\".");
                }

                if (issues.Count > 0)
                {
                    problems.Add($"{name}: {command.Id}: {string.Join(" ", issues)}");
                    continue;
                }

                if (!ids.Add(command.Id))
                {
                    problems.Add($"{name}: {command.Id}: duplicate id, the first definition is kept.");
                    continue;
                }

                commands.Add(command);
            }
        }

        var order = CommandCategories.All.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.Ordinal);
        return new CommandLibrary(
            [.. commands.OrderBy(c => order.GetValueOrDefault(c.Category, int.MaxValue)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)],
            problems);
    }
}
