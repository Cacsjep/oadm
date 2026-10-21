using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// Structural checks of a command definition (format v1). Library commands that fail are skipped and
/// reported; saved, imported and raw commands are refused. Value checks live in <see cref="CommandRenderer"/>.
/// </summary>
public static partial class CommandValidator
{
    private static readonly string[] ForbiddenHeaders = ["Authorization", "Proxy-Authorization", "Cookie", "Host", "Content-Length"];

    /// <summary>Field error of a path that could leave the device.</summary>
    public const string PathProblem = "The path must start with / and stay on the device: no \"..\", \"//\", \"\\\" or host name.";

    /// <summary>
    /// Whether a path template stays on the device's own address: starts with one "/", no backslash, no "//" anywhere,
    /// no scheme or authority ("://", "@" before the query), no ".." segment and no control characters or spaces.
    /// </summary>
    public static bool IsDevicePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        var q = path.IndexOf('?', StringComparison.Ordinal);
        var pathPart = q < 0 ? path : path[..q];
        return !path.Contains('\\', StringComparison.Ordinal)
            && !path.Contains("//", StringComparison.Ordinal)
            && !pathPart.Contains('@', StringComparison.Ordinal)
            && !path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            && !path.Split('/', '?').Contains("..");
    }

    [GeneratedRegex("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex("^([0-9]+\\.[0-9]+|any)$", RegexOptions.CultureInvariant)]
    private static partial Regex MinVersionRegex();

    /// <summary>All problems of <paramref name="command"/>; empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var problems = new List<string>();
        if (!IdRegex().IsMatch(command.Id ?? string.Empty))
        {
            problems.Add($"Id \"{command.Id}\" must look like \"category.name\" (lower case letters, digits, dots and dashes).");
        }

        if (command.Version < 1)
        {
            problems.Add("Version must be 1 or higher.");
        }

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is < 3 or > 80)
        {
            problems.Add("Name must have 3 to 80 characters.");
        }

        if (!CommandCategories.All.Contains(command.Category))
        {
            problems.Add($"Unknown category \"{command.Category}\".");
        }

        ValidateRequires(command, problems);
        var fieldNames = ValidateFields(command, problems);
        ValidateRequest(command.Request, problems);
        ValidateResponse(command.Response, problems);

        if (command.Request is not null)
        {
            foreach (var used in Placeholders.InRequest(command.Request).Where(p => !fieldNames.Contains(p)).Distinct(StringComparer.Ordinal))
            {
                problems.Add($"Placeholder {{{{{used}}}}} has no field.");
            }

            ValidateFileUses(command, problems);
        }

        return problems;
    }

    /// <summary>Throws <see cref="CommandValidationException"/> when <see cref="Validate"/> finds problems.</summary>
    public static void EnsureValid(CommandDefinition command)
    {
        var problems = Validate(command);
        if (problems.Count > 0)
        {
            throw new CommandValidationException(problems);
        }
    }

    private static void ValidateRequires(CommandDefinition command, List<string> problems)
    {
        if (command.Requires is not { Count: > 0 })
        {
            problems.Add("At least one required API (requires) is mandatory, e.g. param-cgi 1.0.");
            return;
        }

        foreach (var requirement in command.Requires)
        {
            if (string.IsNullOrWhiteSpace(requirement.Api))
            {
                problems.Add("A required API needs an id, e.g. param-cgi.");
            }

            if (!MinVersionRegex().IsMatch(requirement.MinVersion ?? string.Empty))
            {
                problems.Add($"Minimum version \"{requirement.MinVersion}\" of {requirement.Api} must look like 1.0 (or any).");
            }
        }
    }

    private static HashSet<string> ValidateFields(CommandDefinition command, List<string> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in command.Fields ?? [])
        {
            if (!FieldNameRegex().IsMatch(field.Name ?? string.Empty))
            {
                problems.Add($"Field name \"{field.Name}\" must start with a letter and contain only letters, digits and _.");
                continue;
            }

            if (!names.Add(field.Name!))
            {
                problems.Add($"Field \"{field.Name}\" is defined twice.");
            }

            if (string.IsNullOrWhiteSpace(field.Label))
            {
                problems.Add($"Field \"{field.Name}\" needs a label.");
            }

            if (!FieldTypes.All.Contains(field.Type))
            {
                problems.Add($"Field \"{field.Name}\" has the unknown type \"{field.Type}\".");
                continue;
            }

            if (field.Type == FieldTypes.Enum && field.Options is not { Count: > 0 })
            {
                problems.Add($"Field \"{field.Name}\" is an enum without options.");
            }

            if (field.IsFile && (field.Pattern is not null || field.Min is not null || field.Max is not null || field.Options is not null))
            {
                problems.Add($"Field \"{field.Name}\" is a file: it takes no pattern, min, max or options.");
            }

            if (field.Min is { } min && field.Max is { } max && min > max)
            {
                problems.Add($"Field \"{field.Name}\": min is greater than max.");
            }

            if (field.Pattern is { } pattern)
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException)
                {
                    problems.Add($"Field \"{field.Name}\" has an invalid pattern.");
                }
            }

            // An empty default ("") means "no default" (the user has to enter a value).
            if (field.Default is { } value && value.ValueKind != JsonValueKind.Null && !field.IsSecret
                && !(value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0)
                && FieldValues.Convert(field, value, out _) is { } error)
            {
                problems.Add($"Default of field \"{field.Name}\": {error}");
            }
        }

        return names;
    }

    private static void ValidateRequest(CommandRequest? request, List<string> problems)
    {
        if (request is null)
        {
            problems.Add("The request is missing.");
            return;
        }

        if (!HttpMethods.All.Contains(request.Method))
        {
            problems.Add($"Unknown method \"{request.Method}\" (GET, POST, PUT, PATCH, DELETE).");
        }

        if (!IsDevicePath(request.Path))
        {
            problems.Add(PathProblem);
        }

        foreach (var key in request.Query?.Keys ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                problems.Add("A query parameter has no name.");
            }
        }

        foreach (var (key, value) in request.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is ':' or ' ' or '\r' or '\n') || (value ?? string.Empty).Any(c => c is '\r' or '\n'))
            {
                problems.Add($"Header \"{key}\" is not valid.");
            }
            else if (ForbiddenHeaders.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"Header \"{key}\" is not allowed (OADM handles authentication).");
            }
        }

        var bodyType = request.EffectiveBodyType;
        if (!BodyTypes.All.Contains(bodyType))
        {
            problems.Add($"Unknown body type \"{bodyType}\" (none, json, form, text, xml, multipart).");
        }
        else
        {
            var body = request.Body;
            switch (bodyType)
            {
                case BodyTypes.None when body is not null:
                    problems.Add("A body needs a body type (json, form, text or xml).");
                    break;
                case BodyTypes.Json when body is null:
                    problems.Add("The JSON body is missing.");
                    break;
                case BodyTypes.Form when body is not JsonObject form || form.Any(p => p.Value is not JsonValue v || v.GetValueKind() != JsonValueKind.String):
                    problems.Add("A form body must be an object of string values.");
                    break;
                case BodyTypes.Text or BodyTypes.Xml when body is not JsonValue text || text.GetValueKind() != JsonValueKind.String:
                    problems.Add($"A {bodyType} body must be a string.");
                    break;
                case BodyTypes.Multipart:
                    ValidateParts(body, problems);
                    break;
            }
        }

        if (request.TimeoutSeconds is < 1 or > CommandLimits.MaxTimeoutSeconds)
        {
            problems.Add($"The timeout must be 1 to {CommandLimits.MaxTimeoutSeconds} seconds.");
        }
    }

    /// <summary>A multipart body: 1 to <see cref="MultipartParts.MaxParts"/> parts, each with a name and exactly one of json, text or file.</summary>
    private static void ValidateParts(JsonNode? body, List<string> problems)
    {
        if (body is not JsonArray parts || parts.Count is 0 or > MultipartParts.MaxParts)
        {
            problems.Add($"A multipart body must be a list of 1 to {MultipartParts.MaxParts} parts.");
            return;
        }

        string[] known = [MultipartParts.Name, MultipartParts.Json, MultipartParts.Text, MultipartParts.File, MultipartParts.ContentType, MultipartParts.FileName];
        foreach (var node in parts)
        {
            if (node is not JsonObject part)
            {
                problems.Add("Every multipart part must be an object.");
                continue;
            }

            var name = StringOf(part[MultipartParts.Name]);
            if (string.IsNullOrWhiteSpace(name) || !IsHeaderSafe(name))
            {
                problems.Add("Every multipart part needs a name without quotes or line breaks.");
                continue;
            }

            if (part.Select(p => p.Key).FirstOrDefault(k => !known.Contains(k)) is { } unknown)
            {
                problems.Add($"Part \"{name}\" has the unknown property \"{unknown}\" (name, json, text, file, contentType, fileName).");
            }

            var kinds = new[] { MultipartParts.Json, MultipartParts.Text, MultipartParts.File }.Count(part.ContainsKey);
            if (kinds != 1)
            {
                problems.Add($"Part \"{name}\" needs exactly one of json, text or file.");
            }

            if (part.ContainsKey(MultipartParts.Text) && StringOf(part[MultipartParts.Text]) is null)
            {
                problems.Add($"The text of part \"{name}\" must be a string.");
            }

            if (part.ContainsKey(MultipartParts.File) && Placeholders.Whole(StringOf(part[MultipartParts.File]) ?? string.Empty) is null)
            {
                problems.Add($"The file of part \"{name}\" must be exactly one {{{{field}}}} of a file field.");
            }

            if (part.ContainsKey(MultipartParts.ContentType)
                && (StringOf(part[MultipartParts.ContentType]) is not { } contentType || !MediaTypeHeaderValue.TryParse(contentType, out _)))
            {
                problems.Add($"The content type of part \"{name}\" is not valid.");
            }

            if (part.ContainsKey(MultipartParts.FileName)
                && (!part.ContainsKey(MultipartParts.File) || StringOf(part[MultipartParts.FileName]) is not { Length: > 0 } fileName
                    || !IsHeaderSafe(fileName) || fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal)))
            {
                problems.Add($"The file name of part \"{name}\" is not valid (file parts only, no folders, quotes or line breaks).");
            }
        }

        static string? StringOf(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

        static bool IsHeaderSafe(string text) => !text.Any(c => char.IsControl(c) || c == '"');
    }

    /// <summary>
    /// A file field is only the file of a multipart part ("file": "{{field}}"): its bytes cannot go into a path, query,
    /// header or text. Every other placeholder must not name a file field.
    /// </summary>
    private static void ValidateFileUses(CommandDefinition command, List<string> problems)
    {
        var files = (command.Fields ?? []).Where(f => f.IsFile).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var request = command.Request;
        var fileParts = new List<string>();
        CommandRequest withoutFileParts = request;
        if (request.EffectiveBodyType == BodyTypes.Multipart && request.Body is JsonArray parts)
        {
            var kept = new JsonArray();
            foreach (var part in parts)
            {
                if (part is JsonObject obj && obj[MultipartParts.File] is JsonValue file && file.GetValueKind() == JsonValueKind.String)
                {
                    fileParts.Add(file.GetValue<string>());
                    var copy = (JsonObject)obj.DeepClone();
                    copy.Remove(MultipartParts.File);
                    kept.Add(copy);
                }
                else
                {
                    kept.Add(part?.DeepClone());
                }
            }

            withoutFileParts = new CommandRequest { Method = request.Method, Path = request.Path, Query = request.Query, Headers = request.Headers, Body = kept };
        }

        foreach (var name in Placeholders.InRequest(withoutFileParts).Where(files.Contains).Distinct(StringComparer.Ordinal))
        {
            problems.Add($"File field \"{name}\" can only be the file of a multipart part.");
        }

        foreach (var name in fileParts.Select(Placeholders.Whole).OfType<string>().Where(n => !files.Contains(n)).Distinct(StringComparer.Ordinal))
        {
            if (command.Fields?.Any(f => f.Name == name) == true)
            {
                problems.Add($"Field \"{name}\" is the file of a multipart part, so its type must be file.");
            }
        }
    }

    private static void ValidateResponse(CommandResponse? response, List<string> problems)
    {
        if (response is null)
        {
            problems.Add("The response interpretation is missing.");
            return;
        }

        if (!ResponseKinds.All.Contains(response.Kind))
        {
            problems.Add($"Unknown response kind \"{response.Kind}\" (param-cgi, json-rpc, rest, text, xml, raw).");
        }

        if (response.ErrorPattern is { } errorPattern)
        {
            try
            {
                _ = new Regex(errorPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                problems.Add("The errorPattern is not a valid regular expression.");
            }
        }

        foreach (var extract in response.Extract ?? [])
        {
            if (string.IsNullOrWhiteSpace(extract.Label))
            {
                problems.Add("Every extracted value needs a label.");
            }

            if (string.IsNullOrWhiteSpace(extract.Path) == string.IsNullOrWhiteSpace(extract.Param))
            {
                problems.Add($"Extracted value \"{extract.Label}\" needs either a path or a param.");
            }
        }
    }
}

/// <summary>The command or the entered values are not valid; nothing was sent. <see cref="Exception.Message"/> joins the problems.</summary>
public sealed class CommandValidationException : Exception
{
    public CommandValidationException(IReadOnlyList<string> problems)
        : base(string.Join(" ", problems))
    {
        Problems = problems;
    }

    public CommandValidationException()
        : this([])
    {
    }

    public CommandValidationException(string message)
        : base(message)
    {
        Problems = [message];
    }

    public CommandValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    public IReadOnlyList<string> Problems { get; }
}
