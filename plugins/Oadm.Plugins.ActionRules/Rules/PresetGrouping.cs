using System.Globalization;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>One recipient of one device.</summary>
public sealed record RecipientMember(DeviceRulesRow Device, RecipientView Recipient);

/// <summary>Recipients of many devices with the same name and kind; their settings with a value or a count of values.</summary>
public sealed record RecipientGroup(string Key, IReadOnlyList<RecipientMember> Members, string Name, string Template, IReadOnlyList<ParameterSummary> Parameters)
{
    public int DeviceCount => Members.Select(m => m.Device.DeviceId).Distinct().Count();

    public string KindText => RecipientKinds.Of(Template);

    public string ValuesText => string.Join(" · ", Parameters.Where(p => !p.IsSecret).Select(p =>
        p.Value is not null ? p.Name + " " + (p.Value.Length == 0 ? "(empty)" : p.Value)
        : string.Create(CultureInfo.CurrentCulture, $"{p.Name}: {p.Values:N0} values")));
}

/// <summary>One schedule of one device.</summary>
public sealed record ScheduleMember(DeviceRulesRow Device, ScheduleView Schedule);

/// <summary>Schedules of many devices with the same name and times.</summary>
public sealed record ScheduleGroup(string Key, IReadOnlyList<ScheduleMember> Members, string Name, string Definition, bool IsBuiltIn)
{
    public int DeviceCount => Members.Select(m => m.Device.DeviceId).Distinct().Count();

    public string WhenText => ScheduleText.Describe(Definition);

    /// <summary>Rules on all devices that use these schedules.</summary>
    public int UsedBy => Members.Sum(m => m.Schedule.UsedBy);
}

/// <summary>The names of the recipient kinds as the device's web interface shows them.</summary>
public static class RecipientKinds
{
    public static string Of(string template) => template switch
    {
        "com.axis.recipient.ftp" => "FTP",
        "com.axis.recipient.sftp" => "SFTP",
        "com.axis.recipient.http" => "HTTP",
        "com.axis.recipient.https" => "HTTPS",
        "com.axis.recipient.smtp" => "Email",
        "com.axis.recipient.tcp" => "TCP",
        "com.axis.recipient.networkshare" => "Network share",
        "com.axis.recipient.sip" => "SIP",
        _ => template.Split('.')[^1],
    };
}

/// <summary>Groups the recipients and schedules of all devices in O(items).</summary>
public static class PresetGrouping
{
    public static IReadOnlyList<RecipientGroup> Recipients(IEnumerable<DeviceRulesRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new Dictionary<string, List<RecipientMember>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var recipient in row.Recipients)
            {
                var key = recipient.Name.Trim().ToUpperInvariant() + "|" + recipient.Template;
                if (!groups.TryGetValue(key, out var members))
                {
                    groups[key] = members = [];
                }

                members.Add(new RecipientMember(row, recipient));
            }
        }

        return [.. groups.Select(g => new RecipientGroup(g.Key, g.Value, g.Value[0].Recipient.Name.Trim(), g.Value[0].Recipient.Template, Summaries(g.Value)))
            .OrderByDescending(g => g.DeviceCount).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static IReadOnlyList<ScheduleGroup> Schedules(IEnumerable<DeviceRulesRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new Dictionary<string, List<ScheduleMember>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var schedule in row.Schedules)
            {
                var key = schedule.Name.Trim().ToUpperInvariant() + "|" + ScheduleText.Normalize(schedule.Definition);
                if (!groups.TryGetValue(key, out var members))
                {
                    groups[key] = members = [];
                }

                members.Add(new ScheduleMember(row, schedule));
            }
        }

        return [.. groups.Select(g => new ScheduleGroup(g.Key, g.Value, g.Value[0].Schedule.Name.Trim(), g.Value[0].Schedule.Definition, g.Value.All(m => m.Schedule.IsBuiltIn)))
            .OrderBy(g => g.IsBuiltIn).ThenByDescending(g => g.DeviceCount).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static List<ParameterSummary> Summaries(List<RecipientMember> members)
    {
        var order = new List<string>();
        var values = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var p in member.Recipient.Parameters)
            {
                if (!values.TryGetValue(p.Name, out var set))
                {
                    values[p.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    order.Add(p.Name);
                }

                set.Add(RuleText.IsSecret(p.Name) ? (member.Recipient.StoredSecrets.Contains(p.Name) ? "set" : string.Empty) : p.Value);
            }
        }

        return [.. order.Select(name => new ParameterSummary(
            name,
            values[name].Count == 1 && !RuleText.IsSecret(name) ? values[name].First() : null,
            values[name].Count,
            RuleText.IsSecret(name)))];
    }
}
