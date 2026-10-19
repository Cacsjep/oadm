using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>
/// A line of an imported file whose device did not answer (row of WaitingDevices): kept until the server adds it, the
/// device is added another way or a user removes it. Nothing drops off by itself.
/// </summary>
public sealed class WaitingDevice
{
    public Guid Id { get; set; }

    /// <summary>The address as the file has it (IP or host name, optional scheme and port); the device address on add.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary><see cref="WaitingDeviceStore.Normalize"/> of <see cref="Address"/>: one entry per address.</summary>
    public string NormalizedAddress { get; set; } = string.Empty;

    /// <summary>The MAC address an export of AXIS Device Manager names: another device at the address gets no password.</summary>
    public string? ExpectedSerial { get; set; }

    /// <summary>The login of the file's line, tried first; null = the credential list only.</summary>
    public string? UserName { get; set; }

    /// <summary>Output of <see cref="CredentialProtector.Protect"/> with the entry id as associated data; null without a login.</summary>
#pragma warning disable CA1819 // EF entity blob column
    public byte[]? EncryptedPassword { get; set; }
#pragma warning restore CA1819

    /// <summary>Tags the device gets when it is added.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Where the line came from ("Import from file site.csv").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The user who imported it.</summary>
    public string AddedBy { get; set; } = string.Empty;

    /// <summary>When the line was kept, renewed when it is kept again: a try started before that writes nothing back.</summary>
    public DateTime CreatedUtc { get; set; }

    public DateTime? LastTryUtc { get; set; }

    /// <summary>What the last try found, in plain words; empty before the first try.</summary>
    public string LastResult { get; set; } = string.Empty;

    /// <summary>The device answered but rejected every login: not tried again by itself (no repeated wrong passwords).</summary>
    public bool LoginFailed { get; set; }

    /// <summary>Tries since the entry was kept (again).</summary>
    public int Tries { get; set; }

    public override string ToString() => $"WaitingDevice {Id} ({Address})";
}

/// <summary>A line to keep waiting; the password stays in server memory until it is stored encrypted.</summary>
public sealed record WaitingLine(string Address, string? ExpectedSerial, string? UserName, string? Password, IReadOnlyList<string> Tags)
{
    public override string ToString() => $"WaitingLine {{ Address = {Address}, UserName = {UserName}, Password = *** }}";
}

/// <summary>A waiting device as listed to clients: never a password.</summary>
public sealed record WaitingDeviceItem(
    Guid Id,
    string Address,
    string? ExpectedSerial,
    IReadOnlyList<string> Tags,
    string Source,
    string AddedBy,
    DateTime CreatedUtc,
    DateTime? LastTryUtc,
    string LastResult,
    bool LoginFailed,
    int Tries,
    bool HasLogin);

/// <summary>
/// A waiting device with its login decrypted, for one try. Server memory only; <see cref="ToString"/> redacts it.
/// <paramref name="Kept"/> is the entry's <see cref="WaitingDevice.CreatedUtc"/> when it was read: the try writes back (or
/// adds the device) only while the entry is still the same (not removed, not kept again with another login).
/// </summary>
public sealed record WaitingTry(
    Guid Id,
    string Address,
    string? ExpectedSerial,
    DeviceCredentials? Login,
    IReadOnlyList<string> Tags,
    string Source,
    string AddedBy,
    DateTime Kept)
{
    public override string ToString() => $"WaitingTry {{ Id = {Id}, Address = {Address}, Login = {(Login is null ? "none" : "***")} }}";
}

/// <summary>The outcome of one try, written back in a batch (only onto the entry as the try read it).</summary>
public sealed record WaitingResult(Guid Id, DateTime Kept, string Result, bool LoginFailed);

/// <summary>An entry's address and MAC address, to find the ones OADM manages by now.</summary>
public sealed record WaitingKey(Guid Id, string NormalizedAddress, string? ExpectedSerial);

/// <summary>What <see cref="WaitingDeviceStore.AddAsync"/> stored.</summary>
public sealed record WaitingAddResult(int Added, int Updated, int Total);

/// <summary>A page of <see cref="WaitingDeviceStore.ListAsync"/>; <paramref name="NextAfter"/> continues it (null: the end).</summary>
public sealed record WaitingPage(IReadOnlyList<WaitingDeviceItem> Items, int Total, string? NextAfter);

/// <summary>The list was full.</summary>
public sealed class WaitingListFullException(string message) : InvalidOperationException(message);

/// <summary>
/// The waiting list of the import ("Add 4 now, 2 later"). One entry per address (an import of the same
/// address again replaces its login, MAC address and tags and is tried like new), at most <see cref="MaxEntries"/>.
/// Passwords are encrypted with the master key like the credential list (entry id as associated data) and never listed.
/// Writes go one at a time (two imports at once never break the limit or the one entry per address), and writes for many
/// entries are one transaction.
/// </summary>
public sealed class WaitingDeviceStore(IDbContextFactory<OadmDbContext> dbFactory, CredentialProtector protector, TimeProvider time) : IDisposable
{
    /// <summary>As many as one server manages devices.</summary>
    public const int MaxEntries = DeviceLimits.MaxDevices;

    public const int MaxAddressLength = 300;
    public const int MaxSourceLength = 200;
    public const int MaxResultLength = 500;

    /// <summary>The text when the list is full.</summary>
    public static readonly string FullText = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"The waiting list holds at most {MaxEntries:N0} devices. Remove devices from it first.");

    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>Raised after entries were added, removed or tried (not on every single result).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The address as a device record stores it, upper case: no scheme ("https://cam:8443" = "cam:8443"), no trailing "/",
    /// an IPv6 address without a port without brackets. One entry per such address; also compared with managed devices.
    /// </summary>
    public static string Normalize(string? address)
    {
        var text = (address ?? string.Empty).Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        text = text.TrimEnd('/');
        if (text.Length > 1 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1];
        }

        return text.ToUpperInvariant();
    }

    /// <summary>
    /// Keeps the lines: a new address is added, a known one gets the new login, MAC address and tags and is tried again
    /// as new (login failed cleared, kept time renewed). One transaction.
    /// </summary>
    /// <exception cref="WaitingListFullException">The list would hold more than <see cref="MaxEntries"/>; nothing was stored.</exception>
    public async Task<WaitingAddResult> AddAsync(IReadOnlyList<WaitingLine> lines, string source, string addedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var now = time.GetUtcNow().UtcDateTime;
        var distinct = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Address) && l.Address.Trim().Length <= MaxAddressLength)
            .GroupBy(l => Normalize(l.Address), StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
        source = Cut(source.Trim(), MaxSourceLength);

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var keys = distinct.Select(l => Normalize(l.Address)).ToHashSet(StringComparer.Ordinal);
            var existing = new Dictionary<string, WaitingDevice>(StringComparer.Ordinal);
            foreach (var chunk in keys.Chunk(500))
            {
                foreach (var row in await db.WaitingDevices.Where(w => chunk.Contains(w.NormalizedAddress)).ToListAsync(ct).ConfigureAwait(false))
                {
                    existing[row.NormalizedAddress] = row;
                }
            }

            var count = await db.WaitingDevices.CountAsync(ct).ConfigureAwait(false);
            var added = distinct.Count(l => !existing.ContainsKey(Normalize(l.Address)));
            if (count + added > MaxEntries)
            {
                throw new WaitingListFullException(FullText);
            }

            foreach (var line in distinct)
            {
                var key = Normalize(line.Address);
                if (!existing.TryGetValue(key, out var row))
                {
                    row = new WaitingDevice { Id = Guid.NewGuid(), NormalizedAddress = key };
                    db.WaitingDevices.Add(row);
                }

                row.Address = line.Address.Trim();
                row.CreatedUtc = now;
                row.ExpectedSerial = string.IsNullOrWhiteSpace(line.ExpectedSerial) ? null : DeviceSerial.Normalize(line.ExpectedSerial);
                var hasLogin = !string.IsNullOrEmpty(line.Password);
                row.UserName = hasLogin ? (string.IsNullOrWhiteSpace(line.UserName) ? "root" : line.UserName.Trim()) : null;
                row.EncryptedPassword = hasLogin ? protector.Protect(line.Password!, row.Id.ToByteArray()) : null;
                row.Tags = [.. line.Tags];
                row.Source = source;
                row.AddedBy = Cut(addedBy, 64);
                row.LoginFailed = false;
                row.Tries = 0;
                row.LastResult = string.Empty;
                row.LastTryUtc = null;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
            return new WaitingAddResult(added, distinct.Count - added, count + added);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.WaitingDevices.CountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>How many there are and how many wait for a login (not tried by themselves).</summary>
    public async Task<(int Count, int LoginFailed)> CountsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var count = await db.WaitingDevices.CountAsync(ct).ConfigureAwait(false);
        var failed = await db.WaitingDevices.CountAsync(w => w.LoginFailed, ct).ConfigureAwait(false);
        return (count, failed);
    }

    /// <summary>
    /// A page in address order, after the address <paramref name="after"/> (null: from the start). A cursor, not an offset:
    /// entries removed while the client reads the pages (a round adds devices) never make it skip or repeat one.
    /// </summary>
    public async Task<WaitingPage> ListAsync(string? after, int limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var total = await db.WaitingDevices.CountAsync(ct).ConfigureAwait(false);
        var query = db.WaitingDevices.AsNoTracking();
        if (!string.IsNullOrEmpty(after))
        {
#pragma warning disable CA1309 // translated to SQL: SQLite compares TEXT byte by byte (ordinal)
            query = query.Where(w => string.Compare(w.NormalizedAddress, after) > 0);
#pragma warning restore CA1309
        }

        var take = limit <= 0 ? MaxEntries : limit;
        var rows = await query.OrderBy(w => w.NormalizedAddress).Take(take).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => new WaitingDeviceItem(r.Id, r.Address, r.ExpectedSerial, r.Tags, r.Source, r.AddedBy, r.CreatedUtc,
            r.LastTryUtc, r.LastResult, r.LoginFailed, r.Tries, r.EncryptedPassword is not null)).ToList();
        return new WaitingPage(items, total, rows.Count == take ? rows[^1].NormalizedAddress : null);
    }

    /// <summary>Address and MAC address of every entry (also those whose login failed).</summary>
    public async Task<IReadOnlyList<WaitingKey>> ListKeysAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.WaitingDevices.AsNoTracking().Select(w => new WaitingKey(w.Id, w.NormalizedAddress, w.ExpectedSerial)).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The entries to try, decrypted, the longest untried first: <paramref name="ids"/> (any state), else every entry whose
    /// login did not fail (with <paramref name="includeLoginFailed"/>: every entry). A password that cannot be decrypted
    /// (other master key) counts as no login: the credential list is tried.
    /// </summary>
    public async Task<IReadOnlyList<WaitingTry>> ToTryAsync(IReadOnlyCollection<Guid>? ids, bool includeLoginFailed, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.WaitingDevices.AsNoTracking();
        List<WaitingDevice> rows;
        if (ids is not null)
        {
            rows = [];
            foreach (var chunk in ids.Distinct().Chunk(500))
            {
                rows.AddRange(await query.Where(w => chunk.Contains(w.Id)).ToListAsync(ct).ConfigureAwait(false));
            }
        }
        else
        {
            rows = await (includeLoginFailed ? query : query.Where(w => !w.LoginFailed)).ToListAsync(ct).ConfigureAwait(false);
        }

        return rows.OrderBy(r => r.LastTryUtc ?? DateTime.MinValue).ThenBy(r => r.CreatedUtc)
            .Select(r => new WaitingTry(r.Id, r.Address, r.ExpectedSerial, Decrypt(r), r.Tags, r.Source, r.AddedBy, r.CreatedUtc)).ToList();
    }

    /// <summary>Whether the entry still exists as a try read it (not removed, not kept again since).</summary>
    public async Task<bool> IsCurrentAsync(Guid id, DateTime kept, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.WaitingDevices.AsNoTracking().Where(w => w.Id == id).Select(w => new { w.CreatedUtc }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is not null && row.CreatedUtc == kept;
    }

    /// <summary>
    /// Writes the outcomes of tries in one transaction, each only onto the entry as its try read it: an entry kept again
    /// meanwhile (a corrected password) keeps its fresh state.
    /// </summary>
    public async Task RecordAsync(IReadOnlyCollection<WaitingResult> results, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var byId = results.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var chunk in byId.Keys.Chunk(500))
            {
                foreach (var row in await db.WaitingDevices.Where(w => chunk.Contains(w.Id)).ToListAsync(ct).ConfigureAwait(false))
                {
                    var result = byId[row.Id];
                    if (row.CreatedUtc != result.Kept)
                    {
                        continue;
                    }

                    row.LastTryUtc = now;
                    row.LastResult = Cut(result.Result, MaxResultLength);
                    row.LoginFailed = result.LoginFailed;
                    row.Tries++;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes entries (<paramref name="all"/>: every entry). Returns how many were removed.</summary>
    public async Task<int> RemoveAsync(IReadOnlyCollection<Guid> ids, bool all, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        int removed;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (all)
            {
                removed = await db.WaitingDevices.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            else
            {
                removed = 0;
                foreach (var chunk in ids.Distinct().Chunk(500))
                {
                    removed += await db.WaitingDevices.Where(w => chunk.Contains(w.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _write.Release();
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// Removes the entries whose device OADM manages by now (added another way): the same address (<see cref="Normalize"/>
    /// of the device's address) or the expected MAC address. Every entry, also those whose login failed. Returns the ids.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>> RemoveManagedAsync(IReadOnlySet<string> managedAddresses, IReadOnlySet<string> managedSerials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(managedAddresses);
        ArgumentNullException.ThrowIfNull(managedSerials);
        var gone = (await ListKeysAsync(ct).ConfigureAwait(false))
            .Where(k => managedAddresses.Contains(k.NormalizedAddress) || (k.ExpectedSerial is { } s && managedSerials.Contains(s)))
            .Select(k => k.Id).ToList();
        if (gone.Count > 0)
        {
            await RemoveAsync(gone, all: false, ct).ConfigureAwait(false);
        }

        return gone;
    }

    private DeviceCredentials? Decrypt(WaitingDevice row)
    {
        if (row.EncryptedPassword is null || string.IsNullOrEmpty(row.UserName))
        {
            return null;
        }

        try
        {
            return new DeviceCredentials(row.UserName, protector.Unprotect(row.EncryptedPassword, row.Id.ToByteArray()));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    public void Dispose() => _write.Dispose();
}
