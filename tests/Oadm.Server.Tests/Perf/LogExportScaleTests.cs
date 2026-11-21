using System.Globalization;
using System.Text;

using Oadm.Contracts.Logging;
using Oadm.Server.Logging.Export;

using PdfSharp.Pdf.IO;

using Xunit.Abstractions;

namespace Oadm.Server.Tests.Perf;

/// <summary>The Logs page at its sizes: 14 days of server log files read with a From time, and the PDF of 5,000 entries.</summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class LogExportScaleTests(ITestOutputHelper output)
{
    private const int LinesPerDay = 20_000;

    [Fact]
    public async Task AFromTimeReadsOnlyTheFilesOfItsDaysAndAnExportOf5000EntriesStaysFast()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-log-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            for (var day = 0; day < 14; day++)
            {
                var text = new StringBuilder(LinesPerDay * 110);
                for (var i = 0; i < LinesPerDay; i++)
                {
                    var at = first.AddDays(day).AddSeconds(i * 4);
                    text.Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                        .Append(i % 50 == 0 ? " [WRN] " : " [INF] ").Append("Oadm.Core.Devices.DevicePoller: Device 10.0.").Append(i % 250).Append(".1 answered in 12 ms\n");
                }

                await File.WriteAllTextAsync(Path.Combine(folder, $"oadm-server-{first.AddDays(day):yyyyMMdd}.log"), text.ToString());
            }

            var from = first.AddDays(12).AddHours(6);
            LogFilePage page = null!;
            await ServerScale.MeasureAsync(output, "Server log, 14 days x 20,000 lines, a search without a match from the last 2 days (reads 2 files, measured about 140 ms)", TimeSpan.FromSeconds(2), () =>
            {
                page = LogFileReader.Read(folder, "oadm-server-*.log", 0, 200, LogFileReader.Rank("Error"), "10.0.", CancellationToken.None, notBefore: from);
                return Task.CompletedTask;
            });
            Assert.Equal((0, false), (page.Entries.Count, page.HasMore));

            await ServerScale.MeasureAsync(output, "Server log, 14 days x 20,000 lines, the newest 5,000 for an export (measured about 55 ms)", TimeSpan.FromSeconds(1), () =>
            {
                page = LogFileReader.ReadForExport(folder, "oadm-server-*.log", LogExportLimits.MaxEntries, 0, null, CancellationToken.None, notBefore: from);
                return Task.CompletedTask;
            });
            Assert.Equal(LogExportLimits.MaxEntries, page.Entries.Count);

            var clock = LogExportClock.Of("UTC", 0);
            var table = new LogTable(LogExportGrpcService.ServerColumns,
                [.. page.Entries.Select(e => new[] { clock.Text(e.Time), clock.Iso(e.Time), e.Level, LogExportGrpcService.ShortSource(e.Source), e.Message, e.Exception })]);
            var head = new LogExportHead(LogExportGrpcService.ServerLogName, ["Dates: from " + clock.Minutes(from)], "OADM", "admin", DateTimeOffset.UtcNow, table.Rows.Count, true);
            RenderedLogPdf pdf = null!;
            await ServerScale.MeasureAsync(output, "PDF of 5,000 server log entries (measured about 2.2 s)", TimeSpan.FromSeconds(25), () =>
            {
                pdf = LogPdf.Render(table, head);
                return Task.CompletedTask;
            });
            output.WriteLine($"{pdf.Pages} pages, {pdf.Pdf.Length / 1024} KB");
            using (var document = PdfReader.Open(new MemoryStream(pdf.Pdf), PdfDocumentOpenMode.Import))
            {
                Assert.Equal(pdf.Pages, document.PageCount);
            }

            Assert.InRange(pdf.Pages, 50, 400);
            byte[] csv = null!;
            await ServerScale.MeasureAsync(output, "CSV of 5,000 server log entries (measured about 4 ms)", TimeSpan.FromSeconds(1), () =>
            {
                csv = LogCsv.ToBytes(table);
                return Task.CompletedTask;
            });
            Assert.True(csv.Length > 5_000 * 50);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
