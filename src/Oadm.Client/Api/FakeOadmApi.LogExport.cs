using System.Globalization;
using System.Text;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.Logging;
using Oadm.Contracts.Text;
using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// The filters and exports of the Logs page in fake mode: the server log and the audit log filtered by range and search
/// like the server, and the exports as small files (CSV like the server; a one-page PDF with the count, fake mode has no
/// PDF writer). The requests are kept for tests.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly List<object> _logExports = [];

    /// <summary>The export requests in order (tests).</summary>
    public IReadOnlyList<object> LogExports
    {
        get
        {
            lock (_gate)
            {
                return [.. _logExports];
            }
        }
    }

    /// <summary>While set, an export waits for it before it answers (tests of the busy state and Cancel).</summary>
    public TaskCompletionSource? LogExportGate { get; set; }

    public Task<ServerLogPage> ListServerLogAsync(ListServerLogRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
        }

        var limit = request.Limit <= 0 ? 200 : Math.Clamp(request.Limit, 1, LogFileReader.MaxPageSize);
        var page = ServerLogMatches(request).Skip(Math.Max(0, request.Offset)).Take(limit + 1).ToList();
        var reply = new ServerLogPage { HasMore = page.Count > limit };
        reply.Entries.AddRange(page.Take(limit).Select(ToServerLogEntry));
        return Task.FromResult(reply);
    }

    public Task<AuditList> ListAuditAsync(ListAuditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            var matching = AuditMatchesLocked(request);
            var reply = new AuditList { TotalCount = _audit.Count, MatchCount = matching.Count };
            reply.Entries.AddRange(matching
                .Where(e => request.BeforeId <= 0 || e.Id < request.BeforeId)
                .Skip(Math.Max(0, request.Offset))
                .Take(request.Limit <= 0 ? 10_000 : request.Limit)
                .Select(e => e.Clone()));
            return Task.FromResult(reply);
        }
    }

    public async Task<Oadm.Sdk.Plugins.PluginDownload> ExportServerLogAsync(ExportServerLogRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await BeforeExportAsync(request, ct).ConfigureAwait(false);
        var rows = ServerLogMatches(request.Filter ?? new ListServerLogRequest()).Take(LogExportLimits.MaxEntries)
            .Select(e => new[] { e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), e.Level, e.Source, e.Message }).ToList();
        return ExportFile(LogExportLimits.ServerLog, request.Options, ["Time", "Level", "Source", "Message"], rows);
    }

    public async Task<Oadm.Sdk.Plugins.PluginDownload> ExportAuditAsync(ExportAuditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await BeforeExportAsync(request, ct).ConfigureAwait(false);
        List<string[]> rows;
        lock (_gate)
        {
            SeedAuthLocked();
            rows = [.. AuditMatchesLocked(request.Filter ?? new ListAuditRequest()).Take(LogExportLimits.MaxEntries).Select(e => new[]
            {
                e.Time?.ToDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty, e.UserName, e.ClientAddress, e.Action, e.Target, e.Detail,
            })];
            AuditLocked("Exported log", "Audit log", FormatOf(request.Options) + string.Create(CultureInfo.CurrentCulture, $", {rows.Count:N0} entries"));
        }

        return ExportFile(LogExportLimits.AuditLog, request.Options, ["Time", "User", "Client", "Action", "Target", "Detail"], rows);
    }

    public async Task<Oadm.Sdk.Plugins.PluginDownload> ExportClientLogAsync(ExportClientLogRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Entries.Count > LogExportLimits.MaxEntries)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "An export holds at most 5,000 entries."));
        }

        await BeforeExportAsync(request, ct).ConfigureAwait(false);
        var rows = request.Entries.Select(e => new[]
        {
            e.Time?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty, e.Level, e.Message,
        }).ToList();
        return ExportFile(LogExportLimits.ClientLog, request.Options, ["Time", "Level", "Message"], rows);
    }

    private async Task BeforeExportAsync(object request, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _logExports.Add(request);
        }

        if (LogExportGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static string FormatOf(LogExportOptions? options) => options?.Format == LogExportFormat.Pdf ? "PDF" : "CSV";

    private static Oadm.Sdk.Plugins.PluginDownload ExportFile(string log, LogExportOptions? options, string[] columns, List<string[]> rows)
    {
        var pdf = options?.Format == LogExportFormat.Pdf;
        byte[] bytes;
        if (pdf)
        {
            bytes = MinimalPdf(string.Create(CultureInfo.InvariantCulture, $"OADM {log} log: {rows.Count} entries (fake mode)"));
        }
        else
        {
            var sb = new StringBuilder();
            CsvWriter.AppendRecord(sb, columns);
            foreach (var row in rows)
            {
                CsvWriter.AppendRecord(sb, row);
            }

            bytes = CsvWriter.ToBytes(sb.ToString());
        }

        return new Oadm.Sdk.Plugins.PluginDownload(new MemoryStream(bytes), LogExportLimits.FileName(log, pdf, DateTimeOffset.Now), bytes.Length);
    }

    /// <summary>A valid one-page PDF with one line of text (Helvetica, ASCII only).</summary>
    private static byte[] MinimalPdf(string line)
    {
        var text = new string([.. line.Where(c => c is >= ' ' and <= '~' and not '(' and not ')' and not '\\')]);
        var content = $"BT /F1 14 Tf 50 780 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static IEnumerable<LogFileEntry> ServerLogMatches(ListServerLogRequest request)
    {
        var minRank = string.IsNullOrEmpty(request.MinLevel) ? 0 : LogFileReader.Rank(request.MinLevel);
        DateTimeOffset? from = ParseTime(request.NotBefore), to = ParseTime(request.NotAfter);
        var search = request.Search;
        return FakeServerLog.Value.Where(e => e.Rank >= minRank
            && (from is null || e.Time >= from)
            && (to is null || e.Time <= to)
            && (string.IsNullOrWhiteSpace(search)
                || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Exception.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private static DateTimeOffset? ParseTime(string text) =>
        string.IsNullOrEmpty(text) ? null
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time
        : throw new RpcException(new Status(StatusCode.InvalidArgument, "Enter a time like 2026-11-14T09:30:00Z."));

    /// <summary>The audit entries of a filter, newest first.</summary>
    private List<AuditEntry> AuditMatchesLocked(ListAuditRequest request)
    {
        var from = request.NotBefore?.ToDateTimeOffset();
        var to = request.NotAfter?.ToDateTimeOffset();
        var text = request.Search.Trim();
        return [.. _audit.OrderByDescending(e => e.Id).Where(e =>
            (from is null || (e.Time is { } t1 && t1.ToDateTimeOffset() >= from))
            && (to is null || (e.Time is { } t2 && t2.ToDateTimeOffset() <= to))
            && (text.Length == 0 || new[] { e.UserName, e.ClientAddress, e.Action, e.Target, e.Detail }.Any(f => f.Contains(text, StringComparison.OrdinalIgnoreCase))))];
    }

    private static ServerLogEntry ToServerLogEntry(LogFileEntry e) => new()
    {
        Time = e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        Level = e.Level,
        Source = e.Source,
        Message = e.Message,
        Exception = e.Exception,
    };
}
