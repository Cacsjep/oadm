using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Oadm.Core.Persistence;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Events;

namespace Oadm.Core.Compliance;

/// <summary>A compliance check as the server and the clients see it.</summary>
public sealed class ComplianceCheck
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string TypeId { get; set; } = string.Empty;

    public JsonObject Settings { get; set; } = [];

    public DeviceSelector Devices { get; set; } = new();

    public ComplianceSeverity Severity { get; set; }

    /// <summary>Minutes between two reads of a device (15..1440); 0 for types that use cached data only.</summary>
    public int IntervalMinutes { get; set; }

    public bool Enabled { get; set; } = true;

    public bool BuiltIn { get; set; }

    public int Version { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ChangedUtc { get; set; }

    public ComplianceCheck Clone() => new()
    {
        Id = Id,
        Name = Name,
        TypeId = TypeId,
        Settings = (JsonObject)Settings.DeepClone(),
        Devices = Devices.Clone(),
        Severity = Severity,
        IntervalMinutes = IntervalMinutes,
        Enabled = Enabled,
        BuiltIn = BuiltIn,
        Version = Version,
        CreatedUtc = CreatedUtc,
        ChangedUtc = ChangedUtc,
    };
}

/// <summary>A save refused because another user changed the check meanwhile, or a rule of the checks.</summary>
public sealed class ComplianceValidationException(string message) : Exception(message);

/// <summary>
/// The checks (table ComplianceChecks) and their results (ComplianceResults) in the database. Writes of checks are
/// serialized; results are written in batches by the engine.
/// </summary>
public sealed class ComplianceStore(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider time) : IDisposable
{
    /// <summary>At most this many checks, the default ones included.</summary>
    public const int MaxChecks = 50;

    public const int MaxNameLength = 64;

    /// <summary>Bounds of the read interval in minutes.</summary>
    public const int MinInterval = 15;

    public const int MaxInterval = 1440;

    public const int DefaultInterval = 60;

    /// <summary>The server setting that turns compliance on ("true"); missing = off.</summary>
    public const string EnabledSettingKey = "Compliance.Enabled";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _write = new(1, 1);

    public void Dispose() => _write.Dispose();

    /// <summary>Set once the default checks were created: a deleted default check is not created again.</summary>
    public const string DefaultsCreatedSettingKey = "Compliance.DefaultsCreated";

    /// <summary>
    /// Creates the default checks (Reachable, Login works) once per server. They are ordinary checks: the user may change,
    /// turn off or delete them and add them again with New check.
    /// </summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.Settings.AnyAsync(s => s.Key == DefaultsCreatedSettingKey, ct).ConfigureAwait(false))
        {
            return;
        }

        db.Settings.Add(new Settings.Setting { Key = DefaultsCreatedSettingKey, ValueJson = "true" });
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (id, name, typeId) in BuiltInComplianceTypes.Defaults)
        {
            if (!await db.ComplianceChecks.AnyAsync(c => c.Id == id, ct).ConfigureAwait(false))
            {
                db.ComplianceChecks.Add(new ComplianceCheckEntity
                {
                    Id = id,
                    Name = name,
                    TypeId = typeId,
                    DevicesJson = JsonSerializer.Serialize(new DeviceSelector { Mode = DeviceSelectorMode.All }, Json),
                    Severity = ComplianceSeverity.Problem,
                    Enabled = true,
                    BuiltIn = true,
                    Version = 1,
                    CreatedUtc = now,
                    ChangedUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether compliance is on for the server (off by default).</summary>
    public async Task<bool> GetEnabledAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == EnabledSettingKey, ct).ConfigureAwait(false);
        return row?.ValueJson == "true";
    }

    /// <summary>Turns compliance on or off; removes every result when it goes off.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == EnabledSettingKey, ct).ConfigureAwait(false);
            if (row is null)
            {
                db.Settings.Add(new Settings.Setting { Key = EnabledSettingKey, ValueJson = enabled ? "true" : "false" });
            }
            else
            {
                row.ValueJson = enabled ? "true" : "false";
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (!enabled)
            {
                await db.ComplianceResults.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<IReadOnlyList<ComplianceCheck>> ListChecksAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ComplianceChecks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToModel)];
    }

    /// <summary>
    /// Creates (empty id) or replaces a check; returns the stored one. The version must be the stored one (0 = no check);
    /// a default check keeps its name, type and severity. Throws <see cref="KeyNotFoundException"/> and
    /// <see cref="ComplianceValidationException"/>.
    /// </summary>
    public async Task<ComplianceCheck> SaveCheckAsync(ComplianceCheck incoming, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var now = time.GetUtcNow().UtcDateTime;
            ComplianceCheckEntity row;
            if (incoming.Id == Guid.Empty)
            {
                if (await db.ComplianceChecks.CountAsync(ct).ConfigureAwait(false) >= MaxChecks)
                {
                    throw new ComplianceValidationException($"At most {MaxChecks} checks.");
                }

                row = new ComplianceCheckEntity { Id = Guid.NewGuid(), CreatedUtc = now, TypeId = incoming.TypeId };
                db.ComplianceChecks.Add(row);
            }
            else
            {
                row = await db.ComplianceChecks.FirstOrDefaultAsync(c => c.Id == incoming.Id, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The check no longer exists.");
                if (incoming.Version != 0 && incoming.Version != row.Version)
                {
                    throw new ComplianceValidationException($"Another user changed the check {row.Name} meanwhile. Close the dialog and make your changes again.");
                }
            }

            var name = incoming.Name.Trim();
            if (await db.ComplianceChecks.AnyAsync(c => c.Id != row.Id && c.Name == name, ct).ConfigureAwait(false))
            {
                throw new ComplianceValidationException($"A check named {name} already exists.");
            }

            row.Name = name;
            row.TypeId = incoming.TypeId;
            row.Severity = incoming.Severity;
            row.Enabled = incoming.Enabled;
            row.SettingsJson = incoming.Settings.ToJsonString();
            row.DevicesJson = JsonSerializer.Serialize(incoming.Devices, Json);
            row.IntervalMinutes = incoming.IntervalMinutes;
            row.Version++;
            row.ChangedUtc = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToModel(row);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Turns a check on or off; returns it. A default check cannot be turned off.</summary>
    public async Task<ComplianceCheck> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.ComplianceChecks.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The check no longer exists.");
            if (row.Enabled != enabled)
            {
                row.Enabled = enabled;
                row.Version++;
                row.ChangedUtc = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return ToModel(row);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a check and its results; returns its name. A default check cannot be deleted.</summary>
    public async Task<string> DeleteCheckAsync(Guid id, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.ComplianceChecks.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The check no longer exists.");
            await db.ComplianceResults.Where(r => r.CheckId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.ComplianceChecks.Remove(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row.Name;
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<IReadOnlyList<ComplianceResultEntity>> ListResultsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ComplianceResults.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes and removes results in one transaction (inserted or replaced by key).</summary>
    public async Task WriteResultsAsync(
        IReadOnlyCollection<ComplianceResultEntity> upserts, IReadOnlyCollection<(Guid CheckId, Guid DeviceId)> removals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);
        if (upserts.Count == 0 && removals.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        if (upserts.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbTransaction;
            command.CommandText =
                "INSERT INTO ComplianceResults (CheckId, DeviceId, Outcome, Detail, CheckedUtc, SinceUtc) VALUES ($c, $d, $o, $t, $k, $s) " +
                "ON CONFLICT (CheckId, DeviceId) DO UPDATE SET Outcome = excluded.Outcome, Detail = excluded.Detail, " +
                "CheckedUtc = excluded.CheckedUtc, SinceUtc = excluded.SinceUtc";
            var check = Add(command, "$c");
            var device = Add(command, "$d");
            var outcome = Add(command, "$o");
            var detail = Add(command, "$t");
            var checkedUtc = Add(command, "$k");
            var since = Add(command, "$s");
            foreach (var row in upserts)
            {
                check.Value = row.CheckId.ToString().ToUpperInvariant();
                device.Value = row.DeviceId.ToString().ToUpperInvariant();
                outcome.Value = (int)row.Outcome;
                detail.Value = row.Detail;
                checkedUtc.Value = row.CheckedUtc;
                since.Value = row.SinceUtc;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        if (removals.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbTransaction;
            command.CommandText = "DELETE FROM ComplianceResults WHERE CheckId = $c AND DeviceId = $d";
            var check = Add(command, "$c");
            var device = Add(command, "$d");
            foreach (var (checkId, deviceId) in removals)
            {
                check.Value = checkId.ToString().ToUpperInvariant();
                device.Value = deviceId.ToString().ToUpperInvariant();
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static DbParameter Add(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static ComplianceCheck ToModel(ComplianceCheckEntity row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        TypeId = row.TypeId,
        Settings = JsonNode.Parse(string.IsNullOrEmpty(row.SettingsJson) ? "{}" : row.SettingsJson) as JsonObject ?? [],
        Devices = JsonSerializer.Deserialize<DeviceSelector>(string.IsNullOrEmpty(row.DevicesJson) ? "{}" : row.DevicesJson, Json) ?? new DeviceSelector(),
        Severity = row.Severity,
        IntervalMinutes = row.IntervalMinutes,
        Enabled = row.Enabled,
        BuiltIn = row.BuiltIn,
        Version = row.Version,
        CreatedUtc = row.CreatedUtc,
        ChangedUtc = row.ChangedUtc,
    };
}
