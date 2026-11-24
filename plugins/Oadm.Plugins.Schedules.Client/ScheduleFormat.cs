using System.Globalization;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>The short texts of a schedule and its runs: when it runs, the next run, the counts, the estimate, the stages.</summary>
public static class ScheduleFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>"Mondays 23:00", "Mon, Wed, Fri 23:00 until 05:00", "Daily 02:00", "Day 1 of each month 23:00", "24 Nov 2026, 23:00".</summary>
    public static string WhenText(ScheduleWhen when)
    {
        ArgumentNullException.ThrowIfNull(when);
        var time = Time(when.Time);
        var text = when.Recurrence switch
        {
            Recurrence.Once => when.Date is { } date ? date.ToString("d MMM yyyy", Culture) + ", " + time : time,
            Recurrence.Daily => "Daily " + time,
            Recurrence.Weekly => Days(when.Days) + " " + time,
            Recurrence.Monthly => string.Create(Culture, $"Day {when.DayOfMonth ?? 1} of each month {time}"),
            _ => time,
        };
        return when.WindowEnd is { } end ? text + " until " + Time(end) : text;
    }

    /// <summary>"Mondays", "Mon, Wed, Fri", "Every day".</summary>
    public static string Days(IReadOnlyList<DayOfWeek> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var ordered = days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
        return ordered.Count switch
        {
            0 => string.Empty,
            1 => Culture.DateTimeFormat.GetDayName(ordered[0]) + "s",
            7 => "Every day",
            _ => string.Join(", ", ordered.Select(d => Culture.DateTimeFormat.GetAbbreviatedDayName(d))),
        };
    }

    /// <summary>"23:00".</summary>
    public static string Time(TimeOnly time) => time.ToString("HH:mm", Culture);

    /// <summary>"Mon 24 Nov, 23:00" in server time (the offset the server sent); a time of another year with the year.</summary>
    public static string Moment(DateTimeOffset? time, DateTimeOffset now)
    {
        if (time is not { } t)
        {
            return string.Empty;
        }

        return t.Year == now.Year ? t.ToString("ddd d MMM, HH:mm", Culture) : t.ToString("ddd d MMM yyyy, HH:mm", Culture);
    }

    /// <summary>"212 done, 3 warnings, 3 failed, 12 offline, 5 not started": the counts that are not 0.</summary>
    public static string Counts(ScheduleRun run) => string.Join(", ", CountParts(run));

    private static List<string> CountParts(ScheduleRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var parts = new List<string>();
        Add(parts, run.Done, "done");
        Add(parts, run.Warnings, run.Warnings == 1 ? "warning" : "warnings");
        Add(parts, run.Failed, "failed");
        foreach (var (status, count) in run.NotReachable.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            Add(parts, count, status.ToLowerInvariant());
        }

        Add(parts, run.NotStarted, "not started");
        return parts;
    }

    /// <summary>
    /// The Last run cell and the text of a run that stopped or never ran: the reason and the counts it does not say itself
    /// ("Stopped at stage 1: 1 failed, 6 not started"; "The owner no longer has the right Restart."), else the counts.
    /// </summary>
    public static string Summary(ScheduleRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var counts = Counts(run);
        return run.Result switch
        {
            RunResult.Missed or RunResult.Skipped => SchedulesTexts.ResultText(run.Result),
            RunResult.Stopped or RunResult.Failed when run.Reason is { Length: > 0 } reason => WithReason(reason.Trim(), CountParts(run)),
            RunResult.Running => counts.Length == 0 ? SchedulesTexts.Running : SchedulesTexts.Running + ", " + counts,
            _ => counts.Length == 0 ? SchedulesTexts.ResultText(run.Result) : counts,
        };
    }

    /// <summary>The reason, then the counts it does not name: after a sentence as a new sentence, else after a comma.</summary>
    private static string WithReason(string reason, List<string> parts)
    {
        // "Stopped at stage 3: 1 failed" is a phrase (no period of its own); "The owner no longer has the right Restart." a sentence.
        var phrase = reason.Contains(':', StringComparison.Ordinal) || !reason.EndsWith('.');
        var head = phrase ? reason.TrimEnd('.') : reason;
        var rest = parts.Where(p => !reason.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Count == 0)
        {
            return head;
        }

        var text = string.Join(", ", rest);
        return phrase ? head + ", " + text : head + " " + Capital(text);
    }

    /// <summary>"about 3 h 20 min", "about 24 h", "about 5 min".</summary>
    public static string Duration(TimeSpan duration)
    {
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero));
        if (minutes < 60)
        {
            return string.Create(Culture, $"about {minutes} min");
        }

        if (minutes >= 600)
        {
            return string.Create(Culture, $"about {(int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero):N0} h");
        }

        var rounded = (int)(Math.Round(minutes / 5.0, MidpointRounding.AwayFromZero) * 5);
        var (h, m) = (rounded / 60, rounded % 60);
        return m == 0 ? string.Create(Culture, $"about {h} h") : string.Create(Culture, $"about {h} h {m} min");
    }

    /// <summary>"1 → 3 → 8 → 15 → 30 → 93".</summary>
    public static string StagesText(IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        return string.Join(" → ", sizes.Select(s => s.ToString("N0", Culture)));
    }

    private static void Add(List<string> parts, int count, string what)
    {
        if (count > 0)
        {
            parts.Add(count.ToString("N0", Culture) + " " + what);
        }
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
