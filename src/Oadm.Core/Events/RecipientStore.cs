using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Events;

/// <summary>A recipient as the server keeps it: password values encrypted (<see cref="RuleStore.SecretPrefix"/>).</summary>
public sealed record StoredRecipient(Guid Id, string Name, string TypeId, JsonObject Settings, DateTime UpdatedUtc);

/// <summary>A recipient the server refuses; <see cref="Field"/> is the field the message belongs below.</summary>
public sealed class RecipientValidationException : Exception
{
    /// <summary>The field of the name.</summary>
    public const string NameField = "name";

    public RecipientValidationException()
        : base("The recipient is not valid.")
    {
    }

    public RecipientValidationException(string message)
        : base(message)
    {
    }

    public RecipientValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RecipientValidationException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    /// <summary>A field key of the type or <see cref="NameField"/>; empty for the whole recipient.</summary>
    public string Field { get; } = string.Empty;
}

/// <summary>
/// The recipients of the event rules (table Recipients): shared targets, a rule keeps a link to one. Password values are
/// stored encrypted with the master key and never leave the server: <see cref="ForClient"/> removes them and lists their
/// fields; an empty password on save keeps the stored value. Writes are serialized, and every create, change and delete goes
/// through this class and raises <see cref="Changed"/> once.
/// </summary>
public sealed class RecipientStore(IDbContextFactory<OadmDbContext> dbFactory, ISecretProtector secrets, TimeProvider time) : IDisposable
{
    /// <summary>At most this many recipients.</summary>
    public const int MaxRecipients = 200;

    /// <summary>Longest name.</summary>
    public const int MaxNameLength = RecipientType.MaxNameLength;

    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>A recipient was created, changed or deleted: its id, once per write, after the write is stored.</summary>
    public event EventHandler<Guid>? Changed;

    public void Dispose() => _write.Dispose();

    /// <summary>Every recipient, sorted by name.</summary>
    public async Task<IReadOnlyList<StoredRecipient>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Recipients.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToStored).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<StoredRecipient?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Recipients.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    /// <summary>
    /// Creates (<paramref name="id"/> null) or changes a recipient. <paramref name="settings"/> keeps only the
    /// <paramref name="fields"/> of the type; password fields are encrypted, an empty one keeps the stored value. Returns the
    /// stored recipient and whether it is new. Throws <see cref="KeyNotFoundException"/> for an unknown id and
    /// <see cref="RecipientValidationException"/> for a name, the type of a recipient that exists or
    /// <see cref="MaxRecipients"/>.
    /// </summary>
    public async Task<(StoredRecipient Recipient, bool Created)> SaveAsync(
        Guid? id, string name, string typeId, JsonObject settings, IReadOnlyList<ActionField> fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(typeId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fields);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new RecipientValidationException(RecipientValidationException.NameField, "Enter a name.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new RecipientValidationException(RecipientValidationException.NameField, string.Create(CultureInfo.InvariantCulture, $"At most {MaxNameLength} characters."));
        }

        (StoredRecipient Recipient, bool Created) result;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Recipients.ToListAsync(ct).ConfigureAwait(false);
            var row = id is { } known
                ? rows.Find(r => r.Id == known) ?? throw new KeyNotFoundException(RecipientType.NotFoundText)
                : null;
            if (rows.Exists(r => r.Id != row?.Id && string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RecipientValidationException(RecipientValidationException.NameField, RecipientType.NameTakenText);
            }

            if (row is null && rows.Count >= MaxRecipients)
            {
                throw new RecipientValidationException(string.Create(CultureInfo.InvariantCulture, $"At most {MaxRecipients} recipients."));
            }

            if (row is not null && !string.Equals(row.TypeId, typeId, StringComparison.Ordinal))
            {
                throw new RecipientValidationException("The type of a recipient cannot change.");
            }

            var recipientId = row?.Id ?? Guid.NewGuid();
            var stored = Protect(recipientId, settings, row is null ? null : ToStored(row).Settings, fields);
            if (row is null)
            {
                row = new RecipientEntity { Id = recipientId, TypeId = typeId };
                db.Recipients.Add(row);
            }

            var created = db.Entry(row).State == EntityState.Added;
            row.Name = trimmed;
            row.SettingsJson = stored.ToJsonString();
            row.UpdatedUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            result = (ToStored(row), created);
        }
        finally
        {
            _write.Release();
        }

        Oadm.Core.Auth.CallerContext.Raise(Changed, this, result.Recipient.Id);
        return result;
    }

    /// <summary>Removes a recipient. Returns it, null when unknown.</summary>
    public async Task<StoredRecipient?> DeleteAsync(Guid id, CancellationToken ct)
    {
        StoredRecipient removed;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Recipients.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            db.Recipients.Remove(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            removed = ToStored(row);
        }
        finally
        {
            _write.Release();
        }

        Oadm.Core.Auth.CallerContext.Raise(Changed, this, id);
        return removed;
    }

    /// <summary>The values for a client: password values removed, and the fields that have one.</summary>
    public static (JsonObject Values, IReadOnlyList<string> StoredSecretFields) ForClient(StoredRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var values = new JsonObject();
        var stored = new List<string>();
        foreach (var (key, node) in recipient.Settings)
        {
            if (IsSecret(node))
            {
                stored.Add(key);
            }
            else
            {
                values[key] = node?.DeepClone();
            }
        }

        return (values, stored);
    }

    /// <summary>The values for a run or a test: password values decrypted. Unreadable ones (other master key) become empty.</summary>
    public JsonObject DecryptSettings(StoredRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var settings = (JsonObject)recipient.Settings.DeepClone();
        foreach (var (key, node) in settings.ToList())
        {
            if (!IsSecret(node))
            {
                continue;
            }

            try
            {
                settings[key] = secrets.Unprotect(node!.GetValue<string>()[RuleStore.SecretPrefix.Length..], Purpose(recipient.Id, key));
            }
#pragma warning disable CA1031 // A value of an older master key cannot be read; the run goes on without it.
            catch (Exception)
#pragma warning restore CA1031
            {
                settings[key] = string.Empty;
            }
        }

        return settings;
    }

    private JsonObject Protect(Guid id, JsonObject settings, JsonObject? previous, IReadOnlyList<ActionField> fields)
    {
        var stored = new JsonObject();
        foreach (var field in fields)
        {
            var hasValue = settings.TryGetPropertyValue(field.Key, out var node) && node is not null;
            var text = hasValue ? node!.ToString() : string.Empty;
            if (text.StartsWith(RuleStore.SecretPrefix, StringComparison.Ordinal))
            {
                hasValue = false; // a client never sends encrypted values
                text = string.Empty;
            }

            if (field.Type != ActionFieldType.Password)
            {
                if (hasValue)
                {
                    stored[field.Key] = node!.DeepClone();
                }
            }
            else if (text.Length > 0)
            {
                stored[field.Key] = RuleStore.SecretPrefix + secrets.Protect(text, Purpose(id, field.Key));
            }
            else if (previous?[field.Key] is { } kept && IsSecret(kept))
            {
                stored[field.Key] = kept.DeepClone();
            }
        }

        return stored;
    }

    private static bool IsSecret(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith(RuleStore.SecretPrefix, StringComparison.Ordinal);

    private static string Purpose(Guid id, string field) => $"oadm:recipient:{id:N}:{field}";

    private static StoredRecipient ToStored(RecipientEntity row)
    {
        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(row.SettingsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        return new StoredRecipient(row.Id, row.Name, row.TypeId, settings, row.UpdatedUtc);
    }
}
