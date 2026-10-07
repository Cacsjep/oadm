using System.Globalization;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// Saved commands, stored on the server in the plugin settings (key <see cref="SettingsKey"/>) and shared by all
/// clients. Password values are encrypted with the server master key (<see cref="ISecretProtector"/>, purpose
/// "vapix-commander:&lt;id&gt;:&lt;field&gt;") or dropped when the host has no protector; they are never returned,
/// exported or logged.
/// </summary>
#pragma warning disable CA1001 // The gate lives as long as the plugin (one instance per server process).
public sealed class SavedCommandStore
#pragma warning restore CA1001
{
    public const string SettingsKey = "savedCommands";

    /// <summary>Upper bound so one plugin setting stays small.</summary>
    public const int MaxCommands = 500;

    private readonly IPluginSettings _settings;
    private readonly ISecretProtector? _secrets;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SavedCommandStore(IPluginSettings settings, ISecretProtector? secrets, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _secrets = secrets;
        _time = time ?? TimeProvider.System;
    }

    public bool CanStoreSecrets => _secrets is not null;

    public async Task<IReadOnlyList<CommandListItem>> ListAsync(CancellationToken ct)
    {
        var records = await ReadAsync(ct).ConfigureAwait(false);
        return [.. records.OrderBy(r => r.Command.Category, StringComparer.Ordinal).ThenBy(r => r.Command.Name, StringComparer.OrdinalIgnoreCase).Select(ToItem)];
    }

    public async Task<CommandDefinition?> FindAsync(string id, CancellationToken ct)
    {
        var records = await ReadAsync(ct).ConfigureAwait(false);
        return records.Find(r => r.Command.Id == id)?.Command.Clone();
    }

    /// <summary>Stored password values of a saved command (decrypted, server memory only), by field name.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(string id, CancellationToken ct)
    {
        var records = await ReadAsync(ct).ConfigureAwait(false);
        var record = records.Find(r => r.Command.Id == id);
        if (record?.Secrets is not { Count: > 0 } || _secrets is null)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, value) in record.Secrets)
        {
            result[field] = _secrets.Unprotect(value, Purpose(id, field));
        }

        return result;
    }

    /// <summary>
    /// Saves (adds or replaces) a command. A missing id is generated from the name ("custom.day-night-level-50").
    /// Throws <see cref="CommandValidationException"/> for an invalid command.
    /// </summary>
    public async Task<CommandListItem> SaveAsync(CommandDefinition command, string? previousId, string? owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(ct).ConfigureAwait(false);
            var record = Upsert(records, command.Clone(), previousId, owner);
            await WriteAsync(records, ct).ConfigureAwait(false);
            return ToItem(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(ct).ConfigureAwait(false);
            if (records.RemoveAll(r => r.Command.Id == id) == 0)
            {
                return false;
            }

            await WriteAsync(records, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Library-format JSON of the given saved commands (all when empty), without password values.</summary>
    public async Task<ExportReply> ExportAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var records = await ReadAsync(ct).ConfigureAwait(false);
        var commands = records.Where(r => ids.Count == 0 || ids.Contains(r.Command.Id)).Select(r => WithoutSecrets(r.Command)).ToList();
        var categories = commands.Select(c => c.Category).Distinct(StringComparer.Ordinal).ToList();
        var category = categories.Count == 1 ? categories[0] : CommandCategories.Custom;
        var json = CommandJson.WriteLibraryFile(new LibraryFile { FormatVersion = CommandLimits.FormatVersion, Category = category, Commands = commands });
        var fileName = commands.Count == 1 ? Slug(commands[0].Name) + ".json" : "vapix-commands.json";
        return new ExportReply { Json = json, FileName = fileName, Count = commands.Count };
    }

    /// <summary>Imports a library/export file: valid commands are saved (same id replaces), invalid ones are reported.</summary>
    public async Task<ImportReply> ImportAsync(string json, string? owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        LibraryFile file;
        try
        {
            file = CommandJson.ParseLibraryFile(json);
        }
        catch (JsonException ex)
        {
            return new ImportReply { Problems = ["The file is not a VAPIX Commander command file: " + ex.Message] };
        }

        if (file.FormatVersion != CommandLimits.FormatVersion)
        {
            return new ImportReply { Problems = [$"formatVersion {file.FormatVersion} is not supported (expected 1)."] };
        }

        var reply = new ImportReply();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(ct).ConfigureAwait(false);
            foreach (var command in file.Commands ?? [])
            {
                try
                {
                    Upsert(records, command, command.Id, owner);
                    reply.Imported++;
                }
                catch (CommandValidationException ex)
                {
                    reply.Problems.Add($"{(string.IsNullOrEmpty(command.Name) ? command.Id : command.Name)}: {ex.Message}");
                }
            }

            if (reply.Imported > 0)
            {
                await WriteAsync(records, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        return reply;
    }

    /// <summary>"custom." + lower-case name with dashes.</summary>
    public static string NewId(string name, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var baseId = "custom." + Slug(name);
        var id = baseId;
        for (var i = 2; used.Contains(id); i++)
        {
            id = baseId + "-" + i.ToString(CultureInfo.InvariantCulture);
        }

        return id;
    }

    private static string Slug(string? name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? string.Empty).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 60)
        {
            slug = slug[..60].Trim('-');
        }

        return slug.Length == 0 ? "command" : slug;
    }

    private static string Purpose(string id, string field) => "vapix-commander:" + id + ":" + field;

    private static CommandDefinition WithoutSecrets(CommandDefinition command)
    {
        var copy = command.Clone();
        foreach (var field in copy.Fields.Where(f => f.IsSecret))
        {
            field.Default = null;
        }

        return copy;
    }

    private SavedRecord Upsert(List<SavedRecord> records, CommandDefinition command, string? previousId, string? owner)
    {
        command.Name = command.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command.Id))
        {
            command.Id = NewId(command.Name, records.Where(r => r.Command.Id != previousId).Select(r => r.Command.Id));
        }

        if (string.IsNullOrWhiteSpace(command.Category))
        {
            command.Category = CommandCategories.Custom;
        }

        CommandValidator.EnsureValid(command);
        var previous = records.Find(r => r.Command.Id == (previousId ?? command.Id)) ?? records.Find(r => r.Command.Id == command.Id);
        if (previous is null && records.Count >= MaxCommands)
        {
            throw new CommandValidationException($"At most {MaxCommands} saved commands are allowed.");
        }

        // Password values: encrypt what was entered, keep what was stored before for fields left empty.
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in command.Fields.Where(f => f.IsSecret))
        {
            var entered = field.Default is { ValueKind: JsonValueKind.String } d ? d.GetString() : null;
            field.Default = null;
            if (!string.IsNullOrEmpty(entered))
            {
                if (_secrets is not null)
                {
                    secrets[field.Name] = _secrets.Protect(entered, Purpose(command.Id, field.Name));
                }
            }
            else if (previous?.Secrets is { } old && old.TryGetValue(field.Name, out var kept) && _secrets is not null)
            {
                // Re-bind the stored value to the (possibly renamed) id.
                secrets[field.Name] = previous.Command.Id == command.Id
                    ? kept
                    : _secrets.Protect(_secrets.Unprotect(kept, Purpose(previous.Command.Id, field.Name)), Purpose(command.Id, field.Name));
            }
        }

        var now = _time.GetUtcNow();
        var record = new SavedRecord
        {
            Command = command,
            Secrets = secrets.Count == 0 ? null : secrets,
            CreatedUtc = previous?.CreatedUtc ?? now,
            UpdatedUtc = now,
            UpdatedBy = owner,
        };
        if (previous is not null)
        {
            records.Remove(previous);
        }

        records.RemoveAll(r => r.Command.Id == command.Id);
        records.Add(record);
        return record;
    }

    private static CommandListItem ToItem(SavedRecord record) => new()
    {
        Source = CommandSources.Saved,
        Command = WithoutSecrets(record.Command),
        StoredSecretFields = record.Secrets is null ? [] : [.. record.Secrets.Keys],
        UpdatedUtc = record.UpdatedUtc,
        UpdatedBy = record.UpdatedBy,
    };

    private async Task<List<SavedRecord>> ReadAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync(SettingsKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<SavedFile>(json, CommandJson.Api)?.Commands ?? [];
    }

    private Task WriteAsync(List<SavedRecord> records, CancellationToken ct) =>
        _settings.SetAsync(SettingsKey, JsonSerializer.Serialize(new SavedFile { Commands = records }, CommandJson.Api), ct);

    private sealed class SavedFile
    {
        public List<SavedRecord> Commands { get; set; } = [];
    }

    private sealed class SavedRecord
    {
        public CommandDefinition Command { get; set; } = new();

        /// <summary>Encrypted password values by field name.</summary>
        public Dictionary<string, string>? Secrets { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public DateTimeOffset UpdatedUtc { get; set; }

        public string? UpdatedBy { get; set; }
    }
}
