using Grpc.Core;

using Oadm.Contracts.Logging;
using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>The server log on the Logs page: the log files read a page at a time, newest first, with filter and permission.</summary>
public sealed class ServerLogTests
{
    private const string Day1 = """
        2026-10-16 23:59:58.100 +02:00 [INF] Oadm.Server.Hosting.OadmServerHost: Server started
        2026-10-16 23:59:59.200 +02:00 [WRN] Oadm.Core.Events.ActionRunner: Event rule Gate: action Send email failed: smtp.example.com:587 cannot be reached
        """;

    private const string Day2 = """
        2026-10-17 08:00:00.000 +02:00 [INF] Oadm.Core.Tasks.TaskEngine: Task Restart device done
        2026-10-17 08:00:01.000 +02:00 [ERR] Oadm.Core.Events.ActionRunner: Event rule Gate: action Send HTTP request failed unexpectedly
        System.IO.IOException: The connection was reset.
           at Oadm.Plugins.Notifications.SendHttpAction.ExecuteAsync()
        2026-10-17 08:00:02.000 +02:00 [INF] A line without a source: still a message
        """;

    [Fact]
    public void LogFilesAreReadNewestFirstWithExceptionsAndSources()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "oadm-server-20261016.log"), Day1);
            File.WriteAllText(Path.Combine(folder, "oadm-server-20261017.log"), Day2);
            File.WriteAllText(Path.Combine(folder, "client-20261017.log"), Day2); // another pattern

            var all = LogFileReader.Read(folder, "oadm-server-*.log", 0, 10, 0, null, CancellationToken.None);
            Assert.False(all.HasMore);
            Assert.Equal(5, all.Entries.Count);
            Assert.Equal(("Information", "", "A line without a source: still a message"), (all.Entries[0].Level, all.Entries[0].Source, all.Entries[0].Message));
            Assert.Equal(("Error", "Oadm.Core.Events.ActionRunner"), (all.Entries[1].Level, all.Entries[1].Source));
            Assert.StartsWith("System.IO.IOException: The connection was reset.", all.Entries[1].Exception, StringComparison.Ordinal);
            Assert.Equal("Server started", all.Entries[4].Message);
            Assert.Equal(new DateTimeOffset(2026, 10, 16, 23, 59, 58, 100, TimeSpan.FromHours(2)), all.Entries[4].Time);

            // Pages, levels and search over both files.
            var page = LogFileReader.Read(folder, "oadm-server-*.log", 1, 2, 0, null, CancellationToken.None);
            Assert.True(page.HasMore);
            Assert.Equal(["Event rule Gate: action Send HTTP request failed unexpectedly", "Task Restart device done"], page.Entries.Select(e => e.Message));
            var problems = LogFileReader.Read(folder, "oadm-server-*.log", 0, 10, LogFileReader.Rank("Warning"), "gate", CancellationToken.None);
            Assert.Equal(["Error", "Warning"], problems.Entries.Select(e => e.Level));
            Assert.Single(LogFileReader.Read(folder, "oadm-server-*.log", 0, 10, 0, "connection was reset", CancellationToken.None).Entries); // in the exception
            Assert.Empty(LogFileReader.Read(Path.Combine(folder, "missing"), "*.log", 0, 10, 0, null, CancellationToken.None).Entries);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheServerLogNeedsItsPermissionAndShowsWhatTheServerWrote()
    {
        await using var host = await TestServerHost.StartAsync();
        var logs = host.Get<OadmPaths>().LogsDirectory;
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "oadm-server-20261017.log"), Day2);

        var page = await new Proto.ServerLogService.ServerLogServiceClient(host.Invoker).ListAsync(new Proto.ListServerLogRequest { Limit = 2, MinLevel = "Information" });
        Assert.True(page.HasMore);
        Assert.Equal("A line without a source: still a message", page.Entries[0].Message);
        Assert.Equal("2026-10-17T08:00:01.000+02:00", page.Entries[1].Time);
        Assert.Contains("IOException", page.Entries[1].Exception, StringComparison.Ordinal);

        // An operator does not see it until the role gets "See the server log".
        var op = new Proto.ServerLogService.ServerLogServiceClient(await host.InvokerForUserAsync("op", UserRole.Operator));
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await op.ListAsync(new Proto.ListServerLogRequest()));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Contains("See the server log", denied.Status.Detail, StringComparison.Ordinal);
    }
}
