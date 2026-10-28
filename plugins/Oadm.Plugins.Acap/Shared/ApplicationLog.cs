using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.Acap;

/// <summary>One line of an application's log: the device's time, level and process when the line has them.</summary>
public sealed record AppLogLine(string Text)
{
    /// <summary>The time the device wrote ("2026-10-27T07:45:40.900+01:00"); null for a line without one.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>"CRIT", "ERR", "WARNING", "NOTICE", "INFO", "DEBUG"; null for a line without one.</summary>
    public string? Level { get; init; }

    /// <summary>The process ("eva[8617]"); null for a line without one.</summary>
    public string? Process { get; init; }

    /// <summary>The text after the process, else the whole line.</summary>
    public string Message { get; init; } = Text;
}

/// <summary>The log lines of an application, oldest first (newest last); <see cref="Truncated"/> when older lines were dropped.</summary>
public sealed record AppLog(IReadOnlyList<AppLogLine> Lines, bool Truncated)
{
    public static readonly AppLog Empty = new([], false);

    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// The device's log lines of one application: <c>GET /axis-cgi/admin/systemlog.cgi?appname=&lt;Name&gt;</c> (read only; seen on
/// AXIS P3265-V, AXIS OS 12.11: a header line "----- Contents of SYSTEM_LOG for 'eva' -----", blank lines, then
/// "&lt;time&gt; &lt;host&gt; [ LEVEL ] process[pid]: message"). At most <see cref="MaxLines"/> lines and <see cref="MaxBytes"/>
/// are kept: the newest.
/// </summary>
public static class AppLogReader
{
    public const string LogPath = "/axis-cgi/admin/systemlog.cgi";

    public const int MaxLines = 10_000;

    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The longest line kept; longer ones are cut.</summary>
    public const int MaxLineLength = 8 * 1024;

    /// <summary>The window's text for an application without log lines.</summary>
    public const string NoLines = "No log lines from this application.";

    private static readonly Regex LinePattern = new(
        @"^(?<time>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d))\s+(?<host>\S+)\s+\[\s*(?<level>[A-Za-z]+)\s*\]\s+(?:(?<process>[^\s:]+):\s)?(?<message>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><c>/axis-cgi/admin/systemlog.cgi?appname=&lt;Name&gt;</c>.</summary>
    public static string BuildUri(string application)
    {
        if (!EapReader.IsValidAppName(application))
        {
            throw new ArgumentException("Invalid application name.", nameof(application));
        }

        return $"{LogPath}?appname={Uri.EscapeDataString(application)}";
    }

    /// <summary>Parses a whole answer (see <see cref="ReadAsync(TextReader, CancellationToken)"/>).</summary>
    public static AppLog Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return Read(reader);
    }

    /// <summary>Reads the answer line by line and keeps only the newest lines within the limits (memory stays bounded).</summary>
    public static async Task<AppLog> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await ReadAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>Reads lines until the end; the newest <see cref="MaxLines"/> lines and <see cref="MaxBytes"/> are kept.</summary>
    public static async Task<AppLog> ReadAsync(TextReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var kept = new Kept();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            kept.Add(line);
        }

        return kept.ToLog();
    }

    private static AppLog Read(TextReader reader)
    {
        var kept = new Kept();
        while (reader.ReadLine() is { } line)
        {
            kept.Add(line);
        }

        return kept.ToLog();
    }

    /// <summary>One line, or null for the header and blank lines.</summary>
    public static AppLogLine? ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(text) || (text.StartsWith("-----", StringComparison.Ordinal) && text.EndsWith("-----", StringComparison.Ordinal)))
        {
            return null;
        }

        if (text.Length > MaxLineLength)
        {
            text = text[..MaxLineLength];
        }

        var match = LinePattern.Match(text);
        if (!match.Success)
        {
            return new AppLogLine(text);
        }

        return new AppLogLine(text)
        {
            Time = DateTimeOffset.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null,
            Level = match.Groups["level"].Value.ToUpperInvariant(),
            Process = match.Groups["process"].Success ? match.Groups["process"].Value : null,
            Message = match.Groups["message"].Value,
        };
    }

    /// <summary>The newest lines within the limits.</summary>
    private sealed class Kept
    {
        private readonly Queue<AppLogLine> _lines = new();
        private long _bytes;
        private bool _truncated;

        public void Add(string raw)
        {
            if (ParseLine(raw) is not { } line)
            {
                return;
            }

            _lines.Enqueue(line);
            _bytes += Size(line);
            while (_lines.Count > MaxLines || _bytes > MaxBytes)
            {
                _bytes -= Size(_lines.Dequeue());
                _truncated = true;
            }
        }

        public AppLog ToLog() => _lines.Count == 0 && !_truncated ? AppLog.Empty : new AppLog([.. _lines], _truncated);

        private static long Size(AppLogLine line) => Encoding.UTF8.GetByteCount(line.Text) + 1;
    }
}
