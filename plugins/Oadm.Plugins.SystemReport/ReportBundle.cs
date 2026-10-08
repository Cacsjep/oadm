using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Oadm.Plugins.SystemReport;

/// <summary>One device of a bundle: its facts, and the report file on disk or the error.</summary>
public sealed record BundleEntry(DeviceReportStatus Device, string? ReportPath, string? Mode, DateTimeOffset? CapturedUtc);

/// <summary>
/// File names inside the bundle and the bundle itself: one ZIP with every device's report (stored as they came from the
/// device, they are ZIPs already) plus <see cref="SystemReportPluginInfo.SummaryFileName"/> listing reports and failures.
/// </summary>
public static class ReportBundle
{
    /// <summary>
    /// <c>&lt;address&gt;_&lt;MAC&gt;_&lt;model&gt;_&lt;yyyyMMdd-HHmmss&gt;.zip</c> (UTC), e.g.
    /// <c>10.0.0.48_B8A44F631339_P3265-V_20261008-095856.zip</c>. Characters other than letters, digits, '.' and '-'
    /// become '-' (IPv6 addresses, host names with odd characters); the "AXIS " prefix of the model is dropped.
    /// </summary>
    public static string FileName(string address, string serial, string? model, DateTimeOffset capturedUtc)
    {
        var parts = new List<string> { Safe(address), Safe(serial) };
        var shortModel = (model ?? string.Empty).Trim();
        if (shortModel.StartsWith("AXIS ", StringComparison.OrdinalIgnoreCase))
        {
            shortModel = shortModel[5..];
        }

        if (shortModel.Length > 0)
        {
            parts.Add(Safe(shortModel));
        }

        parts.Add(capturedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        return string.Join('_', parts.Where(p => p.Length > 0)) + ".zip";
    }

    /// <summary>Default name of the bundle the client saves: <c>oadm-system-reports-&lt;yyyy-MM-dd&gt;.zip</c>.</summary>
    public static string DefaultBundleName(DateTime localNow) =>
        "oadm-system-reports-" + localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>
    /// Writes the bundle to <paramref name="bundlePath"/>: the reports in entry order (a name used twice gets "-2", "-3"),
    /// then the summary. Reports are stored without compression (they are ZIPs). Returns the names used per entry.
    /// </summary>
    public static async Task<IReadOnlyList<string?>> WriteAsync(string bundlePath, IReadOnlyList<BundleEntry> entries, DateTimeOffset createdUtc, string oadmVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var names = new string?[entries.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemReportPluginInfo.SummaryFileName };
        await using (var file = new FileStream(bundlePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                if (entry.ReportPath is null || !File.Exists(entry.ReportPath))
                {
                    continue;
                }

                var name = Unique(entry.Device.FileName ?? FileName(entry.Device.Address, entry.Device.Serial, entry.Device.Model, entry.CapturedUtc ?? createdUtc), used);
                names[i] = name;
                var zipEntry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                zipEntry.LastWriteTime = entry.CapturedUtc ?? createdUtc;
                await using var target = await zipEntry.OpenAsync(ct).ConfigureAwait(false);
                await using var source = new FileStream(entry.ReportPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            var summary = zip.CreateEntry(SystemReportPluginInfo.SummaryFileName, CompressionLevel.Optimal);
            summary.LastWriteTime = createdUtc;
            await using (var writer = new StreamWriter(await summary.OpenAsync(ct).ConfigureAwait(false), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(Summary(entries, names, createdUtc, oadmVersion).AsMemory(), ct).ConfigureAwait(false);
            }
        }

        return names;
    }

    /// <summary>The text of summary.txt: counts, the reports with their device, then the failures with the reason.</summary>
    public static string Summary(IReadOnlyList<BundleEntry> entries, IReadOnlyList<string?> names, DateTimeOffset createdUtc, string oadmVersion)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(names);
        var ok = entries.Where((_, i) => names[i] is not null).Count();
        var failed = entries.Count - ok;
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.Append("OADM system reports\r\n");
        text.Append(c, $"Created: {createdUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC by OADM {oadmVersion}\r\n");
        text.Append(c, $"Devices: {entries.Count}, reports: {ok}, failed: {failed}\r\n");
        text.Append("Each report is the device's server report (VAPIX serverreport.cgi) as the device sent it. Times are UTC.\r\n");

        if (ok > 0)
        {
            text.Append("\r\nReports\r\n");
            for (var i = 0; i < entries.Count; i++)
            {
                if (names[i] is { } name)
                {
                    var d = entries[i].Device;
                    text.Append(c, $"  {name}  {d.Address}  {d.Serial}  {d.Model ?? "-"}  {Size(d.Size)}  mode {entries[i].Mode}\r\n");
                }
            }
        }

        if (failed > 0)
        {
            text.Append("\r\nFailed\r\n");
            for (var i = 0; i < entries.Count; i++)
            {
                if (names[i] is null)
                {
                    var d = entries[i].Device;
                    text.Append(c, $"  {d.Address}  {d.Serial}  {d.Model ?? "-"}  {d.Error ?? "No report"}\r\n");
                }
            }
        }

        return text.ToString();
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (bytes + 1023) / 1024)} KB");

    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{extension}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Safe(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' ? ch : '-');
        }

        return builder.ToString().Trim('.', '-');
    }
}
