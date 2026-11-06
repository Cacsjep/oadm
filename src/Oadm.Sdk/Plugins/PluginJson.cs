using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Sdk.Plugins;

/// <summary>How <see cref="PluginJson.CreateOptions"/> writes and reads enums.</summary>
public enum PluginJsonEnums
{
    /// <summary>As numbers (an enum type with its own converter keeps that).</summary>
    Numbers = 0,

    /// <summary>As their names: "NotRunning".</summary>
    Names = 1,

    /// <summary>As their names in camelCase: "notRunning".</summary>
    CamelCase = 2,
}

/// <summary>
/// The JSON of a plugin's page calls, events, task payloads and stored settings, shared by its server and client part:
/// the common serializer options and parse methods with one error rule. An empty or broken text from a caller is an
/// <see cref="ArgumentException"/> with a plain message, which the server answers as a refusal (INVALID_ARGUMENT), never
/// as a failure of the plugin. The message never quotes the text or the reader's own error: a payload may hold a password.
/// SDK 0.12.0.
/// </summary>
/// <remarks>
/// A plugin keeps the options in a static of its own (<see cref="CreateOptions"/>): the serializer remembers the plugin's
/// types in them, and they must go away with the plugin when it is unloaded.
/// </remarks>
public static class PluginJson
{
    /// <summary>What <see cref="Parse{T}"/> says for no text at all.</summary>
    public const string EmptyRequest = "The request is empty.";

    /// <summary>What the parse methods say for text that is not the expected JSON.</summary>
    public const string UnreadableRequest = "The request cannot be read.";

    /// <summary>
    /// New options with the common settings: camelCase names, names read in any case, numbers also read from strings
    /// (<see cref="JsonSerializerDefaults.Web"/>), and null values left out unless <paramref name="writeNulls"/>.
    /// Create them once per plugin and keep them in a static field.
    /// </summary>
    public static JsonSerializerOptions CreateOptions(PluginJsonEnums enums = PluginJsonEnums.Numbers, bool writeNulls = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = writeNulls ? JsonIgnoreCondition.Never : JsonIgnoreCondition.WhenWritingNull,
        };
        switch (enums)
        {
            case PluginJsonEnums.Names:
                options.Converters.Add(new JsonStringEnumConverter());
                break;
            case PluginJsonEnums.CamelCase:
                options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                break;
        }

        return options;
    }

    /// <summary>The JSON text of <paramref name="value"/>.</summary>
    public static string Serialize<T>(T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(value, options);
    }

    /// <summary>
    /// The value of a text that must be there. No text, blank text or <c>null</c>: <see cref="ArgumentException"/>
    /// (<see cref="EmptyRequest"/>); broken JSON: <see cref="ArgumentException"/> (<see cref="UnreadableRequest"/>).
    /// </summary>
    public static T Parse<T>(string? json, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException(EmptyRequest);
        }

        return Read<T>(json, options) ?? throw new ArgumentException(EmptyRequest);
    }

    /// <summary>
    /// The value of a text that may be left out: no text, blank text or <c>null</c> reads like <c>{}</c>, every member
    /// at its default. Broken JSON: <see cref="ArgumentException"/> (<see cref="UnreadableRequest"/>).
    /// </summary>
    public static T ParseOrEmpty<T>(string? json, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(json) && Read<T>(json, options) is { } value)
        {
            return value;
        }

        return Read<T>("{}", options) ?? throw new ArgumentException(UnreadableRequest);
    }

    /// <summary>
    /// The value of stored text that may be missing or damaged (settings, records, files): null for no text, blank text,
    /// <c>null</c> or broken JSON. Never throws for the text.
    /// </summary>
    public static T? TryParse<T>(string? json, JsonSerializerOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Read<T>(string json, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException)
        {
            // Not chained and not quoted: the reader's text names .NET types and positions, and the payload may hold a password.
            throw new ArgumentException(UnreadableRequest);
        }
    }
}
