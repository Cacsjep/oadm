using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// One VAPIX Commander command, format v1 (docs/vapix-commander/command-format.md, command.schema.json).
/// Library files and saved commands use the same shape.
/// </summary>
public sealed class CommandDefinition
{
    public string Id { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Docs { get; set; }

    public List<ApiRequirement> Requires { get; set; } = [];

    public bool Writes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Dangerous { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasVideoOnly { get; set; }

    public List<CommandField> Fields { get; set; } = [];

    public CommandRequest Request { get; set; } = new();

    public CommandResponse Response { get; set; } = new();

    public CommandDefinition Clone() => CommandJson.Clone(this);
}

public sealed class ApiRequirement
{
    public string Api { get; set; } = string.Empty;

    public string MinVersion { get; set; } = string.Empty;
}

public sealed class CommandField
{
    public string Name { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>string, integer, number, boolean, enum, password (<see cref="FieldTypes"/>).</summary>
    public string Type { get; set; } = FieldTypes.String;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Default { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrueValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FalseValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FieldOption>? Options { get; set; }

    [JsonIgnore]
    public bool IsRequired => Required ?? true;

    [JsonIgnore]
    public bool IsSecret => Type == FieldTypes.Password;
}

public sealed class FieldOption
{
    public JsonElement Value { get; set; }

    public string Label { get; set; } = string.Empty;
}

public sealed class CommandRequest
{
    public string Method { get; set; } = "GET";

    public string Path { get; set; } = "/";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Query { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>none, json, form, text, xml (<see cref="BodyTypes"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyType { get; set; }

    /// <summary>JSON value for json, object of strings for form, string for text/xml.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Body { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutSeconds { get; set; }

    [JsonIgnore]
    public string EffectiveBodyType => string.IsNullOrEmpty(BodyType) ? BodyTypes.None : BodyType;

    [JsonIgnore]
    public int EffectiveTimeoutSeconds => TimeoutSeconds ?? CommandLimits.DefaultTimeoutSeconds;
}

public sealed class CommandResponse
{
    /// <summary>param-cgi, json-rpc, rest, text, xml, raw (<see cref="ResponseKinds"/>).</summary>
    public string Kind { get; set; } = ResponseKinds.Raw;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Success { get; set; }

    /// <summary>Optional regex: a body that matches is a device error even with HTTP 2xx (e.g. ptz.cgi "Error: ..."); the matching line is shown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorPattern { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponseExtract>? Extract { get; set; }
}

public sealed class ResponseExtract
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Dotted path into a JSON (or XML) response, e.g. "data.propertyList.ProdNbr"; array index as ".0" or "[0]"; placeholders allowed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>param.cgi parameter name, e.g. "Brand.ProdNbr" (the "root." prefix and surrounding quotes are optional); placeholders allowed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; set; }
}

/// <summary>A library file: <c>{ "formatVersion": 1, "category": "...", "commands": [ ... ] }</c>. Also the export format.</summary>
public sealed class LibraryFile
{
    public int FormatVersion { get; set; } = 1;

    public string Category { get; set; } = string.Empty;

    public List<CommandDefinition> Commands { get; set; } = [];
}

#pragma warning disable CA1720 // The names are the field types of the JSON format.
public static class FieldTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Enum = "enum";
    public const string Password = "password";

    public static readonly IReadOnlyList<string> All = [String, Integer, Number, Boolean, Enum, Password];
}
#pragma warning restore CA1720

public static class BodyTypes
{
    public const string None = "none";
    public const string Json = "json";
    public const string Form = "form";
    public const string Text = "text";
    public const string Xml = "xml";

    public static readonly IReadOnlyList<string> All = [None, Json, Form, Text, Xml];
}

public static class ResponseKinds
{
    public const string ParamCgi = "param-cgi";
    public const string JsonRpc = "json-rpc";
    public const string Rest = "rest";
    public const string Text = "text";
    public const string Xml = "xml";
    public const string Raw = "raw";

    public static readonly IReadOnlyList<string> All = [ParamCgi, JsonRpc, Rest, Text, Xml, Raw];
}

public static class HttpMethods
{
    public static readonly IReadOnlyList<string> All = ["GET", "POST", "PUT", "PATCH", "DELETE"];
}

public static class CommandCategories
{
    /// <summary>Category of saved (user-made) commands.</summary>
    public const string Custom = "Custom";

    /// <summary>Library categories in display order, then <see cref="Custom"/>.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "Common", "System", "Network", "Security", "Users", "Time", "Video", "Image", "PTZ", "Audio", "I/O",
        "Events", "Storage", "Applications", "Maintenance", Custom,
    ];
}

public static class CommandLimits
{
    public const int DefaultTimeoutSeconds = 15;
    public const int MaxTimeoutSeconds = 600;
    public const int FormatVersion = 1;
}
