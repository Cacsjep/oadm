using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// One VAPIX Commander command, format v1 (plugins/Oadm.Plugins.VapixCommander/Schema/command-format.md, command.schema.json).
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

    /// <summary>"any": the device must list the API, in any version (a CGI whose interface does not change with the listed major version).</summary>
    public const string AnyVersion = "any";

    public bool IsAnyVersion => string.Equals(MinVersion, AnyVersion, StringComparison.OrdinalIgnoreCase);
}

public sealed class CommandField
{
    public string Name { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>string, integer, number, boolean, enum, password, file (<see cref="FieldTypes"/>).</summary>
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

    [JsonIgnore]
    public bool IsFile => Type == FieldTypes.File;
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

    /// <summary>none, json, form, text, xml, multipart (<see cref="BodyTypes"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyType { get; set; }

    /// <summary>JSON value for json, object of strings for form, string for text/xml, array of parts for multipart (<see cref="MultipartParts"/>).</summary>
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

    /// <summary>
    /// A value only some devices have (WDR, an audio output, a zoom lens): left out when the answer lacks it instead of
    /// "(not found)"; a param.cgi group whose values are all optional may be missing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Optional { get; set; }
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

    /// <summary>A file the user chooses; the value is {"name", "data"} with the content in base64 (<see cref="Oadm.Sdk.Actions.ActionFile"/>).</summary>
    public const string File = "file";

    public static readonly IReadOnlyList<string> All = [String, Integer, Number, Boolean, Enum, Password, File];
}
#pragma warning restore CA1720

public static class BodyTypes
{
    public const string None = "none";
    public const string Json = "json";
    public const string Form = "form";
    public const string Text = "text";
    public const string Xml = "xml";
    public const string Multipart = "multipart";

    public static readonly IReadOnlyList<string> All = [None, Json, Form, Text, Xml, Multipart];
}

/// <summary>
/// The parts of a multipart body (multipart/form-data): an array of objects, each with a <c>name</c> and exactly one of
/// <c>json</c> (a JSON value, filled like a json body), <c>text</c> (a string with placeholders) or <c>file</c> (exactly
/// "{{field}}" of a file field); optional <c>contentType</c>, and <c>fileName</c> for a file part (else the chosen file's name).
/// </summary>
public static class MultipartParts
{
    public const string Name = "name";
    public const string Json = "json";
    public const string Text = "text";
    public const string File = "file";
    public const string ContentType = "contentType";
    public const string FileName = "fileName";

    public const int MaxParts = 10;
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
        "Siren and light", "Events", "Storage", "Applications", "Maintenance", Custom,
    ];
}

public static class CommandLimits
{
    public const int DefaultTimeoutSeconds = 15;
    public const int MaxTimeoutSeconds = 600;
    public const int FormatVersion = 1;

    /// <summary>Largest file of a file field (1 MB): it travels with the command and is kept with a saved command.</summary>
    public const int MaxFileBytes = 1024 * 1024;
}
