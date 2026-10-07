using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.VapixCommander;

/// <summary>JSON shape of commands, library files and the page API (camelCase), shared by server and client part.</summary>
public static class CommandJson
{
    /// <summary>Strict reading of commands and library files: unknown properties are errors (the schema has additionalProperties false).</summary>
    public static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Page API messages: tolerant reading, compact writing.</summary>
    public static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Export files: indented, like the library files.</summary>
    public static readonly JsonSerializerOptions Indented = new(Strict) { WriteIndented = true };

    public static CommandDefinition Clone(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.Deserialize<CommandDefinition>(JsonSerializer.Serialize(command, Api), Api)!;
    }

    /// <summary>Parses a library or export file. Throws <see cref="JsonException"/> for malformed JSON or unknown properties.</summary>
    public static LibraryFile ParseLibraryFile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<LibraryFile>(json, Strict) ?? throw new JsonException("The file is empty.");
    }

    public static string WriteLibraryFile(LibraryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return JsonSerializer.Serialize(file, Indented);
    }

    public static T Read<T>(string? json)
        where T : class, new()
    {
        return string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Api) ?? new T();
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Api);
}
