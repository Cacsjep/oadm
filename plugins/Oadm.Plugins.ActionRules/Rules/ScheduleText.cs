using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>How a schedule repeats, as the page edits it.</summary>
public enum ScheduleKind
{
    /// <summary>A time range on chosen weekdays (may end the next day).</summary>
    Weekly,

    /// <summary>A pulse every N minutes.</summary>
    Every,

    /// <summary>Any other iCalendar text, edited as text.</summary>
    Custom,
}

/// <summary>A schedule in the page's terms; <see cref="ScheduleText.Build"/> turns it into the device's iCalendar text.</summary>
public sealed record ScheduleShape(ScheduleKind Kind, IReadOnlyList<DayOfWeek> Days, TimeSpan From, TimeSpan To, int EveryMinutes, string Text);

/// <summary>
/// The device's schedules are iCalendar text (<c>DTSTART:19700101T080000</c>, <c>DTEND</c>, <c>RRULE:FREQ=WEEKLY;BYDAY=MO</c>),
/// verified on AXIS OS 10.12, 11.9 and 12.11. The web interface writes two shapes: a weekly range (DTSTART and DTEND on
/// 1970-01-01, DTEND on the 2nd when the range ends the next day, BYDAY the days) and a pulse (DTSTART only,
/// <c>FREQ=MINUTELY;INTERVAL=n</c> or <c>SECONDLY</c>). Everything else is shown and edited as text.
/// </summary>
public static partial class ScheduleText
{
    public const int MaxNameLength = 64;
    public const int MaxTextLength = 1_024;
    public const string OwnIdPrefix = "com.axis.schedules.oadm.";

    private static readonly (string Code, DayOfWeek Day, string Short)[] Codes =
    [
        ("MO", DayOfWeek.Monday, "Mon"), ("TU", DayOfWeek.Tuesday, "Tue"), ("WE", DayOfWeek.Wednesday, "Wed"),
        ("TH", DayOfWeek.Thursday, "Thu"), ("FR", DayOfWeek.Friday, "Fri"), ("SA", DayOfWeek.Saturday, "Sat"),
        ("SU", DayOfWeek.Sunday, "Sun"),
    ];

    /// <summary>The days in week order Monday to Sunday.</summary>
    public static IReadOnlyList<DayOfWeek> Week { get; } = [.. Codes.Select(c => c.Day)];

    /// <summary>The definition with line breaks as the device writes them and without blanks around lines.</summary>
    public static string Normalize(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return string.Join('\n', definition.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>The shape of a definition (<see cref="ScheduleKind.Custom"/> when it is not one the page edits).</summary>
    public static ScheduleShape Parse(string definition)
    {
        var text = Normalize(definition);
        var custom = new ScheduleShape(ScheduleKind.Custom, [], TimeSpan.Zero, TimeSpan.Zero, 0, text);
        var lines = text.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1]);
        if (lines.Count != text.Split('\n').Length || !lines.TryGetValue("DTSTART", out var start) || !lines.TryGetValue("RRULE", out var rule)
            || StampPattern().Match(start) is not { Success: true } s || s.Groups["d"].Value != "01")
        {
            return custom;
        }

        var rules = rule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
        var from = Time(s);
        if (!lines.ContainsKey("DTEND") && rules.TryGetValue("FREQ", out var freq) && from == TimeSpan.Zero && rules.Count <= 2)
        {
            var interval = rules.TryGetValue("INTERVAL", out var i) && int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
            return freq switch
            {
                "MINUTELY" when interval is > 0 => custom with { Kind = ScheduleKind.Every, EveryMinutes = interval },
                "HOURLY" when interval is > 0 && interval <= 24 * 7 => custom with { Kind = ScheduleKind.Every, EveryMinutes = interval * 60 },
                _ => custom,
            };
        }

        if (lines.Count != 3 || !lines.TryGetValue("DTEND", out var end) || StampPattern().Match(end) is not { Success: true } e
            || e.Groups["d"].Value is not ("01" or "02") || rules.GetValueOrDefault("FREQ") != "WEEKLY"
            || !rules.TryGetValue("BYDAY", out var byDay) || rules.Count != 2)
        {
            return custom;
        }

        var days = new List<DayOfWeek>();
        foreach (var code in byDay.Split(','))
        {
            if (Codes.FirstOrDefault(c => c.Code == code) is not { Code: not null } match)
            {
                return custom;
            }

            days.Add(match.Day);
        }

        var to = Time(e);
        var nextDay = e.Groups["d"].Value == "02";
        if (nextDay ? to > from : to <= from)
        {
            return custom; // a range longer than a day, or one that ends before it starts
        }

        return custom with { Kind = ScheduleKind.Weekly, Days = [.. Week.Where(days.Contains)], From = from, To = to };
    }

    /// <summary>The device's text for a shape (Custom: the text as it is).</summary>
    public static string Build(ScheduleShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        switch (shape.Kind)
        {
            case ScheduleKind.Weekly:
                var nextDay = shape.To <= shape.From;
                var days = string.Join(',', Codes.Where(c => shape.Days.Contains(c.Day)).Select(c => c.Code));
                return $"DTSTART:19700101T{Stamp(shape.From)}\nDTEND:1970010{(nextDay ? '2' : '1')}T{Stamp(shape.To)}\nRRULE:FREQ=WEEKLY;BYDAY={days}";
            case ScheduleKind.Every:
                return string.Create(CultureInfo.InvariantCulture, $"DTSTART:19700101T000000\nRRULE:FREQ=MINUTELY;INTERVAL={shape.EveryMinutes}");
            default:
                return Normalize(shape.Text);
        }
    }

    /// <summary>What is wrong with a shape before any device sees it, or null.</summary>
    public static string? Problem(ScheduleShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return shape.Kind switch
        {
            ScheduleKind.Weekly when shape.Days.Count == 0 => "Choose at least one day.",
            ScheduleKind.Weekly when shape.From == shape.To => "The schedule must end at another time than it starts.",
            ScheduleKind.Weekly when !IsMinute(shape.From) || !IsMinute(shape.To) => "Enter the times as hh:mm.",
            ScheduleKind.Every when shape.EveryMinutes is < 1 or > 10_080 => "Enter 1 to 10,080 minutes.",
            ScheduleKind.Custom when Normalize(shape.Text).Length == 0 => "Enter the schedule.",
            ScheduleKind.Custom when shape.Text.Length > MaxTextLength => "The schedule is too long.",
            ScheduleKind.Custom when !Normalize(shape.Text).StartsWith("DTSTART:", StringComparison.OrdinalIgnoreCase) => "The schedule must start with DTSTART.",
            _ => null,
        };
    }

    /// <summary>"Mon to Fri 08:00 to 18:00", "Wed, Fri 18:00 to 08:00 the next day", "Every 5 minutes", or the text.</summary>
    public static string Describe(string definition)
    {
        var shape = Parse(definition);
        return shape.Kind switch
        {
            ScheduleKind.Weekly => Days(shape.Days) + " " + Clock(shape.From) + " to " + Clock(shape.To) + (shape.To <= shape.From ? " the next day" : string.Empty),
            ScheduleKind.Every => shape.EveryMinutes switch
            {
                1 => "Every minute",
                60 => "Every hour",
                var m when m % 60 == 0 => string.Create(CultureInfo.CurrentCulture, $"Every {m / 60:N0} hours"),
                var m => string.Create(CultureInfo.CurrentCulture, $"Every {m:N0} minutes"),
            },
            _ => Span(shape.Text) ?? shape.Text.Replace('\n', ' '),
        };
    }

    /// <summary>
    /// A weekly range over several days without BYDAY, like the built-in Weekdays (DTSTART 1970-01-05, DTEND 1970-01-10:
    /// "Mon 00:00 to Sat 00:00"); 1970-01-01 was a Thursday. Null for anything else.
    /// </summary>
    private static string? Span(string text)
    {
        var lines = text.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1].ToUpperInvariant());
        if (lines.Count != 3 || lines.GetValueOrDefault("RRULE") != "FREQ=WEEKLY"
            || !lines.TryGetValue("DTSTART", out var start) || !lines.TryGetValue("DTEND", out var end)
            || SpanPattern().Match(start) is not { Success: true } s || SpanPattern().Match(end) is not { Success: true } e)
        {
            return null;
        }

        static string Day(Match m) => Codes[(int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) + 2) % 7].Short;
        return Day(s) + " " + Clock(Time(s)) + " to " + Day(e) + " " + Clock(Time(e));
    }

    /// <summary>"Mon to Fri", "Every day", "Wed, Fri".</summary>
    public static string Days(IReadOnlyCollection<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var indexes = Week.Select((d, i) => (d, i)).Where(x => days.Contains(x.d)).Select(x => x.i).ToList();
        if (indexes.Count == 7)
        {
            return "Every day";
        }

        if (indexes.Count >= 3 && indexes[^1] - indexes[0] == indexes.Count - 1)
        {
            return Codes[indexes[0]].Short + " to " + Codes[indexes[^1]].Short;
        }

        return string.Join(", ", indexes.Select(i => Codes[i].Short));
    }

    /// <summary>The id OADM gives a schedule it creates: the same on every device (<c>com.axis.schedules.oadm.night-shift</c>).</summary>
    public static string OwnId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var slug = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        return OwnIdPrefix + (text.Length == 0 ? "schedule" : text.Length > 40 ? text[..40].TrimEnd('-') : text);
    }

    /// <summary>"hh:mm" -> time of day, or null.</summary>
    public static TimeSpan? ParseClock(string? text) =>
        text is not null && TimeSpan.TryParseExact(text.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    public static string Clock(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static bool IsMinute(TimeSpan t) => t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) && t.Seconds == 0 && t.Milliseconds == 0;

    private static TimeSpan Time(Match m) => new(
        int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
        int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
        int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture));

    private static string Stamp(TimeSpan t) => t.ToString(@"hhmmss", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^197001(?<d>\d\d)T(?<h>[01]\d|2[0-3])(?<m>[0-5]\d)(?<s>[0-5]\d)$")]
    private static partial Regex StampPattern();

    [GeneratedRegex(@"^197001(?<d>0[1-9]|1[0-4])T(?<h>[01]\d|2[0-3])(?<m>[0-5]\d)(?<s>[0-5]\d)$")]
    private static partial Regex SpanPattern();
}
