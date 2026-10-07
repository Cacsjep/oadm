using System.Text.Json;

namespace Oadm.Plugins.VapixCommander;

/// <summary>Method names of <c>ICorePlugin.InvokeAsync</c> used by the page (payloads: the records below, camelCase JSON).</summary>
public static class CommanderMethods
{
    public const string ListLibrary = "listLibrary";
    public const string ListSaved = "listSaved";
    public const string Save = "save";
    public const string Delete = "delete";
    public const string Export = "export";
    public const string Import = "import";
    public const string TryRequest = "tryRequest";
    public const string CheckCompatibility = "checkCompatibility";
    public const string Rollout = "rollout";
}

public static class CommandSources
{
    public const string Library = "library";
    public const string Saved = "saved";

    /// <summary>A command built in the raw editor, sent in full.</summary>
    public const string Inline = "inline";
}

/// <summary>Which command: a library or saved one by id (the server's definition is used), or an inline definition.</summary>
public sealed class CommandRef
{
    public string Source { get; set; } = CommandSources.Library;

    public string? Id { get; set; }

    public CommandDefinition? Command { get; set; }
}

/// <summary>A command as listed for the page. Password defaults are never included.</summary>
public sealed class CommandListItem
{
    public string Source { get; set; } = CommandSources.Library;

    public CommandDefinition Command { get; set; } = new();

    /// <summary>Saved commands: password fields with a value stored (encrypted) on the server; leave them empty to use it.</summary>
    public List<string> StoredSecretFields { get; set; } = [];

    public DateTimeOffset? UpdatedUtc { get; set; }

    public string? UpdatedBy { get; set; }
}

public sealed class CommandListReply
{
    public List<CommandListItem> Commands { get; set; } = [];

    /// <summary>Library files or commands that could not be loaded ("Video.json: ...").</summary>
    public List<string> Problems { get; set; } = [];
}

public sealed class SaveCommandRequest
{
    /// <summary>The command; password field defaults are stored encrypted (or dropped without a protector), never returned.</summary>
    public CommandDefinition Command { get; set; } = new();

    /// <summary>Id of the saved command being edited (renaming replaces it); null for a new one.</summary>
    public string? PreviousId { get; set; }

    public string? Owner { get; set; }
}

public sealed class SaveCommandReply
{
    public string? Error { get; set; }

    public CommandListItem? Saved { get; set; }
}

public sealed class DeleteCommandRequest
{
    public string Id { get; set; } = string.Empty;
}

public sealed class OkReply
{
    public bool Ok { get; set; }

    public string? Error { get; set; }
}

public sealed class ExportRequest
{
    /// <summary>Saved command ids; empty = all.</summary>
    public List<string> Ids { get; set; } = [];
}

public sealed class ExportReply
{
    /// <summary>Library file JSON (formatVersion 1), password values omitted.</summary>
    public string Json { get; set; } = string.Empty;

    public string FileName { get; set; } = "vapix-commands.json";

    public int Count { get; set; }
}

public sealed class ImportRequest
{
    public string Json { get; set; } = string.Empty;

    public string? Owner { get; set; }
}

public sealed class ImportReply
{
    public int Imported { get; set; }

    public List<string> Problems { get; set; } = [];
}

public sealed class TryCommandRequest
{
    public Guid DeviceId { get; set; }

    public CommandRef Command { get; set; } = new();

    public Dictionary<string, JsonElement> Values { get; set; } = [];

    /// <summary>Must be true for commands that write; the page asks the user first.</summary>
    public bool Confirmed { get; set; }
}

public sealed class TryCommandReply
{
    /// <summary>Nothing was sent: validation, compatibility, device state or missing confirmation.</summary>
    public string? Error { get; set; }

    public bool NeedsConfirmation { get; set; }

    public TryOutcome? Outcome { get; set; }
}

/// <summary>The Postman-like view of one sent command.</summary>
public sealed class TryOutcome
{
    public bool Success { get; set; }

    public string Summary { get; set; } = string.Empty;

    public List<NamedValue> Values { get; set; } = [];

    public List<NamedValue> Parameters { get; set; } = [];

    public int? StatusCode { get; set; }

    public string? ReasonPhrase { get; set; }

    public double DurationMs { get; set; }

    public List<NamedValue> Headers { get; set; } = [];

    public string? ContentType { get; set; }

    public string? Body { get; set; }

    public bool BodyTruncated { get; set; }

    public string? RequestLine { get; set; }

    public string? RequestBody { get; set; }

    public static TryOutcome From(CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new TryOutcome
        {
            Success = outcome.Success,
            Summary = outcome.Summary,
            Values = [.. outcome.Values],
            Parameters = [.. outcome.Parameters],
            StatusCode = outcome.StatusCode,
            ReasonPhrase = outcome.ReasonPhrase,
            DurationMs = Math.Round(outcome.Duration.TotalMilliseconds),
            Headers = [.. outcome.ResponseHeaders],
            ContentType = outcome.ContentType,
            Body = outcome.Body,
            BodyTruncated = outcome.BodyTruncated,
            RequestLine = outcome.RequestLine,
            RequestBody = outcome.RequestBody,
        };
    }
}

public sealed class CompatibilityRequest
{
    public List<Guid> DeviceIds { get; set; } = [];

    public List<CommandRef> Commands { get; set; } = [];
}

public sealed class CompatibilityReply
{
    public List<DeviceCompatibility> Devices { get; set; } = [];
}

public sealed class DeviceCompatibility
{
    public Guid DeviceId { get; set; }

    /// <summary>One entry per requested command, same order.</summary>
    public List<CompatibilityEntry> Commands { get; set; } = [];
}

public sealed class CompatibilityEntry
{
    public CompatibilityState State { get; set; }

    public string Text { get; set; } = string.Empty;
}

public sealed class RolloutCommand
{
    public CommandRef Command { get; set; } = new();

    public Dictionary<string, JsonElement> Values { get; set; } = [];
}

public sealed class RolloutRequest
{
    public List<Guid> DeviceIds { get; set; } = [];

    public List<RolloutCommand> Commands { get; set; } = [];

    public bool StopOnFirstError { get; set; }

    /// <summary>Must be true when a command writes; the page shows the confirmation summary first.</summary>
    public bool Confirmed { get; set; }

    public string? Owner { get; set; }
}

public sealed class RolloutReply
{
    public string? Error { get; set; }

    public bool NeedsConfirmation { get; set; }

    public Guid RolloutId { get; set; }

    public List<Guid> TaskIds { get; set; } = [];
}

/// <summary>Payload of the rollout task (in server memory only; it may carry password values).</summary>
internal sealed class RolloutPayload
{
    public Guid RolloutId { get; set; }

    public bool StopOnFirstError { get; set; }

    public List<RolloutPayloadCommand> Commands { get; set; } = [];

    public override string ToString() => $"Rollout {RolloutId} ({Commands.Count} commands)";
}

internal sealed class RolloutPayloadCommand
{
    public CommandDefinition Command { get; set; } = new();

    public Dictionary<string, JsonElement> Values { get; set; } = [];
}
