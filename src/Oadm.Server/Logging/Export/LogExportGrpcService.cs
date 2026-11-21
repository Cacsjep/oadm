using System.Globalization;

using Grpc.Core;

using Oadm.Contracts.Logging;
using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Server.Auth;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Logging.Export;

/// <summary>
/// gRPC LogExportService: what a tab of the Logs page shows as a CSV or PDF file (docs/spec/main-window.md "Logs page"), the
/// newest <see cref="LogExportLimits.MaxEntries"/> entries of its filter. Every export needs "Export logs" besides the right to
/// see the log (AccessPolicy; the server log's own permission is checked here) and writes one audit entry "Exported log".
/// The file is made in memory (5,000 rows are a few MB at most) and sent in chunks, uncompressed like every file download.
/// </summary>
public sealed class LogExportGrpcService(OadmPaths paths, AuditLog audit, RoleStore roles, ServerSettingsStore settings, TimeProvider time)
    : Proto.LogExportService.LogExportServiceBase
{
    public const string ServerLogName = "Server log";
    public const string AuditLogName = "Audit log";
    public const string ClientLogName = "Client log";

    private const int ChunkBytes = 256 * 1024;

    public static IReadOnlyList<LogColumn> ServerColumns { get; } =
    [
        new("Time", 1.6), new("Time (ISO 8601)", 0, InPdf: false), new("Level", 1.1), new("Source", 1.8), new("Message", 7.5), new("Exception", 0, InPdf: false),
    ];

    public static IReadOnlyList<LogColumn> ClientColumns { get; } =
    [
        new("Time", 1.6), new("Time (ISO 8601)", 0, InPdf: false), new("Level", 1.1), new("Message", 9.3),
    ];

    public static IReadOnlyList<LogColumn> AuditColumns { get; } =
    [
        new("Time", 1.6), new("Time (ISO 8601)", 0, InPdf: false), new("User", 1.4), new("Client", 1.4), new("Action", 2.0), new("Target", 2.0), new("Detail", 4.0),
    ];

    public override async Task ExportServerLog(Proto.ExportServerLogRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        AuthInterceptor.Require(HostPermissions.LogsServer, roles.Catalog);
        var ct = context.CancellationToken;
        var filter = request.Filter ?? new Proto.ListServerLogRequest();
        var options = request.Options ?? new Proto.LogExportOptions();
        var notAfter = ServerLogGrpcService.ParseTime(filter.NotAfter);
        var notBefore = ServerLogGrpcService.ParseTime(filter.NotBefore);
        var clock = LogExportClock.Of(options.TimeZone, options.UtcOffsetMinutes);
        var page = await Task.Run(
            () => LogFileReader.ReadForExport(paths.LogsDirectory, ServerLogGrpcService.FilePattern, LogExportLimits.MaxEntries,
                ServerLogGrpcService.MinRank(filter.MinLevel), filter.Search, ct, notAfter, notBefore),
            ct).ConfigureAwait(false);
        var rows = page.Entries.Select(e => new[]
        {
            clock.Text(e.Time), clock.Iso(e.Time), e.Level, ShortSource(e.Source), e.Message, e.Exception,
        }).ToList();
        var lines = FilterLines(clock, options.RangeName, notBefore, notAfter, filter.MinLevel, filter.Search);
        await SendAsync(LogExportLimits.ServerLog, ServerLogName, new LogTable(ServerColumns, rows), page.HasMore, lines, clock, options,
            AuditRange(options.RangeName, notBefore, notAfter, filter.MinLevel, filter.Search), responseStream, context).ConfigureAwait(false);
    }

    public override async Task ExportAudit(Proto.ExportAuditRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var filter = AuditGrpcService.FilterOf(request.Filter ?? new Proto.ListAuditRequest());
        var options = request.Options ?? new Proto.LogExportOptions();
        var clock = LogExportClock.Of(options.TimeZone, options.UtcOffsetMinutes);
        var page = await audit.QueryAsync(filter, LogExportLimits.MaxEntries, 0, ct).ConfigureAwait(false);
        var rows = page.Entries.Select(e =>
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc));
            return new[] { clock.Text(at), clock.Iso(at), e.UserName, e.ClientAddress, e.Action, e.Target, e.Detail };
        }).ToList();
        DateTimeOffset? from = filter.NotBeforeUtc is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)) : null;
        DateTimeOffset? to = filter.NotAfterUtc is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;
        var lines = FilterLines(clock, options.RangeName, from, to, null, filter.Search);
        await SendAsync(LogExportLimits.AuditLog, AuditLogName, new LogTable(AuditColumns, rows), page.MatchCount > rows.Count, lines, clock, options,
            AuditRange(options.RangeName, from, to, null, filter.Search), responseStream, context).ConfigureAwait(false);
    }

    public override async Task ExportClientLog(Proto.ExportClientLogRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (request.Entries.Count > LogExportLimits.MaxEntries)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                string.Create(CultureInfo.InvariantCulture, $"An export holds at most {LogExportLimits.MaxEntries:N0} entries.")));
        }

        var options = request.Options ?? new Proto.LogExportOptions();
        var clock = LogExportClock.Of(options.TimeZone, options.UtcOffsetMinutes);
        var rows = request.Entries.Select(e =>
        {
            var at = e.Time is null ? DateTimeOffset.MinValue : e.Time.ToDateTimeOffset();
            return new[] { clock.Text(at), clock.Iso(at), Cut(e.Level, 20), Cut(e.Message, LogExportLimits.MaxMessageLength) };
        }).ToList();
        DateTimeOffset? from = request.NotBefore?.ToDateTimeOffset();
        DateTimeOffset? to = request.NotAfter?.ToDateTimeOffset();
        var search = Cut(request.Search, 200);
        var level = Cut(request.MinLevel, 20);
        var lines = FilterLines(clock, Cut(options.RangeName, 80), from, to, level, search);
        await SendAsync(LogExportLimits.ClientLog, ClientLogName, new LogTable(ClientColumns, rows), false, lines, clock, options,
            AuditRange(options.RangeName, from, to, level, search), responseStream, context).ConfigureAwait(false);
    }

    private async Task SendAsync(string log, string logName, LogTable table, bool limited, IReadOnlyList<string> filterLines, LogExportClock clock,
        Proto.LogExportOptions options, string auditRange, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var caller = AuthInterceptor.RequireCaller();
        var pdf = options.Format == Proto.LogExportFormat.Pdf;
        var now = clock.Local(time.GetUtcNow());
        byte[] file;
        if (pdf)
        {
            var server = (await settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName;
            var head = new LogExportHead(logName, filterLines, server, caller.AuditUserName ?? caller.UserName, now, table.Rows.Count, limited);
            file = await Task.Run(() => LogPdf.Render(table, head, ct).Pdf, ct).ConfigureAwait(false);
        }
        else
        {
            file = await Task.Run(() => LogCsv.ToBytes(table), ct).ConfigureAwait(false);
        }

        await audit.WriteAsync(AuditActions.LogExported, logName, AuditDetail(pdf, table.Rows.Count, auditRange), ct).ConfigureAwait(false);

        context.WriteOptions = new WriteOptions(WriteFlags.NoCompress); // a file download
        await responseStream.WriteAsync(new Proto.DownloadChunk { FileName = LogExportLimits.FileName(log, pdf, now), Length = file.Length }, ct).ConfigureAwait(false);
        for (var offset = 0; offset < file.Length; offset += ChunkBytes)
        {
            await responseStream.WriteAsync(new Proto.DownloadChunk { Data = Google.Protobuf.ByteString.CopyFrom(file, offset, Math.Min(ChunkBytes, file.Length - offset)) }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>"PDF, 1,234 entries, Last 24 hours: 2026-11-20 10:00 to 2026-11-21 10:00 UTC, search "gate"".</summary>
    internal static string AuditDetail(bool pdf, int entries, string range) =>
        string.Create(CultureInfo.InvariantCulture, $"{(pdf ? "PDF" : "CSV")}, {(entries == 1 ? "1 entry" : $"{entries:N0} entries")}, {range}");

    /// <summary>The range and filter of the audit entry, in UTC (the entry is read in other time zones).</summary>
    internal static string AuditRange(string? rangeName, DateTimeOffset? from, DateTimeOffset? to, string? minLevel, string? search)
    {
        var utc = LogExportClock.Of("UTC", 0);
        var range = from is null && to is null ? "all dates" : RangeTimes(utc, from, to) + " UTC";
        if (!string.IsNullOrWhiteSpace(rangeName))
        {
            range = rangeName.Trim() + ": " + range;
        }

        var parts = new List<string> { range };
        if (!string.IsNullOrWhiteSpace(minLevel))
        {
            parts.Add("level " + LevelTitle(minLevel));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add("search \"" + search.Trim() + "\"");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The filter lines of the PDF head: "Dates: Last 24 hours, 2026-11-20 10:00 to 2026-11-21 10:00", "Level: ...", "Search: ...".</summary>
    internal static IReadOnlyList<string> FilterLines(LogExportClock clock, string? rangeName, DateTimeOffset? from, DateTimeOffset? to, string? minLevel, string? search)
    {
        var lines = new List<string>();
        var times = from is null && to is null ? null : RangeTimes(clock, from, to);
        // "Custom" says nothing in a file: its times say it all ("Dates: 2026-11-20 10:00 to 2026-11-21 10:00").
        var name = string.IsNullOrWhiteSpace(rangeName) || (times is not null && string.Equals(rangeName.Trim(), "Custom", StringComparison.OrdinalIgnoreCase)) ? null : rangeName.Trim();
        lines.Add("Dates: " + (name, times) switch
        {
            (null, null) => "All",
            (null, { } t) => t,
            ({ } n, null) => n,
            ({ } n, { } t) => n + ", " + t,
        });
        if (!string.IsNullOrWhiteSpace(minLevel))
        {
            lines.Add("Level: " + LevelTitle(minLevel));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            lines.Add("Search: " + search.Trim());
        }

        return lines;
    }

    private static string RangeTimes(LogExportClock clock, DateTimeOffset? from, DateTimeOffset? to) => (from, to) switch
    {
        ({ } f, { } t) => clock.Minutes(f) + " to " + clock.Minutes(t),
        ({ } f, null) => "from " + clock.Minutes(f),
        (null, { } t) => "until " + clock.Minutes(t),
        _ => string.Empty,
    };

    /// <summary>The level filter as the Logs page names it: "Information and above", "Warnings and errors", "Errors only".</summary>
    internal static string LevelTitle(string minLevel) => minLevel.Trim() switch
    {
        "Information" => "Information and above",
        "Warning" => "Warnings and errors",
        "Error" => "Errors only",
        var other => other,
    };

    /// <summary>The class that logged it without its namespace, as the grid shows it: "ActionRunner".</summary>
    internal static string ShortSource(string source) => source.Length == 0 ? string.Empty : source[(source.LastIndexOf('.') + 1)..];

    private static string Cut(string? text, int max)
    {
        var value = text ?? string.Empty;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
