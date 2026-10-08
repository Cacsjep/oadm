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

    [GeneratedRegex("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
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
                problems.Add($"Minimum version \"{requirement.MinVersion}\" of {requirement.Api} must look like 1.0.");
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
            problems.Add($"Unknown body type \"{bodyType}\" (none, json, form, text, xml).");
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
            }
        }

        if (request.TimeoutSeconds is < 1 or > CommandLimits.MaxTimeoutSeconds)
        {
            problems.Add($"The timeout must be 1 to {CommandLimits.MaxTimeoutSeconds} seconds.");
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
