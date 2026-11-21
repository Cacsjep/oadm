using System.Text;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.Logging;
using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Server.Logging.Export;
using Oadm.Server.Tests.Support;

using PdfSharp.Pdf.IO;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// The exports of the Logs page (docs/spec/main-window.md "Logs page"): CSV and PDF of the server log, the audit log and the
/// client's rows, the newest 5,000, the permission Export logs and one audit entry per export.
/// </summary>
public sealed class LogExportTests
{
    private const string Day = """
        2026-10-17 08:00:00.000 +02:00 [INF] Oadm.Core.Tasks.TaskEngine: Task Restart device done
        2026-10-17 08:00:01.000 +02:00 [ERR] Oadm.Core.Events.ActionRunner: Event rule Gate: action failed, "quoted"
        System.IO.IOException: The connection was reset.
        2026-10-17 09:30:00.000 +02:00 [WRN] Oadm.Core.Events.ActionRunner: Event rule Gate: slow
        """;

    private static readonly Proto.LogExportOptions Csv = new() { Format = Proto.LogExportFormat.Csv, TimeZone = "UTC" };
    private static readonly Proto.LogExportOptions Pdf = new() { Format = Proto.LogExportFormat.Pdf, TimeZone = "UTC", RangeName = "Custom" };

    private static async Task<(string Name, long Length, byte[] Data)> ReadAsync(AsyncServerStreamingCall<Proto.DownloadChunk> call)
    {
        using (call)
        {
            string? name = null;
            long length = 0;
            using var data = new MemoryStream();
            await foreach (var chunk in call.ResponseStream.ReadAllAsync())
            {
                if (name is null)
                {
                    name = chunk.FileName;
                    length = chunk.Length;
                    continue;
                }

                chunk.Data.WriteTo(data);
            }

            return (name!, length, data.ToArray());
        }
    }

    private static string Text(byte[] csv)
    {
        Assert.Equal(Encoding.UTF8.GetPreamble(), csv[..3]); // BOM: Excel reads UTF-8
        return Encoding.UTF8.GetString(csv, 3, csv.Length - 3);
    }

    [Fact]
    public async Task TheServerLogExportHoldsTheTabsFilterInTheUsersTimeAndIsAudited()
    {
        await using var host = await TestServerHost.StartAsync();
        var logs = host.Get<OadmPaths>().LogsDirectory;
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "oadm-server-20261017.log"), Day);
        var client = new Proto.LogExportService.LogExportServiceClient(host.Invoker);

        var filter = new Proto.ListServerLogRequest { Search = "gate", NotBefore = "2026-10-17T08:00:00+02:00", NotAfter = "2026-10-17T09:00:00+02:00" };
        var (name, length, data) = await ReadAsync(client.ExportServerLog(new Proto.ExportServerLogRequest { Filter = filter, Options = Csv }));
        Assert.Matches(@"^oadm-server-\d{4}-\d{2}-\d{2}\.csv$", name);
        Assert.Equal(length, data.Length);
        var lines = Text(data).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time,Time (ISO 8601),Level,Source,Message,Exception", lines[0]);
        Assert.Equal("2026-10-17 06:00:01,2026-10-17T06:00:01+00:00,Error,ActionRunner,\"Event rule Gate: action failed, \"\"quoted\"\"\",System.IO.IOException: The connection was reset.", lines[1]);
        Assert.Equal(2, lines.Length);

        // Another time zone: the same instant in the user's time.
        var berlin = new Proto.LogExportOptions { TimeZone = "no such zone", UtcOffsetMinutes = 120 };
        var local = Text((await ReadAsync(client.ExportServerLog(new Proto.ExportServerLogRequest { Filter = filter, Options = berlin }))).Data);
        Assert.Contains("2026-10-17 08:00:01,2026-10-17T08:00:01+02:00,", local, StringComparison.Ordinal);

        var entry = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.First(e => e.Action == AuditActions.LogExported);
        Assert.Equal(LogExportGrpcService.ServerLogName, entry.Target);
        Assert.Equal("CSV, 1 entry, 2026-10-17 06:00 to 2026-10-17 07:00 UTC, search \"gate\"", entry.Detail);
    }

    [Fact]
    public async Task TheAuditExportIsForAdministratorsAndItsPdfEmbedsRoboto()
    {
        await using var host = await TestServerHost.StartAsync();
        var audit = host.Get<AuditLog>();
        for (var i = 0; i < 300; i++)
        {
            await audit.WriteAsync("tech" + (i % 3), "10.0.0." + i, AuditActions.TaskRun, "Restart", "On camera " + i);
        }

        // A long word (a URL) wraps in its cell.
        await audit.WriteAsync("admin", "10.0.0.1", AuditActions.WebInterfaceOpened, "10.0.0.1",
            "https://10.0.0.1/axis-cgi/basicdeviceinfo.cgi?method=getAllProperties&apiVersion=1.3&context=oadm-export-check-of-a-long-word");

        var client = new Proto.LogExportService.LogExportServiceClient(host.Invoker);
        var csv = Text((await ReadAsync(client.ExportAudit(new Proto.ExportAuditRequest { Filter = new Proto.ListAuditRequest { Search = "TECH1" }, Options = Csv }))).Data);
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time,Time (ISO 8601),User,Client,Action,Target,Detail", rows[0]);
        Assert.Equal(101, rows.Length); // the header and 100 entries of tech1
        Assert.All(rows.Skip(1), r => Assert.Contains(",tech1,", r, StringComparison.Ordinal));

        var (name, _, pdf) = await ReadAsync(client.ExportAudit(new Proto.ExportAuditRequest { Filter = new Proto.ListAuditRequest(), Options = Pdf }));
        Assert.Matches(@"^oadm-audit-\d{4}-\d{2}-\d{2}\.pdf$", name);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        using (var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import))
        {
            Assert.True(document.PageCount is >= 5 and <= 20, document.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)); // 300 and more rows on A4 landscape
            var page = document.Pages[0];
            Assert.True(page.Orientation == PdfSharp.PageOrientation.Landscape || page.Width.Point > page.Height.Point, $"{page.Width.Point} x {page.Height.Point}, {page.Orientation}"); // A4 landscape
        }

        var raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("Roboto", raw, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", raw, StringComparison.Ordinal); // embedded, never a system font
        WriteSample("sample-audit.pdf", pdf);

        // The export is audited once per file, and an operator may not export the audit log.
        Assert.Equal(2, (await host.Audit.ListAsync(new Proto.ListAuditRequest { Search = AuditActions.LogExported })).MatchCount);
        var op = new Proto.LogExportService.LogExportServiceClient(await host.InvokerForUserAsync("op", Oadm.Sdk.Plugins.UserRole.Operator));
        var denied = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(op.ExportAudit(new Proto.ExportAuditRequest { Options = Csv })));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    [Fact]
    public async Task ExportsNeedExportLogsAndTheServerLogAlsoItsOwnPermission()
    {
        await using var host = await TestServerHost.StartAsync();
        var roles = new Proto.RoleService.RoleServiceClient(host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Readers", Permissions = { } } } });
        var role = saved.Roles.Single(r => r.Name == "Readers");
        var reader = new Proto.LogExportService.LogExportServiceClient(await host.InvokerForRoleAsync("reader", Guid.Parse(role.Id)));
        var clientRows = new Proto.ExportClientLogRequest { Options = Csv, Entries = { new Proto.ClientLogEntry { Time = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch), Level = "Information", Message = "hi" } } };

        var denied = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(reader.ExportClientLog(clientRows)));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Contains("Your role does not allow: Export logs.", denied.Status.Detail, StringComparison.Ordinal);

        // Export logs alone: the client log yes, the server log not without "See the server log".
        await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Id = role.Id, Name = "Readers", Permissions = { HostPermissions.LogsExport } } } });
        Assert.NotEmpty((await ReadAsync(reader.ExportClientLog(clientRows))).Data);
        var server = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(reader.ExportServerLog(new Proto.ExportServerLogRequest { Options = Csv })));
        Assert.Equal(StatusCode.PermissionDenied, server.StatusCode);
        Assert.Contains("See the server log", server.Status.Detail, StringComparison.Ordinal);

        await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Id = role.Id, Name = "Readers", Permissions = { HostPermissions.LogsExport, HostPermissions.LogsServer } } } });
        Assert.NotEmpty((await ReadAsync(reader.ExportServerLog(new Proto.ExportServerLogRequest { Options = Csv }))).Data);

        // Operators have Export logs by default, viewers not.
        Assert.Contains(HostPermissions.All, p => p.Key == HostPermissions.LogsExport && p.OperatorDefault && !p.ViewerDefault);
    }

    [Fact]
    public async Task TheClientLogExportTakesAtMost5000RowsAndCutsLongMessages()
    {
        await using var host = await TestServerHost.StartAsync();
        var client = new Proto.LogExportService.LogExportServiceClient(host.Invoker);
        var time = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
        var many = new Proto.ExportClientLogRequest { Options = Csv };
        many.Entries.AddRange(Enumerable.Range(0, LogExportLimits.MaxEntries + 1).Select(i => new Proto.ClientLogEntry { Time = time, Level = "Information", Message = "m" + i }));
        var refused = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(client.ExportClientLog(many)));
        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);

        var request = new Proto.ExportClientLogRequest
        {
            Options = new Proto.LogExportOptions { Format = Proto.LogExportFormat.Csv, TimeZone = "UTC", RangeName = "Last hour" },
            MinLevel = "Warning",
            Search = "connect",
            NotBefore = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero)),
            Entries =
            {
                new Proto.ClientLogEntry { Time = time, Level = "Warning", Message = "=Connect " + new string('x', LogExportLimits.MaxMessageLength) },
            },
        };
        var (name, _, data) = await ReadAsync(client.ExportClientLog(request));
        Assert.Matches(@"^oadm-client-\d{4}-\d{2}-\d{2}\.csv$", name);
        var rows = Text(data).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time,Time (ISO 8601),Level,Message", rows[0]);
        Assert.StartsWith("2026-11-21 09:00:00,2026-11-21T09:00:00+00:00,Warning,'=Connect xxx", rows[1], StringComparison.Ordinal); // never a formula
        Assert.EndsWith("…", rows[1], StringComparison.Ordinal);
        Assert.True(rows[1].Length < LogExportLimits.MaxMessageLength + 80);

        var entry = (await host.Audit.ListAsync(new Proto.ListAuditRequest { Search = AuditActions.LogExported })).Entries.Single();
        Assert.Equal((LogExportGrpcService.ClientLogName, "CSV, 1 entry, Last hour: from 2026-11-21 08:00 UTC, level Warnings and errors, search \"connect\""), (entry.Target, entry.Detail));
    }

    [Fact]
    public void ThePdfHeadNamesTheFilterTheServerAndWhoExportedItWhen()
    {
        var clock = LogExportClock.Of("UTC", 0);
        var filter = LogExportGrpcService.FilterLines(clock, "Last 24 hours", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero), "Error", "gate");
        Assert.Equal(["Dates: Last 24 hours, 2026-11-20 10:00 to 2026-11-21 10:00", "Level: Errors only", "Search: gate"], filter);
        Assert.Equal(["Dates: All"], LogExportGrpcService.FilterLines(clock, null, null, null, null, null));

        var head = new LogExportHead("Audit log", filter, "OADM-HQ", "admin", new DateTimeOffset(2026, 11, 21, 10, 5, 0, TimeSpan.Zero), 5_000, Limited: true);
        Assert.Equal([.. filter, "The newest 5,000 entries", "Server: OADM-HQ", "Exported 2026-11-21 10:05 by admin"], LogPdf.HeadLines(head));

        // Long words without spaces (paths, addresses) can wrap in their cell.
        Assert.Contains('​', LogPdf.Breakable("https://10.0.0.1/axis-cgi/basicdeviceinfo.cgi?a=b&c=d/and/more/and/more/and/more"));
        Assert.Equal("a b", LogPdf.Breakable("a\nb"));
    }

    /// <summary>Writes a sample file to <c>OADM_SCREENSHOT_DIR</c> (for looking at it), when set.</summary>
    internal static void WriteSample(string name, byte[] data)
    {
        var folder = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), data);
    }
}
