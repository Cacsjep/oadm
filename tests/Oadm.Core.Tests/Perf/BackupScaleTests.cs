using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Backup;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Persistence;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>A backup of 5,000 devices and 50,000 tasks: made, checked and staged in a reasonable time.</summary>
[Trait("Category", "Perf")]
public sealed class BackupScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FiveThousandDevicesAndFiftyThousandTasks()
    {
        var dir = TestDatabase.NewTempDirectory();
        try
        {
            var db = await TestDatabase.CreateAsync(dir);
            await using (var ctx = await db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
            {
                ctx.Devices.AddRange(Enumerable.Range(0, 5000).Select(i => new Device { Serial = $"B8A44F{i:X6}", Address = $"10.1.{i / 250}.{i % 250}", Model = "P3265-V" }));
                var now = DateTime.UtcNow;
                ctx.Tasks.AddRange(Enumerable.Range(0, 50_000).Select(i => new TaskEntity { PluginId = "oadm.restart", Name = "Restart device", Status = TaskState.Done, Owner = "admin", CreatedUtc = now.AddSeconds(-i) }));
                await ctx.SaveChangesAsync();
            }

            var archive = new BackupArchive(db.Paths);
            var file = Path.Combine(dir, "scale.oadmbackup");
            var watch = Stopwatch.StartNew();
            BackupContents contents;
            await using (var stream = File.Create(file))
            {
                contents = await archive.CreateAsync(stream, "correct horse", new BackupHeader { Kind = "manual" }, [], CancellationToken.None);
            }

            var create = watch.Elapsed;
            await db.CloseAsync();
            watch.Restart();
            await using (var stream = File.OpenRead(file))
            {
                await archive.StageAsync(stream, "correct horse", "scale", "admin", "", CancellationToken.None);
            }

            var stage = watch.Elapsed;
            output.WriteLine($"create {create.TotalSeconds:0.0} s, check and stage {stage.TotalSeconds:0.0} s, {new FileInfo(file).Length / 1024 / 1024} MB");

            Assert.Equal(5000, contents.Manifest.Counts.Devices);
            Assert.Equal(50_000, contents.Manifest.Counts.Tasks);
            Assert.True(create < TimeSpan.FromSeconds(60), $"create {create.TotalSeconds} s");
            Assert.True(stage < TimeSpan.FromSeconds(60), $"stage {stage.TotalSeconds} s");
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }
}
