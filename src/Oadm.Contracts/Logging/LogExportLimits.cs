using System.Globalization;

namespace Oadm.Contracts.Logging;

/// <summary>
/// The rules of a log export (Logs page, LogExportService, the MCP tool oadm_logs_export; docs/spec/main-window.md "Logs
/// page"): at most <see cref="MaxEntries"/> entries, the newest of the filter, and the file name
/// <c>oadm-&lt;log&gt;-yyyy-MM-dd.csv</c> or <c>.pdf</c>.
/// </summary>
public static class LogExportLimits
{
    /// <summary>Most entries of one export: the newest of the filter (grid order).</summary>
    public const int MaxEntries = 5_000;

    /// <summary>Longest message of a client log row the server takes (longer ones are cut).</summary>
    public const int MaxMessageLength = 4_000;

    /// <summary>"server", "client", "audit": the part of the file name.</summary>
    public const string ServerLog = "server";

    public const string ClientLog = "client";

    public const string AuditLog = "audit";

    /// <summary>"oadm-server-2026-11-21.pdf".</summary>
    public static string FileName(string log, bool pdf, DateTimeOffset localNow) =>
        string.Create(CultureInfo.InvariantCulture, $"oadm-{log}-{localNow:yyyy-MM-dd}.{(pdf ? "pdf" : "csv")}");
}
