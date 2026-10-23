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

    /// <summary><see cref="WaitingDeviceStore.Normalize(string?, Guid?)"/> of <see cref="Address"/> and <see cref="SiteId"/>: one entry per (site, address).</summary>
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

    /// <summary>
    /// The id of the user who queued the entry (null for entries from before device groups and the system): a try checks
    /// that user's current devices, never the caller's (docs/spec/device-scope.md, Decisions).
    /// </summary>
    public Guid? QueuedByUserId { get; set; }

    /// <summary>When the line was kept, renewed when it is kept again: a try started before that writes nothing back.</summary>
    public DateTime CreatedUtc { get; set; }

    public DateTime? LastTryUtc { get; set; }

    /// <summary>What the last try found, in plain words; empty before the first try.</summary>
    public string LastResult { get; set; } = string.Empty;

    /// <summary>The device answered but rejected every login: not tried again by itself (no repeated wrong passwords).</summary>
    public bool LoginFailed { get; set; }

    /// <summary>
    /// The address answers through several sites with different devices that OADM does not manage: which one is meant is
    /// not known, so it is not tried again by itself (Try now, or keep it again with a Site column).
    /// </summary>
    public bool Held { get; set; }

    /// <summary>Tries since the entry was kept (again).</summary>
    public int Tries { get; set; }

    /// <summary>
    /// Only tried through this site (the import's Site column); null = everywhere. The route is part of
    /// <see cref="NormalizedAddress"/> once sites are in use, so (site, address) stays unique.
    /// </summary>
    public Guid? SiteId { get; set; }

    public override string ToString() => $"WaitingDevice {Id} ({Address})";
}

/// <summary>
/// The entries of the waiting list a user sees and changes (docs/spec/device-scope.md, Decisions "New devices": the waiting
/// list is scoped by site). A user with all devices: every entry. A user with a device group: the entries of the group's
/// sites and the entries for everywhere the user kept, never those of other users (also with "This server": they may
/// have been kept by an administrator, and Try now runs as their keeper with the credential list).
/// </summary>
/// <param name="All">Every entry.</param>
/// <param name="Local">The group has "This server" (where the user's own entries are tried; it never widens the scope).</param>
/// <param name="Sites">The group's sites.</param>
/// <param name="UserId">The user (their own entries for everywhere).</param>
public sealed record WaitingScope(bool All, bool Local, IReadOnlySet<Guid> Sites, Guid? UserId)
{
    /// <summary>Every entry (users with all devices).</summary>
    public static WaitingScope Everything { get; } = new(true, true, new HashSet<Guid>(), null);

    /// <summary>True when the entry of <paramref name="siteId"/> kept by <paramref name="queuedBy"/> is in the scope.</summary>
    public bool Covers(Guid? siteId, Guid? queuedBy) =>
        All || (siteId is { } site ? Sites.Contains(site) : UserId is { } user && queuedBy == user);

    internal IQueryable<WaitingDevice> Apply(IQueryable<WaitingDevice> query)
    {
        if (All)
        {
            return query;
        }

        var sites = Sites.Select(s => (Guid?)s).ToList();
        if (UserId is not { } user)
        {
            return query.Where(w => w.SiteId != null && sites.Contains(w.SiteId));
        }

        return query.Where(w => w.SiteId == null ? w.QueuedByUserId == user : sites.Contains(w.SiteId));
    }
}

/// <summary>A line to keep waiting; the password stays in server memory until it is stored encrypted.</summary>
/// <param name="SiteId">The import's Site column: only tried through this site; null = everywhere.</param>
public sealed record WaitingLine(string Address, string? ExpectedSerial, string? UserName, string? Password, IReadOnlyList<string> Tags, Guid? SiteId = null)
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
    bool HasLogin,
    Guid? SiteId = null,
    bool Held = false);

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
    DateTime Kept,
    Guid? SiteId = null,
    Guid? QueuedByUserId = null)
{
    public override string ToString() => $"WaitingTry {{ Id = {Id}, Address = {Address}, Login = {(Login is null ? "none" : "***")} }}";
}

/// <summary>The outcome of one try, written back in a batch (only onto the entry as the try read it).</summary>
public sealed record WaitingResult(Guid Id, DateTime Kept, string Result, bool LoginFailed, bool Held = false);

/// <summary>An entry's (site, address) key and MAC address, to find the ones OADM manages by now.</summary>
public sealed record WaitingKey(Guid Id, string NormalizedAddress, string? ExpectedSerial);

/// <summary>A managed device as the waiting list compares it: its MAC address, address and route.</summary>
public sealed record ManagedAddress(string Serial, string Address, Guid? SiteId);

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
    /// The key of an entry: <see cref="Normalize(string?)"/> of the address, and for an entry of one site "@" and the site's
    /// id after it (docs/spec/sites.md, "Overlapping networks": two sites may both have 192.168.0.20). One entry per key; the
    /// address part keeps the address order of the list. Also compared with managed devices (address and route).
    /// </summary>
    public static string Normalize(string? address, Guid? siteId) =>
        siteId is { } site ? Normalize(address) + "@" + site.ToString("N").ToUpperInvariant() : Normalize(address);

    /// <summary>
    /// The key of an entry for everywhere kept by a user without all devices: <see cref="Normalize(string?)"/>, "#" and the
    /// user's id, so it never meets another user's entry for the same address (docs/spec/device-scope.md, Small leaks closed).
    /// <see cref="AddressPart"/> gives the address part back for the comparison with managed devices.
    /// </summary>
    public static string KeeperKey(string? address, Guid userId) => Normalize(address) + "#" + userId.ToString("N").ToUpperInvariant();

    /// <summary>The key without a keeper part (<see cref="KeeperKey"/>): what is compared with managed devices.</summary>
    public static string AddressPart(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        const int suffix = 33; // "#" and 32 hex digits
        return key.Length > suffix && key[^suffix] == '#' && Guid.TryParseExact(key[^(suffix - 1)..], "N", out _) ? key[..^suffix] : key;
    }

    /// <summary>
    /// The address as a device record stores it, upper case: no scheme ("https://cam:8443" = "cam:8443"), no trailing "/",
    /// an IPv6 address without a port without brackets.
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
    public Task<WaitingAddResult> AddAsync(IReadOnlyList<WaitingLine> lines, string source, string addedBy, CancellationToken ct) =>
        AddAsync(lines, source, addedBy, null, ct);

    /// <inheritdoc cref="AddAsync(IReadOnlyList{WaitingLine}, string, string, CancellationToken)"/>
    /// <param name="lines">The lines.</param>
    /// <param name="source">Where they came from.</param>
    /// <param name="addedBy">The name of the user who keeps them.</param>
    /// <param name="queuedBy">The id of that user: a try checks the devices of that user at the time of the try.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<WaitingAddResult> AddAsync(IReadOnlyList<WaitingLine> lines, string source, string addedBy, Guid? queuedBy, CancellationToken ct) =>
        AddAsync(lines, source, addedBy, queuedBy, WaitingScope.Everything, ct);

    /// <inheritdoc cref="AddAsync(IReadOnlyList{WaitingLine}, string, string, CancellationToken)"/>
    /// <param name="lines">The lines.</param>
    /// <param name="source">Where they came from.</param>
    /// <param name="addedBy">The name of the user who keeps them.</param>
    /// <param name="queuedBy">The id of that user: a try checks the devices of that user at the time of the try.</param>
    /// <param name="scope">
    /// The keeper's entries (<see cref="WaitingScope"/>). A user without all devices never changes an entry outside it: the
    /// entries for everywhere of such a user have their own key (<see cref="KeeperKey"/>), so another user's entry for the
    /// same address stays as it is and is not counted; <see cref="WaitingAddResult.Total"/> counts the scope.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<WaitingAddResult> AddAsync(IReadOnlyList<WaitingLine> lines, string source, string addedBy, Guid? queuedBy, WaitingScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.All && queuedBy is null)
        {
            throw new ArgumentException("A user without all devices keeps entries as that user.", nameof(queuedBy));
        }

        string KeyOf(WaitingLine line) => !scope.All && line.SiteId is null
            ? KeeperKey(line.Address, queuedBy!.Value)
            : Normalize(line.Address, line.SiteId);

        var now = time.GetUtcNow().UtcDateTime;
        var distinct = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Address) && l.Address.Trim().Length <= MaxAddressLength)
            .GroupBy(KeyOf, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
        source = Cut(source.Trim(), MaxSourceLength);

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var keys = distinct.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            var existing = new Dictionary<string, WaitingDevice>(StringComparer.Ordinal);
            foreach (var chunk in keys.Chunk(500))
            {
                foreach (var row in await db.WaitingDevices.Where(w => chunk.Contains(w.NormalizedAddress)).ToListAsync(ct).ConfigureAwait(false))
                {
                    existing[row.NormalizedAddress] = row;
                }
            }

            // An entry outside the keeper's scope (fail closed: not expected, the keys of such entries differ) is left alone.
            var foreign = existing.Where(e => !scope.Covers(e.Value.SiteId, e.Value.QueuedByUserId)).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            var count = await db.WaitingDevices.CountAsync(ct).ConfigureAwait(false);
            var added = distinct.Count(l => !existing.ContainsKey(KeyOf(l)));
            if (count + added > MaxEntries)
            {
                throw new WaitingListFullException(FullText);
            }

            var updated = 0;
            foreach (var line in distinct)
            {
                var key = KeyOf(line);
                if (foreign.Contains(key))
                {
                    continue;
                }

                if (!existing.TryGetValue(key, out var row))
                {
                    row = new WaitingDevice { Id = Guid.NewGuid(), NormalizedAddress = key, SiteId = line.SiteId };
                    db.WaitingDevices.Add(row);
                }
                else
                {
                    updated++;
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
                row.QueuedByUserId = queuedBy;
                row.LoginFailed = false;
                row.Held = false;
                row.Tries = 0;
                row.LastResult = string.Empty;
                row.LastTryUtc = null;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var total = scope.All ? count + added : await scope.Apply(db.WaitingDevices.AsNoTracking()).CountAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
            return new WaitingAddResult(added, updated, total);
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
    public Task<(int Count, int LoginFailed)> CountsAsync(CancellationToken ct) => CountsAsync(WaitingScope.Everything, ct);

    /// <summary>How many entries of <paramref name="scope"/> there are and how many of them wait for a login.</summary>
    public async Task<(int Count, int LoginFailed)> CountsAsync(WaitingScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = scope.Apply(db.WaitingDevices.AsNoTracking());
        var count = await rows.CountAsync(ct).ConfigureAwait(false);
        var failed = await rows.CountAsync(w => w.LoginFailed, ct).ConfigureAwait(false);
        return (count, failed);
    }

    /// <summary>The ids of <paramref name="ids"/> that exist and are in <paramref name="scope"/> (one query per 500 ids).</summary>
    public async Task<IReadOnlySet<Guid>> InScopeAsync(IReadOnlyCollection<Guid> ids, WaitingScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(scope);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var found = new HashSet<Guid>();
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            found.UnionWith(await scope.Apply(db.WaitingDevices.AsNoTracking()).Where(w => chunk.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct).ConfigureAwait(false));
        }

        return found;
    }

    /// <summary>The ids of every entry of <paramref name="scope"/>.</summary>
    public async Task<IReadOnlyList<Guid>> IdsAsync(WaitingScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await scope.Apply(db.WaitingDevices.AsNoTracking()).Select(w => w.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A page in address order, after the address <paramref name="after"/> (null: from the start). A cursor, not an offset:
    /// entries removed while the client reads the pages (a round adds devices) never make it skip or repeat one.
    /// </summary>
    public Task<WaitingPage> ListAsync(string? after, int limit, CancellationToken ct) => ListAsync(after, limit, WaitingScope.Everything, ct);

    /// <inheritdoc cref="ListAsync(string?, int, CancellationToken)"/>
    /// <param name="after">The cursor.</param>
    /// <param name="limit">The page size (0: every entry).</param>
    /// <param name="scope">Only the entries of this scope (the total too).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<WaitingPage> ListAsync(string? after, int limit, WaitingScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = scope.Apply(db.WaitingDevices.AsNoTracking());
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(after))
        {
#pragma warning disable CA1309 // translated to SQL: SQLite compares TEXT byte by byte (ordinal)
            query = query.Where(w => string.Compare(w.NormalizedAddress, after) > 0);
#pragma warning restore CA1309
        }

        var take = limit <= 0 ? MaxEntries : limit;
        var rows = await query.OrderBy(w => w.NormalizedAddress).Take(take).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => new WaitingDeviceItem(r.Id, r.Address, r.ExpectedSerial, r.Tags, r.Source, r.AddedBy, r.CreatedUtc,
            r.LastTryUtc, r.LastResult, r.LoginFailed, r.Tries, r.EncryptedPassword is not null, r.SiteId, r.Held)).ToList();
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
            // A scheduled round leaves the failed logins and the held entries alone; Try now for all takes them too.
            rows = await (includeLoginFailed ? query : query.Where(w => !w.LoginFailed && !w.Held)).ToListAsync(ct).ConfigureAwait(false);
        }

        return rows.OrderBy(r => r.LastTryUtc ?? DateTime.MinValue).ThenBy(r => r.CreatedUtc)
            .Select(r => new WaitingTry(r.Id, r.Address, r.ExpectedSerial, Decrypt(r), r.Tags, r.Source, r.AddedBy, r.CreatedUtc, r.SiteId, r.QueuedByUserId)).ToList();
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
                    row.Held = result.Held;
                    row.Tries++;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }

        Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
    }

    /// <summary>Removes entries (<paramref name="all"/>: every entry). Returns how many were removed.</summary>
    public Task<int> RemoveAsync(IReadOnlyCollection<Guid> ids, bool all, CancellationToken ct) => RemoveAsync(ids, all, WaitingScope.Everything, ct);

    /// <summary>
    /// Removes entries of <paramref name="scope"/> (<paramref name="all"/>: every entry of it; ids outside it are left alone,
    /// the caller refuses them first). Returns how many were removed.
    /// </summary>
    public async Task<int> RemoveAsync(IReadOnlyCollection<Guid> ids, bool all, WaitingScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(scope);
        int removed;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (all)
            {
                removed = await scope.Apply(db.WaitingDevices).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            else
            {
                removed = 0;
                foreach (var chunk in ids.Distinct().Chunk(500))
                {
                    removed += await scope.Apply(db.WaitingDevices).Where(w => chunk.Contains(w.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _write.Release();
        }

        if (removed > 0)
        {
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>The entries kept for <paramref name="siteId"/> are removed (the site was removed). Returns how many.</summary>
    public async Task<int> RemoveForSiteAsync(Guid siteId, CancellationToken ct)
    {
        int removed;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            removed = await db.WaitingDevices.Where(w => w.SiteId == siteId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }

        if (removed > 0)
        {
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// The entries kept for <paramref name="from"/> wait for <paramref name="to"/> instead (Replace with): their key moves to
    /// the new site; an address the new site already keeps stays as the new site has it. One transaction. Returns how many moved.
    /// </summary>
    public async Task<int> MoveSiteAsync(Guid from, Guid to, CancellationToken ct)
    {
        int moved = 0;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var rows = await db.WaitingDevices.Where(w => w.SiteId == from).ToListAsync(ct).ConfigureAwait(false);
            var keys = rows.Select(r => Normalize(r.Address, to)).ToList();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in keys.Chunk(500))
            {
                taken.UnionWith(await db.WaitingDevices.Where(w => chunk.Contains(w.NormalizedAddress)).Select(w => w.NormalizedAddress).ToListAsync(ct).ConfigureAwait(false));
            }

            for (var i = 0; i < rows.Count; i++)
            {
                if (!taken.Add(keys[i]))
                {
                    db.WaitingDevices.Remove(rows[i]);
                    continue;
                }

                rows[i].SiteId = to;
                rows[i].NormalizedAddress = keys[i];
                moved++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }

        if (moved > 0)
        {
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return moved;
    }

    /// <summary>
    /// Removes the entries whose device OADM manages by now (added another way): the same (route, address) or the
    /// expected MAC address. An entry of a site compares with the devices of that site, an entry for everywhere with the
    /// devices on the server's own network (a device at a site with the same address may be another one: such an entry
    /// leaves when its try finds its device managed). Every entry, also those whose login failed. Returns the ids.
    /// </summary>
    public Task<IReadOnlyCollection<Guid>> RemoveManagedAsync(IReadOnlyCollection<ManagedAddress> managed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(managed);
        return RemoveManagedAsync(
            managed.Select(d => Normalize(d.Address, d.SiteId)).ToHashSet(StringComparer.Ordinal),
            managed.Select(d => DeviceSerial.Normalize(d.Serial)).ToHashSet(StringComparer.Ordinal), ct);
    }

    /// <summary>
    /// Removes the entries whose key (<see cref="Normalize(string?, Guid?)"/>) is in <paramref name="managedAddresses"/> or
    /// whose expected MAC address is in <paramref name="managedSerials"/>. Returns the ids.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>> RemoveManagedAsync(IReadOnlySet<string> managedAddresses, IReadOnlySet<string> managedSerials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(managedAddresses);
        ArgumentNullException.ThrowIfNull(managedSerials);
        var gone = (await ListKeysAsync(ct).ConfigureAwait(false))
            .Where(k => managedAddresses.Contains(AddressPart(k.NormalizedAddress)) || (k.ExpectedSerial is { } s && managedSerials.Contains(s)))
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
