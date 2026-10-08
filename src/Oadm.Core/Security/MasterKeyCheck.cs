using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Oadm.Core.Persistence;
using Oadm.Core.Settings;

namespace Oadm.Core.Security;

/// <summary>
/// Shown to every client until dismissed after the server had to replace its master key: how many device
/// credentials and credential list entries could not be read anymore and were removed.
/// </summary>
/// <param name="Id">Identity of this replacement (clients remember dismissed ids).</param>
/// <param name="ReplacedUtc">When the key was replaced.</param>
/// <param name="Devices">Devices whose stored credentials were removed (now CredentialsRequired).</param>
/// <param name="CredentialListEntries">Credential list entries removed.</param>
public sealed record KeyReplacedNotice(string Id, DateTime ReplacedUtc, int Devices, int CredentialListEntries)
{
    /// <summary>"The server's key was replaced: 3 devices need their credentials again" (+ list entries).</summary>
    public string Message
    {
        get
        {
            var text = string.Create(CultureInfo.InvariantCulture,
                $"The server's key was replaced: {Devices} {(Devices == 1 ? "device needs its" : "devices need their")} credentials again");
            return CredentialListEntries == 0
                ? text + "."
                : text + string.Create(CultureInfo.InvariantCulture,
                    $", {CredentialListEntries} {(CredentialListEntries == 1 ? "credential list entry was" : "credential list entries were")} removed.");
        }
    }
}

/// <summary>
/// Startup check of the master key (production hardening 3). <c>Security.KeyCheck</c> stores an HMAC of a fixed
/// label with the key (<see cref="CredentialProtector.ComputeKeyCheck"/>). When master.key is missing, unreadable
/// or does not match the stored value (without a stored value: encrypted rows exist and none of them decrypts),
/// the server warns and continues: it uses a new key (an existing file is kept as a backup), logs an error with the
/// counts, deletes the device credentials and credential list entries it cannot read, sets the affected devices to
/// CredentialsRequired and stores a <see cref="KeyReplacedNotice"/> (<c>Security.KeyReplaced</c>) for the client banner.
/// </summary>
public static partial class MasterKeyCheck
{
    /// <summary>Setting with the key check value (JSON string).</summary>
    public const string KeyCheckSetting = "Security.KeyCheck";

    /// <summary>Setting with the last <see cref="KeyReplacedNotice"/> (JSON), absent when none.</summary>
    public const string KeyReplacedSetting = "Security.KeyReplaced";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Runs the check; returns the notice when the key was replaced now, else null.</summary>
    public static async Task<KeyReplacedNotice?> RunAsync(
        IDbContextFactory<OadmDbContext> dbFactory,
        CredentialProtector protector,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var stored = await ReadStringAsync(db, KeyCheckSetting, ct).ConfigureAwait(false);
        var check = protector.ComputeKeyCheck();
        var credentials = await db.DeviceCredentials.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var entries = await db.CredentialListEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        string? reason = null;
        if (stored is not null && stored != check)
        {
            reason = protector.Origin switch
            {
                MasterKeyOrigin.Created => "master.key is missing",
                MasterKeyOrigin.ReplacedUnreadable => "master.key could not be read (wrong size)",
                _ => "master.key does not match the key the data was encrypted with",
            };
        }
        else if (stored is null && credentials.Count + entries.Count > 0
            && !credentials.Any(c => Readable(protector, c.EncryptedPassword, c.DeviceId.ToByteArray()))
            && !entries.Any(e => Readable(protector, e.EncryptedPassword, e.Id.ToByteArray())))
        {
            // Data from a version without key check: nothing can be read with this key.
            reason = protector.Origin == MasterKeyOrigin.Loaded ? "master.key does not match the stored credentials" : "master.key is missing or unreadable";
        }

        if (reason is null)
        {
            if (stored is null)
            {
                await SettingRows.WriteAsync(db, KeyCheckSetting, JsonSerializer.Serialize(check), ct).ConfigureAwait(false);
            }

            return null;
        }

        if (protector.Origin == MasterKeyOrigin.Loaded && protector.KeyFilePath is { } path)
        {
            // The file in place belongs to other data: keep it as a backup, continue with a new key.
            var fresh = MasterKeyFile.Replace(path);
            protector.ReplaceKey(fresh);
            CryptographicOperations.ZeroMemory(fresh);
        }

        var unreadableCredentials = credentials.Where(c => !Readable(protector, c.EncryptedPassword, c.DeviceId.ToByteArray())).Select(c => c.DeviceId).ToList();
        var unreadableEntries = entries.Where(e => !Readable(protector, e.EncryptedPassword, e.Id.ToByteArray())).Select(e => e.Id).ToList();

        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            if (unreadableCredentials.Count > 0)
            {
                await db.DeviceCredentials.Where(c => unreadableCredentials.Contains(c.DeviceId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.Devices.Where(d => unreadableCredentials.Contains(d.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, Sdk.Devices.DeviceStatus.CredentialsRequired), ct).ConfigureAwait(false);
            }

            if (unreadableEntries.Count > 0)
            {
                await db.CredentialListEntries.Where(e => unreadableEntries.Contains(e.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            var notice = new KeyReplacedNotice(Guid.NewGuid().ToString("N"), time.GetUtcNow().UtcDateTime, unreadableCredentials.Count, unreadableEntries.Count);
            await SettingRows.WriteAsync(db, KeyCheckSetting, JsonSerializer.Serialize(protector.ComputeKeyCheck()), ct).ConfigureAwait(false);
            await SettingRows.WriteAsync(db, KeyReplacedSetting, JsonSerializer.Serialize(notice, Json), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            LogKeyReplaced(logger, reason, notice.Devices, notice.CredentialListEntries);
            return notice;
        }
    }

    /// <summary>The notice stored by the last replacement, or null.</summary>
    public static async Task<KeyReplacedNotice?> ReadNoticeAsync(ServerSettingsStore settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = await settings.GetJsonAsync(KeyReplacedSetting, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KeyReplacedNotice>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Readable(CredentialProtector protector, byte[] blob, byte[] associatedData)
    {
        try
        {
            _ = protector.Unprotect(blob, associatedData);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadStringAsync(OadmDbContext db, string key, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (row?.ValueJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(row.ValueJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The server's master key was replaced ({Reason}). A new key is in use: {Devices} device credential(s) and {Entries} credential list entr(y/ies) could not be read and were removed; those devices need their credentials again. Plugin secrets encrypted with the old key (saved command passwords, CA keys) cannot be read either. The previous key file, if any, was kept as master.key.replaced-<time>.")]
    private static partial void LogKeyReplaced(ILogger logger, string reason, int devices, int entries);
}
