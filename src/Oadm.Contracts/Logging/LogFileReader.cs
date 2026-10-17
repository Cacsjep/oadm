using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oadm.Contracts.Logging;

/// <summary>One entry of a log file: time, level ("Information"), source ("Oadm.Core.Events.ActionRunner"), message, exception.</summary>
public sealed record LogFileEntry(DateTimeOffset Time, string Level, string Source, string Message, string Exception)
{
    /// <summary>"Verbose" 0 ... "Fatal" 5, for the level filter.</summary>
    public int Rank => LogFileReader.Rank(Level);
}

/// <summary>One page of log entries, newest first; <see cref="HasMore"/>: older entries match too.</summary>
public sealed record LogFilePage(IReadOnlyList<LogFileEntry> Entries, bool HasMore);

/// <summary>
/// Reads the Serilog text files of the server and the client (<c>oadm-server-20261017.log</c>, <c>client-20261017_001.log</c>),
/// newest entry first, in pages: the template is <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}:
/// {Message}{NewLine}{Exception}</c>; lines that do not start with a time belong to the entry before them (exceptions, long
/// messages). Files in use are read shared, so the logger keeps writing. Only the files a page needs are read.
/// </summary>
public static partial class LogFileReader
{
    /// <summary>Most entries of one page.</summary>
    public const int MaxPageSize = 500;

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[([A-Z]{3})\] ?(.*)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Head();

    /// <summary>
    /// The page <paramref name="offset"/> .. <paramref name="offset"/> + <paramref name="limit"/> of the entries at or above
    /// <paramref name="minRank"/> that contain <paramref name="search"/> (any case, in source, message or exception), newest
    /// first, from the files of <paramref name="folder"/> matching <paramref name="pattern"/>. A missing folder is an empty page.
    /// </summary>
    public static LogFilePage Read(string folder, string pattern, int offset, int limit, int minRank, string? search, CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPageSize);
        if (!Directory.Exists(folder))
        {
            return new LogFilePage([], false);
        }

        // Newest file first: a day sorts after the day before, "_001" after the day's first file.
        var files = Directory.GetFiles(folder, pattern).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
        var page = new List<LogFileEntry>(limit);
        var skipped = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var entries = ReadFile(file);
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry.Rank < minRank || !Matches(entry, search))
                {
                    continue;
                }

                if (skipped < offset)
                {
                    skipped++;
                    continue;
                }

                if (page.Count == limit)
                {
                    return new LogFilePage(page, true);
                }

                page.Add(entry);
            }
        }

        return new LogFilePage(page, false);
    }

    /// <summary>The entries of one file, oldest first; unreadable files count as empty.</summary>
    public static List<LogFileEntry> ReadFile(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Parse(text);
    }

    /// <summary>The entries of a log text, oldest first.</summary>
    public static List<LogFileEntry> Parse(string text)
    {
        var entries = new List<LogFileEntry>();
        DateTimeOffset time = default;
        string level = string.Empty, source = string.Empty, message = string.Empty;
        var extra = new StringBuilder();
        var open = false;

        void Close()
        {
            if (open)
            {
                entries.Add(new LogFileEntry(time, level, source, message, extra.ToString().TrimEnd()));
            }

            extra.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var head = Head().Match(line);
            if (head.Success && DateTimeOffset.TryParseExact(head.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                Close();
                open = true;
                time = parsed;
                level = LevelName(head.Groups[2].Value);
                (source, message) = SplitSource(head.Groups[3].Value);
            }
            else if (open && line.Length > 0)
            {
                extra.AppendLine(line);
            }
        }

        Close();
        return entries;
    }

    /// <summary>"Verbose" 0, "Debug" 1, "Information" 2, "Warning" 3, "Error" 4, "Fatal" 5.</summary>
    public static int Rank(string level) => level switch
    {
        "Verbose" => 0,
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        "Fatal" => 5,
        _ => 2,
    };

    private static string LevelName(string code) => code switch
    {
        "VRB" => "Verbose",
        "DBG" => "Debug",
        "INF" => "Information",
        "WRN" => "Warning",
        "ERR" => "Error",
        "FTL" => "Fatal",
        _ => "Information",
    };

    /// <summary>"Oadm.Core.Events.ActionRunner: text": a source is one word with a dot before ": "; else the whole line is the message.</summary>
    private static (string Source, string Message) SplitSource(string rest)
    {
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0)
        {
            var source = rest[..colon];
            if (source.Contains('.', StringComparison.Ordinal) && !source.Contains(' ', StringComparison.Ordinal))
            {
                return (source, rest[(colon + 2)..]);
            }
        }

        return (string.Empty, rest);
    }

    private static bool Matches(LogFileEntry entry, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Exception.Contains(search, StringComparison.OrdinalIgnoreCase);
}
