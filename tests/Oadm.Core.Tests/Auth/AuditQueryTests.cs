using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Tests.Persistence;

namespace Oadm.Core.Tests;

/// <summary>The Audit tab's query on the server (docs/spec/main-window.md "Logs page"): range, text in any case, pages by id, counts.</summary>
public sealed class AuditQueryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private TestDatabase _db = null!;
    private AuditLog _log = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _log = new AuditLog(_db.Get<IDbContextFactory<OadmDbContext>>(), _time);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task WriteAsync(string user, string action, string target, string? detail = null)
    {
        await _log.WriteAsync(user, "10.0.0.5", action, target, detail);
        _time.Advance(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task TheRangeAndTheTextFilterOnTheServerAndTheCountsSayHowMany()
    {
        await WriteAsync("admin", "Logged in", "admin");          // 08:00
        await WriteAsync("Jürgen", "Changed user", "Örjan");      // 09:00
        await WriteAsync("op", "Started task", "Restart", "50% done_now"); // 10:00
        await WriteAsync("admin", "Logged out", "admin");         // 11:00

        var all = await _log.QueryAsync(AuditFilter.None, 0, 0, CancellationToken.None);
        Assert.Equal((4, 4), (all.TotalCount, all.MatchCount));
        Assert.Equal(["Logged out", "Started task", "Changed user", "Logged in"], all.Entries.Select(e => e.Action)); // newest first

        // Both ends are inclusive.
        var range = await _log.QueryAsync(new AuditFilter(Start.AddHours(1).UtcDateTime, Start.AddHours(2).UtcDateTime), 0, 0, CancellationToken.None);
        Assert.Equal((4, 2), (range.TotalCount, range.MatchCount));
        Assert.Equal(["Started task", "Changed user"], range.Entries.Select(e => e.Action));
        Assert.Equal(2, (await _log.QueryAsync(new AuditFilter(NotBeforeUtc: Start.AddHours(2).UtcDateTime), 0, 0, CancellationToken.None)).MatchCount);
        Assert.Equal(1, (await _log.QueryAsync(new AuditFilter(NotAfterUtc: Start.UtcDateTime), 0, 0, CancellationToken.None)).MatchCount);

        // Any case, in every text column; also letters outside A-Z.
        Assert.Equal(2, (await _log.QueryAsync(new AuditFilter(Search: "LOGGED"), 0, 0, CancellationToken.None)).MatchCount);
        Assert.Equal(4, (await _log.QueryAsync(new AuditFilter(Search: "10.0.0"), 0, 0, CancellationToken.None)).MatchCount); // the client address
        Assert.Equal("Changed user", Assert.Single((await _log.QueryAsync(new AuditFilter(Search: "jürgen"), 0, 0, CancellationToken.None)).Entries).Action);
        Assert.Equal("Changed user", Assert.Single((await _log.QueryAsync(new AuditFilter(Search: "ÖRJAN"), 0, 0, CancellationToken.None)).Entries).Action);

        // % and _ are plain characters.
        Assert.Equal("Started task", Assert.Single((await _log.QueryAsync(new AuditFilter(Search: "50%"), 0, 0, CancellationToken.None)).Entries).Action);
        Assert.Equal("Started task", Assert.Single((await _log.QueryAsync(new AuditFilter(Search: "e_n"), 0, 0, CancellationToken.None)).Entries).Action);
        Assert.Empty((await _log.QueryAsync(new AuditFilter(Search: "%_%"), 0, 0, CancellationToken.None)).Entries);

        // A count only.
        var count = await _log.QueryAsync(new AuditFilter(Search: "admin"), -1, 0, CancellationToken.None);
        Assert.Equal((0, 2), (count.Entries.Count, count.MatchCount));
    }

    [Fact]
    public async Task PagesGoOnAfterTheirLastEntryAndANewEntryNeverMovesThem()
    {
        for (var i = 0; i < 7; i++)
        {
            await WriteAsync("u" + i, i % 2 == 0 ? "Even" : "Odd", "t" + i);
        }

        var first = await _log.QueryAsync(new AuditFilter(Search: "even"), 2, 0, CancellationToken.None);
        Assert.Equal(["u6", "u4"], first.Entries.Select(e => e.UserName));
        Assert.Equal(4, first.MatchCount);
        await WriteAsync("u7", "Even", "t7"); // newer than every page
        var second = await _log.QueryAsync(new AuditFilter(Search: "even"), 2, first.Entries[^1].Id, CancellationToken.None);
        Assert.Equal(["u2", "u0"], second.Entries.Select(e => e.UserName));
        Assert.Empty((await _log.QueryAsync(new AuditFilter(Search: "even"), 2, second.Entries[^1].Id, CancellationToken.None)).Entries);
    }

    [Fact]
    public void LikePatternsTakeTheirWildcardsLiterally() =>
        Assert.Equal(@"%a\%b\_c\\d%", AuditLog.LikePattern(@"a%b_c\d"));
}
