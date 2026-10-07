using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.VapixCommander.Library.Tests;

/// <summary>One library file (Library/&lt;category&gt;.json) as raw JSON.</summary>
public sealed record LibraryFile(string FileName, string Text, JsonObject Root)
{
    public string Category => Root["category"]!.GetValue<string>();

    public IEnumerable<JsonObject> Commands => Root["commands"]!.AsArray().Select(c => c!.AsObject());
}

/// <summary>Loads the library files copied next to the test assembly and offers helpers shared by the tests.</summary>
public static partial class CommandLibrary
{
    public static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "Library");

    public static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "Schema", "command.schema.json");

    private static readonly Lazy<IReadOnlyList<LibraryFile>> LazyFiles = new(Load);

    public static IReadOnlyList<LibraryFile> Files => LazyFiles.Value;

    public static IEnumerable<JsonObject> AllCommands => Files.SelectMany(f => f.Commands);

    public static JsonObject Command(string id) => AllCommands.Single(c => Id(c) == id);

    public static string Id(JsonObject command) => command["id"]!.GetValue<string>();

    public static bool Writes(JsonObject command) => command["writes"]!.GetValue<bool>();

    public static JsonObject Request(JsonObject command) => command["request"]!.AsObject();

    public static JsonObject Response(JsonObject command) => command["response"]!.AsObject();

    public static string Kind(JsonObject command) => Response(command)["kind"]!.GetValue<string>();

    public static IEnumerable<JsonObject> Fields(JsonObject command) =>
        command["fields"]!.AsArray().Select(f => f!.AsObject());

    public static IEnumerable<JsonObject> Extracts(JsonObject command) =>
        Response(command)["extract"]?.AsArray().Select(e => e!.AsObject()) ?? [];

    [GeneratedRegex(@"\{\{([^{}]*)\}\}")]
    public static partial Regex PlaceholderRegex();

    /// <summary>All strings inside a JSON value (object keys included), recursively.</summary>
    public static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case null:
                yield break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    yield return key;
                    foreach (var s in Strings(value))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var s in Strings(item))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                break;
        }
    }

    /// <summary>Placeholder names used anywhere in the request (path, query, headers, body).</summary>
    public static ISet<string> RequestPlaceholders(JsonObject command) =>
        Strings(Request(command)).SelectMany(s => PlaceholderRegex().Matches(s).Select(m => m.Groups[1].Value)).ToHashSet();

    /// <summary>Placeholder names used in response.extract (param keys and paths).</summary>
    public static ISet<string> ExtractPlaceholders(JsonObject command) =>
        Extracts(command).SelectMany(e => new[] { e["param"], e["path"] })
            .Where(n => n is not null)
            .SelectMany(n => PlaceholderRegex().Matches(n!.GetValue<string>()).Select(m => m.Groups[1].Value))
            .ToHashSet();

    private static List<LibraryFile> Load()
    {
        return Directory.GetFiles(LibraryDirectory, "*.json")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p =>
            {
                var text = File.ReadAllText(p);
                return new LibraryFile(Path.GetFileName(p), text, JsonNode.Parse(text)!.AsObject());
            })
            .ToList();
    }
}
