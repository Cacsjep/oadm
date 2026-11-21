using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Tests.Persistence;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>The Audit tab's query at the audit log's limit (<see cref="AuditLog.MaxEntries"/> = 200,000 entries).</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class AuditQueryScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ASearchAndARangeOverTheWholeLogStayFast()
    {
        await using var db = await TestDatabase.CreateAsync();
        var factory = db.Get<IDbContextFactory<OadmDbContext>>();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] actions = [AuditActions.LoginOk, AuditActions.TaskRun, AuditActions.DevicesTagged, AuditActions.SettingsChanged, AuditActions.Logout];
        for (var batch = 0; batch < AuditLog.MaxEntries; batch += 20_000)
        {
            await using var context = await factory.CreateDbContextAsync();
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = batch; i < batch + 20_000; i++)
            {
                context.AuditEntries.Add(new AuditEntryEntity
                {
                    TimeUtc = start.AddMinutes(i * 2),
                    UserName = "user" + (i % 40),
                    ClientAddress = "10.0." + (i % 250) + ".1",
                    Action = actions[i % actions.Length],
                    Target = "Camera " + i,
                    Detail = i % 1000 == 0 ? "Rare Ölwechsel" : "Restart on 12 devices",
                });
            }

            await context.SaveChangesAsync();
        }

        var log = new AuditLog(factory, TimeProvider.System);
        var filter = new AuditFilter(start.AddDays(30), start.AddDays(200), "rare");
        AuditPage page = null!;
        await PerfScale.MeasureAsync(output, "Audit query, 200,000 entries, range and rare text (measured about 180 ms)", TimeSpan.FromSeconds(2),
            async () => page = await log.QueryAsync(filter, 200, 0, CancellationToken.None));
        Assert.Equal(AuditLog.MaxEntries, page.TotalCount);
        Assert.True(page.MatchCount is > 100 and < 200, page.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await PerfScale.MeasureAsync(output, "Audit query, 200,000 entries, non-ASCII text (measured about 370 ms)", TimeSpan.FromSeconds(4),
            async () => page = await log.QueryAsync(new AuditFilter(Search: "ölwechsel"), 200, 0, CancellationToken.None));
        Assert.Equal(AuditLog.MaxEntries / 1000, page.MatchCount);

        await PerfScale.MeasureAsync(output, "Audit query, 200,000 entries, export of the newest 5,000 of a range (measured about 40 ms)", TimeSpan.FromSeconds(1),
            async () => page = await log.QueryAsync(new AuditFilter(start.AddDays(100), null, null), 5_000, 0, CancellationToken.None));
        Assert.Equal(5_000, page.Entries.Count);
    }
}
